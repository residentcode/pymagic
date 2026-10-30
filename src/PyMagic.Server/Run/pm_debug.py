"""PyMagic debug adapter.

A small debugger built on the standard bdb module (no third-party packages). The host (PyMagic.Server.RunSession)
starts this script, which connects back over a local TCP socket and speaks JSON lines:

  host -> helper   setBreakpoints, setExceptionBreak, mute, go, continue, next, stepIn, stepInto (smart step into one
                   call of the line), stepOut, runTo (run to a line, "force" ignoring the breakpoints on the way), jump
                   (go on from another line of the frame), scopes, variables, values (inline values), evaluate,
                   setVariable, stop
  helper -> host   hello, stopped, running, scopes, variables, values, evaluated, variableSet, jumped, output (log points)

A breakpoint is a line number or {"line", "condition", "hitCondition", "logMessage"}: it stops only when the condition
holds and the hit count matches ("5", ">=5", "%3"...); with a log message it prints the message ({expr} is evaluated
in the frame) and does not stop. Muted breakpoints neither stop nor log.

The functions that returned into the stopped frame since the last step (step over, step out) are kept with their
values and shown as the "Return values" scope of the top frame.

The program's own stdin/stdout/stderr are ordinary pipes handled by the host, not this channel.
"""
import argparse
import bdb
import builtins
import json
import os
import queue
import re
import socket
import sys
import threading
import traceback
import types

MAX_ITEMS = 200
ATOMIC = (str, bytes, bytearray, int, float, complex, bool, type(None), types.ModuleType, types.FunctionType,
          types.BuiltinFunctionType, types.MethodType)


def short_repr(value, limit=200):
    try:
        text = repr(value)
    except BaseException as e:  # a broken __repr__ must not kill the session
        text = "<repr failed: %s: %s>" % (type(e).__name__, e)
    return text if len(text) <= limit else text[:limit - 1] + "…"


def children(value):
    """Expandable members of a value as (name, value) pairs, or None for leaf values."""
    if isinstance(value, ATOMIC):
        return None
    try:
        if isinstance(value, dict):
            return [(short_repr(k, 60), v) for k, v in list(value.items())[:MAX_ITEMS]] or None
        if isinstance(value, (list, tuple)):
            return [(str(i), v) for i, v in enumerate(value[:MAX_ITEMS])] or None
        if isinstance(value, (set, frozenset)):
            return [(str(i), v) for i, v in enumerate(list(value)[:MAX_ITEMS])] or None
        members = getattr(value, "__dict__", None)
        if isinstance(members, (dict, types.MappingProxyType)):
            shown = [(k, v) for k, v in list(members.items())[:MAX_ITEMS] if not (k.startswith("__") and k.endswith("__"))]
            return shown or None
    except BaseException:
        return None
    return None


class Debugger(bdb.Bdb):
    def __init__(self, sock, root, just_my_code=True):
        super().__init__()
        self.sock = sock
        self.root = os.path.normcase(os.path.abspath(root))
        self.just_my_code = just_my_code
        self.commands = queue.Queue()
        self.send_lock = threading.Lock()
        self.stack = []            # [(frame, lineno)] innermost first while stopped
        self.refs = {}             # variable reference -> list of (name, value) or an object
        self.next_ref = 1
        self.stepping_out = False
        self.go = threading.Event()
        self.main_thread = threading.main_thread()
        self.stop_lock = threading.RLock()   # one thread at a time is stopped; the others wait here until it is resumed
        self.last_thread = None              # the thread the user last stopped in: stepping follows it, not the other threads
        self.meta = {}             # (path, line) -> {"hit": (operator, number) or None, "log": message or None}
        self.scope_refs = {}       # scope variable reference -> ("locals" | "globals", frame id)
        self.break_on_raise = False
        self.exception_classes = []   # "break on raised" only for these class names (and their subclasses); empty: all
        self.raised = None         # the exception the program was last stopped on while it was being raised
        self.muted = False
        self.run_to = None         # (canonical path, line, force) of Run to Cursor
        self.step_target = None    # (function name, origin frame, origin line) of Smart Step Into
        self.return_origin = None  # the frame whose callees' return values are kept while stepping
        self.returns = []          # [(name, value)] returned into return_origin since the last resume
        self._hit_key = None
        self._hit_value = False

    # ---------------------------------------------------------------- channel

    def send(self, message):
        data = (json.dumps(message) + "\n").encode("utf-8")
        with self.send_lock:
            try:
                self.sock.sendall(data)
            except OSError:
                os._exit(1)

    def read_loop(self):
        stream = self.sock.makefile("r", encoding="utf-8", newline="\n")
        for line in stream:
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            kind = msg.get("type")
            if kind == "setBreakpoints":
                self.apply_breakpoints(msg.get("file", ""), msg.get("lines", []))
            elif kind == "setExceptionBreak":
                self.break_on_raise = msg.get("mode") == "raised"
                self.exception_classes = [c.strip() for c in msg.get("classes") or [] if c.strip()]
            elif kind == "mute":
                self.muted = bool(msg.get("on"))
            elif kind == "go":
                self.go.set()
            elif kind == "stop":
                os._exit(137)
            else:
                self.commands.put(msg)
        os._exit(0)  # the host went away

    HIT_CONDITION = re.compile(r"^\s*(==|>=|<=|>|<|%)?\s*(\d+)\s*$")

    def apply_breakpoints(self, filename, lines):
        path = os.path.abspath(filename)
        try:
            self.clear_all_file_breaks(path)
        except Exception:
            pass
        canonical = self.canonic(path)
        self.meta = {k: v for k, v in self.meta.items() if k[0] != canonical}
        for item in lines:
            spec = item if isinstance(item, dict) else {"line": item}
            line = int(spec.get("line", 0))
            condition = (spec.get("condition") or "").strip() or None
            self.set_break(path, line, cond=condition)
            hit = None
            match = self.HIT_CONDITION.match(spec.get("hitCondition") or "")
            if match:
                hit = (match.group(1) or "==", int(match.group(2)))
            log = spec.get("logMessage") or None
            if hit or log:
                self.meta[(canonical, line)] = {"hit": hit, "log": log}

    def break_here(self, frame):
        # bdb asks this twice for one line event (once to decide, once in user_line), and asking counts a hit
        key = (id(frame), frame.f_lineno, self._line_event)
        if key == self._hit_key:
            return self._hit_value
        if self.run_to is not None and (self.canonic(frame.f_code.co_filename), frame.f_lineno) == self.run_to[:2]:
            self.run_to = None
            self._hit_key, self._hit_value = key, True
            return True
        if self.muted or (self.run_to is not None and self.run_to[2]):
            result = False
        else:
            result = super().break_here(frame)
        if result:
            canonical = self.canonic(frame.f_code.co_filename)
            info = self.meta.get((canonical, frame.f_lineno))
            if info:
                bp = (bdb.Breakpoint.bplist.get((canonical, frame.f_lineno)) or [None])[0]
                hits = bp.hits if bp is not None else 1
                if info["hit"] and not self.hit_matches(info["hit"], hits):
                    result = False
                elif info["log"]:
                    self.log(frame, info["log"])
                    result = False
        self._hit_key, self._hit_value = key, result
        return result

    @staticmethod
    def hit_matches(condition, hits):
        op, n = condition
        return {"==": hits == n, ">=": hits >= n, "<=": hits <= n, ">": hits > n, "<": hits < n, "%": n > 0 and hits % n == 0}[op]

    def log(self, frame, template):
        def value(match):
            try:
                return str(eval(match.group(1), frame.f_globals, frame.f_locals))
            except BaseException as e:
                return "<%s>" % type(e).__name__
        text = re.sub(r"\{([^{}]+)\}", value, template)
        self.send({"type": "output", "stream": "stdout", "text": text + "\n"})

    _line_event = 0

    def dispatch_line(self, frame):
        self._line_event += 1
        return super().dispatch_line(frame)

    # ---------------------------------------------------------------- bdb hooks

    def is_user_file(self, filename):
        if not filename or filename.startswith("<"):
            return False
        path = os.path.normcase(os.path.abspath(filename))
        if not path.startswith(self.root + os.sep):
            return False
        lowered = path.replace("\\", "/")
        return "/site-packages/" not in lowered and "/.venv/" not in lowered and "/venv/" not in lowered

    def stop_here(self, frame):
        if self.just_my_code and not self.is_user_file(frame.f_code.co_filename):
            return False
        if self.step_target is not None:
            name, origin, line = self.step_target
            if frame is origin:
                if frame.f_lineno == line:
                    return False   # still on the line, between its calls
            elif frame.f_back is origin:
                if not self.is_target(frame, name):
                    return False   # another call of the line: run through it
            elif frame is not origin.f_back:
                return False       # deeper inside a call of the line
            self.step_target = None   # the call (or, when it was not made, the next line) is reached
        return super().stop_here(frame)

    @staticmethod
    def is_target(frame, name):
        code = frame.f_code.co_name
        if code == name:
            return True
        if code in ("__init__", "__new__", "__call__"):   # Point(...) enters Point.__init__
            owner = frame.f_locals.get("self", frame.f_locals.get("cls"))
            owner = owner if isinstance(owner, type) else type(owner)
            return any(c.__name__ == name for c in getattr(owner, "__mro__", ()))
        return False

    def dispatch_call(self, frame, arg):
        traced = super().dispatch_call(frame, arg)
        if traced is None and self.return_origin is not None and frame.f_back is self.return_origin:
            return self.trace_dispatch   # traced only so that its return value is seen
        return traced

    def dispatch_return(self, frame, arg):
        if self.return_origin is not None and frame.f_back is self.return_origin and frame.f_code.co_name not in ("<genexpr>", "<listcomp>", "<dictcomp>", "<setcomp>"):
            self.returns.append((frame.f_code.co_name + "()", arg))
            del self.returns[:-20]
        return super().dispatch_return(frame, arg)

    def set_continue(self):
        # unlike bdb.set_continue this keeps tracing on, so breakpoints added while running still work for new calls
        self._set_stopinfo(self.botframe, None, -1)

    def user_line(self, frame):
        reason = "breakpoint" if self.break_here(frame) else "step"
        if reason == "step" and self.last_thread not in (None, threading.get_ident()):
            return   # a step of another thread (a web server's worker, say) while the user is stepping in this one
        with self.stop_lock:
            self.stack = self.collect(frame)
            self.stop("stopped", reason)

    def user_return(self, frame, return_value):
        # "step out" ends in the caller right after the call returns, while its line is still being completed
        if not self.stepping_out:
            return
        if self.last_thread not in (None, threading.get_ident()):
            return
        self.stepping_out = False
        caller = frame.f_back
        if caller is None or caller is self.botframe or not self.is_user_file(caller.f_code.co_filename):
            return
        with self.stop_lock:
            self.stack = self.collect(caller)
            self.stop("stopped", "step")

    QUIET_EXCEPTIONS = (StopIteration, StopAsyncIteration, GeneratorExit, KeyboardInterrupt, SystemExit)

    def break_anywhere(self, frame):
        # a frame is only traced when something could stop in it; raising can stop anywhere in user code
        if self.break_on_raise and self.is_user_file(frame.f_code.co_filename):
            return True
        if self.run_to is not None and self.canonic(frame.f_code.co_filename) == self.run_to[0]:
            return True
        return super().break_anywhere(frame)

    def dispatch_exception(self, frame, arg):
        # bdb only reports exceptions of the frame it is stepping in; "break on raised" wants every one in user code
        if self.break_on_raise:
            self.user_exception(frame, arg)
        return super().dispatch_exception(frame, arg)

    def user_exception(self, frame, exc_info):
        # by default only uncaught exceptions stop the program (see run_script); "raised" stops at every raise in user code
        etype, value, _ = exc_info
        if not self.break_on_raise or issubclass(etype, self.QUIET_EXCEPTIONS) or not self.is_user_file(frame.f_code.co_filename):
            return
        if self.exception_classes and not any(c.__name__ in self.exception_classes for c in etype.__mro__):
            return
        if value is self.raised:
            return   # the same exception passing through the callers
        self.raised = value
        with self.stop_lock:
            self.stack = self.collect(frame)
            self.stop("stopped", "exception", "%s: %s" % (etype.__name__, value))

    def collect(self, frame):
        stack = []
        while frame is not None and frame is not self.botframe:
            stack.append((frame, frame.f_lineno))
            frame = frame.f_back
        return stack

    # ---------------------------------------------------------------- stopped state

    def frames_payload(self):
        return [{"id": i, "name": f.f_code.co_name, "file": os.path.abspath(f.f_code.co_filename)
                 if not f.f_code.co_filename.startswith("<") else f.f_code.co_filename, "line": line,
                 "first": f.f_code.co_firstlineno} for i, (f, line) in enumerate(self.stack)]

    def stop(self, kind, reason, message=None):
        with self.stop_lock:
            self.refs.clear()
            self.scope_refs.clear()
            self.run_to = None
            self.step_target = None
            self.return_origin = None
            self.last_thread = threading.get_ident()
            self.send({"type": kind, "reason": reason, "frames": self.frames_payload(), "message": message,
                       "thread": threading.current_thread().name})
            self.wait_for_resume()

    def ref_for(self, value):
        members = children(value)
        if not members:
            return 0
        ref = self.next_ref
        self.next_ref += 1
        self.refs[ref] = members
        return ref

    def describe(self, name, value):
        return {"name": name, "value": short_repr(value), "type": type(value).__name__, "ref": self.ref_for(value)}

    def scopes(self, frame_id):
        frame = self.stack[frame_id][0]
        local_items = [(k, v) for k, v in frame.f_locals.items()]
        global_items = [(k, v) for k, v in frame.f_globals.items() if not (k.startswith("__") and k.endswith("__"))]
        result = []
        if frame_id == 0 and self.returns:
            ref = self.next_ref
            self.next_ref += 1
            self.refs[ref] = list(self.returns)
            result.append({"name": "Return values", "ref": ref})
        for title, items in (("Locals", local_items), ("Globals", global_items)):
            if frame.f_locals is frame.f_globals and title == "Locals":
                continue  # module level: locals are the globals
            ref = self.next_ref
            self.next_ref += 1
            self.refs[ref] = items
            self.scope_refs[ref] = (title.lower(), frame_id)
            result.append({"name": title, "ref": ref})
        return result

    def evaluate(self, frame_id, expression, eval_only=False):
        frame = self.stack[frame_id][0]
        try:
            try:
                code = compile(expression, "<watch>", "eval")
            except SyntaxError:
                if eval_only:
                    raise
                exec(compile(expression, "<console>", "exec"), frame.f_globals, frame.f_locals)
                return {"result": "", "valueType": "", "ref": 0}
            value = eval(code, frame.f_globals, frame.f_locals)
            return {"result": short_repr(value, 1000), "valueType": type(value).__name__, "ref": self.ref_for(value)}
        except BaseException as e:
            return {"error": "%s: %s" % (type(e).__name__, e)}

    def values(self, frame_id, names):
        """The short values of the names a frame can see, for the values shown at the end of the editor's lines."""
        frame = self.stack[frame_id][0]
        found = {}
        for name in names[:MAX_ITEMS]:
            for space in (frame.f_locals, frame.f_globals):
                if name in space:
                    value = space[name]
                    if not isinstance(value, (types.ModuleType, types.FunctionType, type)):
                        found[name] = short_repr(value, 60)
                    break
        return found

    def jump(self, line):
        """Goes on from another line of the top frame; Python allows it only from the frame's own trace event."""
        frame = self.stack[0][0] if self.stack else None
        if frame is None:
            return {"error": "Not stopped."}
        try:
            frame.f_lineno = line
        except (ValueError, AttributeError) as e:
            return {"error": "Cannot jump to line %d: %s" % (line, e)}
        self.stack[0] = (frame, frame.f_lineno)
        return {"frames": self.frames_payload()}

    def set_variable(self, ref, name, expression):
        """Assigns the value of an expression to a local or global variable of the scope that `ref` belongs to."""
        scope = self.scope_refs.get(ref)
        members = self.refs.get(ref)
        if scope is None or members is None or name not in [n for n, _ in members]:
            return {"error": "Only the variables of a frame can be changed here."}
        kind, frame_id = scope
        frame = self.stack[frame_id][0]
        try:
            value = eval(compile(expression, "<value>", "eval"), frame.f_globals, frame.f_locals)
            if kind == "globals":
                frame.f_globals[name] = value
            else:
                frame.f_locals[name] = value
                try:
                    import ctypes
                    ctypes.pythonapi.PyFrame_LocalsToFast(ctypes.py_object(frame), ctypes.c_int(0))
                except Exception:
                    pass   # newer Pythons write through f_locals themselves
        except BaseException as e:
            return {"error": "%s: %s" % (type(e).__name__, e)}
        self.refs[ref] = [(n, value if n == name else v) for n, v in members]
        described = self.describe(name, value)
        described["type_"] = described.pop("type")
        return {"name": name, "value": described["value"], "valueType": described["type_"], "ref": described["ref"]}

    def wait_for_resume(self):
        while True:
            msg = self.commands.get()
            kind = msg.get("type")
            rid = msg.get("id")
            frame = self.stack[0][0] if self.stack else None
            if kind in ("continue", "next", "stepIn", "stepInto", "stepOut", "runTo"):
                self.stepping_out = False
                self.returns = []
            if kind == "continue":
                self.set_continue()
            elif kind == "next" and frame is not None:
                self.return_origin = frame
                self.set_next(frame)
            elif kind == "stepIn":
                self.set_step()
            elif kind == "stepInto" and frame is not None:
                self.step_target = (msg.get("target", ""), frame, frame.f_lineno)
                self.set_step()
            elif kind == "stepOut" and frame is not None:
                self.stepping_out = True
                self.return_origin = frame.f_back
                self.set_return(frame)
            elif kind == "runTo":
                self.run_to = (self.canonic(os.path.abspath(msg.get("file", ""))), int(msg.get("line", 0)), bool(msg.get("force")))
                self.set_continue()
            elif kind == "values":
                self.send({"type": "values", "id": rid, "values": self.values(int(msg.get("frameId", 0)), msg.get("names") or [])})
                continue
            elif kind == "jump":
                answer = self.jump(int(msg.get("line", 0)))
                answer.update({"type": "jumped", "id": rid})
                self.send(answer)
                continue
            elif kind == "scopes":
                self.send({"type": "scopes", "id": rid, "scopes": self.scopes(int(msg.get("frameId", 0)))})
                continue
            elif kind == "variables":
                members = self.refs.get(int(msg.get("ref", 0)), [])
                self.send({"type": "variables", "id": rid, "ref": msg.get("ref"),
                           "items": [self.describe(n, v) for n, v in members]})
                continue
            elif kind == "evaluate":
                answer = self.evaluate(int(msg.get("frameId", 0)), msg.get("expression", ""), bool(msg.get("evalOnly")))
                answer.update({"type": "evaluated", "id": rid})
                self.send(answer)
                continue
            elif kind == "setVariable":
                answer = self.set_variable(int(msg.get("ref", 0)), msg.get("name", ""), msg.get("expression", ""))
                answer.update({"type": "variableSet", "id": rid})
                self.send(answer)
                continue
            else:
                continue
            self.refs.clear()
            self.stack = []
            self.send({"type": "running"})
            return

    # ---------------------------------------------------------------- running the program

    def run_script(self, path, script_args):
        with open(path, "rb") as f:
            source = f.read()
        code = compile(source, path, "exec")
        sys.argv = [path] + script_args
        sys.path.insert(0, os.path.dirname(path))
        globs = {"__name__": "__main__", "__file__": path, "__builtins__": builtins, "__doc__": None, "__package__": None}

        self.reset()
        self.botframe = sys._getframe()
        self.set_continue()
        sys.settrace(self.trace_dispatch)
        threading.settrace(self.trace_dispatch)   # threads the program starts (a web server's workers) are debugged too
        try:
            exec(code, globs)
            return 0
        except SystemExit as e:
            sys.settrace(None)
            if e.code is None:
                return 0
            if isinstance(e.code, int):
                return e.code
            print(e.code, file=sys.stderr)
            return 1
        except BaseException:
            sys.settrace(None)
            etype, value, tb = sys.exc_info()
            tb = tb.tb_next if tb is not None else None  # drop this runner's own frame
            traceback.print_exception(etype, value, tb)
            sys.stderr.flush()
            self.post_mortem(etype, value, tb)
            return 1
        finally:
            sys.settrace(None)

    def post_mortem(self, etype, value, tb):
        if value is self.raised:
            return   # the user already looked at it when it was raised
        frames = []
        while tb is not None:
            frames.append((tb.tb_frame, tb.tb_lineno))
            tb = tb.tb_next
        self.stack = list(reversed(frames))
        message = "%s: %s" % (etype.__name__, value)
        self.send({"type": "stopped", "reason": "exception", "frames": self.frames_payload(), "message": message})
        # wait until the user resumes (any resume command ends the session)
        while True:
            msg = self.commands.get()
            kind = msg.get("type")
            rid = msg.get("id")
            if kind in ("continue", "next", "stepIn", "stepOut", "stepInto", "runTo"):
                return
            if kind == "scopes":
                self.send({"type": "scopes", "id": rid, "scopes": self.scopes(int(msg.get("frameId", 0)))})
            elif kind == "values":
                self.send({"type": "values", "id": rid, "values": self.values(int(msg.get("frameId", 0)), msg.get("names") or [])})
            elif kind == "jump":
                self.send({"type": "jumped", "id": rid, "error": "The program has ended with this exception; it cannot go on."})
            elif kind == "variables":
                members = self.refs.get(int(msg.get("ref", 0)), [])
                self.send({"type": "variables", "id": rid, "ref": msg.get("ref"),
                           "items": [self.describe(n, v) for n, v in members]})
            elif kind == "evaluate":
                answer = self.evaluate(int(msg.get("frameId", 0)), msg.get("expression", ""), bool(msg.get("evalOnly")))
                answer.update({"type": "evaluated", "id": rid})
                self.send(answer)
            elif kind == "setVariable":
                answer = self.set_variable(int(msg.get("ref", 0)), msg.get("name", ""), msg.get("expression", ""))
                answer.update({"type": "variableSet", "id": rid})
                self.send(answer)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--host", default="127.0.0.1", help="where the host listens (WSL and Docker reach it by another address)")
    parser.add_argument("--token", required=True)
    parser.add_argument("--root", required=True)
    parser.add_argument("--script", required=True)
    parser.add_argument("--all-code", action="store_true", help="also step into library code")
    parser.add_argument("args", nargs="*")
    options = parser.parse_args()

    sock = socket.create_connection((options.host, options.port))
    dbg = Debugger(sock, options.root, just_my_code=not options.all_code)
    dbg.send({"type": "hello", "token": options.token})
    threading.Thread(target=dbg.read_loop, daemon=True).start()
    dbg.go.wait()

    os.chdir(options.root)
    code = dbg.run_script(os.path.abspath(options.script), options.args)
    sys.stdout.flush()
    sys.stderr.flush()
    sys.exit(code)


if __name__ == "__main__":
    main()

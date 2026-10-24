"""PyMagic debug adapter.

A small debugger built on the standard bdb module (no third-party packages). The host (PyMagic.Server.RunSession)
starts this script, which connects back over a local TCP socket and speaks JSON lines:

  host -> helper   setBreakpoints, setExceptionBreak, go, continue, next, stepIn, stepOut, scopes, variables, evaluate,
                   setVariable, stop
  helper -> host   hello, stopped, running, scopes, variables, evaluated, variableSet, output (log points)

A breakpoint is a line number or {"line", "condition", "hitCondition", "logMessage"}: it stops only when the condition
holds and the hit count matches ("5", ">=5", "%3"...); with a log message it prints the message ({expr} is evaluated
in the frame) and does not stop.

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
        self.meta = {}             # (path, line) -> {"hit": (operator, number) or None, "log": message or None}
        self.scope_refs = {}       # scope variable reference -> ("locals" | "globals", frame id)
        self.break_on_raise = False
        self.raised = None         # the exception the program was last stopped on while it was being raised
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
        return super().stop_here(frame)

    def set_continue(self):
        # unlike bdb.set_continue this keeps tracing on, so breakpoints added while running still work for new calls
        self._set_stopinfo(self.botframe, None, -1)

    def user_line(self, frame):
        reason = "breakpoint" if self.break_here(frame) else "step"
        self.stack = self.collect(frame)
        self.stop("stopped", reason)

    def user_return(self, frame, return_value):
        # "step out" ends in the caller right after the call returns, while its line is still being completed
        if not self.stepping_out:
            return
        self.stepping_out = False
        caller = frame.f_back
        if caller is None or caller is self.botframe or not self.is_user_file(caller.f_code.co_filename):
            return
        self.stack = self.collect(caller)
        self.stop("stopped", "step")

    QUIET_EXCEPTIONS = (StopIteration, StopAsyncIteration, GeneratorExit, KeyboardInterrupt, SystemExit)

    def break_anywhere(self, frame):
        # a frame is only traced when something could stop in it; raising can stop anywhere in user code
        if self.break_on_raise and self.is_user_file(frame.f_code.co_filename):
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
        if value is self.raised:
            return   # the same exception passing through the callers
        self.raised = value
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
                 if not f.f_code.co_filename.startswith("<") else f.f_code.co_filename, "line": line}
                for i, (f, line) in enumerate(self.stack)]

    def stop(self, kind, reason, message=None):
        self.refs.clear()
        self.scope_refs.clear()
        self.send({"type": kind, "reason": reason, "frames": self.frames_payload(), "message": message})
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
            if kind in ("continue", "next", "stepIn"):
                self.stepping_out = False
            if kind == "continue":
                self.set_continue()
            elif kind == "next" and frame is not None:
                self.set_next(frame)
            elif kind == "stepIn":
                self.set_step()
            elif kind == "stepOut" and frame is not None:
                self.stepping_out = True
                self.set_return(frame)
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
            if kind in ("continue", "next", "stepIn", "stepOut"):
                return
            if kind == "scopes":
                self.send({"type": "scopes", "id": rid, "scopes": self.scopes(int(msg.get("frameId", 0)))})
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
    parser.add_argument("--token", required=True)
    parser.add_argument("--root", required=True)
    parser.add_argument("--script", required=True)
    parser.add_argument("--all-code", action="store_true", help="also step into library code")
    parser.add_argument("args", nargs="*")
    options = parser.parse_args()

    sock = socket.create_connection(("127.0.0.1", options.port))
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

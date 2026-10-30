"""PyMagic debug adapter.

A small debugger built on the standard bdb module (no third-party packages). The host (PyMagic.Server.RunSession)
starts this script, which connects back over a local TCP socket and speaks JSON lines:

  host -> helper   setBreakpoints, setExceptionBreak, mute, go, continue, next, stepIn, stepInto (smart step into one
                   call of the line), stepOut, runTo (run to a line, "force" ignoring the breakpoints on the way), jump
                   (go on from another line of the frame), scopes, variables, values (inline values), table (Data View), evaluate,
                   setVariable, setName (a name of a frame), reprOf (a value loaded on demand), typeSource, setLoadPolicy, stop
  helper -> host   hello, stopped, running, scopes, variables, values, table, evaluated, variableSet, setName, reprOf,
                   typeSource, jumped, output (log points)

While one thread is stopped every other one waits at its next traced event, unless the breakpoint that stopped it says
"suspend": "thread".

A breakpoint is a line number or {"line", "condition", "hitCondition", "logMessage"}: it stops only when the condition
holds and the hit count matches ("5", ">=5", "%3"...); with a log message it prints the message ({expr} is evaluated
in the frame) and does not stop. Muted breakpoints neither stop nor log. With "function" (on a `def` line) it stops
when that function is called ("codeLine": where its code starts, the first decorator); with "after" ({"file", "line"})
it does nothing until that other breakpoint has been hit. "next" and "stepIn" take "force": Force Step Over passes the
breakpoints of the calls, Force Step Into may stop in library code.

The functions that returned into the stopped frame since the last step (step over, step out) are kept with their
values and shown as the "Return values" scope of the top frame.

The program's own stdin/stdout/stderr are ordinary pipes handled by the host, not this channel.
"""
import argparse
import bdb
import builtins
import contextlib
import io
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
        self.force_into = False    # Force Step Into: this one step may stop in library code
        self.force_over = False    # Force Step Over: no breakpoint stops until the step ends
        self.hit_lines = set()     # (canonical path, line) of the breakpoints hit so far, for "disabled until hit"
        self.hit_suspend = None    # "thread" when the breakpoint that stops lets the other threads run
        self.resumed = threading.Event()   # cleared while a stop suspends every thread
        self.resumed.set()
        self.stopped_ident = None  # the thread that is stopped while the others wait
        self.lazy = False          # the variables loading policy "on demand": values of objects are shown when asked
        self.run_to = None         # (canonical path, line, force) of Run to Cursor
        self.step_target = None    # (function name, origin frame, origin line) of Smart Step Into
        self.return_origin = None  # the frame whose callees' return values are kept while stepping
        self.returns = []          # [(name, value)] returned into return_origin since the last resume
        self._hit_key = None
        self._hit_value = False

    # ---------------------------------------------------------------- channel

    attached = False   # put into a running program (Attach to Process): never end it, take the debugger out instead
    detached = False

    def send(self, message):
        data = (json.dumps(message) + "\n").encode("utf-8")
        with self.send_lock:
            try:
                self.sock.sendall(data)
            except OSError:
                if not self.attached:
                    os._exit(1)
                threading.Thread(target=self.detach, daemon=True).start()

    def detach(self):
        """Takes the debugger out of a program it was attached to; the program runs on as before."""
        if self.detached:
            return
        self.detached = True
        self.muted = True
        self.break_on_raise = False
        self.clear_all_breaks()
        threading.settrace_all_threads(None)
        for top in sys._current_frames().values():
            frame = top
            while frame is not None:
                frame.f_trace = None
                frame = frame.f_back
        self.resumed.set()
        self.commands.put({"type": "continue"})   # a stopped thread goes on
        try:
            self.sock.close()
        except OSError:
            pass

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
            elif kind == "setLoadPolicy":
                self.lazy = msg.get("policy") == "onDemand"
            elif kind == "go":
                self.go.set()
            elif kind in ("stop", "detach") and self.attached:
                self.detach()
                return
            elif kind == "stop":
                os._exit(137)
            else:
                self.commands.put(msg)
        if self.attached:
            self.detach()   # the host went away: the program it was attached to goes on
            return
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
            line = int(spec.get("codeLine") or spec.get("line", 0))   # a decorated function's code starts at its first decorator
            condition = (spec.get("condition") or "").strip() or None
            # on a `def` line the breakpoint stops when the function is called, not when it is defined
            self.set_break(path, line, cond=condition, funcname=spec.get("function") or None)
            hit = None
            match = self.HIT_CONDITION.match(spec.get("hitCondition") or "")
            if match:
                hit = (match.group(1) or "==", int(match.group(2)))
            log = spec.get("logMessage") or None
            after = spec.get("after")   # {"file", "line"}: disabled until that breakpoint is hit
            after = (self.canonic(os.path.join(self.root, after.get("file", ""))), int(after.get("line", 0))) if isinstance(after, dict) else None
            suspend = "thread" if spec.get("suspend") == "thread" else None
            if hit or log or after or suspend:
                self.meta[(canonical, line)] = {"hit": hit, "log": log, "after": after, "suspend": suspend}

    def break_here(self, frame):
        # bdb asks this twice for one line event (once to decide, once in user_line), and asking counts a hit
        key = (id(frame), frame.f_lineno, self._line_event)
        if key == self._hit_key:
            return self._hit_value
        if self.run_to is not None and (self.canonic(frame.f_code.co_filename), frame.f_lineno) == self.run_to[:2]:
            self.run_to = None
            self._hit_key, self._hit_value = key, True
            return True
        if self.muted or self.force_over or (self.run_to is not None and self.run_to[2]):
            result = False
        else:
            result = super().break_here(frame)
        if result:
            canonical = self.canonic(frame.f_code.co_filename)
            # a function breakpoint sits on the `def` line, which is not the line being run
            line = frame.f_lineno if (canonical, frame.f_lineno) in bdb.Breakpoint.bplist else frame.f_code.co_firstlineno
            self.hit_lines.add((canonical, line))
            info = self.meta.get((canonical, line))
            self.hit_suspend = info.get("suspend") if info else None
            if info:
                bp = (bdb.Breakpoint.bplist.get((canonical, line)) or [None])[0]
                hits = bp.hits if bp is not None else 1
                if info.get("after") and info["after"] not in self.hit_lines:
                    result = False
                elif info["hit"] and not self.hit_matches(info["hit"], hits):
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
        if self.just_my_code and not self.force_into and not self.is_user_file(frame.f_code.co_filename):
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

    reader_ident = None   # the thread that reads the host's messages: never traced, never held

    def trace_dispatch(self, frame, event, arg):
        if threading.get_ident() == self.reader_ident:
            return None   # settrace_all_threads (Attach) reaches the debugger's own reader thread too
        # "suspend all": while one thread is stopped, every other one waits at its next traced event (a call, a line)
        if not self.resumed.is_set() and threading.get_ident() != self.stopped_ident:
            self.resumed.wait()
        return super().trace_dispatch(frame, event, arg)

    def stop(self, kind, reason, message=None):
        with self.stop_lock:
            self.refs.clear()
            self.scope_refs.clear()
            self.run_to = None
            self.step_target = None
            self.return_origin = None
            self.force_into = self.force_over = False
            self.last_thread = threading.get_ident()
            # a breakpoint set to "this thread only" lets the other threads run on; anything else suspends them all
            suspend_all = not (reason == "breakpoint" and self.hit_suspend == "thread")
            if suspend_all:
                self.stopped_ident = threading.get_ident()
                self.resumed.clear()
            self.send({"type": kind, "reason": reason, "frames": self.frames_payload(), "message": message,
                       "thread": threading.current_thread().name, "suspended": "all" if suspend_all else "thread"})
            try:
                self.wait_for_resume()
            finally:
                self.stopped_ident = None
                self.resumed.set()

    def ref_for(self, value):
        members = children(value)
        if not members:
            return 0
        ref = self.next_ref
        self.next_ref += 1
        self.refs[ref] = members
        return ref

    SIMPLE = (int, float, complex, bool, type(None))

    def describe(self, name, value):
        if self.lazy and not isinstance(value, self.SIMPLE) and not (isinstance(value, (str, bytes)) and len(value) <= 200):
            # on demand: the value (whose repr may be slow) is computed only when the page asks for it ("reprOf")
            return {"name": name, "value": None, "lazy": True, "type": type(value).__name__, "ref": self.ref_for(value)}
        return {"name": name, "value": short_repr(value), "type": type(value).__name__, "ref": self.ref_for(value)}

    def repr_of(self, ref, index):
        members = self.refs.get(ref) or []
        if not 0 <= index < len(members):
            return {"error": "That value is gone; the program has moved on."}
        return {"value": short_repr(members[index][1])}

    def set_name(self, frame_id, name, expression):
        """Assigns to a name of a frame, as the values at the end of the lines show it: a local, else a global."""
        frame = self.stack[frame_id][0]
        try:
            value = eval(compile(expression, "<value>", "eval"), frame.f_globals, frame.f_locals)
            self.assign(frame, "locals" if name in frame.f_locals and frame.f_locals is not frame.f_globals else "globals", name, value)
        except BaseException as e:
            return {"error": "%s: %s" % (type(e).__name__, e)}
        return {"name": name, "value": short_repr(value), "valueType": type(value).__name__}

    @staticmethod
    def assign(frame, kind, name, value):
        if kind == "globals":
            frame.f_globals[name] = value
            return
        frame.f_locals[name] = value
        try:
            import ctypes
            ctypes.pythonapi.PyFrame_LocalsToFast(ctypes.py_object(frame), ctypes.c_int(0))
        except Exception:
            pass   # newer Pythons write through f_locals themselves

    def type_source(self, frame_id, expression):
        """Where the class of a value (or the class itself) is written, for Jump to Type Source."""
        import inspect
        frame = self.stack[frame_id][0]
        try:
            value = eval(compile(expression, "<type>", "eval"), frame.f_globals, frame.f_locals)
            cls = value if isinstance(value, type) else type(value)
            # a class of the debugged script: its module "__main__" is this helper, so its methods tell the file
            own = [f.__code__.co_filename for f in vars(cls).values() if isinstance(f, types.FunctionType)] if cls.__module__ == "__main__" else []
            if own:
                path = own[0]
                with open(path, encoding="utf-8", errors="replace") as source:
                    found = [n for n, text in enumerate(source, 1) if re.match(r"\s*class\s+%s\b" % re.escape(cls.__name__), text)]
                line = found[0] if found else 1
            else:
                path = inspect.getsourcefile(cls)
                line = inspect.getsourcelines(cls)[1]
        except BaseException as e:
            return {"error": "No source for this type (%s: %s)" % (type(e).__name__, e)}
        return {"file": os.path.abspath(path), "line": line, "name": cls.__qualname__}

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

    def evaluate(self, frame_id, expression, eval_only=False, limit=1000, detail=False):
        """The value of an expression in a frame. Not eval_only (the console, the Evaluate window): statements run too,
        and what they print is caught and returned as "output". With detail: the text of a string and the JSON of a
        dict or list, for the value dialog's tree."""
        frame = self.stack[frame_id][0]
        out = io.StringIO()
        try:
            try:
                code = compile(expression, "<watch>", "eval")
            except SyntaxError:
                if eval_only:
                    raise
                with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
                    exec(compile(expression, "<console>", "exec"), frame.f_globals, frame.f_locals)
                return {"result": "", "valueType": "", "ref": 0, "output": out.getvalue()}
            if eval_only:
                value = eval(code, frame.f_globals, frame.f_locals)
            else:
                with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
                    value = eval(code, frame.f_globals, frame.f_locals)
            answer = {"result": short_repr(value, limit), "valueType": type(value).__name__, "ref": self.ref_for(value), "output": out.getvalue()}
            if detail:
                if isinstance(value, str):
                    answer["text"] = value[:limit]
                elif isinstance(value, (dict, list, tuple)):
                    try:
                        text = json.dumps(value, default=repr)
                        if len(text) <= limit:
                            answer["json"] = text
                    except (TypeError, ValueError, RecursionError):
                        pass
            return answer
        except BaseException as e:
            return {"error": "%s: %s" % (type(e).__name__, e), "output": out.getvalue()}

    def values(self, frame_id, names):
        """The short values of the names a frame can see, for the values shown at the end of the editor's lines."""
        frame = self.stack[frame_id][0]
        found = {}
        for name in names[:MAX_ITEMS]:
            for space in (frame.f_locals, frame.f_globals):
                if name in space:
                    value = space[name]
                    if self.lazy and not isinstance(value, self.SIMPLE + (str, bytes)):
                        pass   # on demand: no repr of objects until asked
                    elif not isinstance(value, (types.ModuleType, types.FunctionType, type)):
                        found[name] = short_repr(value, 60)
                    break
        return found

    TABLE_ROWS = 1000

    def table(self, frame_id, expression):
        """A value as rows and columns for the Data View: a pandas DataFrame or Series, a NumPy array, a list of rows
        (lists, tuples or dicts), a dict, or any other sequence as one column."""
        frame = self.stack[frame_id][0]
        try:
            value = eval(compile(expression, "<table>", "eval"), frame.f_globals, frame.f_locals)
        except BaseException as e:
            return {"error": "%s: %s" % (type(e).__name__, e)}
        cell = lambda v: short_repr(v, 120)
        kind = type(value).__name__
        try:
            if hasattr(value, "iloc") and hasattr(value, "columns"):          # pandas DataFrame
                part = value.iloc[:self.TABLE_ROWS]
                return {"kind": kind, "total": len(value), "columns": [str(c) for c in value.columns],
                        "index": [cell(i) for i in part.index], "rows": [[cell(v) for v in row] for row in part.itertuples(index=False)]}
            if hasattr(value, "iloc") and hasattr(value, "index"):            # pandas Series
                part = value.iloc[:self.TABLE_ROWS]
                return {"kind": kind, "total": len(value), "columns": [str(value.name) if value.name is not None else "value"],
                        "index": [cell(i) for i in part.index], "rows": [[cell(v)] for v in part]}
            if hasattr(value, "ndim") and hasattr(value, "tolist"):          # NumPy array
                items = value.tolist() if value.ndim else [value.tolist()]
                value = items if value.ndim <= 2 else [[cell(x) for x in row] for row in items]
            if isinstance(value, dict):
                items = list(value.items())[:self.TABLE_ROWS]
                return {"kind": kind, "total": len(value), "columns": ["key", "value"], "index": [str(i) for i in range(len(items))],
                        "rows": [[cell(k), cell(v)] for k, v in items]}
            items = list(value)[:self.TABLE_ROWS] if hasattr(value, "__iter__") and not isinstance(value, (str, bytes)) else None
        except BaseException as e:
            return {"error": "%s: %s" % (type(e).__name__, e)}
        if items is None:
            return {"error": "A %s is not shown as a table." % kind}
        total = len(value) if hasattr(value, "__len__") else len(items)
        index = [str(i) for i in range(len(items))]
        if items and all(isinstance(r, dict) for r in items):
            columns = []
            for r in items:
                columns += [k for k in r if k not in columns]
            return {"kind": kind, "total": total, "columns": [str(c) for c in columns], "index": index,
                    "rows": [[cell(r[c]) if c in r else "" for c in columns] for r in items]}
        if items and all(isinstance(r, (list, tuple)) for r in items):
            width = max(len(r) for r in items)
            return {"kind": kind, "total": total, "columns": [str(i) for i in range(width)], "index": index,
                    "rows": [[cell(r[i]) if i < len(r) else "" for i in range(width)] for r in items]}
        return {"kind": kind, "total": total, "columns": ["value"], "index": index, "rows": [[cell(v)] for v in items]}

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

    def answer_extra(self, kind, msg):
        frame_id = int(msg.get("frameId", 0))
        if kind == "reprOf":
            answer = self.repr_of(int(msg.get("ref", 0)), int(msg.get("index", -1)))
        elif kind == "setName":
            answer = self.set_name(frame_id, msg.get("name", ""), msg.get("expression", ""))
        else:
            answer = self.type_source(frame_id, msg.get("expression", ""))
        answer.update({"type": kind, "id": msg.get("id")})
        return answer

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
                self.force_over = bool(msg.get("force"))
                self.set_next(frame)
            elif kind == "stepIn":
                self.force_into = bool(msg.get("force"))
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
            elif kind == "table":
                answer = self.table(int(msg.get("frameId", 0)), msg.get("expression", ""))
                answer.update({"type": "table", "id": rid})
                self.send(answer)
                continue
            elif kind in ("reprOf", "setName", "typeSource"):
                self.send(self.answer_extra(kind, msg))
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
                answer = self.evaluate(int(msg.get("frameId", 0)), msg.get("expression", ""), bool(msg.get("evalOnly")), int(msg.get("limit") or 1000), bool(msg.get("detail")))
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
            elif kind == "table":
                answer = self.table(int(msg.get("frameId", 0)), msg.get("expression", ""))
                answer.update({"type": "table", "id": rid})
                self.send(answer)
            elif kind in ("reprOf", "setName", "typeSource"):
                self.send(self.answer_extra(kind, msg))
            elif kind == "jump":
                self.send({"type": "jumped", "id": rid, "error": "The program has ended with this exception; it cannot go on."})
            elif kind == "variables":
                members = self.refs.get(int(msg.get("ref", 0)), [])
                self.send({"type": "variables", "id": rid, "ref": msg.get("ref"),
                           "items": [self.describe(n, v) for n, v in members]})
            elif kind == "evaluate":
                answer = self.evaluate(int(msg.get("frameId", 0)), msg.get("expression", ""), bool(msg.get("evalOnly")), int(msg.get("limit") or 1000), bool(msg.get("detail")))
                answer.update({"type": "evaluated", "id": rid})
                self.send(answer)
            elif kind == "setVariable":
                answer = self.set_variable(int(msg.get("ref", 0)), msg.get("name", ""), msg.get("expression", ""))
                answer.update({"type": "variableSet", "id": rid})
                self.send(answer)


def attach(host, port, token, root):
    """Run by sys.remote_exec (Python 3.14, PEP 768) inside a program that is already running: connects to the host
    and traces every thread from now on, also the frames already running, so breakpoints in them stop."""
    sock = socket.create_connection((host, port))
    dbg = Debugger(sock, root)
    dbg.attached = True
    dbg.send({"type": "hello", "token": token})
    reader = threading.Thread(target=dbg.read_loop, daemon=True, name="pymagic-debugger")
    reader.start()
    dbg.reader_ident = reader.ident
    dbg.go.wait(15)   # the host sends the breakpoints first
    dbg.reset()
    outermost = sys._getframe()
    while outermost.f_back is not None:
        outermost = outermost.f_back
    dbg.botframe = outermost
    dbg.set_continue()
    threading.settrace_all_threads(dbg.trace_dispatch)
    for ident, top in sys._current_frames().items():
        if ident == reader.ident:
            continue
        frame = top
        while frame is not None:
            frame.f_trace = dbg.trace_dispatch
            frame = frame.f_back


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

"""PyMagic debug adapter.

A small debugger built on the standard bdb module (no third-party packages). The host (PyMagic.Server.RunSession)
starts this script, which connects back over a local TCP socket and speaks JSON lines:

  host -> helper   setBreakpoints, setExceptionBreak, setFunctionBreaks, setDataBreak, clearDataBreak, mute, go, pause, continue, next, stepIn, stepInto (smart step into one
                   call of the line), stepOut, runTo (run to a line, "force" ignoring the breakpoints on the way), jump
                   (go on from another line of the frame), scopes, variables, values (inline values), table (Data View), evaluate,
                   setVariable, setName (a name of a frame), reprOf (a value loaded on demand), typeSource, setLoadPolicy, stop
  helper -> host   hello, stopped, running, scopes, variables, values, table, evaluated, variableSet, setName, reprOf,
                   typeSource, jumped, dataBreakSet, dataBreakEnded (a local's call returned), output (log points)

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
import dis
import builtins
import contextlib
import io
import json
import keyword
import os
import queue
import re
import socket
import sys
import threading
import time
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


def member_keys(value, count):
    """What each member that children() lists is reached by: a dict's keys, a list's indexes, an object's attribute names."""
    try:
        if isinstance(value, dict):
            return list(value.keys())[:count]
        if isinstance(value, (list, tuple, set, frozenset)):
            return list(range(count))
        return [k for k, _ in children(value) or []]
    except BaseException:
        return []


# a generator, coroutine or async generator: its "return" at a yield is only a pause
_RESUMABLE = 0x20 | 0x80 | 0x200


def suspended(frame):
    """Whether a frame that returns only waits at a yield (or an await): it goes on later, as the same frame."""
    code = frame.f_code
    if not code.co_flags & _RESUMABLE:
        return False
    try:
        op = dis.opname[code.co_code[frame.f_lasti]]
        if op in ("YIELD_VALUE", "YIELD_FROM"):
            return True
        # 3.13 and newer stand at the RESUME after the yield already
        return op == "RESUME" and frame.f_lasti >= 2 and dis.opname[code.co_code[frame.f_lasti - 2]] == "YIELD_VALUE"
    except (IndexError, AttributeError):
        return False


def fingerprint(value):
    """What a data breakpoint compares: the value itself for numbers and strings, otherwise its identity and, for a list,
    a dict or a set, the identities of its items (so `items.append(x)` and `d[k] = v` count as changes)."""
    if isinstance(value, (int, float, complex, bool, type(None), str, bytes)):
        return (type(value), value)
    try:
        if isinstance(value, (list, bytearray)) and len(value) <= 10000:
            return (id(value), tuple(map(id, value)) if isinstance(value, list) else bytes(value))
        if isinstance(value, dict) and len(value) <= 10000:
            return (id(value), tuple((id(k), id(v)) for k, v in value.items()))
        if isinstance(value, set) and len(value) <= 10000:
            return (id(value), frozenset(map(id, value)))
    except BaseException:
        pass
    return (id(value),)


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
        self.owners = {}           # variable reference of a value's members -> (the value, what each member is reached by)
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
        self.data_breaks = {}      # key -> {"expression", "frame" (a local's), "get", "mark" (its fingerprint), "shown"}

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
        if hasattr(threading, "settrace_all_threads"):
            threading.settrace_all_threads(None)
        else:
            threading.settrace(None)
            sys.settrace(None)
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
            elif kind == "pause":
                self.pause()
            elif kind == "setFunctionBreaks":
                self.set_function_breaks(msg.get("names") or [])
            elif kind == "clearDataBreak":
                self.data_breaks.pop(int(msg.get("key", 0)), None)
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

    # ---------------------------------------------------------------- breakpoints in Django templates

    TEMPLATE_SUFFIXES = (".html", ".htm", ".txt", ".xml", ".jinja", ".jinja2", ".j2")
    template_breaks = None      # normcase(abspath) -> set of lines
    template_place = None       # (path, line) while stopped in a template
    last_template_stop = None   # (path, line, context): one stop for the nodes of one line in one rendering
    templates_hooked = False

    def apply_template_breakpoints(self, filename, lines):
        if self.template_breaks is None:
            self.template_breaks = {}
        path = os.path.normcase(os.path.abspath(filename))
        wanted = set()
        for item in lines:
            spec = item if isinstance(item, dict) else {"line": item}
            if spec.get("enabled", True) is not False:
                wanted.add(int(spec.get("line", 0)))
        if wanted:
            self.template_breaks[path] = wanted
        else:
            self.template_breaks.pop(path, None)
        self.hook_templates()

    def hook_templates(self):
        """Django's template nodes are wrapped once django.template.base is imported (now, or when the program imports it)."""
        if self.templates_hooked:
            return
        self.templates_hooked = True
        if "django.template.base" in sys.modules:
            self.patch_templates(sys.modules["django.template.base"])
            return
        debugger = self

        class Finder:
            def find_spec(self, name, path=None, target=None):
                if name != "django.template.base":
                    return None
                import importlib.util
                sys.meta_path.remove(self)
                try:
                    spec = importlib.util.find_spec(name)
                finally:
                    sys.meta_path.insert(0, self)
                if spec is None or spec.loader is None:
                    return spec
                original = spec.loader.exec_module

                def exec_module(module):
                    original(module)
                    debugger.patch_templates(module)
                spec.loader.exec_module = exec_module
                return spec
        sys.meta_path.insert(0, Finder())

    def patch_templates(self, module):
        node_class = getattr(module, "Node", None)
        if node_class is None or getattr(node_class, "_pm_wrapped", False):
            return
        node_class._pm_wrapped = True
        # Node's own, and TextNode's (which renders its text without Node's)
        for cls in (node_class, getattr(module, "TextNode", None)):
            if cls is not None and "render_annotated" in vars(cls):
                self.wrap_render(cls)

    def wrap_render(self, cls):
        original = cls.render_annotated
        debugger = self

        def render_annotated(node, context):
            place = debugger.template_hit(node, context)
            if place is not None:
                # the stop runs untraced: called from traced code, the debugger's own lines would be traced too, and resuming
                # (setting the event other threads wait on) would then wait on itself
                trace = sys.gettrace()
                sys.settrace(None)
                try:
                    debugger.stop_in_template(place, context)
                finally:
                    sys.settrace(trace)
            return original(node, context)
        cls.render_annotated = render_annotated

    def template_hit(self, node, context):
        """(file, line) when a breakpoint is on the line of the node being rendered, the first node of that line in this rendering."""
        if not self.template_breaks or self.muted:
            return None
        try:
            name = getattr(getattr(node, "origin", None), "name", None)
            token = getattr(node, "token", None)
            if not name or token is None:
                return None
            path = os.path.normcase(os.path.abspath(name))
            # a text node spans lines (the HTML between two tags): a breakpoint on any of them stops there
            last = token.lineno + (token.contents.count("\n") if getattr(token, "token_type", None) is not None and token.token_type.name == "TEXT" else 0)
            lines = [n for n in sorted(self.template_breaks.get(path, ())) if token.lineno <= n <= last]
            # the line just stopped at (the node before, on the same line) is not stopped at again
            lines = [n for n in lines if (path, n, id(context)) != self.last_template_stop]
            if not lines:
                return None
            line = lines[0]
            key = (path, line, id(context))
            self.last_template_stop = key
            return (os.path.abspath(name), line)
        except Exception:
            return None

    _template_frames = {}

    def stop_in_template(self, place, context):
        # the template's names become the locals of a frame made for them, so the Variables pane shows them as any frame's
        try:
            values = {k: v for k, v in context.flatten().items() if isinstance(k, str) and k.isidentifier() and not keyword.iskeyword(k) and not k.startswith("_pm_")}
        except Exception:
            values = {}
        names = tuple(sorted(values))
        make = self._template_frames.get(names)
        if make is None:
            source = "def template_frame(_pm_debugger, _pm_place%s):\n    _pm_debugger.template_stop(sys._getframe(), _pm_place)\n" % "".join(", " + n for n in names)
            space = {"sys": sys}
            exec(compile(source, "<template>", "exec"), space)
            make = self._template_frames[names] = space["template_frame"]
        make(self, place, **values)

    def template_stop(self, frame, place):
        with self.stop_lock:
            self.stack = self.collect(frame)
            self.template_place = place
            try:
                self.stop("stopped", "breakpoint")
            finally:
                self.template_place = None
        # a step from a template goes on to the next stop: the template has no lines of Python to step through
        self.set_continue()

    def apply_breakpoints(self, filename, lines):
        if filename.lower().endswith(self.TEMPLATE_SUFFIXES):
            self.apply_template_breakpoints(filename, lines)
            return
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
        if lines and self.go.is_set():
            # a function that is already running was not traced when it started: its frame is traced from now on, so a
            # breakpoint added below its current line stops in this call too, not only in the next one
            self.trace_running_frames(lambda frame: self.canonic(frame.f_code.co_filename) == canonical)

    def trace_running_frames(self, wanted):
        """Gives the frames of every thread of the program that `wanted` accepts the trace function, so that their next
        lines are seen; the debugger's own thread is left alone."""
        for ident, top in sys._current_frames().items():
            if ident == self.reader_ident:
                continue
            frame = top
            while frame is not None:
                if frame.f_trace is None and frame.f_code.co_filename != __file__ and wanted(frame):
                    frame.f_trace = self.trace_dispatch
                frame = frame.f_back

    # ---------------------------------------------------------------- function breakpoints by name, data breakpoints

    function_breaks = ()     # "json.dumps", "Order.total", "main": a call of a function of that name stops at its first line
    function_last = frozenset()
    function_hit = None      # the frame whose first line a function breakpoint stops at
    data_message = None      # what changed, for the stop of a data breakpoint
    next_data_key = 1

    def set_function_breaks(self, names):
        self.function_breaks = tuple(n.strip() for n in names if isinstance(n, str) and n.strip())
        self.function_last = frozenset(n.rpartition(".")[2] for n in self.function_breaks)

    def function_matches(self, frame):
        """A call of a function named by a function breakpoint: by its name, its qualified name (`Order.total`) or its
        module and qualified name (`json.dumps`); library functions too, since the user named them."""
        code = frame.f_code
        if code.co_name not in self.function_last or code.co_filename == __file__:
            return False
        qualname = getattr(code, "co_qualname", code.co_name)
        full = (frame.f_globals.get("__name__") or "") + "." + qualname
        return any(n in (qualname, full) or full.endswith("." + n) for n in self.function_breaks)

    def set_data_break(self, frame_id, expression):
        """Break when the value changes: of a name (a local is watched in its own frame), an attribute (`self.total`) or an
        item (`prices['a']`), whose object is found once, now; the program stops at the line after the one that changed it."""
        import ast
        frame = self.stack[frame_id][0]
        try:
            tree = ast.parse(expression.strip(), mode="eval").body
            evaluate = lambda node: eval(compile(ast.Expression(node), "<data>", "eval"), frame.f_globals, frame.f_locals)
            owner_frame = None
            if isinstance(tree, ast.Name):
                name = tree.id
                if frame.f_locals is not frame.f_globals and name in frame.f_locals:
                    owner_frame = frame
                    get = lambda: owner_frame.f_locals[name]
                elif name in frame.f_globals:
                    space = frame.f_globals
                    get = lambda: space[name]
                else:
                    return {"error": "%s is not a name of this frame." % name}
            elif isinstance(tree, ast.Attribute):
                base, attr = evaluate(tree.value), tree.attr
                get = lambda: getattr(base, attr)
            elif isinstance(tree, ast.Subscript):
                base, index = evaluate(tree.value), evaluate(tree.slice)
                get = lambda: base[index]
            else:
                return {"error": "Only a name, an attribute or an item can be watched for changes."}
            value = get()
        except BaseException as e:
            return {"error": "%s: %s" % (type(e).__name__, e)}
        key = self.next_data_key
        self.next_data_key += 1
        self.data_breaks[key] = {"expression": expression.strip(), "frame": owner_frame, "get": get, "mark": fingerprint(value), "shown": short_repr(value, 80)}
        self.trace_running_frames(lambda f: self.is_user_file(f.f_code.co_filename))
        return {"key": key, "value": short_repr(value, 80)}

    def check_data(self, frame):
        """What a data breakpoint saw change since it was last looked at, or None."""
        if not self.data_breaks or not self.is_user_file(frame.f_code.co_filename):
            return None
        for watch in list(self.data_breaks.values()):
            if watch["frame"] is not None and watch["frame"] is not frame:
                continue
            try:
                value = watch["get"]()
            except BaseException:
                continue   # deleted for now: a value given again is a change
            mark = fingerprint(value)
            if mark != watch["mark"]:
                before, watch["mark"], watch["shown"] = watch["shown"], mark, short_repr(value, 80)
                return "%s changed: %s -> %s" % (watch["expression"], before, watch["shown"])
        return None

    # ---------------------------------------------------------------- pause

    PAUSE_ANYWHERE_AFTER = 1.0   # seconds: a program that stays in library code (a server's loop) is paused there
    PAUSE_MAIN_FIRST = 0.5       # seconds: until then only the main thread stops for a pause
    pausing = None               # the time the user asked for a pause, until a thread stops

    def pause(self):
        """Pause: the program stops at its next line of Python, in the project's files when one is reached within a second,
        else wherever it is (a library's loop); a thread waiting in C code (input(), a socket) stops when it comes back."""
        if self.stack or self.detached:
            return   # already stopped
        self.pausing = time.monotonic()
        self.last_thread = None
        self.trace_running_frames(lambda frame: True)
        self._set_stopinfo(None, None)   # bdb's set_step, from this thread: every traced frame stops at its next line

    def break_here(self, frame):
        # bdb asks this twice for one line event (once to decide, once in user_line), and asking counts a hit
        key = (id(frame), frame.f_lineno, self._line_event)
        if key == self._hit_key:
            return self._hit_value
        if self.run_to is not None and (self.canonic(frame.f_code.co_filename), frame.f_lineno) == self.run_to[:2]:
            self.run_to = None
            self._hit_key, self._hit_value = key, True
            return True
        if self.function_hit is frame and not self.muted:
            self.function_hit = None
            self._hit_key, self._hit_value = key, True
            return True
        message = None if self.muted else self.check_data(frame)
        if message:
            self.data_message = message
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
        if self.pausing is not None:
            if frame.f_code.co_filename == __file__ or threading.get_ident() == self.reader_ident:
                return False
            waited = time.monotonic() - self.pausing
            if threading.current_thread() is not self.main_thread and waited < self.PAUSE_MAIN_FIRST:
                return False   # the main thread, when it runs Python, is where the program is shown paused
            return self.is_user_file(frame.f_code.co_filename) or waited > self.PAUSE_ANYWHERE_AFTER
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
        if self.function_breaks and not self.muted and self.function_matches(frame):
            self.function_hit = frame   # stops at the call's first line (break_here)
            return self.trace_dispatch
        traced = super().dispatch_call(frame, arg)
        if traced is None and self.return_origin is not None and frame.f_back is self.return_origin:
            return self.trace_dispatch   # traced only so that its return value is seen
        return traced

    def dispatch_return(self, frame, arg):
        for key, watch in list(self.data_breaks.items()):
            # a local's data breakpoint ends with its call; a generator (or coroutine) that only stops at a yield goes on later in the
            # same frame, so its locals are still watched
            if watch["frame"] is frame and not suspended(frame):
                del self.data_breaks[key]
                self.send({"type": "dataBreakEnded", "key": key, "expression": watch["expression"]})
        if self.return_origin is not None and frame.f_back is self.return_origin and frame.f_code.co_name not in ("<genexpr>", "<listcomp>", "<dictcomp>", "<setcomp>"):
            self.returns.append((frame.f_code.co_name + "()", arg))
            del self.returns[:-20]
        return super().dispatch_return(frame, arg)

    def set_continue(self):
        # unlike bdb.set_continue this keeps tracing on, so breakpoints added while running still work for new calls
        self._set_stopinfo(self.botframe, None, -1)

    def user_line(self, frame):
        reason = "breakpoint" if self.break_here(frame) else "step"
        message, self.data_message = self.data_message, None
        if message:
            reason = "data"
            with self.stop_lock:
                self.stack = self.collect(frame)
                self.stop("stopped", reason, message)
            return
        if self.pausing is not None and reason == "step":
            reason = "pause"
            self.pausing = None
        elif reason == "step" and self.last_thread not in (None, threading.get_ident()):
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
        if (self.break_on_raise or self.data_breaks) and self.is_user_file(frame.f_code.co_filename):
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
        # a thread's own startup frames (threading's _bootstrap, run) are not shown under its code
        while len(stack) > 1 and os.path.basename(stack[-1][0].f_code.co_filename) == "threading.py":
            stack.pop()
        return stack

    # ---------------------------------------------------------------- stopped state

    own_count = None   # how many frames of self.stack are the stopped thread's; the other threads' follow them

    @staticmethod
    def frame_info(i, f, line):
        return {"id": i, "name": f.f_code.co_name, "file": os.path.abspath(f.f_code.co_filename)
                if not f.f_code.co_filename.startswith("<") else f.f_code.co_filename, "line": line, "first": f.f_code.co_firstlineno}

    def other_threads(self):
        """The stacks of the threads that are not stopped, innermost first, without the debugger's own frames (a thread
        that waits while another one is stopped is inside this helper's trace function)."""
        names = {t.ident: t.name for t in threading.enumerate()}
        found = []
        for ident, top in sys._current_frames().items():
            if ident in (threading.get_ident(), self.reader_ident):
                continue
            stack = self.collect(top)
            k = 0
            while k < len(stack) and (stack[k][0].f_code.co_filename == __file__ or os.path.basename(stack[k][0].f_code.co_filename) in ("threading.py", "bdb.py")):
                k += 1
            if any(stack[j][0].f_code.co_filename == __file__ for j in range(k)):
                stack = stack[k:]
            if stack:
                found.append((names.get(ident, "Thread-%d" % ident), stack))
        found.sort(key=lambda t: (t[0] != "MainThread", t[0]))
        return found

    def threads_payload(self):
        threads, i = [], self.own_count or 0
        for name, stack in self.thread_stacks:
            frames = []
            for f, line in stack:
                frames.append(self.frame_info(i, f, line))
                i += 1
            threads.append({"name": name, "frames": frames})
        return threads

    thread_stacks = ()

    def frames_payload(self):
        own = self.stack[:self.own_count] if self.own_count is not None else self.stack
        frames = [self.frame_info(i, f, line) for i, (f, line) in enumerate(own)]
        if self.template_place and frames:
            # stopped in a template: its top frame is the template's line (the frame of its variables), the helper's own frames left out
            frames[0].update({"name": "template", "file": self.template_place[0], "line": self.template_place[1], "first": self.template_place[1]})
            frames = [frames[0]] + [f for f in frames[1:] if not f["file"].endswith("pm_debug.py")]
        return frames

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
            self.owners.clear()
            self.scope_refs.clear()
            self.run_to = None
            self.step_target = None
            self.return_origin = None
            self.force_into = self.force_over = False
            self.pausing = None
            self.last_thread = threading.get_ident()
            # the other threads' frames follow the stopped thread's in self.stack, so their variables can be looked at too
            self.own_count = len(self.stack)
            try:
                self.thread_stacks = self.other_threads()
            except Exception:
                self.thread_stacks = ()
            for _, stack in self.thread_stacks:
                self.stack.extend(stack)
            # a breakpoint set to "this thread only" lets the other threads run on; anything else suspends them all
            suspend_all = not (reason == "breakpoint" and self.hit_suspend == "thread")
            if suspend_all:
                self.stopped_ident = threading.get_ident()
                self.resumed.clear()
            self.send({"type": kind, "reason": reason, "frames": self.frames_payload(), "message": message,
                       "thread": threading.current_thread().name, "suspended": "all" if suspend_all else "thread",
                       "threads": self.threads_payload()})
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
        self.owners[ref] = (value, member_keys(value, len(members)))
        return ref

    def set_member(self, ref, name, expression, frame_id):
        """Set Value on a member of an object, a dict, a list: its attribute, its key or its index is assigned the value
        of the expression, evaluated in the selected frame."""
        owner, keys = self.owners[ref]
        members = self.refs[ref]
        names = [n for n, _ in members]
        if name not in names:
            return {"error": "That member is gone; the program has moved on."}
        index = names.index(name)
        if isinstance(owner, (tuple, frozenset, set, str, bytes)):
            return {"error": "A %s cannot be changed in place." % type(owner).__name__}
        frame = self.stack[frame_id][0] if 0 <= frame_id < len(self.stack) else None
        try:
            value = eval(compile(expression, "<value>", "eval"), frame.f_globals if frame else {}, frame.f_locals if frame else {})
            if isinstance(owner, (dict, list)):
                owner[keys[index]] = value
            else:
                setattr(owner, keys[index], value)
        except BaseException as e:
            return {"error": "%s: %s" % (type(e).__name__, e)}
        self.refs[ref] = [(n, value if i == index else v) for i, (n, v) in enumerate(members)]
        described = self.describe(name, value)
        return {"name": name, "value": described["value"], "valueType": described["type"], "ref": described["ref"]}

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
        local_items = [(k, v) for k, v in frame.f_locals.items() if not k.startswith("_pm_")]
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
        ipython = None if eval_only else self.ipython()
        injected = False
        try:
            if ipython is not None:
                # IPython's syntax (%magics, !commands, name?) becomes Python that calls the shell; the names of the frame are its namespace
                shell, transformer = ipython
                expression = transformer.transform_cell(expression).rstrip("\n")
                shell.user_ns.update(frame.f_globals)   # added to IPython's own names (In, Out, _ih), which its magics use
                shell.user_ns.update(frame.f_locals)
                if "get_ipython" not in frame.f_globals:
                    frame.f_globals["get_ipython"] = lambda: shell
                    injected = True
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
            shown = short_repr(value, limit)
            if ipython is not None and value is not None:
                try:
                    from IPython.lib.pretty import pretty
                    shown = pretty(value, max_width=100)
                    shown = shown if len(shown) <= limit else shown[:limit - 1] + "…"
                except Exception:
                    pass
            elif ipython is not None:
                shown = ""   # IPython shows nothing for None, as its prompt does
            answer = {"result": shown, "valueType": type(value).__name__, "ref": self.ref_for(value), "output": out.getvalue()}
            if ipython is not None:
                answer["ipython"] = True
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
        finally:
            if injected:
                frame.f_globals.pop("get_ipython", None)

    _ipython = None   # (shell, transformer) once made; False when the program's Python has no IPython

    def ipython(self):
        """IPython's shell and input transformer for the console, when the debugged program's Python has IPython; made
        without history file, and without taking the program's __main__, display hook or exception hook."""
        if self._ipython is None:
            saved = (sys.modules.get("__main__"), sys.displayhook, sys.excepthook)
            try:
                from traitlets.config import Config
                from IPython.core.interactiveshell import InteractiveShell
                from IPython.core.inputtransformer2 import TransformerManager
                config = Config()
                config.HistoryManager.hist_file = ":memory:"
                config.InteractiveShell.colors = "nocolor"
                shell = InteractiveShell.instance(config=config, user_ns={})
                self._ipython = (shell, TransformerManager())
            except Exception:
                self._ipython = False
            finally:
                main, displayhook, excepthook = saved
                if main is not None:
                    sys.modules["__main__"] = main
                sys.displayhook, sys.excepthook = displayhook, excepthook
        return self._ipython or None

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

    def set_variable(self, ref, name, expression, frame_id=0):
        """Assigns the value of an expression to a local or global variable of the scope that `ref` belongs to, or to
        the member of an object, a dict or a list that `ref` lists."""
        scope = self.scope_refs.get(ref)
        if scope is None and ref in self.owners:
            return self.set_member(ref, name, expression, frame_id)
        members = self.refs.get(ref)
        if scope is None or members is None or name not in [n for n, _ in members]:
            return {"error": "Only the variables of a frame and the members of a value can be changed here."}
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
            elif kind == "setDataBreak":
                answer = self.set_data_break(int(msg.get("frameId", 0)), msg.get("expression", ""))
                answer.update({"type": "dataBreakSet", "id": rid})
                self.send(answer)
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
                answer = self.set_variable(int(msg.get("ref", 0)), msg.get("name", ""), msg.get("expression", ""), int(msg.get("frameId", 0)))
                answer.update({"type": "variableSet", "id": rid})
                self.send(answer)
                continue
            else:
                continue
            self.refs.clear()
            self.owners.clear()
            self.stack = []
            self.own_count, self.thread_stacks = None, ()
            self.send({"type": "running"})
            return

    # ---------------------------------------------------------------- running the program

    def run_script(self, path, script_args, package=None, add_folder=True):
        with open(path, "rb") as f:
            source = f.read()
        code = compile(source, path, "exec")
        sys.argv = [path] + script_args
        if add_folder:
            sys.path.insert(0, os.path.dirname(path))
        # the program's own __main__ module, as python gives it: code that reaches its globals through sys.modules["__main__"]
        # (enum.global_enum, pickle, doctest) must not find this helper's
        main_module = types.ModuleType("__main__")
        main_module.__dict__.update({"__file__": path, "__builtins__": builtins, "__package__": package})
        sys.modules["__main__"] = main_module
        globs = main_module.__dict__

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
        self.own_count, self.thread_stacks = None, ()
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
                answer = self.set_variable(int(msg.get("ref", 0)), msg.get("name", ""), msg.get("expression", ""), int(msg.get("frameId", 0)))
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


def connect(host, port, token="", root=None):
    """Debug server: called by a program that was started elsewhere (another computer, a container, a shell of its own) to
    connect back to the IDE that listens at host:port. From the next line on the program is debugged like any other
    session: the IDE's breakpoints stop it, it can be stepped and inspected. The program keeps running when the IDE
    stops listening or hangs up. `root` is the program's own project folder (the files "just my code" means); default: the
    current folder."""
    sock = socket.create_connection((host, int(port)))
    dbg = Debugger(sock, root or os.getcwd())
    dbg.attached = True
    dbg.send({"type": "hello", "token": token})
    reader = threading.Thread(target=dbg.read_loop, daemon=True, name="pymagic-debugger")
    reader.start()
    dbg.reader_ident = reader.ident
    dbg.go.wait(15)   # the IDE sends its breakpoints first
    dbg.reset()
    outermost = sys._getframe()
    while outermost.f_back is not None:
        outermost = outermost.f_back
    dbg.botframe = outermost
    dbg.set_continue()
    settrace_all = getattr(threading, "settrace_all_threads", None)
    if settrace_all is not None:
        settrace_all(dbg.trace_dispatch)
    else:
        threading.settrace(dbg.trace_dispatch)
        sys.settrace(dbg.trace_dispatch)
    for ident, top in sys._current_frames().items():
        if ident == reader.ident:
            continue
        frame = top
        while frame is not None:
            frame.f_trace = dbg.trace_dispatch
            frame = frame.f_back
    return dbg


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--host", default="127.0.0.1", help="where the host listens (WSL and Docker reach it by another address)")
    parser.add_argument("--token", required=True)
    parser.add_argument("--root", required=True)
    parser.add_argument("--script", help="the file to run")
    parser.add_argument("--module", help="or the module to run, as python -m does")
    parser.add_argument("--all-code", action="store_true", help="also step into library code")
    parser.add_argument("--tests", help="or PyMagic's test runner (pm_tests.py), whose results go through this connection")
    parser.add_argument("args", nargs="*")
    options = parser.parse_args()

    sock = socket.create_connection((options.host, options.port))
    dbg = Debugger(sock, options.root, just_my_code=not options.all_code)
    dbg.send({"type": "hello", "token": options.token})
    reader = threading.Thread(target=dbg.read_loop, daemon=True, name="pymagic-debugger")
    reader.start()
    dbg.reader_ident = reader.ident
    dbg.go.wait()

    os.chdir(options.root)
    if options.tests:
        # the test runner's messages (collected, testStarted, testResult) share this helper's connection
        class TestChannel(types.ModuleType):
            go = threading.Event()

            def send(self, message):
                dbg.send(message)
        channel = TestChannel("pymagic_test_channel")
        channel.go.set()
        sys.modules["pymagic_test_channel"] = channel
        code = dbg.run_script(os.path.abspath(options.tests), ["--port", "0", "--token", "-", "--root", options.root] + options.args)
    elif options.module:
        # python -m: the module's file (a package's __main__), run as __main__ of its package, with the folder the run starts in first on sys.path
        import importlib.util
        sys.path.insert(0, os.getcwd())
        spec = importlib.util.find_spec(options.module)
        package = options.module
        if spec is not None and spec.submodule_search_locations is not None:
            spec = importlib.util.find_spec(options.module + ".__main__")
        else:
            package = options.module.rpartition(".")[0]
        if spec is None or not spec.origin or not os.path.isfile(spec.origin):
            print("No module named %s" % options.module, file=sys.stderr)
            sys.exit(1)
        code = dbg.run_script(spec.origin, options.args, package=package or None, add_folder=False)
    else:
        code = dbg.run_script(os.path.abspath(options.script), options.args)
    sys.stdout.flush()
    sys.stderr.flush()
    sys.exit(code)


if __name__ == "__main__":
    main()

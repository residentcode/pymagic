"""PyMagic's notebook kernel: runs the code cells of a notebook one after the other in one namespace.

    python pm_kernel.py --port PORT --token TOKEN --root PROJECT

It connects to the host on a local socket and talks in lines of JSON.

From the host:  {"type": "execute", "id": ..., "code": "...", "debug": {"host", "port", "token", "file"}}   {"type": "interrupt"}   {"type": "shutdown"}
                {"type": "inspect", "id": ..., "path": [["v", "name"], ["i", 3], ["a", "attr"]]}      the members of a value of the Variables window
To the host:    {"type": "ready", "python": "3.13.1"}
                {"type": "stream", "id", "name": "stdout" | "stderr", "text"}
                {"type": "result", "id", "data": {mime: text}, "execution_count"}      the value of a last expression
                {"type": "display", "id", "data": {mime: text}}                        display(x), figures
                {"type": "error", "id", "ename", "evalue", "traceback": [lines]}
                {"type": "variables", "id", "vars": [{"name", "type", "value", "size", "expandable"}]}   the user's names, after each cell
                {"type": "children", "id", "path", "children": [{"name", "key", "type", "value", "size", "expandable"}]}   the answer to inspect
                {"type": "done", "id", "status": "ok" | "error", "execution_count"}

Cells may end with an expression (its value is shown), use `display(x)`, `!shell command` and `%pip install x`; a few other
IPython magics are accepted and ignored (`%matplotlib inline`). Matplotlib figures are sent as PNGs after the cell.
"""
import argparse
import ast
import base64
import builtins
import ctypes
import io
import json
import linecache
import os
import queue
import socket
import subprocess
import sys
import threading
import traceback
import types

_send_lock = threading.Lock()
_sock_file = None
_current_id = None
_running = False


def send(message):
    with _send_lock:
        _sock_file.write((json.dumps(message) + "\n").encode("utf-8"))
        _sock_file.flush()


class Stream(io.TextIOBase):
    """Stands in for sys.stdout / sys.stderr: what is written goes to the host as a stream message."""

    def __init__(self, name):
        self.name = name

    def writable(self):
        return True

    def write(self, text):
        if text and _current_id is not None:
            send({"type": "stream", "id": _current_id, "name": self.name, "text": text})
        return len(text)

    def flush(self):
        pass

    @property
    def encoding(self):
        return "utf-8"

    def isatty(self):
        return False


# ------------------------------------------------------------ what a value looks like

_REPRS = [
    ("_repr_png_", "image/png"),
    ("_repr_jpeg_", "image/jpeg"),
    ("_repr_svg_", "image/svg+xml"),
    ("_repr_html_", "text/html"),
    ("_repr_markdown_", "text/markdown"),
    ("_repr_latex_", "text/latex"),
    ("_repr_json_", "application/json"),
]


def bundle(value):
    """The representations of a value by MIME type; text/plain is always there."""
    data = {}
    for method, mime in _REPRS:
        fn = getattr(value, method, None)
        if fn is None or isinstance(value, type):
            continue
        try:
            got = fn()
        except Exception:
            continue
        if isinstance(got, tuple):
            got = got[0]
        if got is None:
            continue
        if isinstance(got, bytes):
            got = base64.b64encode(got).decode("ascii") if mime.startswith("image/") and mime != "image/svg+xml" else got.decode("utf-8", "replace")
        data[mime] = got
    try:
        data["text/plain"] = repr(value)
    except Exception:
        data["text/plain"] = "<%s object>" % type(value).__name__
    return data


def display(*values):
    for value in values:
        send({"type": "display", "id": _current_id, "data": bundle(value)})


def send_figures():
    plt = sys.modules.get("matplotlib.pyplot")
    if plt is None:
        return
    try:
        for number in plt.get_fignums():
            figure = plt.figure(number)
            buffer = io.BytesIO()
            figure.savefig(buffer, format="png", bbox_inches="tight")
            send({"type": "display", "id": _current_id, "data": {
                "image/png": base64.b64encode(buffer.getvalue()).decode("ascii"), "text/plain": "<Figure>"}})
        plt.close("all")
    except Exception:
        pass


# ------------------------------------------------------------ the few magics

def run_shell(command):
    process = subprocess.Popen(command, shell=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
    for line in process.stdout:
        sys.stdout.write(line)
    process.wait()


def rewrite_magics(code):
    """`!cmd` and `%pip ...` become Python calls; line magics that only configure display are dropped."""
    out = []
    for line in code.split("\n"):
        stripped = line.lstrip()
        indent = line[: len(line) - len(stripped)]
        if stripped.startswith("!"):
            out.append(indent + "__pm_shell__(%r)" % stripped[1:])
        elif stripped.startswith("%pip ") or stripped.startswith("%conda "):
            out.append(indent + "__pm_shell__(%r)" % (sys.executable + " -m " + stripped[1:]))
        elif stripped.startswith("%matplotlib") or stripped.startswith("%load_ext") or stripped.startswith("%autoreload"):
            out.append(indent + "pass")
        else:
            out.append(line)
    return "\n".join(out)


# ------------------------------------------------------------ running a cell

namespace = {"__name__": "__main__", "__builtins__": builtins, "display": display}
execution_count = 0
_root = os.getcwd()


def start_debugger(debug):
    """Debug Cell: connects to the IDE's debug server (the same pm_debug the programs are debugged with) and traces this thread."""
    if debug.get("helper") and debug["helper"] not in sys.path:
        sys.path.insert(0, debug["helper"])
    import pm_debug
    os.chdir(_root)               # the breakpoints are files of the project: they are looked up from its folder
    return pm_debug.connect(debug.get("host", "127.0.0.1"), int(debug["port"]), debug.get("token", ""), _root)


def run_cell(code, debug=None):
    """Runs the code; returns "ok" or "error". An expression at the end is evaluated and shown. With `debug` the cell runs under the debugger."""
    global execution_count
    execution_count += 1
    filename = "<cell-%d>" % execution_count
    if debug:
        # a file name of the project that stands for the cell: the IDE's breakpoints of the cell are set on it, and a stop in it is shown in the cell
        filename = os.path.normpath(os.path.join(_root, debug["file"]))
    source = rewrite_magics(code)
    linecache.cache[filename] = (len(source), None, source.splitlines(True), filename)
    if debug:
        # bdb looks a breakpoint's line up under the file name in its canonical form (lower case on Windows)
        canonical = os.path.normcase(os.path.abspath(filename))
        linecache.cache[canonical] = (len(source), None, source.splitlines(True), canonical)
    dbg = None
    before = os.getcwd()
    try:
        tree = ast.parse(source, filename)
        last = None
        if tree.body and isinstance(tree.body[-1], ast.Expr):
            last = ast.Expression(tree.body.pop().value)
        body = compile(tree, filename, "exec")
        final = compile(last, filename, "eval") if last is not None else None
        if debug:
            dbg = start_debugger(debug)
        exec(body, namespace)
        if final is not None:
            value = eval(final, namespace)
            if value is not None:
                namespace["_"] = value
                send({"type": "result", "id": _current_id, "data": bundle(value), "execution_count": execution_count})
        return "ok"
    except BaseException as error:  # noqa: BLE001 - everything a cell raises is shown, not fatal
        report(error, filename)
        return "error"
    finally:
        if dbg is not None:
            try:
                dbg.detach()          # the program goes on without the debugger
                dbg.sock.shutdown(socket.SHUT_RDWR)   # (its reader thread still holds the socket: this is what lets the IDE see the connection end)
            except BaseException:  # noqa: BLE001
                pass
            try:
                os.chdir(before)
            except OSError:
                pass
        send_figures()


def report(error, filename):
    frames = traceback.extract_tb(error.__traceback__)
    # leave out the kernel's own frames: they start at this file
    mine = os.path.abspath(__file__)
    shown = [f for f in frames if os.path.abspath(f.filename) != mine]
    lines = []
    if shown:
        lines.append("Traceback (most recent call last):\n")
        lines.extend(traceback.format_list(shown))
    lines.extend(traceback.format_exception_only(type(error), error))
    text = "".join(lines).rstrip("\n").split("\n")
    send({"type": "error", "id": _current_id, "ename": type(error).__name__, "evalue": str(error), "traceback": text})


_HIDDEN = {"display", "__pm_shell__", "__builtins__", "__name__"}


def short_repr(value, limit=120):
    try:
        text = repr(value)
    except Exception:
        text = "<%s object>" % type(value).__name__
    text = " ".join(text.splitlines())
    return text if len(text) <= limit else text[: limit - 3] + "..."


def size_of(value):
    shape = getattr(value, "shape", None)
    if isinstance(shape, tuple):
        return "x".join(str(n) for n in shape)
    if isinstance(value, (str, bytes, list, tuple, dict, set, frozenset, range)):
        return str(len(value))
    return ""


def variables():
    """The names a cell left in the namespace (not the private ones and not the kernel's own), for the Variables window."""
    out = []
    for name, value in list(namespace.items()):
        if name in _HIDDEN or name.startswith("_"):
            continue
        out.append({"name": name, "type": type(value).__name__, "value": short_repr(value), "size": size_of(value), "expandable": can_expand(value)})
        if len(out) >= 300:
            break
    out.sort(key=lambda v: v["name"])
    return out


# ------------------------------------------------------------ the members of a value (the Variables window opens objects lazily)

MAX_CHILDREN = 100
_LEAF = (str, bytes, bytearray, int, float, complex, bool, type(None), type, types.ModuleType, types.FunctionType,
         types.BuiltinFunctionType, types.MethodType)


def _frame_like(value):
    """A table with named columns (a pandas DataFrame, found by what it has, not by importing pandas)."""
    try:
        return getattr(value, "columns", None) is not None and hasattr(value, "dtypes") and hasattr(value, "shape") and not callable(value.columns)
    except Exception:
        return False


def _array_like(value):
    try:
        shape = getattr(value, "shape", None)
        return isinstance(shape, tuple) and len(shape) >= 1 and hasattr(value, "dtype") and not _frame_like(value)
    except Exception:
        return False


def _slot_names(value):
    names = []
    for klass in type(value).__mro__:
        slots = klass.__dict__.get("__slots__", ())
        names.extend([slots] if isinstance(slots, str) else list(slots))
    return [n for n in names if isinstance(n, str) and not n.startswith("__") and hasattr(value, n)]


def _attribute_names(value):
    own = getattr(value, "__dict__", None)
    names = [k for k in (list(own) if isinstance(own, dict) else []) if isinstance(k, str) and not (k.startswith("__") and k.endswith("__"))]
    return names + [n for n in _slot_names(value) if n not in names]


def can_expand(value):
    """Whether a value has members to show (cheap: it is asked for every variable)."""
    if isinstance(value, _LEAF):
        return False
    try:
        if isinstance(value, (dict, list, tuple, set, frozenset)):
            return len(value) > 0
        if _frame_like(value):
            return len(value.columns) > 0
        if _array_like(value):
            return value.shape[0] > 0
        return len(_attribute_names(value)) > 0
    except Exception:
        return False


def child_items(value):
    """The members of a value as (label, key, child): `key` is how to reach the child again from the value."""
    if isinstance(value, _LEAF):
        return []
    if isinstance(value, dict):
        return [(short_repr(k, 60), ["i", i], v) for i, (k, v) in enumerate(list(value.items())[:MAX_CHILDREN])]
    if isinstance(value, (list, tuple)):
        return [(str(i), ["i", i], v) for i, v in enumerate(value[:MAX_CHILDREN])]
    if isinstance(value, (set, frozenset)):
        return [(str(i), ["i", i], v) for i, v in enumerate(list(value)[:MAX_CHILDREN])]
    if _frame_like(value):
        columns = list(value.columns)[:MAX_CHILDREN]
        return [(str(c), ["c", i], value[c]) for i, c in enumerate(columns)]
    if _array_like(value):
        rows = value.iloc if hasattr(value, "iloc") else value
        return [(str(i), ["n", i], rows[i]) for i in range(min(int(value.shape[0]), MAX_CHILDREN))]
    return [(name, ["a", name], getattr(value, name)) for name in _attribute_names(value)[:MAX_CHILDREN]]


def follow(value, step):
    """The child of `value` that a key of child_items names."""
    how, which = step[0], step[1]
    if how == "i":
        if isinstance(value, dict):
            return list(value.items())[which][1]
        if isinstance(value, (set, frozenset)):
            return list(value)[which]
        return value[which]
    if how == "c":
        return value[list(value.columns)[which]]
    if how == "n":
        return (value.iloc if hasattr(value, "iloc") else value)[which]
    if how == "a":
        return getattr(value, which)
    raise KeyError(how)


def describe(label, key, value):
    return {"name": label, "key": key, "type": type(value).__name__, "value": short_repr(value), "size": size_of(value), "expandable": can_expand(value)}


def inspect_path(path):
    """The members of the value that `path` leads to: the first step names a variable, the others are keys of child_items."""
    first = path[0]
    if first[0] != "v" or first[1] not in namespace:
        raise KeyError("the name is gone")
    value = namespace[first[1]]
    for step in path[1:]:
        value = follow(value, step)
    return [describe(label, key, child) for label, key, child in child_items(value)]


def answer_inspect(message):
    out = {"type": "children", "id": message.get("id"), "path": message.get("path")}
    try:
        out["children"] = inspect_path(message.get("path") or [])
    except BaseException as error:  # noqa: BLE001 - a value that cannot be looked into is reported, not fatal
        out["children"] = []
        out["error"] = "%s: %s" % (type(error).__name__, error)
    send(out)


def no_input(*_args, **_kwargs):
    raise RuntimeError("input() is not available in notebook cells")


# ------------------------------------------------------------ the loop

jobs = queue.Queue()
main_thread_id = threading.get_ident()


def reader(stream):
    try:
        for raw in stream:
            try:
                message = json.loads(raw.decode("utf-8"))
            except ValueError:
                continue
            kind = message.get("type")
            if kind == "interrupt" and _running:
                # raise KeyboardInterrupt in the thread that runs the cell (only while one runs: an idle kernel has nothing to stop)
                ctypes.pythonapi.PyThreadState_SetAsyncExc(ctypes.c_ulong(main_thread_id), ctypes.py_object(KeyboardInterrupt))
            elif kind == "shutdown":
                return
            elif kind == "execute":
                jobs.put(message)
            elif kind == "inspect":
                answer_inspect(message)         # answered here, so a value can be opened while a cell runs
    except OSError:
        pass                      # the host went away without closing the socket properly (it was killed)
    finally:
        # without the host there is nothing to run for: stop (also stops a cell that is still running)
        jobs.put(None)
        if _running:
            ctypes.pythonapi.PyThreadState_SetAsyncExc(ctypes.c_ulong(main_thread_id), ctypes.py_object(SystemExit))


def main():
    global _sock_file, _current_id, _running
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--host", default="127.0.0.1", help="where the host listens (WSL and Docker reach it by another address)")
    parser.add_argument("--token", required=True)
    parser.add_argument("--root", default=os.getcwd())
    args = parser.parse_args()

    connection = socket.create_connection((args.host, args.port), timeout=15)
    connection.settimeout(None)
    _sock_file = connection.makefile("rwb", buffering=0)
    _sock_file.write((json.dumps({"token": args.token}) + "\n").encode("utf-8"))

    global _root
    _root = os.path.abspath(args.root)
    os.chdir(args.root)
    if args.root not in sys.path:
        sys.path.insert(0, args.root)
    os.environ.setdefault("MPLBACKEND", "Agg")
    sys.stdout, sys.stderr = Stream("stdout"), Stream("stderr")
    builtins.input = no_input
    namespace["__pm_shell__"] = run_shell

    threading.Thread(target=reader, args=(_sock_file,), daemon=True).start()
    send({"type": "ready", "python": sys.version.split()[0]})

    while True:
        try:
            job = jobs.get()
        except KeyboardInterrupt:
            continue                       # an interrupt that arrived just as the cell ended
        if job is None:
            break
        _current_id = job.get("id")
        status = "ok"
        _running = True
        try:
            status = run_cell(job.get("code", ""), job.get("debug"))
        except KeyboardInterrupt:
            # an interrupt that arrived between two statements of the cell
            send({"type": "error", "id": _current_id, "ename": "KeyboardInterrupt", "evalue": "", "traceback": ["KeyboardInterrupt"]})
            status = "error"
        finally:
            _running = False
        try:
            send({"type": "variables", "id": _current_id, "vars": variables()})
        except Exception:
            pass
        send({"type": "done", "id": _current_id, "status": status, "execution_count": execution_count})
        _current_id = None


if __name__ == "__main__":
    main()

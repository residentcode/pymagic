"""PyMagic's notebook kernel: runs the code cells of a notebook one after the other in one namespace.

    python pm_kernel.py --port PORT --token TOKEN --root PROJECT

It connects to the host on a local socket and talks in lines of JSON.

From the host:  {"type": "execute", "id": ..., "code": "..."}   {"type": "interrupt"}   {"type": "shutdown"}
To the host:    {"type": "ready", "python": "3.13.1"}
                {"type": "stream", "id", "name": "stdout" | "stderr", "text"}
                {"type": "result", "id", "data": {mime: text}, "execution_count"}      the value of a last expression
                {"type": "display", "id", "data": {mime: text}}                        display(x), figures
                {"type": "error", "id", "ename", "evalue", "traceback": [lines]}
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


def run_cell(code):
    """Runs the code; returns "ok" or "error". An expression at the end is evaluated and shown."""
    global execution_count
    execution_count += 1
    filename = "<cell-%d>" % execution_count
    source = rewrite_magics(code)
    linecache.cache[filename] = (len(source), None, source.splitlines(True), filename)
    try:
        tree = ast.parse(source, filename)
        last = None
        if tree.body and isinstance(tree.body[-1], ast.Expr):
            last = ast.Expression(tree.body.pop().value)
        body = compile(tree, filename, "exec")
        final = compile(last, filename, "eval") if last is not None else None
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
            status = run_cell(job.get("code", ""))
        except KeyboardInterrupt:
            # an interrupt that arrived between two statements of the cell
            send({"type": "error", "id": _current_id, "ename": "KeyboardInterrupt", "evalue": "", "traceback": ["KeyboardInterrupt"]})
            status = "error"
        finally:
            _running = False
        send({"type": "done", "id": _current_id, "status": status, "execution_count": execution_count})
        _current_id = None


if __name__ == "__main__":
    main()

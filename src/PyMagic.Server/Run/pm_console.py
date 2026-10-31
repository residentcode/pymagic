"""PyMagic's Python console: an interpreter that keeps its names between commands.

The host sends JSON lines over a local TCP socket, as to the debugger:
  consoleExec     {id, code}       run it; an expression's value is printed as the prompt does. Answer: consoleDone {id, more}
                                   (more: the code is not complete yet, as after "def f():")
  consoleRunFile  {id, path}       run a file in the console's names (Run File in Python Console). Answer: consoleDone {id}
  consoleVars     {id}             the console's names. Answer: consoleVars {id, vars: [{name, type, value}]}
  consoleComplete {id, text}       the completions of the last word. Answer: consoleCompletions {id, items}
What the code prints goes to this program's output, which the host shows.
"""
import argparse
import builtins
import codeop
import json
import os
import queue
import socket
import sys
import threading
import traceback


class Console:
    def __init__(self, sock):
        self.sock = sock
        self.lock = threading.Lock()
        self.commands = queue.Queue()
        self.go = threading.Event()
        self.names = {"__name__": "__main__", "__builtins__": builtins, "__doc__": None}
        self.compiler = codeop.CommandCompiler()

    def send(self, message):
        data = (json.dumps(message) + "\n").encode("utf-8")
        with self.lock:
            try:
                self.sock.sendall(data)
            except OSError:
                os._exit(1)

    def read_loop(self):
        for line in self.sock.makefile("r", encoding="utf-8", newline="\n"):
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            kind = msg.get("type")
            if kind == "go":
                self.go.set()
            elif kind == "stop":
                os._exit(137)
            else:
                self.commands.put(msg)
        os._exit(0)

    def show_error(self):
        etype, value, tb = sys.exc_info()
        tb = tb.tb_next if tb is not None else None   # not this console's own frame
        traceback.print_exception(etype, value, tb)

    def run_code(self, source, filename="<console>"):
        """Runs what was typed; False when it is not a complete statement yet."""
        try:
            code = self.compiler(source, filename, "single")
        except (OverflowError, SyntaxError, ValueError):
            # several statements at once (a selection sent from the editor): run them as a block
            try:
                code = compile(source, filename, "exec")
            except (OverflowError, SyntaxError, ValueError):
                traceback.print_exception(*sys.exc_info()[:2], None)
                return True
        if code is None:
            # several lines: a complete block may end without the blank line the prompt wants
            if source.endswith("\n") or "\n" not in source.strip():
                return False
            try:
                code = compile(source, filename, "exec")
            except SyntaxError:
                return False
        try:
            exec(code, self.names)
        except SystemExit:
            pass
        except BaseException:
            self.show_error()
        return True

    def setup_django(self, root):
        """The settings manage.py names, its folder on sys.path, and django.setup(): the models can be used at once."""
        import re
        for folder, dirs, names in os.walk(root):
            dirs[:] = [d for d in dirs if d not in (".git", ".venv", "venv", "node_modules", "__pycache__") and not d.startswith(".")]
            if "manage.py" in names:
                with open(os.path.join(folder, "manage.py"), encoding="utf-8", errors="replace") as f:
                    found = re.search(r"DJANGO_SETTINGS_MODULE['\"]\s*,\s*['\"]([\w.]+)", f.read())
                if found:
                    os.environ.setdefault("DJANGO_SETTINGS_MODULE", found.group(1))
                if folder not in sys.path:
                    sys.path.insert(0, folder)
                break
        try:
            import django
            django.setup()
            from django.conf import settings
            print("Django %s, settings %s" % (django.get_version(), settings.SETTINGS_MODULE))
        except Exception:
            self.show_error()

    def run_file(self, path):
        try:
            with open(path, "rb") as f:
                code = compile(f.read(), path, "exec")
        except (OSError, SyntaxError):
            self.show_error()
            return
        self.names["__file__"] = path
        folder = os.path.dirname(path)
        if folder not in sys.path:
            sys.path.insert(0, folder)
        try:
            exec(code, self.names)
        except SystemExit:
            pass
        except BaseException:
            self.show_error()

    def variables(self):
        out = []
        for name, value in sorted(self.names.items()):
            if name.startswith("__") or type(value).__name__ == "module":
                continue
            try:
                text = repr(value)
            except Exception as e:
                text = "<repr failed: %s>" % e
            if len(text) > 300:
                text = text[:297] + "..."
            out.append({"name": name, "type": type(value).__name__, "value": text})
        return out

    def completions(self, text):
        import rlcompleter
        completer = rlcompleter.Completer(self.names)
        word = text
        for stop in " \t\n()[]{},:;=+-*/%<>!&|^~\"'":
            word = word.rsplit(stop, 1)[-1]
        items = []
        for i in range(200):
            try:
                found = completer.complete(word, i)
            except Exception:
                break
            if found is None:
                break
            items.append(found)
        return {"word": word, "items": items}

    def loop(self):
        while True:
            msg = self.commands.get()
            kind, rid = msg.get("type"), msg.get("id")
            if kind == "consoleExec":
                complete = self.run_code(msg.get("code", ""))
                sys.stdout.flush()
                sys.stderr.flush()
                self.send({"type": "consoleDone", "id": rid, "more": not complete})
            elif kind == "consoleRunFile":
                self.run_file(os.path.abspath(msg.get("path", "")))
                sys.stdout.flush()
                sys.stderr.flush()
                self.send({"type": "consoleDone", "id": rid, "more": False})
            elif kind == "consoleVars":
                self.send({"type": "consoleVars", "id": rid, "vars": self.variables()})
            elif kind == "consoleComplete":
                self.send({"type": "consoleCompletions", "id": rid, **self.completions(msg.get("text", ""))})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--token", required=True)
    parser.add_argument("--root", required=True)
    parser.add_argument("--startup", default=None, help="a script run first in the console's names")
    parser.add_argument("--django", action="store_true", help="set Django up first, as manage.py shell does (the Django console)")
    options = parser.parse_args()
    sock = socket.create_connection((options.host, options.port))
    console = Console(sock)
    console.send({"type": "hello", "token": options.token})
    threading.Thread(target=console.read_loop, daemon=True).start()
    console.go.wait()
    os.chdir(options.root)
    if options.root not in sys.path:
        sys.path.insert(0, options.root)
    print("Python %s on %s" % (sys.version.split()[0], sys.platform))
    if options.django:
        console.setup_django(options.root)
    if options.startup and os.path.isfile(options.startup):
        console.run_file(options.startup)
    sys.stdout.flush()
    console.send({"type": "consoleReady"})
    console.loop()


if __name__ == "__main__":
    main()

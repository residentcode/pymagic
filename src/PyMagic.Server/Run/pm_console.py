"""PyMagic's Python console: an interpreter that keeps its names between commands.

The host sends JSON lines over a local TCP socket, as to the debugger:
  consoleExec     {id, code}       run it; an expression's value is printed as the prompt does. Answer: consoleDone {id, more}
                                   (more: the code is not complete yet, as after "def f():")
  consoleRunFile  {id, path}       run a file in the console's names (Run File in Python Console). Answer: consoleDone {id}
  consoleVars     {id}             the console's names. Answer: consoleVars {id, vars: [{name, type, value}]}
  consoleComplete {id, text}       the completions of the last word. Answer: consoleCompletions {id, items}
  consoleAttach   {id, host, port, token, helper}   Attach Debugger: connect to the IDE's debug server (pm_debug, from the folder
                                   `helper`); the code run from then on stops at the IDE's breakpoints. Answer: consoleDone {id, error?}
                                   With via "console" (an interpreter elsewhere than the IDE's computer, which cannot reach its debug
                                   server) pm_debug comes as text (`helperSource`) and talks over this connection: what it sends goes
                                   out as debugOut {text}, what the debug server sends comes in as debugIn {text} ("" when it is gone),
                                   and debugEnd tells that the debugger let go.
  consoleDetach   {id}             the debugger lets go; the console goes on. Answer: consoleDone {id}
With --ipython the code runs in IPython's InteractiveShell (magics, Out[n]) when IPython is installed; if it is not, a note is
printed and the console is the plain one.
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


class ConsoleChannel:
    """The debugger's connection to the IDE over the console's own (a socket for pm_debug: sendall, makefile, close, shutdown), for an
    interpreter on another machine, in WSL or in a container, from which the IDE's debug server cannot be reached."""

    def __init__(self, console):
        self.console = console
        self.incoming = queue.Queue()
        self.closed = False

    def free_threads(self):
        # the console's reader carries what the IDE sends to the debugger: held at a stop, nothing more would come
        return [self.console.reader_ident] if self.console.reader_ident is not None else []

    def sendall(self, data):
        if self.closed:
            raise OSError("the channel to the IDE is closed")
        self.console.send({"type": "debugOut", "text": data.decode("utf-8")})

    def feed(self, text):
        self.incoming.put(text if text else None)

    def makefile(self, *args, **kwargs):
        return self.lines()

    def lines(self):
        while True:
            text = self.incoming.get()
            if text is None:
                return
            for line in text.splitlines():
                yield line + "\n"

    def close(self):
        if self.closed:
            return
        self.closed = True
        self.incoming.put(None)
        self.console.send({"type": "debugEnd"})

    def shutdown(self, how=None):
        self.close()


class Console:
    def __init__(self, sock):
        self.sock = sock
        self.lock = threading.Lock()
        self.commands = queue.Queue()
        self.go = threading.Event()
        self.names = {"__name__": "__main__", "__builtins__": builtins, "__doc__": None}
        self.compiler = codeop.CommandCompiler()
        self.shell = None        # IPython's InteractiveShell when --ipython found IPython
        self.debugger = None     # pm_debug's Debugger while the IDE's debugger is attached (Attach Debugger)

    def use_ipython(self):
        """Makes this console IPython's shell (magics, Out[n], its completion); False, with a note, when IPython is not installed."""
        try:
            import IPython
            from IPython.core.interactiveshell import InteractiveShell
            from traitlets.config import Config
        except ImportError:
            print("IPython is not installed in this Python (pip install ipython): this is the plain console")
            return None
        config = Config()
        config.InteractiveShell.colors = "NoColor"      # the output is shown as plain text
        config.InteractiveShell.autoindent = False
        self.shell = InteractiveShell.instance(config=config)
        self.names = self.shell.user_ns                 # the names, the variables list and run-file all go through the shell's namespace
        print("IPython %s" % IPython.__version__)
        return IPython.__version__

    def send(self, message):
        data = (json.dumps(message) + "\n").encode("utf-8")
        with self.lock:
            try:
                self.sock.sendall(data)
            except OSError:
                os._exit(1)

    reader_ident = None   # the thread that reads the host's messages: the debugger neither traces nor holds it (it carries the debugger's input)
    channel = None        # the debugger's way to the IDE over this connection (ConsoleChannel), while attached that way

    def read_loop(self):
        self.reader_ident = threading.get_ident()
        for line in self.sock.makefile("r", encoding="utf-8", newline="\n"):
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            kind = msg.get("type")
            if kind == "debugIn":
                # here, not in the queue of commands: the command that runs may be the one stopped at a breakpoint
                channel = self.channel
                if channel is not None:
                    channel.feed(msg.get("text") or "")
            elif kind == "go":
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
        if self.shell is not None:
            status = self.shell.input_transformer_manager.check_complete(source)[0]
            if status == "incomplete" and not source.endswith("\n"):
                return False        # a block goes on until an empty line, as at IPython's prompt
            self.shell.run_cell(source, store_history=True)
            return True
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

    def attach(self, msg):
        """Connects this thread (the one that runs the commands) to the IDE's debug server; an earlier connection is let go first."""
        self.detach()
        helper = msg.get("helper")
        if helper and helper not in sys.path:
            sys.path.append(helper)
        source = msg.get("helperSource")
        if source and "pm_debug" not in sys.modules:
            # the IDE is on another computer: its copy of pm_debug is not on this one, so it came with the request
            import types
            module = types.ModuleType("pm_debug")
            module.__file__ = "pm_debug.py"
            sys.modules["pm_debug"] = module
            exec(compile(source, "pm_debug.py", "exec"), module.__dict__)
        import pm_debug
        sock = None
        if msg.get("via") == "console":
            sock = self.channel = ConsoleChannel(self)
        self.debugger = pm_debug.connect(msg.get("host", "127.0.0.1"), int(msg["port"]), msg.get("token", ""), os.getcwd(), sock=sock)
        print("The debugger is attached: the breakpoints of the project stop the code run here")

    def detach(self):
        dbg, self.debugger = self.debugger, None
        self.channel = None
        if dbg is None:
            return
        try:
            dbg.detach()
            dbg.sock.shutdown(socket.SHUT_RDWR)   # its reader thread holds the socket: this lets the IDE see the connection end
        except BaseException:  # noqa: BLE001 - the IDE may have gone already
            pass
        print("The debugger is detached")

    def variables(self):
        out = []
        hidden = set(getattr(self.shell, "user_ns_hidden", ()) or ()) | {"In", "Out", "get_ipython", "exit", "quit"} if self.shell is not None else set()
        for name, value in sorted(self.names.items()):
            if name.startswith("__") or type(value).__name__ == "module":
                continue
            if self.shell is not None and (name in hidden or name.startswith("_")):
                continue            # IPython's own names (_, _i, _ih, Out, In...)
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
        if self.shell is not None:
            try:
                # IPython completes magics, paths and attributes of what it knows; its answers are whole words
                _, matches = self.shell.complete(word, text, len(text))
                return {"word": word, "items": [m for m in matches if isinstance(m, str)][:200]}
            except Exception:
                pass
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
            elif kind == "consoleAttach":
                error = None
                try:
                    self.attach(msg)
                except Exception as e:  # noqa: BLE001 - the console goes on without the debugger
                    self.debugger = None
                    error = "The debugger could not attach: %s" % e
                sys.stdout.flush()
                self.send({"type": "consoleDone", "id": rid, "more": False, **({"error": error} if error else {})})
            elif kind == "consoleDetach":
                self.detach()
                sys.stdout.flush()
                self.send({"type": "consoleDone", "id": rid, "more": False})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--token", required=True)
    parser.add_argument("--root", required=True)
    parser.add_argument("--startup", default=None, help="a script run first in the console's names")
    parser.add_argument("--django", action="store_true", help="set Django up first, as manage.py shell does (the Django console)")
    parser.add_argument("--ipython", action="store_true", help="IPython's shell, when IPython is installed")
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
    ipython = console.use_ipython() if options.ipython else None
    if options.django:
        console.setup_django(options.root)
    if options.startup and os.path.isfile(options.startup):
        console.run_file(options.startup)
    sys.stdout.flush()
    console.send({"type": "consoleReady", "ipython": ipython})
    console.loop()


if __name__ == "__main__":
    main()

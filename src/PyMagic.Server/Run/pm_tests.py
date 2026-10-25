"""PyMagic test runner.

Runs pytest when it is installed (it also runs unittest tests), otherwise the standard unittest runner, and reports to the
host over a local TCP socket as JSON lines:

  collected     {tests: [{id, file, line}]}           what is about to run (pytest also lists parametrized cases)
  testStarted   {id}
  testResult    {id, outcome, duration, message, traceback, output, file, line}
  collectError  {file, message}                        import/collection failures

Test ids look like  tests/test_a.py::TestX::test_y  (paths relative to the project root, with forward slashes).
outcome is one of: passed, failed, error, skipped, xfailed, xpassed.
"""
import argparse
import json
import os
import socket
import sys
import threading
import time
import traceback


class Channel:
    def __init__(self, port, token, host="127.0.0.1"):
        self.sock = socket.create_connection((host, port))
        self.lock = threading.Lock()
        self.go = threading.Event()
        self.send({"type": "hello", "token": token})
        threading.Thread(target=self._read, daemon=True).start()

    def send(self, message):
        data = (json.dumps(message) + "\n").encode("utf-8")
        with self.lock:
            try:
                self.sock.sendall(data)
            except OSError:
                os._exit(1)

    def _read(self):
        for line in self.sock.makefile("r", encoding="utf-8", newline="\n"):
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            if msg.get("type") == "go":
                self.go.set()
            elif msg.get("type") == "stop":
                os._exit(137)
        os._exit(0)


def rel_path(root, path):
    try:
        full = os.path.abspath(path)
        if os.path.normcase(full).startswith(os.path.normcase(root) + os.sep):
            return os.path.relpath(full, root).replace(os.sep, "/")
    except (ValueError, TypeError):
        pass
    return None


def first_line(text):
    for line in str(text).splitlines():
        if line.strip():
            return line.strip()
    return ""


# ------------------------------------------------------------------------------------------------ pytest

def run_pytest(channel, root, ids):
    import pytest

    class Plugin:
        def __init__(self):
            self.state = {}
            self.started = set()

        def pytest_collection_modifyitems(self, session, config, items):
            tests = []
            for item in items:
                path, line, _ = item.location
                tests.append({"id": item.nodeid, "file": rel_path(root, os.path.join(str(config.rootpath), path)) or path,
                              "line": (line or 0) + 1})
            channel.send({"type": "collected", "tests": tests})

        def pytest_collectreport(self, report):
            if report.failed:
                channel.send({"type": "collectError", "file": report.nodeid, "message": report.longreprtext})

        def pytest_runtest_logstart(self, nodeid, location):
            channel.send({"type": "testStarted", "id": nodeid})

        def pytest_runtest_logreport(self, report):
            st = self.state.setdefault(report.nodeid, {
                "outcome": "passed", "duration": 0.0, "message": "", "traceback": "", "output": [], "file": None, "line": None})
            st["duration"] += getattr(report, "duration", 0.0) or 0.0
            for title, content in getattr(report, "sections", []):
                if content and ("Captured" in title):
                    st["output"].append(content)

            wasxfail = hasattr(report, "wasxfail")
            if report.when == "call":
                if wasxfail:
                    st["outcome"] = "xfailed" if report.skipped else "xpassed"
                    st["message"] = getattr(report, "wasxfail", "") or ""
                elif report.failed:
                    st["outcome"] = "failed"
                elif report.skipped:
                    st["outcome"] = "skipped"
            elif report.when == "setup" and report.skipped:
                st["outcome"] = "xfailed" if wasxfail else "skipped"
            elif report.failed:
                if st["outcome"] in ("passed", "skipped"):
                    st["outcome"] = "error"   # a failing fixture/setup/teardown

            if report.failed or (report.skipped and not wasxfail):
                self.describe_failure(report, st)

            if report.when == "teardown":
                st = self.state.pop(report.nodeid)
                channel.send({"type": "testResult", "id": report.nodeid, "outcome": st["outcome"],
                              "duration": st["duration"], "message": st["message"], "traceback": st["traceback"],
                              "output": "\n".join(st["output"]), "file": st["file"], "line": st["line"]})

        def describe_failure(self, report, st):
            longrepr = report.longrepr
            if report.skipped and isinstance(longrepr, tuple):
                st["message"] = str(longrepr[2]).replace("Skipped: ", "")
                return
            st["traceback"] = report.longreprtext
            crash = getattr(longrepr, "reprcrash", None)
            if crash is not None:
                st["message"] = first_line(crash.message)
                st["file"] = rel_path(root, crash.path)
                st["line"] = crash.lineno
            else:
                st["message"] = first_line(st["traceback"].splitlines()[-1] if st["traceback"] else "")

    args = ["-p", "no:cacheprovider", "-q", "--tb=short", "--rootdir", root, "-o", "addopts="] + (ids or [root])
    return int(pytest.main(args, plugins=[Plugin()]))


# ------------------------------------------------------------------------------------------------ unittest

def run_unittest(channel, root, ids):
    import unittest

    def module_name(file_part):
        name = file_part[:-3] if file_part.endswith(".py") else file_part
        name = name.replace("/", ".").replace("\\", ".")
        return name[:-9] if name.endswith(".__init__") else name

    def test_id(test):
        cls = type(test)
        module = sys.modules.get(cls.__module__)
        file_part = rel_path(root, getattr(module, "__file__", "") or "") or cls.__module__.replace(".", "/") + ".py"
        method = getattr(test, "_testMethodName", None)
        return "%s::%s::%s" % (file_part, cls.__name__, method) if method else "%s::%s" % (file_part, cls.__name__)

    loader = unittest.TestLoader()
    if not ids:
        # unittest's own discovery only enters packages; find test files the way the IDE's test tree does
        skipped = {".git", "__pycache__", "node_modules", ".venv", "venv", "env", ".tox", "site-packages", "build", "dist"}
        found = []
        for folder, dirs, files in os.walk(root):
            dirs[:] = sorted(d for d in dirs if d not in skipped and not d.startswith("."))
            for name in sorted(files):
                stem = name[:-3]
                if name.endswith(".py") and (stem.lower().startswith("test") or stem.endswith("_test")):
                    found.append(rel_path(root, os.path.join(folder, name)))
        ids = [f for f in found if f]
    suite = unittest.TestSuite()
    for item in ids:
        parts = item.split("::")
        dotted = ".".join([module_name(parts[0])] + parts[1:])
        suite.addTests(loader.loadTestsFromName(dotted))

    flat = []

    def flatten(s):
        for t in s:
            if isinstance(t, unittest.TestSuite):
                flatten(t)
            else:
                flat.append(t)
    flatten(suite)

    failed_loads = [t for t in flat if type(t).__name__ in ("_FailedTest", "ModuleImportFailure", "LoadTestsFailure")]
    real = [t for t in flat if t not in failed_loads]
    for t in failed_loads:
        message = ""
        try:
            getattr(t, t._testMethodName)()
        except BaseException:
            message = traceback.format_exc()
        channel.send({"type": "collectError", "file": getattr(t, "_testMethodName", "?"), "message": message})
    channel.send({"type": "collected", "tests": [{"id": test_id(t), "file": test_id(t).split("::")[0], "line": 0} for t in real]})

    class Result(unittest.TestResult):
        def __init__(self):
            super().__init__()
            self.buffer = True
            self.began = {}
            self.pending = {}

        def startTest(self, test):
            super().startTest(test)
            self.began[test] = time.perf_counter()
            channel.send({"type": "testStarted", "id": test_id(test)})

        def finish(self, test, outcome, message="", tb="", exc_info=None):
            self.pending[test] = (outcome, message, tb, exc_info)

        def location(self, exc_info):
            file, line = None, None
            if exc_info:
                for frame in traceback.extract_tb(exc_info[2]):
                    rel = rel_path(root, frame.filename)
                    if rel and "unittest" not in frame.filename.replace("\\", "/").split("/"):
                        file, line = rel, frame.lineno
            return file, line

        def addSuccess(self, test):
            super().addSuccess(test)
            self.finish(test, "passed")

        def addFailure(self, test, err):
            super().addFailure(test, err)
            self.finish(test, "failed", "%s: %s" % (err[0].__name__, first_line(err[1])), self._exc_info_to_string(err, test), err)

        def addError(self, test, err):
            super().addError(test, err)
            self.finish(test, "error", "%s: %s" % (err[0].__name__, first_line(err[1])), self._exc_info_to_string(err, test), err)

        def addSkip(self, test, reason):
            super().addSkip(test, reason)
            self.finish(test, "skipped", reason)

        def addExpectedFailure(self, test, err):
            super().addExpectedFailure(test, err)
            self.finish(test, "xfailed", "expected failure")

        def addUnexpectedSuccess(self, test):
            super().addUnexpectedSuccess(test)
            self.finish(test, "xpassed", "unexpected success")

        def addSubTest(self, test, subtest, err):
            super().addSubTest(test, subtest, err)
            if err is not None:
                self.finish(test, "failed", "%s: %s" % (err[0].__name__, first_line(err[1])), self._exc_info_to_string(err, test), err)

        def stopTest(self, test):
            outcome, message, tb, exc_info = self.pending.pop(test, ("passed", "", "", None))
            file, line = self.location(exc_info)
            channel.send({"type": "testResult", "id": test_id(test), "outcome": outcome,
                          "duration": time.perf_counter() - self.began.pop(test, time.perf_counter()),
                          "message": message, "traceback": tb, "output": "", "file": file, "line": line})
            super().stopTest(test)

    result = Result()
    unittest.TestSuite(real).run(result)
    print("Ran %d tests" % result.testsRun)
    sys.stdout.flush()
    return 0 if result.wasSuccessful() and not failed_loads else 1


# ---------------------------------------------------------------- line coverage (no dependency: sys.monitoring, else sys.settrace)

SKIP_DIRS = {".git", ".hg", ".venv", "venv", "env", ".env", "node_modules", "__pycache__", "site-packages", ".tox", ".mypy_cache", ".pytest_cache", "build", "dist", ".pymagic"}


class Coverage:
    """Records the lines of the project's own files that run, and knows which lines could run."""

    def __init__(self, root):
        self.root = os.path.normcase(os.path.abspath(root))
        self.executed = {}
        self._tracked = {}
        self._monitoring = hasattr(sys, "monitoring") and hasattr(sys.monitoring, "COVERAGE_ID") and not os.environ.get("PYMAGIC_COVERAGE_TRACE")

    def _wanted(self, filename):
        wanted = self._tracked.get(filename)
        if wanted is None:
            full = os.path.normcase(os.path.abspath(filename)) if filename and not filename.startswith("<") else ""
            wanted = bool(full) and full.startswith(self.root + os.sep) and filename.endswith(".py")                 and not any(part in SKIP_DIRS for part in full[len(self.root) + 1:].split(os.sep)[:-1])
            self._tracked[filename] = wanted
        return wanted

    def start(self):
        if self._monitoring:
            mon = sys.monitoring
            tool = mon.COVERAGE_ID
            try:
                mon.use_tool_id(tool, "pymagic")
            except ValueError:
                self._monitoring = False
        if self._monitoring:
            def on_line(code, line):
                if self._wanted(code.co_filename):
                    self.executed.setdefault(code.co_filename, set()).add(line)
                else:
                    return mon.DISABLE
            mon.register_callback(tool, mon.events.LINE, on_line)
            mon.set_events(tool, mon.events.LINE)
        else:
            def local(frame, event, arg):
                if event == "line":
                    self.executed.setdefault(frame.f_code.co_filename, set()).add(frame.f_lineno)
                return local

            def global_trace(frame, event, arg):
                if self._wanted(frame.f_code.co_filename):
                    self.executed.setdefault(frame.f_code.co_filename, set()).add(frame.f_lineno)
                    return local
                return None
            sys.settrace(global_trace)
            threading.settrace(global_trace)

    def stop(self):
        if self._monitoring:
            mon = sys.monitoring
            mon.set_events(mon.COVERAGE_ID, 0)
            mon.register_callback(mon.COVERAGE_ID, mon.events.LINE, None)
            mon.free_tool_id(mon.COVERAGE_ID)
        else:
            sys.settrace(None)
            threading.settrace(None)

    @staticmethod
    def _lines_of(source, path):
        import ast
        try:
            code = compile(source, path, "exec")
            tree = ast.parse(source)
        except (SyntaxError, ValueError, MemoryError):
            return None
        lines = set()
        stack = [code]
        while stack:
            c = stack.pop()
            for _start, _end, line in c.co_lines():
                if line is not None and line > 0:
                    lines.add(line)
            stack.extend(k for k in c.co_consts if hasattr(k, "co_lines"))
        # a docstring is not a statement that can be missed
        for node in ast.walk(tree):
            if isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)) and node.body:
                first = node.body[0]
                if isinstance(first, ast.Expr) and isinstance(getattr(first, "value", None), ast.Constant) and isinstance(first.value.value, str):
                    for n in range(first.lineno, (first.end_lineno or first.lineno) + 1):
                        lines.discard(n)
        for number, text in enumerate(source.splitlines(), 1):
            if "pragma: no cover" in text:
                lines.discard(number)
        return lines

    def report(self):
        files = {}
        executed_by_norm = {os.path.normcase(os.path.abspath(k)): v for k, v in self.executed.items()}
        for folder, dirs, names in os.walk(self.root):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS and not d.startswith(".")]
            for name in names:
                if not name.endswith(".py"):
                    continue
                path = os.path.join(folder, name)
                try:
                    with open(path, "r", encoding="utf-8") as f:
                        source = f.read()
                except (OSError, UnicodeDecodeError):
                    continue
                executable = self._lines_of(source, path)
                if executable is None:
                    continue
                ran = executed_by_norm.get(os.path.normcase(os.path.abspath(path)), set()) & executable
                rel = os.path.relpath(path, self.root).replace(os.sep, "/")
                files[rel] = {"executable": sorted(executable), "executed": sorted(ran)}
        return {"files": files}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--host", default="127.0.0.1", help="where the host listens (WSL and Docker reach it by another address)")
    parser.add_argument("--token", required=True)
    parser.add_argument("--root", required=True)
    parser.add_argument("--framework", default="auto", choices=["auto", "pytest", "unittest"])
    parser.add_argument("--coverage", default=None, help="write the lines that ran to this JSON file")
    parser.add_argument("ids", nargs="*")
    options = parser.parse_args()

    channel = Channel(options.port, options.token, options.host)
    channel.go.wait()
    root = os.path.abspath(options.root)
    os.chdir(root)
    if root not in sys.path:
        sys.path.insert(0, root)

    framework = options.framework
    if framework == "auto":
        try:
            import pytest  # noqa: F401
            framework = "pytest"
        except ImportError:
            framework = "unittest"
            print("pytest is not installed: running unittest tests only", file=sys.stderr)

    coverage = None
    if options.coverage:
        coverage = Coverage(root)
        coverage.start()
    try:
        code = run_pytest(channel, root, options.ids) if framework == "pytest" else run_unittest(channel, root, options.ids)
    except ImportError as e:
        print("cannot run tests with %s: %s" % (framework, e), file=sys.stderr)
        code = 2
    if coverage is not None:
        coverage.stop()
        try:
            os.makedirs(os.path.dirname(os.path.abspath(options.coverage)), exist_ok=True)
            with open(options.coverage, "w", encoding="utf-8") as f:
                json.dump(coverage.report(), f)
        except OSError as e:
            print("cannot write the coverage data: %s" % e, file=sys.stderr)
    sys.stdout.flush()
    sys.stderr.flush()
    sys.exit(code)


if __name__ == "__main__":
    main()

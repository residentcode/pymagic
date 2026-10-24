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
    def __init__(self, port, token):
        self.sock = socket.create_connection(("127.0.0.1", port))
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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--token", required=True)
    parser.add_argument("--root", required=True)
    parser.add_argument("--framework", default="auto", choices=["auto", "pytest", "unittest"])
    parser.add_argument("ids", nargs="*")
    options = parser.parse_args()

    channel = Channel(options.port, options.token)
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

    try:
        code = run_pytest(channel, root, options.ids) if framework == "pytest" else run_unittest(channel, root, options.ids)
    except ImportError as e:
        print("cannot run tests with %s: %s" % (framework, e), file=sys.stderr)
        code = 2
    sys.stdout.flush()
    sys.stderr.flush()
    sys.exit(code)


if __name__ == "__main__":
    main()

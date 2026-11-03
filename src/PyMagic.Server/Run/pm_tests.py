"""PyMagic test runner.

Runs pytest when it is installed (it also runs unittest tests), otherwise the standard unittest runner; or, when asked, the
doctests of the project's modules, nose-style tests (unittest classes and plain test functions) or Twisted Trial test cases.
It reports to the host over a local TCP socket as JSON lines:

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
            # a subtest (pytest 9's subtests and unittest's subTest, or the pytest-subtests plugin): a node of its own under its test
            context = getattr(report, "context", None)
            if report.when == "call" and context is not None and hasattr(context, "kwargs"):
                self.subtest(report, context, st)
                return
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

        def subtest(self, report, context, parent):
            """Reports a subtest as test_id[message k=v], as the unittest runner names them; a failing one fails its test too."""
            # pytest's own reports carry the values as their repr already; the plugin's carry the values
            shown = str if type(report).__module__.startswith("_pytest") else repr
            parts = ([str(context.msg)] if getattr(context, "msg", None) is not None else []) + \
                ["%s=%s" % (k, shown(v)) for k, v in dict(context.kwargs).items()]
            label = (" ".join(parts) or "subtest").replace("[", "(").replace("]", ")")
            sub = {"outcome": "passed", "message": "", "traceback": "", "file": None, "line": None}
            if report.failed:
                sub["outcome"] = "failed"
            elif report.skipped:
                sub["outcome"] = "skipped"
            if report.failed or report.skipped:
                self.describe_failure(report, sub)
            if report.failed and parent["outcome"] in ("passed", "skipped"):
                parent.update(outcome="failed", message=sub["message"], traceback=sub["traceback"], file=sub["file"], line=sub["line"])
            channel.send({"type": "testResult", "id": "%s[%s]" % (report.nodeid, label), "outcome": sub["outcome"],
                          "duration": getattr(report, "duration", 0.0) or 0.0, "message": sub["message"], "traceback": sub["traceback"],
                          "output": "", "file": sub["file"], "line": sub["line"]})

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

def django_runner(root, settings_module, failfast):
    """Django's own test runner (the project's TEST_RUNNER), set up as manage.py test does: the settings, django.setup(), the test environment."""
    import re
    if not settings_module and not os.environ.get("DJANGO_SETTINGS_MODULE"):
        # the module manage.py names, as Django's own command line reads it
        for folder, dirs, names in os.walk(root):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS and not d.startswith(".")]
            if "manage.py" in names:
                with open(os.path.join(folder, "manage.py"), encoding="utf-8", errors="replace") as f:
                    found = re.search(r"DJANGO_SETTINGS_MODULE['\"]\s*,\s*['\"]([\w.]+)", f.read())
                if found:
                    settings_module = found.group(1)
                    if folder not in sys.path:
                        sys.path.insert(0, folder)
                break
    if settings_module:
        os.environ["DJANGO_SETTINGS_MODULE"] = settings_module
    import django
    from django.conf import settings
    from django.test.utils import get_runner
    django.setup()
    runner = get_runner(settings)(verbosity=0, interactive=False, failfast=failfast)
    runner.setup_test_environment()
    return runner


def run_unittest(channel, root, ids, flavor="unittest", settings_module=None, failfast=False):
    import unittest
    runner = django_runner(root, settings_module, failfast) if flavor == "django" else None
    if flavor == "trial":
        import twisted.trial.unittest  # noqa: F401  (Trial's test cases run under unittest's runner through Twisted's adapter)

    def module_name(file_part):
        name = file_part[:-3] if file_part.endswith(".py") else file_part
        name = name.replace("/", ".").replace("\\", ".")
        return name[:-9] if name.endswith(".__init__") else name

    def test_id(test):
        if isinstance(test, unittest.FunctionTestCase):
            func = test._testFunc
            module = sys.modules.get(func.__module__)
            file_part = rel_path(root, getattr(module, "__file__", "") or "") or func.__module__.replace(".", "/") + ".py"
            return "%s::%s" % (file_part, func.__name__)
        cls = type(test)
        module = sys.modules.get(cls.__module__)
        file_part = rel_path(root, getattr(module, "__file__", "") or "") or cls.__module__.replace(".", "/") + ".py"
        method = getattr(test, "_testMethodName", None)
        return "%s::%s::%s" % (file_part, cls.__name__, method) if method else "%s::%s" % (file_part, cls.__name__)

    loader = unittest.TestLoader()
    if runner is not None:
        # Django's labels: an app, a module, a class or a test, dotted (shop.tests.ItemTests.test_create); the tree's ids are turned into them
        labels = [i if "::" not in i and "/" not in i and not i.endswith(".py") else ".".join([i.split("::")[0][:-3].replace("/", ".")] + i.split("::")[1:]) for i in ids]
        suite = runner.build_suite(labels)
        ids = []
    elif not ids:
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
    if runner is None:
        suite = unittest.TestSuite()
    for item in ids:
        parts = item.split("::")
        dotted = ".".join([module_name(parts[0])] + parts[1:])
        if flavor == "nose":
            # nose also runs the module's plain functions named test...
            try:
                import importlib
                import types
                module = importlib.import_module(module_name(parts[0]))
                functions = [f for name, f in sorted(vars(module).items())
                             if isinstance(f, types.FunctionType) and f.__module__ == module.__name__ and name.lower().startswith("test")]
                if len(parts) == 2 and any(f.__name__ == parts[1] for f in functions):
                    suite.addTest(unittest.FunctionTestCase(getattr(module, parts[1]),
                                                            setUp=getattr(module, "setup_function", None) and (lambda f=getattr(module, parts[1]): module.setup_function(f)),
                                                            tearDown=getattr(module, "teardown_function", None) and (lambda f=getattr(module, parts[1]): module.teardown_function(f))))
                    continue
                if len(parts) == 1:
                    for f in functions:
                        suite.addTest(unittest.FunctionTestCase(f))
            except Exception:
                pass   # the load below reports the import error
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
            self.sub_began = {}
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
            # each subtest is a node of its own under its test: test_id[i=1], or test_id[message i=1]
            params = getattr(subtest, "params", None) or {}
            label = " ".join(([str(subtest._message)] if getattr(subtest, "_message", None) not in (None, unittest.case._subtest_msg_sentinel) else [])
                             + ["%s=%r" % kv for kv in params.items()]) or "subtest"
            now = time.perf_counter()
            began = self.sub_began.get(test, self.began.get(test, now))
            self.sub_began[test] = now
            if err is None:
                outcome, message, tb, file, line = "passed", "", "", None, None
            else:
                outcome = "failed" if issubclass(err[0], test.failureException) else "error"
                message, tb = "%s: %s" % (err[0].__name__, first_line(err[1])), self._exc_info_to_string(err, test)
                file, line = self.location(err)
                self.finish(test, "failed", message, tb, err)
            channel.send({"type": "testResult", "id": "%s[%s]" % (test_id(test), label.replace("[", "(").replace("]", ")")), "outcome": outcome,
                          "duration": now - began, "message": message, "traceback": tb, "output": "", "file": file, "line": line})

        def stopTest(self, test):
            outcome, message, tb, exc_info = self.pending.pop(test, ("passed", "", "", None))
            file, line = self.location(exc_info)
            channel.send({"type": "testResult", "id": test_id(test), "outcome": outcome,
                          "duration": time.perf_counter() - self.began.pop(test, time.perf_counter()),
                          "message": message, "traceback": tb, "output": "", "file": file, "line": line})
            super().stopTest(test)

    result = Result()
    old_databases = runner.setup_databases() if runner is not None else None
    try:
        if runner is not None and failfast:
            result.failfast = True
        unittest.TestSuite(real).run(result)
    finally:
        if runner is not None:
            runner.teardown_databases(old_databases)
            runner.teardown_test_environment()
    print("Ran %d tests" % result.testsRun)
    sys.stdout.flush()
    return 0 if result.wasSuccessful() and not failed_loads else 1


# ------------------------------------------------------------------------------------------------ doctest

def run_doctest(channel, root, ids):
    """The examples in the docstrings of the project's modules: each docstring with examples is one test."""
    import doctest
    import importlib
    import io

    files = sorted({i.split("::")[0] for i in ids}) if ids else []
    if not ids:
        for folder, dirs, names in os.walk(root):
            dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS and not d.startswith("."))
            for name in sorted(names):
                if name.endswith(".py") and name != "setup.py" and name != "conftest.py":
                    rel = rel_path(root, os.path.join(folder, name))
                    if rel:
                        files.append(rel)
    wanted = {i for i in ids if "::" in i}
    finder = doctest.DocTestFinder()
    found = []
    for rel in files:
        name = rel[:-3].replace("/", ".")
        if name.endswith(".__init__"):
            name = name[:-9]
        try:
            with open(os.path.join(root, rel), encoding="utf-8") as f:
                if ">>>" not in f.read():
                    continue
            module = importlib.import_module(name)
            tests = finder.find(module)
        except BaseException:
            channel.send({"type": "collectError", "file": rel, "message": traceback.format_exc()})
            continue
        for t in tests:
            if not t.examples:
                continue
            short = t.name[len(module.__name__) + 1:] if t.name.startswith(module.__name__ + ".") else t.name
            test_id = "%s::%s" % (rel, short or "module")
            if wanted and test_id not in wanted and not any(test_id.startswith(w + ".") for w in wanted):
                continue
            found.append((test_id, rel, (t.lineno or 0) + 1, t))
    channel.send({"type": "collected", "tests": [{"id": i, "file": f, "line": line} for i, f, line, _ in found]})
    failures = 0
    for test_id, rel, line, t in found:
        channel.send({"type": "testStarted", "id": test_id})
        out = io.StringIO()
        runner = doctest.DocTestRunner(optionflags=doctest.ELLIPSIS | doctest.NORMALIZE_WHITESPACE)
        began = time.perf_counter()
        result = runner.run(t, out=out.write, clear_globs=True)
        report = out.getvalue()
        failed_line = None
        for text in report.splitlines():
            if text.startswith("File ") and ", line " in text:
                try:
                    failed_line = int(text.split(", line ")[1].split(",")[0])
                except ValueError:
                    pass
                break
        outcome = "failed" if result.failed else "passed"
        failures += 1 if result.failed else 0
        channel.send({"type": "testResult", "id": test_id, "outcome": outcome, "duration": time.perf_counter() - began,
                      "message": "%d of %d examples failed" % (result.failed, result.attempted) if result.failed else "",
                      "traceback": report, "output": "", "file": rel if result.failed else None, "line": failed_line})
    print("Ran %d doctests" % len(found))
    sys.stdout.flush()
    return 1 if failures else 0


# ---------------------------------------------------------------- line coverage (no dependency: sys.monitoring, else sys.settrace)

SKIP_DIRS = {".git", ".hg", ".venv", "venv", "env", ".env", "node_modules", "__pycache__", "site-packages", ".tox", ".mypy_cache", ".pytest_cache", "build", "dist", ".pymagic"}


BRANCH_OPS = {"FOR_ITER", "JUMP_IF_TRUE_OR_POP", "JUMP_IF_FALSE_OR_POP"}
# instructions that stand between a jump and the code it reaches, and have no line of their own to show
FILLER_OPS = {"NOT_TAKEN", "END_FOR", "POP_ITER", "CACHE", "NOP", "EXTENDED_ARG"}


def _instructions(code):
    import dis
    return [ins for ins in dis.get_instructions(code)]


def _line_of(ins):
    positions = getattr(ins, "positions", None)
    return positions.lineno if positions is not None else ins.starts_line


def _dest_line(instructions, offset, from_line):
    """The line a jump to this offset reaches: the first real instruction from there on another line than the jump's; 0 when the code ends first."""
    for ins in instructions:
        if ins.offset < offset or ins.opname in FILLER_OPS:
            continue
        line = _line_of(ins)
        if line is not None and line > 0 and line != from_line:
            return line
    return 0


def _branches_of(code):
    """{offset of a conditional jump: (its line, the lines its two ways reach)}, the ways that reach one line left out."""
    instructions = _instructions(code)
    found = {}
    for i, ins in enumerate(instructions):
        if not (ins.opname.startswith("POP_JUMP") or ins.opname in BRANCH_OPS) or not isinstance(ins.argval, int):
            continue
        line = _line_of(ins)
        if line is None or i + 1 >= len(instructions):
            continue
        ways = {_dest_line(instructions, instructions[i + 1].offset, line), _dest_line(instructions, ins.argval, line)}
        if len(ways) == 2:
            found[ins.offset] = (line, ways)
    return found


class Coverage:
    """Records the lines of the project's own files that run and the ways their branches went, and knows which lines and branches could run."""

    def __init__(self, root):
        self.root = os.path.normcase(os.path.abspath(root))
        self.executed = {}
        self.arcs = {}            # filename -> {line of a branch: lines it went to (0: out of the code)}
        self._tracked = {}
        self._code_lines = {}     # code object -> its instructions, for the lines a branch reaches
        self._monitoring = hasattr(sys, "monitoring") and hasattr(sys.monitoring, "COVERAGE_ID") and not os.environ.get("PYMAGIC_COVERAGE_TRACE")

    def _went(self, code, source, destination):
        instructions = self._code_lines.get(code)
        if instructions is None:
            instructions = self._code_lines[code] = _instructions(code)
        line = next((_line_of(i) for i in instructions if i.offset == source), None)
        if line is not None:
            self.arcs.setdefault(code.co_filename, {}).setdefault(line, set()).add(_dest_line(instructions, destination, line))

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
            events = mon.events.LINE
            # 3.14 reports each way of a branch apart, so a way seen once is turned off; before, BRANCH covers both ways and stays on
            sides = [getattr(mon.events, n) for n in ("BRANCH_LEFT", "BRANCH_RIGHT") if hasattr(mon.events, n)]
            if sides:
                def on_side(code, source, destination):
                    if not self._wanted(code.co_filename):
                        return mon.DISABLE
                    self._went(code, source, destination)
                    return mon.DISABLE
                for side in sides:
                    mon.register_callback(tool, side, on_side)
                    events |= side
            elif hasattr(mon.events, "BRANCH"):
                def on_branch(code, source, destination):
                    if not self._wanted(code.co_filename):
                        return mon.DISABLE
                    self._went(code, source, destination)
                mon.register_callback(tool, mon.events.BRANCH, on_branch)
                events |= mon.events.BRANCH
            mon.set_events(tool, events)
        else:
            # without sys.monitoring the ways of a branch are the line that runs next in the same frame
            def tracer(frame):
                arcs = self.arcs.setdefault(frame.f_code.co_filename, {})
                last = [frame.f_lineno]

                def local(frame, event, arg):
                    if event == "line":
                        self.executed.setdefault(frame.f_code.co_filename, set()).add(frame.f_lineno)
                        arcs.setdefault(last[0], set()).add(frame.f_lineno)
                        last[0] = frame.f_lineno
                    elif event == "return":
                        arcs.setdefault(last[0], set()).add(0)
                    return local
                return local

            def global_trace(frame, event, arg):
                if self._wanted(frame.f_code.co_filename):
                    self.executed.setdefault(frame.f_code.co_filename, set()).add(frame.f_lineno)
                    return tracer(frame)
                return None
            sys.settrace(global_trace)
            threading.settrace(global_trace)

    def stop(self):
        if self._monitoring:
            mon = sys.monitoring
            mon.set_events(mon.COVERAGE_ID, 0)
            mon.register_callback(mon.COVERAGE_ID, mon.events.LINE, None)
            for name in ("BRANCH", "BRANCH_LEFT", "BRANCH_RIGHT"):
                if hasattr(mon.events, name):
                    mon.register_callback(mon.COVERAGE_ID, getattr(mon.events, name), None)
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
        lines = Coverage._without_docstrings(source, tree, lines)
        return lines

    @staticmethod
    def _branch_ways(source, path):
        """{line: the lines its branches can go to} over every function of the file."""
        try:
            code = compile(source, path, "exec")
        except (SyntaxError, ValueError, MemoryError):
            return {}
        ways = {}
        stack = [code]
        while stack:
            c = stack.pop()
            for line, reached in _branches_of(c).values():
                ways.setdefault(line, set()).update(reached)
            stack.extend(k for k in c.co_consts if hasattr(k, "co_lines"))
        return ways

    @staticmethod
    def _without_docstrings(source, tree, lines):
        import ast
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
        arcs_by_norm = {}
        for k, v in self.arcs.items():
            merged = arcs_by_norm.setdefault(os.path.normcase(os.path.abspath(k)), {})
            for line, reached in v.items():
                merged.setdefault(line, set()).update(reached)
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
                norm = os.path.normcase(os.path.abspath(path))
                ran = executed_by_norm.get(norm, set()) & executable
                rel = os.path.relpath(path, self.root).replace(os.sep, "/")
                entry = {"executable": sorted(executable), "executed": sorted(ran)}
                # a branch line: the lines its ways reach (0 is out of the function) and those the run took; only lines that ran have taken ways
                went = arcs_by_norm.get(norm, {})
                text_lines = source.splitlines()
                branches = {}
                for line, reached in self._branch_ways(source, path).items():
                    if line in executable and line <= len(text_lines) and "pragma: no branch" not in text_lines[line - 1]:
                        branches[str(line)] = [sorted(reached), sorted(reached & went.get(line, set()))]
                if branches:
                    entry["branches"] = branches
                files[rel] = entry
        return {"files": files}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--host", default="127.0.0.1", help="where the host listens (WSL and Docker reach it by another address)")
    parser.add_argument("--token", required=True)
    parser.add_argument("--root", required=True)
    parser.add_argument("--framework", default="auto", choices=["auto", "pytest", "unittest", "doctest", "nose", "trial", "django"])
    parser.add_argument("--django-settings", default=None, help="the settings module of Django's tests (else manage.py's)")
    parser.add_argument("--failfast", action="store_true", help="stop at the first failure")
    parser.add_argument("--no-django-runner", action="store_true", help="Django set up, the tests run by unittest's runner (no test database)")
    parser.add_argument("--coverage", default=None, help="write the lines that ran to this JSON file")
    parser.add_argument("ids", nargs="*")
    options = parser.parse_args()

    # Debug of tests: this runner runs inside the debugger (pm_debug.py --tests), whose connection carries its messages too
    channel = sys.modules.get("pymagic_test_channel") or Channel(options.port, options.token, options.host)
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
        if framework == "pytest":
            code = run_pytest(channel, root, options.ids)
        elif framework == "doctest":
            code = run_doctest(channel, root, options.ids)
        elif framework == "django" and options.no_django_runner:
            django_runner(root, options.django_settings, options.failfast).teardown_test_environment()   # the settings and django.setup() only
            code = run_unittest(channel, root, options.ids)
        elif framework == "django":
            code = run_unittest(channel, root, options.ids, "django", options.django_settings, options.failfast)
        else:
            code = run_unittest(channel, root, options.ids, framework)
    except ImportError as e:
        print("cannot run tests with %s: %s" % (framework, e), file=sys.stderr)
        code = 2
    finally:
        # what ran is written even when the run stopped on an error of its own (a test database that cannot be made)
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

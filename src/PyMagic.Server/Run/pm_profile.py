"""PyMagic's profiler helper: runs a program under a profiler and writes what it measured as JSON.

    python pm_profile.py --out result.json [--pstats raw.pstats] [--engine auto|cprofile|yappi] --script path/to/script.py -- [arguments]
    python pm_profile.py --out result.json ... --module package.module -- [arguments]          as python -m does
    python pm_profile.py --out result.json ... --tests pytest|unittest|auto -- [test ids]     the tests, as the Tests panel names them
    python pm_profile.py --out result.json --load saved.pstats                                 a profile saved before, read only

A script runs as __main__ with its own folder on sys.path, like `python script.py`. The result is written even when the
program raises or calls sys.exit, and the helper then exits with the code the program would have exited with.

The engine is cProfile, or yappi when it is installed (auto): yappi measures every thread, and wall time, so a program
that waits on threads or asyncio shows where it waited. Both give the same pstats, kept raw with --pstats.
"""
import argparse
import json
import os
import pstats
import runpy
import sys
import tempfile
import time
import traceback

MAX_FUNCTIONS = 1500
MAX_LINKS = 12


def describe(key):
    filename, line, name = key
    builtin = filename.startswith("~") or filename.startswith("<")
    return {
        "file": filename if builtin else os.path.abspath(filename),
        "line": line,
        "name": name,
        "builtin": builtin,
    }


def collect(stats, elapsed):
    """Each function with its calls and times, the functions that called it and those it called, each by time with callees."""
    callees = {}
    for key, (_, _, _, _, callers) in stats.stats.items():
        for caller_key, (cc, nc, tt, ct) in callers.items():
            callees.setdefault(caller_key, []).append((key, nc, tt, ct))
    rows = []
    for key, (primitive, calls, own, cumulative, callers) in stats.stats.items():
        row = describe(key)
        row.update(calls=calls, primitive=primitive, own=own, cumulative=cumulative)
        row["callers"] = []
        for caller_key, (cc, nc, tt, ct) in sorted(callers.items(), key=lambda kv: -kv[1][3])[:MAX_LINKS]:
            entry = describe(caller_key)
            entry.update(calls=nc, own=tt, cumulative=ct)
            row["callers"].append(entry)
        row["callees"] = []
        for callee_key, nc, tt, ct in sorted(callees.get(key, []), key=lambda c: -c[3])[:MAX_LINKS]:
            entry = describe(callee_key)
            entry.update(calls=nc, own=tt, cumulative=ct)
            row["callees"].append(entry)
        rows.append(row)
    rows.sort(key=lambda r: -r["cumulative"])
    return {"seconds": elapsed, "total": stats.total_tt, "calls": stats.total_calls, "functions": rows[:MAX_FUNCTIONS]}


def run_tests(framework, ids):
    """The tests with pytest, or unittest (dotted names from the panel's ids: tests/test_a.py::Case::test_x)."""
    if framework == "auto":
        try:
            import pytest  # noqa: F401
            framework = "pytest"
        except ImportError:
            framework = "unittest"
    if framework == "pytest":
        import pytest
        return pytest.main(["-q", "-p", "no:cacheprovider"] + list(ids))
    import unittest
    names = [i.split("::")[0][:-3].replace("/", ".") + "".join("." + p for p in i.split("::")[1:]) if i.split("::")[0].endswith(".py") else i for i in ids]
    suite = unittest.defaultTestLoader.loadTestsFromNames(names) if names else unittest.defaultTestLoader.discover(os.getcwd())
    result = unittest.TextTestRunner(verbosity=1).run(suite)
    return 0 if result.wasSuccessful() else 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("--pstats", help="where the raw pstats are kept too")
    parser.add_argument("--engine", default="auto", choices=["auto", "cprofile", "yappi"])
    parser.add_argument("--script")
    parser.add_argument("--module")
    parser.add_argument("--tests", choices=["auto", "pytest", "unittest"])
    parser.add_argument("--load", help="a .pstats file to read instead of running anything")
    parser.add_argument("--port")
    parser.add_argument("--token")
    parser.add_argument("--root")
    parser.add_argument("rest", nargs="*")
    args, unknown = parser.parse_known_args()
    rest = [a for a in args.rest + unknown if a != "--"]

    if args.load:
        try:
            stats = pstats.Stats(os.path.abspath(args.load))
        except Exception as e:   # not a pstats file, or one of another Python
            print("%s is not a profile this Python can read: %s" % (args.load, e), file=sys.stderr)
            sys.exit(2)
        result = collect(stats, stats.total_tt)
        result.update(script=os.path.abspath(args.load), exit=0, engine="pstats", loaded=True)
        write(args.out, result)
        return

    engine = args.engine
    yappi = None
    if engine in ("auto", "yappi"):
        try:
            import yappi
            engine = "yappi"
        except ImportError:
            if engine == "yappi":
                print("yappi is not installed for this Python: profiling with cProfile", file=sys.stderr)
            engine = "cprofile"

    if args.script:
        target = os.path.abspath(args.script)
        sys.argv = [target] + rest
        sys.path.insert(0, os.path.dirname(target))
        run = lambda: runpy.run_path(target, run_name="__main__")
    elif args.module:
        target = args.module
        sys.argv = [args.module] + rest
        sys.path.insert(0, os.getcwd())
        run = lambda: runpy.run_module(args.module, run_name="__main__", alter_sys=True)
    elif args.tests:
        target = "tests: " + (" ".join(rest) or "all")
        sys.path.insert(0, os.getcwd())
        def run():
            code = run_tests(args.tests, rest)
            if code:
                raise SystemExit(int(code))
    else:
        parser.error("--script, --module, --tests or --load")

    code = 0
    started = time.perf_counter()
    if engine == "yappi":
        yappi.set_clock_type("wall")
        yappi.start(builtins=True)
    else:
        import cProfile
        profile = cProfile.Profile()
        profile.enable()
    try:
        run()
    except SystemExit as e:
        code = e.code if isinstance(e.code, int) else (0 if e.code is None else 1)
        if isinstance(e.code, str):
            print(e.code, file=sys.stderr)
    except KeyboardInterrupt:
        code = 130
    except BaseException:
        code = 1
        traceback.print_exc()
    finally:
        if engine == "yappi":
            yappi.stop()
        else:
            profile.disable()
        elapsed = time.perf_counter() - started

    raw = args.pstats or os.path.join(tempfile.gettempdir(), "pymagic-profile-%d.pstats" % os.getpid())
    os.makedirs(os.path.dirname(os.path.abspath(raw)), exist_ok=True)
    if engine == "yappi":
        yappi.get_func_stats().save(raw, type="pstat")
    else:
        profile.dump_stats(raw)
    result = collect(pstats.Stats(raw), elapsed)
    result.update(script=target, exit=code, engine=engine)
    if engine == "yappi":
        result["threads"] = [{"name": t.name, "id": t.id, "seconds": t.ttot} for t in yappi.get_thread_stats()]
    write(args.out, result)
    sys.stdout.flush()
    sys.exit(code)


def write(path, result):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(result, f)


if __name__ == "__main__":
    main()

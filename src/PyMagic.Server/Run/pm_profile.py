"""PyMagic's profiler helper: runs a script under cProfile and writes what it measured as JSON.

    python pm_profile.py --out result.json --script path/to/script.py -- [script arguments]

The script runs as __main__ with its own folder on sys.path, like `python script.py`. The result is written even when the
script raises or calls sys.exit, and the helper then exits with the code the script would have exited with.
"""
import argparse
import cProfile
import json
import os
import pstats
import runpy
import sys
import time
import traceback

MAX_FUNCTIONS = 1500
MAX_CALLERS = 12


def describe(key):
    filename, line, name = key
    builtin = filename.startswith("~") or filename.startswith("<")
    return {
        "file": filename if builtin else os.path.abspath(filename),
        "line": line,
        "name": name,
        "builtin": builtin,
    }


def collect(profile, elapsed):
    stats = pstats.Stats(profile)
    rows = []
    for key, (primitive, calls, own, cumulative, callers) in stats.stats.items():
        row = describe(key)
        row.update(calls=calls, primitive=primitive, own=own, cumulative=cumulative)
        top = sorted(callers.items(), key=lambda kv: -kv[1][3])[:MAX_CALLERS]
        row["callers"] = []
        for caller_key, (cc, nc, tt, ct) in top:
            entry = describe(caller_key)
            entry.update(calls=nc, own=tt, cumulative=ct)
            row["callers"].append(entry)
        rows.append(row)
    rows.sort(key=lambda r: -r["cumulative"])
    return {"seconds": elapsed, "total": stats.total_tt, "calls": stats.total_calls, "functions": rows[:MAX_FUNCTIONS]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("--script", required=True)
    parser.add_argument("--port")
    parser.add_argument("--token")
    parser.add_argument("--root")
    parser.add_argument("rest", nargs="*")
    args, unknown = parser.parse_known_args()
    script_args = [a for a in args.rest + unknown if a != "--"]

    script = os.path.abspath(args.script)
    sys.argv = [script] + script_args
    sys.path.insert(0, os.path.dirname(script))

    profile = cProfile.Profile()
    code = 0
    started = time.perf_counter()
    profile.enable()
    try:
        runpy.run_path(script, run_name="__main__")
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
        profile.disable()
        elapsed = time.perf_counter() - started

    result = collect(profile, elapsed)
    result["script"] = script
    result["exit"] = code
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(result, f)
    sys.stdout.flush()
    sys.exit(code)


if __name__ == "__main__":
    main()

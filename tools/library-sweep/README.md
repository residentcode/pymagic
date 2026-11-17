# Library sweep

Finds the Python constructs the type engine does not follow yet. It uses real installed packages the way code uses them and reports where a type
comes out Unknown although the package's source names one. A gap found here is fixed in the engine for every library. Never fix it for one
library only.

1. Make a venv in `%TEMP%` and install packages into it (offline: `uv pip install --offline <name>`). Use only `%TEMP%` and the repo.
2. Generate the probes, one file per top-level package:
   `python gen2.py <venv>\Lib\site-packages <out> 25`
   - `gen2.py` writes usages: instances through a parameter, properties, zero-argument calls, `await`, `with` / `async with` / `for` / `async for`.
   - `gen.py` writes plain references to annotated functions and methods.
   - Both write the source's annotations to `<out>/expect.tsv`.
3. Build the tests to a separate folder and run the probe in shards:
   `PYMAGIC_TYPESHED=<repo>\typeshed PROBE_DIR=<out> PROBE_LIBS=<site-packages> PROBE_SHARD=i/n PROBE_NODIAG=1 PYMAGIC_TEST_ONLY=LibProbe PyMagic.Tests.exe`
4. `python analyse.py <out> u_ --partial` groups the Unknowns by the shape of the annotation, with examples.

Some Unknowns are expected:
- a class type variable with nothing to solve it from (`x: Cursor` where the class is `Cursor[Row]`),
- a compiled function whose module does not say what it returns.

On 2026-11-17, 2,543 usages over 88 packages went from 82 Unknowns to 31. These fixes did it: `async with` / `async for`, `self: T -> T`
and `cls: type[T] -> T`, Callable aliases, `A | B` aliases, PEP 695 `type` aliases, `NewType`, decorator classes with `__get__`, and an `__iter__`
declared to give an Iterable.

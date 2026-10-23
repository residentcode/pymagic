# PyMagic

An independent Python IDE engine, modelled on the *behaviour* of PyCharm's Python front-end
(documented from analysis of the shipped product). No JetBrains code is copied; the engine is
written from scratch in C#.

## Stack

| Layer | Tech | Why |
|---|---|---|
| Engine (lexer, parser, resolve, types) | C# / .NET 9 (`src/PyMagic.Core`) | Fast, strongly typed, good for compiler-style code (immutable trees, pattern matching) |
| Server | C# LSP server (planned, `src/PyMagic.Server`) | One protocol, any editor |
| UI | TypeScript + Monaco (planned, `web/`) | Mature editor component; Electron/Tauri shell later |

## Layout

```
src/PyMagic.Core/Syntax     Token, Lexer, Ast, AstChildren, Parser
src/PyMagic.Core/Semantics  Scope, Symbol, Reference, Binder, SemanticModel, Builtins
src/PyMagic.Core/Flow       ControlFlowGraph, CfgBuilder, ScopeFlow (reaching definitions), FlowModel
src/PyMagic.Core/Types      PyType, Workspace/ModuleInfo, TypeEvaluator (annotations, classes/MRO/members, calls, inspections)
src/PyMagic.Core/Stubs      minimal builtins.pyi and typing.pyi (embedded)
tests/PyMagic.Tests       dependency-free test runner:  dotnet run --project tests/PyMagic.Tests
                          corpus check:  dotnet tests/PyMagic.Tests/bin/Debug/net9.0/PyMagic.Tests.dll corpus <dir>
```

The test project is a plain console runner because this machine's NuGet only has an offline source
(no xunit). Switch to xunit once nuget.org is configured.

## Roadmap (matches the behaviour notes)

1. **Lexer** - indent/dedent, brackets, continuations, strings (PEP 701 f-strings), numbers. *Done.*
2. **Parser** - recursive descent, statement-level error recovery, soft keywords (`match`, `case`, `type`) resolved by speculative parse, AST with source ranges. *Done; parses all 1037 stdlib files and 404 site-packages files with zero diagnostics.* Still open: f-string interior as expression nodes, store-context validation, finer error messages.
3. **Scopes + name tables** - module/class/function/lambda/comprehension/PEP 695 type-parameter scopes, `global`/`nonlocal`, class-scope visibility, walrus-in-comprehension, match captures, star imports, builtins. Every name use is a `Reference` (read/write/delete) linked to a `Symbol` with its declarations. *Done; builds on the whole stdlib with no crashes, the only unresolved names are runtime-injected ones.* Still open: `__all__`, `try/except ImportError` fallbacks, conditional-definition awareness (needs the control-flow graph).
4. **Control-flow graph + reaching definitions** - one CFG per scope built in evaluation order (branches, loops with `break`/`continue`/`else`, `try`/`except`/`else`/`finally` with the finally block built for both the normal and the exceptional path, `match`, short-circuit `and`/`or`/ternary with separate true/false exits, constant conditions, `del`, `except ... as e` unbinding). Forward bit-set dataflow gives, per use of a local, the bindings that may reach it and whether it may still be unbound. Produces unreachable-code and "referenced before assignment" findings; the whole stdlib (1037 files) analyses in about 2.5 s. *Done.* Still open: `NoReturn` calls (needs types), `with` blocks that swallow exceptions, `break`/`return` running through `finally`, and attribute/member resolution.
5. **Types, attribute and call resolution** - type model (classes/instances, unions, generics with type variables, tuples, callables, modules, `super()`), a lazy evaluator with cycle cutting, annotation evaluation (`Optional`, `Union`, `X | None`, `Callable`, `Literal`, `Tuple`, aliases, string forward references, PEP 695 parameters), C3 MRO with unknown-ancestor tracking, member lookup (class attributes, `self.x = ...` instance attributes, `__slots__`, properties, static/class methods, metaclasses, `__getattr__`), method binding and generic substitution, constructors, overload selection, argument-to-parameter mapping (positional, keyword, `*args`, `**kwargs`, defaults, keyword-only, positional-only) with problems reported, type-variable solving from arguments, lenient assignability (numeric promotion, protocols), operators through dunder methods, iteration, subscripts, destructuring, return-type inference (including generators and coroutines). Library types come from bundled minimal `builtins`/`typing` stubs parsed by our own parser; modules are also loaded from search roots. *Done for the core; 184 tests; the 152 top-level stdlib modules inspect in about 3 s without crashes.* Still open: enum/dataclass/namedtuple/TypedDict synthesis, narrowing (`isinstance`, `is None`, truthiness), call-site parameter inference, `NoReturn`, decorators that change signatures, and real typeshed instead of the minimal stubs.
6. Narrowing, dataclass/enum/namedtuple/TypedDict providers.
7. Typeshed + stub packages + `py.typed` import resolution (replaces the minimal bundled stubs).
8. LSP server, then Monaco UI.

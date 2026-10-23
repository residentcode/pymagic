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
src/PyMagic.Core/Syntax   Token, Lexer, Ast, Parser
tests/PyMagic.Tests       dependency-free test runner:  dotnet run --project tests/PyMagic.Tests
                          corpus check:  dotnet tests/PyMagic.Tests/bin/Debug/net9.0/PyMagic.Tests.dll corpus <dir>
```

The test project is a plain console runner because this machine's NuGet only has an offline source
(no xunit). Switch to xunit once nuget.org is configured.

## Roadmap (matches the behaviour notes)

1. **Lexer** - indent/dedent, brackets, continuations, strings (PEP 701 f-strings), numbers. *Done.*
2. **Parser** - recursive descent, statement-level error recovery, soft keywords (`match`, `case`, `type`) resolved by speculative parse, AST with source ranges. *Done; parses all 1037 stdlib files and 404 site-packages files with zero diagnostics.* Still open: f-string interior as expression nodes, store-context validation, finer error messages.
3. Scopes + name tables (`global`/`nonlocal`, comprehensions, class scope rules).
4. Control-flow graph, reaching definitions, resolve of names/attributes.
5. Type model + evaluator: unions, generics, protocols, callables, `TypeEvalContext`-style caches.
6. Typeshed + stub packages + `py.typed` import resolution.
7. Narrowing, dataclass/enum/namedtuple/TypedDict providers.
8. LSP server, then Monaco UI.

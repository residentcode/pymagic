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
src/PyMagic.Core/Syntax   Token, Lexer (done), Parser/AST (next)
tests/PyMagic.Tests       dependency-free test runner:  dotnet run --project tests/PyMagic.Tests
```

The test project is a plain console runner because this machine's NuGet only has an offline source
(no xunit). Switch to xunit once nuget.org is configured.

## Roadmap (matches the behaviour notes)

1. **Lexer** - indent/dedent, brackets, continuations, strings, numbers. *Done, 27 tests.*
2. Parser - recursive descent with error recovery, soft keywords (`match`, `case`, `type`), f-string parts, full AST with ranges.
3. Scopes + name tables (`global`/`nonlocal`, comprehensions, class scope rules).
4. Control-flow graph, reaching definitions, resolve of names/attributes.
5. Type model + evaluator: unions, generics, protocols, callables, `TypeEvalContext`-style caches.
6. Typeshed + stub packages + `py.typed` import resolution.
7. Narrowing, dataclass/enum/namedtuple/TypedDict providers.
8. LSP server, then Monaco UI.

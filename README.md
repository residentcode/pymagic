# PyMagic

PyMagic is a Python IDE for Windows, in the spirit of PyCharm, with its own look. Its engine understands Python on its own, written from scratch in
C#: parsing, scopes, control flow, types, inspections and refactorings. It was modelled on how PyCharm behaves, and no JetBrains code is copied.
The editor is Monaco (the editor of VS Code), in an Electron window.

What it does, in short:

- **Editing**: completion, hover, go to definition, find usages, rename and the other refactorings, inspections with quick fixes (PyCharm's
  Python inspections and PyMagic's own), Emmet in HTML, templates, stylesheets and JSX, Tailwind CSS classes, tag pairs renamed together, full line completion by a small code model that runs on your computer, code vision and inlay hints, formatting (black and ruff style), VS Code's colours for code, a keymap you can change.
- **Frameworks**: Django (models, URLs, templates, settings, REST framework, New Project through Django's own `startproject`), Flask, FastAPI,
  Jinja, pytest. New Project also makes the other kinds PyCharm has: HTML5 Boilerplate, Bootstrap, Vite, React Native, Next.js, Node.js and
  Express, each by its own generator (Node.js needed).
- **Running**: run configurations, a debugger, tests with coverage, a profiler, a Python console, Jupyter notebooks, a terminal.
- **Environments**: venv, uv projects (`uv add` / `uv remove`), conda, and interpreters in WSL, Docker and on SSH hosts.
- **Tools**: Git (commit, log graph, rebase, three-way merge, changelists, shelf, pull requests), a database tool, an HTTP client, deployment
  to servers, JSON Schema checks of config files, local history.
- **Plugins**: the optional features can each be switched off in Settings > Plugins. See [Making a plugin](docs/plugins.md).

Every setting is made in the app (Settings, Ctrl+,). Nothing has to be edited by hand in a `.json` file.

## Install

PyMagic runs on Windows 10 and 11 (x64). There are two ways to get it.

**Run it from the sources** (for development):

1. Install the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (9.0.100 or later), [Node.js](https://nodejs.org) 20 or later,
   and Git.
2. Clone the repository:
   ```bash
   git clone https://github.com/residentcode/pymagic.git
   ```
3. Double-click `PyMagic.cmd` in the cloned folder. The first start installs the app shell (`npm install` in `desktop`). Every start builds the
   editor and the engine when they changed, then opens the project you used last (or `examples\demo`). `PyMagic.cmd C:\my\project` opens a
   folder directly. *File > Open Folder…* (Ctrl+Shift+O) switches project.

`PyMagic-web.cmd` (or `start.ps1`) runs the same editor in a browser tab instead of the app window.

**Make a copy that needs nothing installed**: build it as in [Build the app](#build-the-app). Then run the installer
`desktop\out\PyMagic-Setup-0.1.0.exe`, which installs PyMagic for the current user with a Start menu entry and a desktop shortcut. Or copy the
folder `desktop\out\PyMagic-win32-x64` to any Windows PC and start `PyMagic.exe` in it.

A Python interpreter is not part of PyMagic. Use one installed on the PC, or make one in the app (*Create a .venv…*, or New Project with uv).
PyCharm's typeshed is used when PyCharm is installed; otherwise the bundled one is.

## Build

### The engine and the page

```bash
dotnet build PyMagic.sln -c Release
```

Building `PyMagic.Web` builds the page too: it runs `node build.mjs` in `web`, and `npm install` the first time. Without Node.js it only warns,
and `-p:SkipPageBuild=true` skips it. To build the page alone:

```bash
cd web && npm install && node build.mjs
```

Run the engine with the page in a browser (it opens `http://127.0.0.1:5180/`):

```bash
dotnet run --project src/PyMagic.Web -- --root examples/demo
```

### Build the app

```bash
node desktop/package.mjs
```

It needs internet the first time, for Electron, the .NET runtime package, the installer tools and the model of full line completion
(Qwen2.5-Coder 0.5B, 531 MB, fetched once into `models/` by `models/fetch.mjs` and checked against its SHA-256; it is not in git). It builds
`desktop\out\PyMagic-win32-x64\PyMagic.exe` (about 1.1 GB, with Electron, the .NET runtime and the model inside) and the installer
`desktop\out\PyMagic-Setup-0.1.0.exe` (about 150 MB). `--no-installer` builds the folder only. Close any PyMagic.exe started from
`desktop\out` first: Windows keeps that folder locked while it runs.

### Tests

```bash
dotnet run --project tests/PyMagic.Tests -c Release
```

The engine's tests run in a plain console runner. `PYMAGIC_TEST_ONLY=SchemaTests` runs one class. `PYMAGIC_TEST_FULLLINE=1` also runs the
model of full line completion (fetch it first with `node models/fetch.mjs`).

```bash
cd web && node --test test/*.test.mjs
```

These are the page's tests of its modules without a browser.

```bash
cd desktop && node --test e2e/page.e2e.mjs
```

This is the page in the built PyMagic.exe, driven over the DevTools protocol. Build the app first.

## Visual Studio 2022

Open `PyMagic.sln` in Visual Studio 2022, version 17.12 or later (17.14 is what it is checked with).

- **What to install**: Visual Studio offers what is missing from `.vsconfig` (*ASP.NET and web development*, the .NET 9 SDK and runtime,
  Node.js tools). Node.js itself is needed for the page.
- **SDK and C# version**: `global.json` picks a .NET 9 SDK (9.0.100 or a later 9.0 band). `Directory.Build.props` holds every project at
  C# 13, the newest C# Visual Studio 2022 compiles, so no code that needs Visual Studio 2026 gets in.
- **Running**: *PyMagic.Web* is the startup project. Its launch profile opens `http://127.0.0.1:5180/` on `examples\demo`, and F5 debugs the
  engine with the page in the browser. The status bar of the page shows which build is running.
- **Tests**: `PyMagic.Tests` is a console program. Set it as the startup project and press F5 or Ctrl+F5 to run the engine's tests. Set the
  environment variable `PYMAGIC_TEST_ONLY` in its debug properties to run one class.
- **Solution Items** holds this README, `work_done.md`, the plugin guide and the build settings.

Visual Studio Code works too: open the folder, and use `dotnet build` and `node` from its terminal.

## Plugins

The optional parts of PyMagic are built-in plugins, switched on and off in **Settings > Plugins**: Django, Flask, FastAPI, JavaScript projects, Full line completion, Tailwind CSS, Emmet, HTML tag pairs, Jupyter Notebooks,
Docker, WSL, SSH, Git, JSON Schema, HTTP Client, Database Tools and SQL, Coverage, Diagrams, Profiler, Pull Requests, Deployment, Markdown and
Macros. How to make one is in [docs/plugins.md](docs/plugins.md).

## Stack

| Layer | Tech | Why |
|---|---|---|
| Engine (lexer, parser, resolve, types) | C# / .NET 9 (`src/PyMagic.Core`) | Fast, strongly typed, good for compiler-style code (immutable trees, pattern matching) |
| Server | C# LSP server (`src/PyMagic.Server`, stdio, no dependencies) | One protocol, any editor |
| Host | ASP.NET Core minimal app (`src/PyMagic.Web`) | Serves the UI, a workspace file API and one in-process language server per browser tab over WebSocket |
| UI | JavaScript + Monaco (`web/`, bundled with esbuild) | Mature editor component (the editor of VS Code) |
| App | Electron (`desktop/`, plain JavaScript) | The Windows window that starts the engine; `node desktop/package.mjs` makes PyMagic.exe and its installer |

## Layout

```
src/PyMagic.Core/Syntax     Token, Lexer, Ast, AstChildren, Parser
src/PyMagic.Core/Semantics  Scope, Symbol, Reference, Binder, SemanticModel, Builtins
src/PyMagic.Core/Flow       ControlFlowGraph, CfgBuilder, ScopeFlow (reaching definitions), FlowModel
src/PyMagic.Core/Types      PyType, Workspace/ModuleInfo, TypeEvaluator (annotations, classes/MRO/members, calls, inspections)
src/PyMagic.Core/Stubs      minimal builtins.pyi and typing.pyi (embedded)
src/PyMagic.Server          LanguageService (hover, definition, completion, symbols, diagnostics), JSON-RPC/LSP transport; run: dotnet run --project src/PyMagic.Server -- [--typeshed <dir>] [--root <dir>] [--lib <dir>]
src/PyMagic.Web             pymagic-web: static files, /api/info, /api/files, /api/file (GET/PUT, .py only, confined to the root), /lsp WebSocket
web/src                     main.js (Monaco providers, files, diagnostics), lsp.js (JSON-RPC client), index.html;  cd web && npm install && npm run build
examples/demo               small project with deliberate mistakes for trying the editor
tests/PyMagic.Tests       dependency-free test runner:  dotnet run --project tests/PyMagic.Tests
desktop/e2e               the page in PyMagic.exe, driven over the DevTools protocol:  node --test desktop/e2e/page.e2e.mjs (build the exe first)
                          corpus check:  dotnet tests/PyMagic.Tests/bin/Debug/net9.0/PyMagic.Tests.dll corpus <dir>
```

The test project is a plain console runner because this machine's NuGet only has an offline source
(no xunit). Switch to xunit once nuget.org is configured.

## Open work

Everything that is known to be left, in one place, gathered on 30 October 2026 from the "Not done" and "Still open" notes of items 2 to 154 (the item a line comes from is in brackets), and from PyCharm's own dialogs and menus (what PyMagic's notes never named). **How it is kept:** when an item does one of these, the line goes from here to [work_done.md](work_done.md#done-from-the-open-work-list) and the item says so; a new item's own "Not done" note is added here too. Code signing, auto-update and anything else for selling PyMagic are left out on purpose.

**Editor**

**Run, debug and tests**

**Installer**
- Not seen on screen: the sidebar picture (item 331), shown only on the finish page after an install and in the uninstaller; the folder page's refusal of a folder that is not PyMagic's (only its rule's tests and the silent installs ran).

**Type engine** (ibnbolbna's area until 31 October 2026, ours from then on; what ibnbolbna did and left is in [docs/ibnbolbna-work.md](docs/ibnbolbna-work.md))

**Compared with PyCharm** (its own dialogs and menus, from screenshots of 30 October 2026; none of this was in any item's notes)

**Django compared with PyCharm** (from PyCharm's Django help pages and its 17 Django inspections, checked against the code on 30 October 2026)

**Compared with PyCharm's help, area by area** (from its help contents of about 1,000 pages and its 103 Python inspections, checked against the code on 30 October 2026; areas not compared yet are at the end)
- Python console: Not built: Attach Debugger with an interpreter elsewhere than this computer (WSL, Docker, SSH); the history list is kept while PyMagic runs, not across restarts (item 317).
- Compared on 1 November 2026 (item 224; what was "Not compared yet"), PyMagic had none of these unless said:
  - HTTP client (item 313): Not built: PyCharm's newer extras: `jsonPath`, `client.global` kept across restarts, WebSocket, GraphQL and gRPC requests.
  - Run Anything and the Services window (item 314): one member of a compound at a time is debugged (the Run panel has one debug session; PyCharm opens a debugger tab per member).
  - Deployment (item 324): Not checked: implicit FTPS and a trusted self-signed certificate against a real FTPS server (checked with a small FTPS server of the tests).
  - Remote interpreters over SSH and Docker in depth (item 325): Not checked: a Compose service's libraries against a real Docker engine (checked with recorded docker answers).


### Different from PyCharm on purpose

Not work left: choices made and kept, noted where they were made.

- Left as it is: ruff writes `from m import B as b` before `from m import a` (by name), PyMagic writes the plain one first.
- Measured and left out: keeping the types of unchanged files across edits (the files they depend on are only about a fifth of an edit's time) and parsing only the edited region (10 ms of a 450 ms check); the peak while the problems are computed is still about 2.6 GB.
- New project: unlike PyCharm, a project made with a venv lists the packages of its kind (FastAPI, Flask, Jupyter) in requirements.txt without installing them; Django is installed, so that its own startproject writes the project, and with uv every kind's packages are added with uv add (items 285 and 287).
- Editor menu: Left as they are: *Paste as Plain Text* (every paste into the editor is plain already) and submenus in the editor's right-click menu (Monaco's menu has none; Refactor… and Generate… open PyMagic's own menus, which have them)
- Diagrams: Simpler than PyCharm's: no editing, notes or other layouts, and a model's relations come from its own fields.
- Open Project: no Attach (a second project folder in the same window): the user does not want it; a project opens in this window or a new one (item 301).
- Debugger: left on purpose: attaching to programs of Python 3.13 and earlier, which have no `sys.remote_exec` (PyCharm injects native code into the process; antivirus programs such as Kaspersky quarantine programs that do, so PyMagic attaches to 3.14 and later only)
- Editor: PyMagic's keys differ where PyCharm's are already taken here: next / previous method is Ctrl+Shift+Down / Up (Alt+Down moves the line), the block's start / end is Alt+[ / Alt+] (Ctrl+[ indents)
- Refactoring: Simpler than PyCharm's: Introduce Parameter gives the parameter a default instead of passing the value at each call, Invert Boolean works on variables (not on functions that return one), and moved members do not bring the imports they need.
- Search: Simpler than PyCharm's: the conditions on a variable are a regular expression on its text (no type or count conditions), and duplicates are compared statement by statement.
- Inspections PyMagic has and PyCharm does not (items 341 and 348): *Patch target that does not exist*, *Attribute a mock does not have* and *Name defined in a later cell*, kept; *Incorrect number of parameters for @patch decorated function* reports too few parameters only, since pytest fills extra ones with fixtures (PyCharm also reports too many).
- JSON Schemas (items 360 and 364): no *Load SchemaStore Mappings*: PyCharm downloads SchemaStore's schemas when asked; PyMagic bundles SchemaStore's catalog and its own schemas (up to 500 KB each) and uses them at once; the catalog's entries whose schema is on another site are not used, and a mapping can name a schema file of the project.
- Project tree (item 361): no *Jupyter Connection* under New (a notebook chooses its Jupyter server in its own toolbar) and no *Repair IDE on File* (PyMagic keeps no per-file index to repair; Reload from disk reads the file again).
- Python inspections that PyCharm has and PyMagic does not: Left on purpose: advertising stub packages that could be installed, since PyMagic ships typeshed's third-party stubs and already has those types.

## Work done

The 371 steps built so far, each with its notes, are in [work_done.md](work_done.md). What is left is in [Open work](#open-work).

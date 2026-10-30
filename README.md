# PyMagic

An independent Python IDE engine, modelled on the *behaviour* of PyCharm's Python front-end
(documented from analysis of the shipped product). No JetBrains code is copied; the engine is
written from scratch in C#.

## Stack

| Layer | Tech | Why |
|---|---|---|
| Engine (lexer, parser, resolve, types) | C# / .NET 9 (`src/PyMagic.Core`) | Fast, strongly typed, good for compiler-style code (immutable trees, pattern matching) |
| Server | C# LSP server (`src/PyMagic.Server`, stdio, no dependencies) | One protocol, any editor |
| Host | ASP.NET Core minimal app (`src/PyMagic.Web`) | Serves the UI, a workspace file API and one in-process language server per browser tab over WebSocket |
| UI | JavaScript + Monaco (`web/`, bundled with esbuild) | Mature editor component; Electron/Tauri shell later |

## Try it on your PC

PyMagic is a Windows app: an Electron window (`desktop/`, plain JavaScript) that starts the C# engine in the background. Double-click `PyMagic.cmd`. It needs the .NET 9 SDK and Node.js. The first run installs the packages; every start builds the editor and the engine when they changed, then opens the project you used last (or `examples\demo`). *File > Open Folder...* (Ctrl+Shift+O) switches project, `PyMagic.cmd C:\my\project` opens a folder directly. PyCharm's typeshed is used when PyCharm is installed.

**From Visual Studio** (or `dotnet build` / `dotnet run --project src/PyMagic.Web`): building the engine now builds the page too (it runs `node build.mjs` in `web`, and `npm install` the first time; the build only warns when Node.js is missing, and `-p:SkipPageBuild=true` skips it), and the *PyMagic.Web* launch profile opens `http://127.0.0.1:5180/` on `examples\demo`. Before this the page came from `web/dist`, which git does not keep, so a build in Visual Studio could serve an old page or none. The status bar shows which build is running. To get a copy that needs nothing installed, run `node desktop/package.mjs` (needs internet once, for the .NET runtime package and the installer tools): it builds `desktop\out\PyMagic-win32-x64\PyMagic.exe` (about 470 MB, with Electron and the .NET runtime inside), which can be copied to any Windows PC, and an installer, `desktop\out\PyMagic-Setup-0.1.0.exe` (about 150 MB), that installs PyMagic for the current user with a Start menu entry and a desktop shortcut (`--no-installer` skips it).

`PyMagic-web.cmd` (or `start.ps1`) runs the same editor in a browser tab instead.

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
                          corpus check:  dotnet tests/PyMagic.Tests/bin/Debug/net9.0/PyMagic.Tests.dll corpus <dir>
```

The test project is a plain console runner because this machine's NuGet only has an offline source
(no xunit). Switch to xunit once nuget.org is configured.

## Open work

Everything that is known to be left, in one place, gathered on 30 October 2026 from the "Not done" and "Still open" notes of items 2 to 154 (the item a line comes from is in brackets), and from PyCharm's own dialogs and menus (what PyMagic's notes never named). **How it is kept:** when an item does one of these, the line goes from here and the item says so; a new item's own "Not done" note is added here too. Code signing, auto-update and anything else for selling PyMagic are left out on purpose.

**Frameworks**
- Django admin: `list_display` entries that are callables from other modules, and `get_queryset` / `formfield_for_*` overrides (37).
- Django forms: `clean_<field>` methods and `cleaned_data` keys (37).
- Django templates: the context `{% include ... with a=b %}` gives the included file, the project's context processors, `{% blocktrans %}` bodies, the tags of third-party libraries (`crispy`), and the HTML itself (35).
- Django project: reading `INSTALLED_APPS` (an app is a folder that looks like one), choosing the settings module for commands that need a database (76); the custom managers of other installed apps (125); reverse relations in the project-wide Problems scan, which therefore skips some prefetch names (154).
- REST framework: `DEFAULT_ROUTER` extras (`.json` suffixes, the API root), the routes of other installed apps' urls (`allauth`, `rest_framework.authtoken`) (127); `to_representation` overrides, writable nested serializers without `create()`, the serializer per method in the detail of `@action` routes (130).
- FastAPI: `Security` scopes checked against the OAuth2 scheme, `Annotated[X, Depends(...)]` navigation from the annotation, request-body rules (a body on a `GET`), OpenAPI output, sending a request to a running server from the Endpoints detail (34, 126).
- Flask: the `request.args` names a view reads, checked against its template (128).
- Jinja: `{% include %}` of a list or a variable, line statements (`# for x in y`), the expressions beyond brackets and strings (`{{ a b }}`) (129).
- pytest: `request.param` of `parametrize(..., indirect=True)` (151), fixtures registered by hooks at run time (134), a fixtures panel and a run icon in the gutter (29).

**Editor**
- Hover and signature help: a parameter's documentation in hover and in completion's `name=` items, the class docstring for a constructor call, `name=` after a `*`, documentation of methods of library base classes (119, 121); signature help inside decorators and class bases (118).
- Completion: dictionary keys and `Literal[...]` strings, a setting to leave out the call parentheses (31).
- Find in files: replacing in the selected results only, multi-line patterns, searching library files, a list of recent searches, filtering symbols by kind (20).
- Notebooks: formatting a selection of a cell, formatting on save, cells with magics (120).
- Refactoring: choosing which files or hunks of a preview to apply, word-level highlighting in the diff (19); a module reached through `from pkg import *` and imports written over lines with backslashes are not followed by a move (143).
- Unresolved imports: asking PyPI whether a suggested package exists, an install command for other LSP clients, remembering "ignore this import" (15).
- Language server: semantic tokens, cancelling a request, UTF-8 position encoding (8).
- Formatter: the import settings of ruff and isort not read yet: `lines-after-imports`, `section-order`, `force-wrap-aliases`, `from-first`, `relative-imports-order`, `split-on-trailing-comma` (44).

**Run, debug and tests**
- Debugger: pausing a running program, attaching to a running process, breakpoints added while a frame is already running (they only count in new calls) (10); the stacks of other threads (76); setting attributes and items of objects, watches that expand, exception filters by type, function and data breakpoints (21).
- Test runner: debugging a test, stopping a test run, `unittest` subtests as their own nodes, tests in classes whose bases live in other files (11).
- Coverage: branch coverage, combining runs, exporting a report, running one test with coverage (52).

**Git, packages and interpreters**
- Git: pushing tags (72); choosing lines from the editor's gutter, moving single lines between changelists (73); a merge result built from three editors, picking lines inside one side, conflicts of binary files (116); interactive rebase's *edit*, `--rebase-merges`, rebasing onto another branch, reordering by dragging (117).
- Packages: extra index URLs and proxies, installing from a path or a git URL (refused until there is a confirmation step), version pickers, dependency trees, lock files (uv, poetry, pip-tools), conda or uv-managed environments (14).
- Remote interpreters: a terminal inside WSL or Docker, Linux paths in tracebacks, a Docker container that is already running (71).

**Installer**
- A checkbox for the desktop shortcut and a safe folder page (they need NSIS script), PyMagic's own pictures in the wizard (66).

**Type engine** (ibnbolbna's area; the order is in [issue #3](https://github.com/residentcode/pymagic/issues/3))
- `NoReturn` calls, `with` blocks that swallow exceptions, `break`/`return` through `finally` (4, 5); call-site parameter inference (5).
- Overrides: a rename that follows overriding methods, a change of signature passed on to them, a call hierarchy that includes them, marks and hierarchy for library classes (8, 17, 30).
- Narrowing: the types of the names that `match` patterns bind (`case int() as n`, captures, `*rest`; item 157 narrows the tuple items by their sub-patterns), the "at least n" tuple form for `len(t) >= n` (152); `Literal` through tuples (40).
- attrs and hooks: `attr.s` options such as `these=`, `__init_subclass__` hook parameter types (42).
- Small gaps: `pop` of a required TypedDict key (87), a constant index past the end of a tuple subclass (88).
- The PyCharm inspections that need full types and are not done yet, and those for other tools (pandas, Spark, mock, notebooks) (47, 77).
- Speed on large projects: find usages and rename on one thread, the Problems of unchanged files not remembered, parsing only the edited region, about 1 to 1.5 GB while the problems are computed (78). Keeping the types of unchanged files across edits was measured and left out: the files they depend on are only about a fifth of an edit's time.

**Compared with PyCharm** (its own dialogs and menus, from screenshots of 30 October 2026; none of this was in any item's notes)
- New project: a *New Project* dialog with a location and project types (pure Python, FastAPI, Flask, Django, Jupyter, each with its starting files), *Create Git repository*, *Create a welcome script*, and the interpreter made with it: a project venv, uv (with the path to uv), base conda or a custom environment, and a Python version. PyMagic only opens a folder and makes a `.venv` from the Python panel.
- Run configurations: only "a script" exists (name, script, parameters, folder, variables). Missing: kinds of configuration (Django server with host and port, pytest, a module with `-m`, FastAPI/uvicorn and Flask servers), `.env` files, running with `uv run`, the PYTHONPATH switches (content and source roots), *Store as project file*, *Open the Run panel when started*, and *Edit Configurations...* from the run selector.
- Project tree menu: still missing *Debug* the tests of a folder (debugging a test is not done at all) and *Local History* of a folder; a sources root unmarked, or a folder marked, takes effect by starting the page again (it does so by itself) (155, 156).
- Tab menu: *Override File Type* colours the file but the language server still reads it by its name; a second window (*Open in a new window*) does not share unsaved text with the first, so the file is saved before it opens (155, 156).
- Editor menu: still missing *Paste as Plain Text* (every paste into the editor is plain already); *Generate…* has no getters for public attributes and no `__eq__` / `__repr__` from the fields, and its test calls the class without arguments; the entries are flat where PyCharm has submenus (Monaco's menu has none), and *Refactor…* and *Generate…* are on the first editor only (155, 156).
- Python console: an interactive interpreter beside the editor, with *Execute Line in Python Console* (Alt+Shift+E), *Run File in Python Console* and a view of its variables. PyMagic has the Terminal and notebooks, not a console.
- Terminal: the prompt does not show the virtual environment (`(.venv) PS ...`): the environment is active (its folder first on the PATH, `VIRTUAL_ENV` set) but its activation script is not run.
- Diagrams: of classes, modules and Django models.

**Django compared with PyCharm** (from PyCharm's Django help pages and its 17 Django inspections, checked against the code on 30 October 2026)
- Run configurations: a *Django server* configuration (host, port, extra options, working folder, environment variables, `.env` files, open the browser, test server, no reload, debug only my code, log files in the console, tasks before launch); a *Django tests* configuration (a target: an app, a test case or one test; a custom settings file; options such as `--failfast`; variables; results in the test tree). Today the Django tab has Run server, Debug server and `test` buttons without settings, and the test output is plain text.
- manage.py: a console for it (Ctrl+Alt+R) with command history, completion of commands and their options, and quick documentation; a *Django console*, a Python console with Django set up. Today there are buttons and one line for other arguments, and `shell` runs as a plain command.
- Settings: a Django settings page (Django support on or off, project root, settings file, manage.py script, environment variables, not using Django's test runner); *Mark Directory As → Template folder* and resources, folders in `TEMPLATES['DIRS']` marked by themselves, template languages per file type.
- New Django project: location, Git, environment, template language, templates folder, first app's name, admin on or off.
- Django Structure window: models, admin classes and views, grouped by type or by app; create an app, a model or a view; makemigrations and migrate per app; register a model in the admin; from an admin class to its model.
- Logical structure of a file: models with fields and relations, serializers (nested too), views with their endpoints, admins, forms and signal hooks (`pre_save`, `post_delete`); create a missing ModelAdmin or serializer.
- Model dependency diagram: of the project or one model, with notes, layout, zoom, going to the source, find usages and refactoring.
- Endpoints tab: a generated HTTP request that can be sent while the server runs, the OpenAPI specification, the view's documentation, client code examples, filters, and endpoints from libraries and tests. Today it is a list.
- Templates:
  - breakpoints in `.html` templates, with their variables in the debugger;
  - a live preview beside the editor through the template's URL (URL parameters, back and forward, Refresh);
  - margin icons from a view to its template and from a template to every view that renders it;
  - creating a missing template from its name (Alt+Enter), in a chosen templates folder;
  - live templates you can see and edit (today block tags complete as fixed snippets);
  - completion and checks of the HTML, CSS and JavaScript inside a template.
- Inspections: Django ORM error (an ORM method that cannot be called there); `{% endblock name %}` whose name is not its block's; a broken `#}` comment in Django templates (checked only in Jinja today); a comparison without spaces in `{% if %}`; unresolved Django 6 partial names (`{% partialdef %}`); django-constance keys unresolved or missing from the fieldsets.
- Code insight: an intention that turns a function-based generic view into a class-based one; dotted paths in settings (`INSTALLED_APPS`, `MIDDLEWARE`, `AUTH_USER_MODEL`, `ROOT_URLCONF`) checked and followed.

**Compared with PyCharm's help, area by area** (from its help contents of about 1,000 pages and its 103 Python inspections, checked against the code on 30 October 2026; areas not compared yet are at the end)
- Debugger:
  - inline values: ~~each variable's value shown at the end of its line while stopped, updated on each step~~ (done, item 158); ~~a click on them to view a value, see it as a table or add a watch~~ (done, item 159); still open: changing a value from there;
  - stepping: ~~Step Into My Code (library code is skipped unless asked), Smart Step Into, Run to Cursor, Force Run to Cursor, Jump to Cursor, Show Execution Point~~ (done, item 158); ~~Force Step Into / Over~~ (done, item 159);
  - breakpoints: ~~mute all; exception breakpoints per exception class, on raise, on termination (uncaught), only in the project's files~~ (done, item 158); ~~disabled until another breakpoint is hit, a description, method (function) breakpoints on a `def` line, dragging a breakpoint to another line~~ (done, item 159); still open: suspend one thread or all;
  - variables: ~~return values of the functions just left, set a value (double-click), Quick Evaluate (Ctrl+Alt+F8), hover values~~ (done, item 158); ~~F2 for set value, compare a value with the clipboard, a dialog for one value, Evaluate (Alt+F8) as a non-modal window for several lines~~ (done, item 159); still open: go to a variable's declaration or its type, hover values that open into their children, JSON shown as a tree;
  - ~~watches kept with the project, paused, duplicated, edited and reordered, added from the editor's menu, the Variables pane or by dropping text~~ (done, item 159);
  - ~~Data View: lists, dicts, NumPy arrays, DataFrames and Series as tables~~ (done, item 159);
  - a debug console with IPython; attach to a running process; the variables loading policy (on demand for slow objects); ~~copying the call stack~~ (done, item 158).
  - found while checking item 158: this Monaco build does not draw text injected after a line (`after` decorations), so the Git blame shown at the end of the current line (`git.js`) is probably not drawn either; item 158 draws its values with a CSS `::after` instead.
- Code vision and inlay hints: the number of usages above each function and class and the number of inheritors above each class (a click lists them); the author of most lines; parameter names in calls, the names of `pytest.mark.parametrize` arguments, return types, inferred types of variables, the attribute names of positional class patterns in `match`, and the type of each call in a method chain.
- Editor: breadcrumbs; method separators; the next / previous method (Alt+Down / Alt+Up); the matching brace (Ctrl+Shift+M) and the start / end of a block (Ctrl+[ / Ctrl+]); the last edit location (Ctrl+Shift+Backspace) and recent locations; the switcher (Ctrl+Tab over files and tool windows); a preview tab; tabs taken out into their own window; lens mode (hovering the scrollbar shows that part of the file); context info (Alt+Q); expression type (Ctrl+Shift+P); external documentation in the browser (Shift+F1); a RegExp checker on a regular expression (Alt+Enter); zoom with Ctrl+wheel.
- Code generation: create a class, function or method from its first use (Alt+Enter on an unresolved name); Surround With (`if`, `try`, `for`, `with`); Unwrap / Remove (Ctrl+Shift+Delete); postfix completion (`.if`, `.not`, `.for`, `.return`, `.par`); live templates you see and edit; file templates for new files; Emmet in HTML.
- Refactoring: Introduce Parameter, Introduce Attribute (field), Extract Superclass, Pull Members Up, Push Members Down, Encapsulate Fields, Invert Boolean, Convert to Package / Module, Make Function Top-Level, Move Static Members, Extract Delegate.
- Search: Structural Search and Replace (find code by its shape, with templates and constraints); finding duplicated code; analysing a stack trace pasted from elsewhere (its lines become links).
- Tests: doctest, nose and Twisted Trial; test statistics and export of the results to a file; the history of earlier runs; rerunning automatically after each change; installing a missing test library from a quick fix.
- Coverage: branch coverage in the line popup; going to the next / previous uncovered lines; coverage per file in the project tree; coverage suites and reports (Ctrl+Alt+F6).
- Python console: an interactive interpreter beside the editor that runs a line or a selection from the editor (Alt+Shift+E), with completion, a variables view, its history (Ctrl+Alt+E), a starting script, several consoles, IPython, a command queue and Attach Debugger. Today the Terminal runs `python`, with none of this.
- Notebooks: debugging a cell; a Variables window after each run; Run Section for a Markdown heading; merge and split cells, cell tags; the Jupyter server window (local or remote), the server selector and a Jupyter console per notebook; Markdown headings in the Structure window; opening the notebook in a browser.
- Git: shelve, changelists (grouping changes before a commit), the Log tab with its graph of commits, comparing two branches, pull requests and merge requests (GitHub, GitLab) reviewed and merged inside the editor, a toolbar on a change marker to revert it or show its diff.
- Python inspections that PyCharm has and PyMagic does not: code incompatible with chosen Python versions; deprecated functions, classes and modules; a class without `__init__`; a decorator meant for classes used outside one; a fixture not requested by any test; wrong arguments in `@pytest.mark.parametrize`; the parameter count of a function decorated with `@patch`; incorrect variance; inheritance from classes with disjoint bases; `ClassVar`, `NewType`, `Final`, new-style type parameters and type aliases misused; a redeclared type alias; protocols defined or used wrongly; an invalid `match` pattern; a problematic nesting of decorators; a redundant boolean check; a suspicious boolean condition; suspicious relative imports; a cast between unrelated types and an unnecessary cast; `typing.assert_type`; a type in a docstring that does not fit the inferred one; a missing or empty docstring and a single-quoted one; a missing type hint; a file without an encoding given to `open`; requirements not installed and outdated Poetry versions; stub packages that do not fit or that could be installed; a string conversion without a dunder method.
- Not compared yet: Database tools and SQL, the HTTP client, deployment, remote interpreters over SSH and Docker in depth, the profiler, the inspection list beyond Python and Django, intention actions one by one, Local History, scratch files, language injections, spelling and grammar, Markdown and reStructuredText, macros, Run Anything and the Services window.

**To check** (old notes that later work may have covered; each needs a look before it is counted as open or done)
- Parser: store-context validation and finer error messages (2). Scopes: `__all__`, `try/except ImportError` fallbacks, definitions under conditions (3).
- Typeshed: `__init__.pyi` re-exports of `import a as a`, `__all__ +=`, overloads with an explicit `self`, a cache of parsed stubs on disk (7).
- Black's last corner cases: parentheses in `print((1))`, `with` and `await`, `# type:` comment placement, blank lines inside brackets (22, 43, 75).
- Automated tests of the page itself (the checks in a real browser are scripted by hand for now) (9).

## Work done

The 159 steps built so far, each with its notes, are in [work_done.md](work_done.md). What is left is in [Open work](#open-work).

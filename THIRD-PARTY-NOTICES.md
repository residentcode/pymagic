# Third-party notices

PyMagic's own code is under the license in [LICENSE](LICENSE). It contains, and is built with, software of others,
which keeps its own license. Nothing in PyMagic's license limits what these licenses allow for these parts.

## In this repository

| Part | Where | License |
|---|---|---|
| typeshed (the standard library's and third-party packages' type stubs) | `typeshed/` | Apache License 2.0, see [typeshed/LICENSE](typeshed/LICENSE) |
| SchemaStore's catalog and schemas | `src/PyMagic.Server/SchemaStore/` | Apache License 2.0, see [LICENSE.txt](src/PyMagic.Server/SchemaStore/LICENSE.txt) |
| JSON Schemas of other projects: compose-spec, OpenAPI, AsyncAPI, kubernetes-json-schema (Apache License 2.0); GitLab CI, tox, Mermaid (MIT License); the JSON Schema meta-schemas (BSD 3-Clause License); the Development Containers specification (CC BY 4.0) | `src/PyMagic.Server/Schemas/` | each under its own project's license: which file is under which, and where it comes from, in [Schemas/README.md](src/PyMagic.Server/Schemas/README.md); the license texts in [Schemas/LICENSES](src/PyMagic.Server/Schemas/LICENSES) |
| The Gherkin languages of Cucumber | `src/PyMagic.Server/Gherkin/` | MIT License, see [LICENSE.txt](src/PyMagic.Server/Gherkin/LICENSE.txt) |

## Fetched when PyMagic is built (not in this repository)

| Part | License |
|---|---|
| Qwen2.5-Coder-0.5B (full line completion), fetched by `models/fetch.mjs` | Apache License 2.0, see [models/NOTICE.md](models/NOTICE.md) |
| Phpactor 2026.10.07.0 (PHP's code insight), fetched by `phpactor/fetch.mjs` (its phar holds its own dependencies, each under its license) | MIT License, see [phpactor/LICENSE](phpactor/LICENSE) |
| Monaco Editor | MIT License |
| xterm.js and its fit add-on | MIT License |
| Mermaid | MIT License |
| emmet-monaco-es | MIT License |
| yaml | ISC License |
| Tailwind CSS (build only) | MIT License |
| esbuild (build only) | MIT License |
| Electron | MIT License (Chromium and Node.js inside it keep their own licenses, listed in the `LICENSES.chromium.html` Electron puts in the built app) |
| @electron/packager (build only) | BSD 2-Clause License |
| electron-builder (build only) | MIT License |
| SSH.NET | MIT License |
| LLamaSharp and LLamaSharp.Backend.Cpu (with llama.cpp) | MIT License |
| .NET runtime and ASP.NET Core | MIT License |

The page is built from these and from what they depend on (126 npm packages when this was written, nearly all under the MIT, ISC, Apache 2.0
or BSD licenses). Two of them offer a choice, and PyMagic takes:

- **DOMPurify** (in Mermaid) under the **Apache License 2.0** (it is offered under MPL 2.0 or Apache 2.0).
- **elkjs** (Mermaid's ELK layout) under the **Eclipse Public License 2.0** (it is offered under EPL 2.0 or GPL 3.0 or later). Its source is at
  https://github.com/kieler/elkjs; PyMagic carries it unchanged, inside its bundled `mermaid.js`.

The built app (and its installer) has a `licenses` folder beside its other resources: PyMagic's LICENSE and this file, the license text of
every npm package the page is built from, of the bundled schemas, and of the NuGet packages and .NET (kept in [licenses](licenses) here).
typeshed's and the model's are in their own folders of the app, and Electron puts its own and Chromium's (`LICENSE`,
`LICENSES.chromium.html`) beside PyMagic.exe.

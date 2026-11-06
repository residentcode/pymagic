# Third-party notices

PyMagic's own code is under the license in [LICENSE](LICENSE). It contains, and is built with, software of others,
which keeps its own license. Nothing in PyMagic's license limits what these licenses allow for these parts.

## In this repository

| Part | Where | License |
|---|---|---|
| typeshed (the standard library's and third-party packages' type stubs) | `typeshed/` | Apache License 2.0, see [typeshed/LICENSE](typeshed/LICENSE) |
| SchemaStore's catalog and schemas | `src/PyMagic.Server/SchemaStore/` | Apache License 2.0, see [LICENSE.txt](src/PyMagic.Server/SchemaStore/LICENSE.txt) |
| JSON Schemas of other projects (compose-spec, GitLab CI, Kubernetes, the JSON Schema meta-schemas and others) | `src/PyMagic.Server/Schemas/` | each under its own project's license; where each comes from is in [Schemas/README.md](src/PyMagic.Server/Schemas/README.md) |
| The Gherkin languages of Cucumber | `src/PyMagic.Server/Gherkin/` | MIT License, see [LICENSE.txt](src/PyMagic.Server/Gherkin/LICENSE.txt) |

## Fetched when PyMagic is built (not in this repository)

| Part | License |
|---|---|
| Qwen2.5-Coder-0.5B (full line completion), fetched by `models/fetch.mjs` | Apache License 2.0, see [models/NOTICE.md](models/NOTICE.md) |
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

Their license texts come with each package (in `node_modules`, the NuGet cache, and the built app).

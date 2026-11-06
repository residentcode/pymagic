# Built-in JSON Schemas

The schemas PyMagic checks config files against, built into the app (no download, no question). Fetched 2026-11-02 from:

| File | From |
|---|---|
| package.json | https://json.schemastore.org/package.json |
| pyproject.json | https://json.schemastore.org/pyproject.json |
| workflow.json | https://json.schemastore.org/github-workflow.json |
| compose.json | https://raw.githubusercontent.com/compose-spec/compose-go/master/schema/compose-spec.json |
| gitlab.json | https://gitlab.com/gitlab-org/gitlab-foss/-/raw/master/app/assets/javascripts/editor/schema/ci.json |
| precommit.json | https://www.schemastore.org/pre-commit-config.json |
| mkdocs.json | https://www.schemastore.org/mkdocs-1.6.json |
| dependabot.json | https://www.schemastore.org/dependabot-2.0.json |
| codecov.json | https://www.schemastore.org/codecov.json |
| tox.json | https://raw.githubusercontent.com/tox-dev/tox/main/src/tox/tox.schema.json |
| openapi.json | https://spec.openapis.org/oas/3.0/schema/2021-09-28 |
| openapi31.json | https://spec.openapis.org/oas/3.1/schema/2022-10-07 (fetched 2026-11-03) |
| kustomization.json | https://json.schemastore.org/kustomization.json (fetched 2026-11-03) |
| swagger.json | https://raw.githubusercontent.com/OAI/OpenAPI-Specification/main/_archive_/schemas/v2.0/schema.json |
| k8s-definitions.json | https://raw.githubusercontent.com/yannh/kubernetes-json-schema/master/master/_definitions.json (every built-in Kubernetes kind; the schema of one kind is a $ref into it) |
| asyncapi2.json | https://raw.githubusercontent.com/asyncapi/spec-json-schemas/master/schemas/2.6.0.json (fetched 2026-11-04, as all below) |
| asyncapi3.json | https://raw.githubusercontent.com/asyncapi/spec-json-schemas/master/schemas/3.0.0.json |
| devcontainer.json | https://raw.githubusercontent.com/devcontainers/spec/main/schemas/devContainer.base.schema.json |
| github-action.json, babelrc.json, eslintrc.json, jest.json, jsconfig.json, tsconfig.json, prettierrc.json, stylelintrc.json, tslint.json, tsd.json, typings.json | https://www.schemastore.org/ and the file's name |
| mermaid.json | https://mermaid.js.org/schemas/config.schema.json (served without its `required` lists: it describes the configuration with its defaults filled in, a config file sets only what it changes) |
| jsonschema-4.json, jsonschema-6.json, jsonschema-7.json | https://json-schema.org/draft-04/schema, draft-06/schema, draft-07/schema |
| jsonschema-2019-09.json and jsonschema-2019-09-*.json | https://json-schema.org/draft/2019-09/schema and its meta/core, applicator, validation, meta-data, format, content |
| jsonschema-2020-12.json and jsonschema-2020-12-*.json | https://json-schema.org/draft/2020-12/schema and its meta/core, applicator, unevaluated, validation, meta-data, format-annotation, content |
| http-client-env.json, http-client-private-env.json, markdown-frontmatter.json | written for PyMagic (JetBrains' own are not published): the HTTP Client's environment files, and the usual keys of Markdown front matter |

To bring one up to date, download it again over its file here and rebuild.

## Their licenses

Each schema keeps the license of the project it comes from; the texts are in [LICENSES](LICENSES), fetched 2026-11-06 from each project.

| Files | Project | License | Text |
|---|---|---|---|
| package, pyproject, workflow, precommit, mkdocs, dependabot, codecov, kustomization, github-action, babelrc, eslintrc, jest, jsconfig, tsconfig, prettierrc, stylelintrc, tslint, tsd, typings | SchemaStore (https://github.com/SchemaStore/schemastore) | Apache License 2.0 | [../SchemaStore/LICENSE.txt](../SchemaStore/LICENSE.txt) |
| compose | compose-spec / compose-go (https://github.com/compose-spec/compose-go) | Apache License 2.0 | [LICENSES/compose-spec.txt](LICENSES/compose-spec.txt) |
| gitlab | GitLab FOSS (https://gitlab.com/gitlab-org/gitlab-foss), the MIT part | MIT License | [LICENSES/gitlab.txt](LICENSES/gitlab.txt) |
| tox | tox (https://github.com/tox-dev/tox) | MIT License | [LICENSES/tox.txt](LICENSES/tox.txt) |
| openapi, openapi31, swagger | OpenAPI Specification (https://github.com/OAI/OpenAPI-Specification) | Apache License 2.0 | [LICENSES/openapi.txt](LICENSES/openapi.txt) |
| k8s-definitions | kubernetes-json-schema (https://github.com/yannh/kubernetes-json-schema), made from Kubernetes' own API (Apache License 2.0) | Apache License 2.0 | [LICENSES/kubernetes-json-schema.txt](LICENSES/kubernetes-json-schema.txt) |
| asyncapi2, asyncapi3 | AsyncAPI spec-json-schemas (https://github.com/asyncapi/spec-json-schemas) | Apache License 2.0 | [LICENSES/asyncapi.txt](LICENSES/asyncapi.txt) |
| devcontainer | Development Containers specification (https://github.com/devcontainers/spec), by the Dev Container Spec authors; not changed | Creative Commons Attribution 4.0 International | [LICENSES/devcontainers.txt](LICENSES/devcontainers.txt) |
| mermaid | Mermaid (https://github.com/mermaid-js/mermaid); served without its `required` lists, as said above | MIT License | [LICENSES/mermaid.txt](LICENSES/mermaid.txt) |
| jsonschema-* | JSON Schema specification (https://github.com/json-schema-org/json-schema-spec) | BSD 3-Clause License | [LICENSES/json-schema.txt](LICENSES/json-schema.txt) |
| http-client-env, http-client-private-env, markdown-frontmatter | written for PyMagic | PyMagic's [LICENSE](../../../LICENSE) | |

Renovate's schema is not built in: its project is under the GNU AGPL 3.0, whose terms do not fit PyMagic's license. A project that wants its
renovate.json checked puts a copy of the schema in its own `.pymagic/schemas/renovate.json`, which PyMagic then uses.

SchemaStore's catalog and schemas are in ../SchemaStore (schemas.zip, index.json, LICENSE.txt), from https://github.com/SchemaStore/schemastore (src/schemas/json, every file up to 500 KB) and https://www.schemastore.org/api/json/catalog.json, fetched 2026-11-04; they are Apache License 2.0.

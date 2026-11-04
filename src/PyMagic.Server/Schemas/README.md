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
| renovate.json | https://docs.renovatebot.com/renovate-schema.json |
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

SchemaStore's catalog and schemas are in ../SchemaStore (schemas.zip, index.json, LICENSE.txt), from https://github.com/SchemaStore/schemastore (src/schemas/json, every file up to 500 KB) and https://www.schemastore.org/api/json/catalog.json, fetched 2026-11-04; they are Apache License 2.0.

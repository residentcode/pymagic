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
| swagger.json | https://raw.githubusercontent.com/OAI/OpenAPI-Specification/main/_archive_/schemas/v2.0/schema.json |
| k8s-definitions.json | https://raw.githubusercontent.com/yannh/kubernetes-json-schema/master/master/_definitions.json (every built-in Kubernetes kind; the schema of one kind is a $ref into it) |

To bring one up to date, download it again over its file here and rebuild.

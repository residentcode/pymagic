// package.json, pyproject.toml, GitHub workflows, docker-compose, GitLab CI, pre-commit, mkdocs, dependabot, codecov, tox, Renovate, OpenAPI 3.1 / 3.0 / Swagger 2.0,
// Kustomization and Kubernetes manifests checked against their JSON Schema; a custom resource against the CustomResourceDefinition of the project that declares it.
// The schemas are built into PyMagic (a project's own copy in .pymagic/schemas is used instead): nothing is downloaded and nothing is asked (schema-model.js validates).
// A Kubernetes manifest is any YAML file with an apiVersion and a kind (several documents in one file are each checked); its schema is the one of that kind.
import * as monaco from 'monaco-editor';
import { parse as parseYaml, parseAllDocuments } from 'yaml';
// Which schema a file gets, as PyCharm decides it: none when the project ignores the file, else the project's own mapping (status bar,
// "Schema: ..."), else the file's "$schema" (or YAML's `# yaml-language-server: $schema=`), else its name. A schema's $refs into another
// built-in document (AsyncAPI's of JSON Schema 7, the 2020-12 meta-schema's vocabularies) are loaded with it.
import { validate, locate, schemaOf, openapiSchemaOf, asyncapiSchemaOf, looksKubernetes, kubernetesSchemaOf, crdSchemas, crdKeyOf, parseJsonc, frontMatterOf, chooseSchema, declaredSchema, yamlModeline, externalRefs } from './schema-model';

export { schemaOf };

const OWNER = 'pymagic-schema';

/** The files whose schema is looked for: JSON, YAML, TOML and Markdown, and the dotfiles a schema is known for by name (.babelrc, .eslintrc). */
export const SCHEMA_FILES = /\.(json|jsonc|ya?ml|toml|md|markdown)$/i;

export function setupSchemaCheck(deps) {
    const kept = new Map();               // name -> parsed schema, or null when PyMagic has none of that name (a kind Kubernetes does not have)
    const timers = new Map();
    const choices = new Map();            // a model -> the schema chosen for it: {label, choice} (the status bar's "Schema: ...")

    // the built-in schemas with their titles and $ids, and the project's mappings and ignored files (read again when they are changed)
    let catalog = null;
    const catalogNow = () => (catalog ??= fetch('/api/schemas/list').then((r) => (r.ok ? r.json() : null)).catch(() => null).then((c) => c ?? { registered: [], ids: {}, store: [] }));
    let project = null;
    const projectNow = () => (project ??= fetch('/api/schemas/mappings').then((r) => (r.ok ? r.json() : null)).catch(() => null).then((p) => p ?? { mappings: [], ignored: [] }));

    // a schema the project keeps itself (a mapping's file:, a relative "$schema"), read again after a save
    const files = new Map();
    function fileSchema(path) {
        if (!files.has(path)) files.set(path, fetch('/api/schemas/file?path=' + encodeURIComponent(path)).then((r) => (r.ok ? r.json() : null)).catch(() => null));
        return files.get(path);
    }

    /** The other built-in documents a schema $refs (by $id), loaded before it is used: validate's registry. */
    async function registryFor(schema) {
        const { ids } = await catalogNow();
        const found = new Map();
        const pending = [...externalRefs(schema)];
        while (pending.length && found.size < 40) {
            const id = pending.pop();
            if (found.has(id) || !ids[id]) continue;
            const doc = await schemaFor(ids[id]);
            found.set(id, doc);
            if (doc) pending.push(...externalRefs(doc));
        }
        return found.size ? (id) => found.get(id) ?? null : null;
    }

    function titleOf(name, registered, store = []) {
        return registered.find((r) => r.name === name)?.title ?? store.find((s) => s.name === name)?.title ?? name;
    }

    // the custom resources' schemas, from the CustomResourceDefinitions in the project's YAML files (read again after a save)
    let crds = null;
    async function crdSchemaFor(data) {
        const key = crdKeyOf(data);
        if (!key) return null;
        crds ??= fetch('/api/schemas/crds').then((r) => (r.ok ? r.json() : [])).catch(() => []).then((files) => {
            const docs = [];
            for (const f of files) {
                try { for (const d of parseAllDocuments(f.text, { prettyErrors: false })) if (!d.errors.length) docs.push(d.toJS()); }
                catch { /* a file that does not parse */ }
            }
            return crdSchemas(docs);
        });
        return (await crds).get(key) ?? null;
    }
    window.addEventListener('pymagic:saved', () => { crds = null; files.clear(); });

    async function schemaFor(name) {
        if (kept.has(name)) return kept.get(name);
        const res = await fetch('/api/schemas/get?name=' + encodeURIComponent(name)).catch(() => null);
        if (!res) return null;                                    // the engine is not answering: asked again at the next check
        kept.set(name, res.ok ? await res.json().catch(() => null) : null);
        return kept.get(name);
    }

    /** The file's text as data, by its extension: JSON, TOML (read by Python) or YAML; undefined when it does not parse (the other checks say why). */
    async function dataOf(rel, text) {
        const lower = rel.toLowerCase();
        // JSON with comments and trailing commas as well (tsconfig, devcontainer.json, .eslintrc), and the dotfiles without an extension
        if (/\.jsonc?$/.test(lower) || /(^|\/)\.(renovaterc|babelrc|eslintrc|prettierrc|stylelintrc)$/.test(lower)) {
            try { return parseJsonc(text); }
            catch {
                // .eslintrc, .prettierrc and .stylelintrc may also be YAML
                if (/\.(eslintrc|prettierrc|stylelintrc)$/.test(lower)) { try { return parseYaml(text, { prettyErrors: false }); } catch { return undefined; } }
                return undefined;
            }
        }
        if (lower.endsWith('.toml')) {
            const res = await fetch('/api/toml', { method: 'POST', body: text }).catch(() => null);
            const parsed = res?.ok ? await res.json() : null;
            return parsed && !parsed.error ? parsed : undefined;
        }
        try { return parseYaml(text, { prettyErrors: false }); } catch { return undefined; }
    }

    /** What to check in the file: [{name, data, text, line}] (text and line: where the document is, for a file of several). */
    async function targetsOf(rel, text, model) {
        const { ids, registered, store } = await catalogNow();
        const lower = rel.toLowerCase();
        const isYaml = /\.ya?ml$/.test(lower);
        // what the file says itself: "$schema" in JSON, the yaml-language-server modeline in YAML
        let data;
        let declared = null;
        if (isYaml) declared = declaredSchema(yamlModeline(text), ids, rel);
        else if (!/\.(toml|md|markdown)$/.test(lower)) {
            data = await dataOf(rel, text);
            if (data && typeof data === 'object' && !Array.isArray(data)) declared = declaredSchema(data.$schema, ids, rel);
        }
        const choice = chooseSchema(rel, await projectNow(), declared, store);
        const note = (label) => { choices.set(model, { label, choice }); window.dispatchEvent(new CustomEvent('pymagic:schema-choice', { detail: { model } })); };
        if (choice?.ignored) { note(null); return []; }
        if (choice?.file) {
            note(choice.mapping ?? choice.file);
            return [{ file: choice.file, data: data ?? (await dataOf(rel, text)), text, line: 0 }];
        }
        let base = choice?.name ?? null;
        data ??= base && base !== 'markdown-frontmatter' ? await dataOf(rel, text) : undefined;
        // OpenAPI and AsyncAPI files say by their version which schema is theirs
        if (base === 'openapi') base = openapiSchemaOf(data);
        else if (base === 'asyncapi') base = asyncapiSchemaOf(data);
        if (base === 'markdown-frontmatter') {
            const fm = frontMatterOf(text);
            if (!fm) { note(null); return []; }
            let front;
            try { front = parseYaml(fm.text, { prettyErrors: false }); } catch { front = undefined; }
            note(titleOf(base, registered));
            return [{ name: base, data: front, text: fm.text, line: fm.line }];
        }
        if (base) { note(choice.mapping ?? titleOf(base, registered, store)); return [{ name: base, data, text, line: 0 }]; }
        if (!isYaml || !looksKubernetes(text)) { note(null); return []; }
        note('Kubernetes');
        const targets = [];
        let docs;
        try { docs = parseAllDocuments(text, { prettyErrors: false }); } catch { return []; }
        for (const doc of docs) {
            if (doc.errors.length) continue;
            const data = doc.toJS();
            const name = kubernetesSchemaOf(data);
            if (!name) continue;
            const [from, to] = doc.range;
            targets.push({ name, data, text: text.slice(from, to), line: text.slice(0, from).split('\n').length - 1, manifest: true });
        }
        return targets;
    }

    async function check(model) {
        const rel = deps.relOf(model);
        if (!rel || model.isDisposed()) return;
        const text = model.getValue();
        const targets = await targetsOf(rel, text, model);
        if (!targets.length) { monaco.editor.setModelMarkers(model, OWNER, []); return; }   // no manifest (any more) in the file
        const markers = [];
        let checked = false;
        for (const target of targets) {
            // a kind Kubernetes does not have: the project's CustomResourceDefinition of it, if there is one
            const schema = target.file ? await fileSchema(target.file) : (await schemaFor(target.name)) ?? (target.manifest ? await crdSchemaFor(target.data) : null);
            if (!schema || model.isDisposed()) continue;
            checked = true;
            if (target.data === undefined) continue;
            for (const p of validate(target.data, schema, 200, await registryFor(schema))) {
                const at = locate(target.text, p);
                const line = at.line + target.line;
                markers.push({
                    startLineNumber: line, startColumn: at.start, endLineNumber: line, endColumn: at.end,
                    severity: monaco.MarkerSeverity.Warning, message: p.message, code: 'schema', source: 'PyMagic',
                });
            }
        }
        if (checked && !model.isDisposed()) monaco.editor.setModelMarkers(model, OWNER, markers);
    }

    const watched = new Set();
    function watch(model) {
        const rel = deps.relOf(model);
        if (!rel || !(schemaOf(rel) || SCHEMA_FILES.test(rel))) return;
        watched.add(model);
        void check(model);
        model.onDidChangeContent(() => {
            clearTimeout(timers.get(model));
            timers.set(model, setTimeout(() => void check(model), 1000));
        });
        model.onWillDispose(() => { watched.delete(model); choices.delete(model); });
    }

    monaco.editor.onDidCreateModel((model) => setTimeout(() => watch(model), 0));
    for (const model of monaco.editor.getModels()) watch(model);

    /** Every open file checked again (the project's mappings changed). */
    function recheck() {
        project = null;
        for (const model of watched) if (!model.isDisposed()) void check(model);
    }
    window.addEventListener('pymagic:schema-mappings', recheck);

    // what the status bar's schema picker needs
    return {
        /** The schema chosen for a file: {label (null: none), choice}, or undefined while it is not known yet. */
        choiceOf: (model) => choices.get(model),
        /** Whether a schema is looked for in this file at all (the picker is shown for it). */
        isSchemaFile: (model) => watched.has(model),
        catalog: catalogNow,
        mappings: projectNow,
        /** Saves the project's mappings (.pymagic/schema-mappings.json) and checks the open files again; false when the server refused them. */
        async saveMappings(next) {
            const res = await fetch('/api/schemas/mappings', { method: 'PUT', body: JSON.stringify(next) }).catch(() => null);
            if (!res?.ok) return false;
            recheck();
            return true;
        },
    };
}

// package.json, pyproject.toml, GitHub workflows, docker-compose, GitLab CI, pre-commit, mkdocs, dependabot, codecov, tox, Renovate, OpenAPI 3.1 / 3.0 / Swagger 2.0,
// Kustomization and Kubernetes manifests checked against their JSON Schema; a custom resource against the CustomResourceDefinition of the project that declares it.
// The schemas are built into PyMagic (a project's own copy in .pymagic/schemas is used instead): nothing is downloaded and nothing is asked (schema-model.js validates).
// A Kubernetes manifest is any YAML file with an apiVersion and a kind (several documents in one file are each checked); its schema is the one of that kind.
import * as monaco from 'monaco-editor';
import { parse as parseYaml, parseAllDocuments } from 'yaml';
import { validate, locate, schemaOf, openapiSchemaOf, looksKubernetes, kubernetesSchemaOf, crdSchemas, crdKeyOf } from './schema-model';

export { schemaOf };

const OWNER = 'pymagic-schema';

export function setupSchemaCheck(deps) {
    const kept = new Map();               // name -> parsed schema, or null when PyMagic has none of that name (a kind Kubernetes does not have)
    const timers = new Map();

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
    window.addEventListener('pymagic:saved', () => { crds = null; });

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
        if (lower.endsWith('.json') || lower.endsWith('.renovaterc')) {
            try { return JSON.parse(text); } catch { return undefined; }
        }
        if (lower.endsWith('.toml')) {
            const res = await fetch('/api/toml', { method: 'POST', body: text }).catch(() => null);
            const parsed = res?.ok ? await res.json() : null;
            return parsed && !parsed.error ? parsed : undefined;
        }
        try { return parseYaml(text, { prettyErrors: false }); } catch { return undefined; }
    }

    /** What to check in the file: [{name, data, text, line}] (text and line: where the document is, for a file of several). */
    async function targetsOf(rel, text) {
        const base = schemaOf(rel);
        if (base === 'openapi') {
            const data = await dataOf(rel, text);
            const name = openapiSchemaOf(data);
            return name ? [{ name, data, text, line: 0 }] : [];
        }
        if (base) return [{ name: base, data: await dataOf(rel, text), text, line: 0 }];
        if (!/\.ya?ml$/i.test(rel) || !looksKubernetes(text)) return [];
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
        const targets = await targetsOf(rel, text);
        if (!targets.length) { monaco.editor.setModelMarkers(model, OWNER, []); return; }   // no manifest (any more) in the file
        const markers = [];
        let checked = false;
        for (const target of targets) {
            // a kind Kubernetes does not have: the project's CustomResourceDefinition of it, if there is one
            const schema = (await schemaFor(target.name)) ?? (target.manifest ? await crdSchemaFor(target.data) : null);
            if (!schema || model.isDisposed()) continue;
            checked = true;
            if (target.data === undefined) continue;
            for (const p of validate(target.data, schema)) {
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

    function watch(model) {
        const rel = deps.relOf(model);
        if (!rel || !(schemaOf(rel) || /\.ya?ml$/i.test(rel))) return;
        void check(model);
        model.onDidChangeContent(() => {
            clearTimeout(timers.get(model));
            timers.set(model, setTimeout(() => void check(model), 1000));
        });
    }

    monaco.editor.onDidCreateModel((model) => setTimeout(() => watch(model), 0));
    for (const model of monaco.editor.getModels()) watch(model);
}

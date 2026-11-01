// package.json, pyproject.toml, GitHub workflows, docker-compose, GitLab CI, pre-commit, mkdocs, dependabot, codecov, tox, Renovate, OpenAPI 3.0 / Swagger 2.0 and Kubernetes manifests checked against their JSON Schema from SchemaStore
// or the project that owns it. The schema is downloaded once, after the user says yes, and kept in the project's .pymagic/schemas; without it nothing is checked and nothing is fetched (schema-model.js validates).
// A Kubernetes manifest is any YAML file with an apiVersion and a kind (several documents in one file are each checked); its schema is the one of that kind.
import * as monaco from 'monaco-editor';
import { parse as parseYaml, parseAllDocuments } from 'yaml';
import { validate, locate, schemaOf, openapiSchemaOf, looksKubernetes, kubernetesSchemaOf } from './schema-model';

export { schemaOf };

const OWNER = 'pymagic-schema';
const ASKED = 'pymagic.schemaAsked4';   // the question grew (more schemas, more hosts): an earlier answer does not count

export function setupSchemaCheck(deps) {
    const kept = new Map();               // name -> parsed schema, or null when it is not kept
    const failed = new Set();             // names whose download failed this session (a kind with no schema, no network): not tried again at every edit
    const timers = new Map();
    let answer = null;                    // the user's answer to the download question this session: true / false

    const remembered = () => { try { return localStorage.getItem(ASKED); } catch { return null; } };
    const remember = (v) => { try { localStorage.setItem(ASKED, v); } catch { /* no storage: asked again next session */ } };

    async function schemaFor(name) {
        if (kept.has(name) && kept.get(name)) return kept.get(name);
        if (failed.has(name)) return null;
        let res = await fetch('/api/schemas/get?name=' + name).catch(() => null);
        if (res?.ok) { kept.set(name, await res.json()); return kept.get(name); }
        if (answer === null && remembered() === 'no') answer = false;
        if (answer === null) {
            answer = window.confirm('Check package.json, pyproject.toml, GitHub workflows, docker-compose, GitLab CI, pre-commit, mkdocs, dependabot, codecov, tox, Renovate, OpenAPI / Swagger and Kubernetes files against their JSON Schema?\n\nPyMagic downloads each schema once, when such a file is opened, from SchemaStore (json.schemastore.org, www.schemastore.org) or the project that owns it (raw.githubusercontent.com, gitlab.com, docs.renovatebot.com), and keeps it in .pymagic/schemas of this project. For a Kubernetes file that is the schema of each kind it uses (Deployment, Service, ...). Nothing else is sent.');
            remember(answer ? 'yes' : 'no');
        }
        if (!answer) return null;
        res = await fetch('/api/schemas/download?name=' + name, { method: 'POST' }).catch(() => null);
        if (!res?.ok) { failed.add(name); return null; }
        kept.set(name, await res.json());
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
            targets.push({ name, data, text: text.slice(from, to), line: text.slice(0, from).split('\n').length - 1 });
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
            const schema = await schemaFor(target.name);
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

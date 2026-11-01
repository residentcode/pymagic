// package.json and pyproject.toml checked against their JSON Schema from SchemaStore. The schema is downloaded once, after the user says
// yes, and kept in the project's .pymagic/schemas; without it nothing is checked and nothing is fetched (schema-model.js validates).
import * as monaco from 'monaco-editor';
import { validate, locate } from './schema-model';

const OWNER = 'pymagic-schema';
const ASKED = 'pymagic.schemaAsked';

/** Which schema checks a project file, by its name: 'package' | 'pyproject' | null. */
export function schemaOf(rel) {
    const name = rel.split('/').pop().toLowerCase();
    return name === 'package.json' ? 'package' : name === 'pyproject.toml' ? 'pyproject' : null;
}

export function setupSchemaCheck(deps) {
    const kept = new Map();               // name -> parsed schema, or null when it is not kept
    const timers = new Map();
    let answer = null;                    // the user's answer to the download question this session: true / false

    const remembered = () => { try { return localStorage.getItem(ASKED); } catch { return null; } };
    const remember = (v) => { try { localStorage.setItem(ASKED, v); } catch { /* no storage: asked again next session */ } };

    async function schemaFor(name) {
        if (kept.has(name) && kept.get(name)) return kept.get(name);
        let res = await fetch('/api/schemas/get?name=' + name).catch(() => null);
        if (res?.ok) { kept.set(name, await res.json()); return kept.get(name); }
        if (answer === null && remembered() === 'no') answer = false;
        if (answer === null) {
            answer = window.confirm('Check package.json and pyproject.toml against their JSON Schema?\n\nPyMagic downloads the schemas once from json.schemastore.org (SchemaStore) and keeps them in .pymagic/schemas of this project. Nothing else is sent.');
            remember(answer ? 'yes' : 'no');
        }
        if (!answer) return null;
        res = await fetch('/api/schemas/download?name=' + name, { method: 'POST' }).catch(() => null);
        if (!res?.ok) return null;
        kept.set(name, await res.json());
        return kept.get(name);
    }

    async function dataOf(name, text) {
        if (name === 'package') {
            try { return JSON.parse(text); } catch { return undefined; }     // the editor's own JSON checks say what is wrong
        }
        const res = await fetch('/api/toml', { method: 'POST', body: text }).catch(() => null);
        const parsed = res?.ok ? await res.json() : null;
        return parsed && !parsed.error ? parsed : undefined;                // filecheck says what is wrong with the TOML
    }

    async function check(model) {
        const rel = deps.relOf(model);
        const name = rel && schemaOf(rel);
        if (!name || model.isDisposed()) return;
        const text = model.getValue();
        const schema = await schemaFor(name);
        if (!schema || model.isDisposed()) return;
        const data = await dataOf(name, text);
        if (model.isDisposed()) return;
        const problems = data === undefined ? [] : validate(data, schema);
        monaco.editor.setModelMarkers(model, OWNER, problems.map((p) => {
            const at = locate(text, p);
            return {
                startLineNumber: at.line, startColumn: at.start, endLineNumber: at.line, endColumn: at.end,
                severity: monaco.MarkerSeverity.Warning, message: p.message, code: 'schema', source: 'PyMagic',
            };
        }));
    }

    function watch(model) {
        const rel = deps.relOf(model);
        if (!rel || !schemaOf(rel)) return;
        void check(model);
        model.onDidChangeContent(() => {
            clearTimeout(timers.get(model));
            timers.set(model, setTimeout(() => void check(model), 1000));
        });
    }

    monaco.editor.onDidCreateModel((model) => setTimeout(() => watch(model), 0));
    for (const model of monaco.editor.getModels()) watch(model);
}

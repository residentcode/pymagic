// package.json, pyproject.toml, GitHub workflows, docker-compose, GitLab CI, pre-commit, mkdocs, dependabot, codecov and tox files checked against their JSON Schema from SchemaStore. The schema is downloaded once, after the user says
// yes, and kept in the project's .pymagic/schemas; without it nothing is checked and nothing is fetched (schema-model.js validates).
import * as monaco from 'monaco-editor';
import { parse as parseYaml } from 'yaml';
import { validate, locate } from './schema-model';

const OWNER = 'pymagic-schema';
const ASKED = 'pymagic.schemaAsked3';   // the question grew (more schemas, more hosts): an earlier answer does not count

/** Which schema checks a project file, by its path: 'package' | 'pyproject' | 'workflow' | 'compose' | 'gitlab' | 'precommit' | 'mkdocs' | 'dependabot' | 'codecov' | 'tox' | null. */
export function schemaOf(rel) {
    const lower = rel.toLowerCase();
    const name = lower.split('/').pop();
    if (/(^|\/)\.github\/workflows\/[^/]+\.ya?ml$/.test(lower)) return 'workflow';
    if (/^(docker-)?compose(\.[^/]+)?\.ya?ml$/.test(name)) return 'compose';
    if (/(^|\.)gitlab-ci\.ya?ml$/.test(name)) return 'gitlab';
    if (/^\.pre-commit-config\.ya?ml$/.test(name)) return 'precommit';
    if (/^mkdocs\.ya?ml$/.test(name)) return 'mkdocs';
    if (/(^|\/)\.github\/dependabot\.ya?ml$/.test(lower)) return 'dependabot';
    if (/^\.?codecov\.ya?ml$/.test(name)) return 'codecov';
    if (name === 'tox.toml') return 'tox';
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
            answer = window.confirm('Check package.json, pyproject.toml, GitHub workflows, docker-compose, GitLab CI, pre-commit, mkdocs, dependabot, codecov and tox files against their JSON Schema?\n\nPyMagic downloads each schema once, when such a file is opened, from SchemaStore (json.schemastore.org, www.schemastore.org) or the project that owns it (raw.githubusercontent.com, gitlab.com), and keeps it in .pymagic/schemas of this project. Nothing else is sent.');
            remember(answer ? 'yes' : 'no');
        }
        if (!answer) return null;
        res = await fetch('/api/schemas/download?name=' + name, { method: 'POST' }).catch(() => null);
        if (!res?.ok) return null;
        kept.set(name, await res.json());
        return kept.get(name);
    }

    /** The file's text as data, by its extension: JSON, TOML (read by Python) or YAML; undefined when it does not parse (the other checks say why). */
    async function dataOf(rel, text) {
        const lower = rel.toLowerCase();
        if (lower.endsWith('.json')) {
            try { return JSON.parse(text); } catch { return undefined; }
        }
        if (lower.endsWith('.toml')) {
            const res = await fetch('/api/toml', { method: 'POST', body: text }).catch(() => null);
            const parsed = res?.ok ? await res.json() : null;
            return parsed && !parsed.error ? parsed : undefined;
        }
        try { return parseYaml(text, { prettyErrors: false }); } catch { return undefined; }
    }

    async function check(model) {
        const rel = deps.relOf(model);
        const name = rel && schemaOf(rel);
        if (!name || model.isDisposed()) return;
        const text = model.getValue();
        const schema = await schemaFor(name);
        if (!schema || model.isDisposed()) return;
        const data = await dataOf(rel, text);
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

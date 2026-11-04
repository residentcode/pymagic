// Markers on requirements*.txt and pyproject.toml while they are open: a requirement the interpreter does not have, a Poetry range the
// newest release is out of, and a stub package made for another version of its library (requirements-model.js says which).
import * as monaco from 'monaco-editor';
import { kindOf, requirementLines, problems, normalize } from './requirements-model';

const OWNER = 'pymagic-requirements';

export function setupRequirementsCheck(deps) {
    let installed = null;          // Map(normalized name -> version), or null without an interpreter
    let installedAt = 0;
    const latest = new Map();      // normalized name -> newest version on PyPI (asked once a session)
    const asked = new Set();
    const timers = new Map();

    async function installedPackages() {
        if (installed && Date.now() - installedAt < 30000) return installed;
        try {
            const res = await fetch('/api/packages');
            const data = res.ok ? await res.json() : null;
            installed = data && !data.error ? new Map(data.packages.map((p) => [normalize(p.name), p.version])) : null;
        } catch { installed = null; }
        installedAt = Date.now();
        return installed;
    }

    async function newest(names) {
        await Promise.all(names.filter((n) => !asked.has(n)).map(async (n) => {
            asked.add(n);
            try {
                const res = await fetch('/api/pypi?name=' + encodeURIComponent(n));
                const info = res.ok ? await res.json() : null;
                if (info?.version) latest.set(normalize(n), info.version);
            } catch { /* the index is not reachable: no Poetry marker */ }
        }));
    }

    async function check(model) {
        const rel = deps.relOf(model);
        const kind = rel && kindOf(rel);
        if (!kind || model.isDisposed()) return;
        const entries = requirementLines(model.getValue(), kind);
        const [have] = await Promise.all([installedPackages(), newest(entries.filter((e) => e.poetry).map((e) => e.name))]);
        if (model.isDisposed()) return;
        monaco.editor.setModelMarkers(model, OWNER, problems(entries, have, latest).map((p) => ({
            startLineNumber: p.line, startColumn: p.start, endLineNumber: p.line, endColumn: p.end,
            severity: p.severity === 'warning' ? monaco.MarkerSeverity.Warning : monaco.MarkerSeverity.Info,
            message: p.message, code: p.code, source: 'PyMagic',
        })));
    }

    function watch(model) {
        const rel = deps.relOf(model);
        if (!rel || !kindOf(rel)) return;
        void check(model);
        model.onDidChangeContent(() => {
            clearTimeout(timers.get(model));
            timers.set(model, setTimeout(() => void check(model), 800));
        });
    }

    monaco.editor.onDidCreateModel((model) => setTimeout(() => watch(model), 0));
    for (const model of monaco.editor.getModels()) watch(model);
    // packages installed or removed (here or from outside), or another interpreter: the markers of the open files again
    const again = () => {
        installed = null;
        for (const model of monaco.editor.getModels()) if (kindOf(deps.relOf(model) ?? '')) void check(model);
    };
    window.addEventListener('pymagic:packagesChanged', again);
    window.addEventListener('pymagic:interpreter', again);
}

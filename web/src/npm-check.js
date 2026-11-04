// Markers on a package.json while it is open: a dependency its node_modules does not have (npm-model.js says which). Checked again when the host
// says a node_modules changed (npm install, npm uninstall in the terminal: `node|path` on /api/watch) and when the file is edited.
import * as monaco from 'monaco-editor';
import { isPackageJson, folderOf, dependencyEntries, problems } from './npm-model.js';

const OWNER = 'pymagic-npm';

export function setupNpmCheck(deps) {
    const timers = new Map();

    async function installed(folder) {
        try {
            const res = await fetch('/api/node/installed?dir=' + encodeURIComponent(folder));
            return res.ok ? (await res.json()).packages ?? null : undefined;
        } catch { return undefined; }
    }

    async function check(model) {
        const rel = deps.relOf(model);
        if (!rel || !isPackageJson(rel) || model.isDisposed()) return;
        const entries = dependencyEntries(model.getValue());
        const have = entries.length ? await installed(folderOf(rel)) : null;
        if (model.isDisposed() || have === undefined) return;   // the host did not answer: the markers stay as they were
        monaco.editor.setModelMarkers(model, OWNER, problems(entries, have).map((p) => ({
            startLineNumber: p.line, startColumn: p.start, endLineNumber: p.line, endColumn: p.end,
            severity: monaco.MarkerSeverity.Warning, message: p.message, code: p.code, source: 'PyMagic',
        })));
    }

    function watch(model) {
        const rel = deps.relOf(model);
        if (!rel || !isPackageJson(rel)) return;
        void check(model);
        model.onDidChangeContent(() => {
            clearTimeout(timers.get(model));
            timers.set(model, setTimeout(() => void check(model), 800));
        });
    }

    monaco.editor.onDidCreateModel((model) => setTimeout(() => watch(model), 0));
    for (const model of monaco.editor.getModels()) watch(model);
    // a node_modules changed: the package.json files beside it again
    window.addEventListener('pymagic:nodeModules', (e) => {
        const modules = e.detail ?? '';
        const folder = modules.replace(/\/?node_modules$/i, '');
        for (const model of monaco.editor.getModels()) {
            const rel = deps.relOf(model);
            if (rel && isPackageJson(rel) && folderOf(rel) === folder) void check(model);
        }
    });
}

// The project as the TypeScript service sees it (it sees only the open files otherwise): the project's own JavaScript and TypeScript files,
// its tsconfig.json or jsconfig.json, and the packages' types (node_modules' .d.ts and package.json files) are given to Monaco's
// TypeScript and JavaScript services as files of their own (extra libraries) under the addresses the editor's models have, so an import of
// another file or a package resolves, a type error through it is found, and Go to Definition reaches the other file. The engine gathers them
// (/api/ts/project, /api/ts/packages: TsProject.cs); a change on disk (/api/watch) reads again what it touched: one file, the project, or the
// packages after npm install.
import * as monaco from 'monaco-editor';
import { ROOT, compilerOptionsOf, planRefresh } from './tsproject-model.js';

export function setupTsProject() {
    const ts = monaco.languages.typescript;
    const services = [ts.typescriptDefaults, ts.javascriptDefaults];
    // path -> { text, disposables }: the project's own files and the packages' apart (they are read again apart)
    const code = new Map(), packages = new Map();
    // a project file's address as its model has it (encoded, as Uri.toString writes it); a package's as TypeScript writes it when it
    // resolves node_modules/@scope/name from a file of the project
    const codeName = (path) => monaco.Uri.parse(ROOT + path).toString();
    const packageName = (path) => monaco.Uri.parse(ROOT + path).toString(true);
    const state = { js: false, config: null, truncated: false, packagesTruncated: false };

    function put(map, name, path, text) {
        const had = map.get(path);
        if (had?.text === text) return;
        had?.disposables.forEach((d) => d.dispose());
        map.set(path, { text, disposables: services.map((s) => s.addExtraLib(text, name(path))) });
    }
    function drop(map, path) {
        map.get(path)?.disposables.forEach((d) => d.dispose());
        map.delete(path);
    }
    const getJson = (url) => fetch(url).then((r) => (r.ok ? r.json() : null)).catch(() => null);

    async function readProject() {
        const r = await getJson('/api/ts/project');
        if (!r?.js) {
            state.js = false;
            return;
        }
        state.js = true;
        state.config = r.config ?? null;
        state.truncated = !!r.truncated;
        if (r.config?.error) console.warn('PyMagic: ' + r.config.error);
        const raw = r.config?.compilerOptions ?? null;
        // a jsconfig.json is a project of JavaScript: its files are allowed and checked as it says
        const jsconfig = /jsconfig\.json$/i.test(r.config?.path ?? '');
        ts.typescriptDefaults.setCompilerOptions(compilerOptionsOf(raw, 'typescript'));
        ts.javascriptDefaults.setCompilerOptions(compilerOptionsOf(jsconfig ? { allowJs: true, ...raw } : raw, 'javascript'));
        const seen = new Set();
        for (const f of r.files ?? []) {
            seen.add(f.path);
            put(code, codeName, f.path, f.text);
        }
        for (const path of [...code.keys()]) if (!seen.has(path)) drop(code, path);
    }
    async function readPackages() {
        if (!state.js) return;
        const r = await getJson('/api/ts/packages');
        if (!r) return;
        state.packagesTruncated = !!r.truncated;
        const seen = new Set();
        for (const f of r.files ?? []) {
            seen.add(f.path);
            put(packages, packageName, f.path, f.text);
        }
        for (const path of [...packages.keys()]) if (!seen.has(path)) drop(packages, path);
    }
    async function readFile(path) {
        const res = await fetch('/api/file?path=' + encodeURIComponent(path)).catch(() => null);
        if (res?.ok) put(code, codeName, path, await res.text());
        else drop(code, path);
    }

    let busy = readProject().then(readPackages).catch(() => { });
    window.addEventListener('pymagic:diskChanged', (e) => {
        const plan = planRefresh(e.detail);
        busy = busy.then(async () => {
            if (plan.project || (plan.packages && !state.js)) await readProject();
            else if (state.js) for (const f of plan.files) await readFile(f);
            if (plan.packages) await readPackages();
        }).catch(() => { });
    });
    // for the checks: what the service was given
    return {
        ready: () => busy,
        state: () => ({ ...state, files: code.size, packages: packages.size }),
    };
}

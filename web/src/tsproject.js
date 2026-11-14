// The project as the TypeScript service sees it (it sees only the open files otherwise): the project's own JavaScript and TypeScript files,
// its tsconfig.json or jsconfig.json, and the packages' types (node_modules' .d.ts and package.json files) are given to Monaco's
// TypeScript and JavaScript services as files of their own (extra libraries) under the addresses the editor's models have, so an import of
// another file or a package resolves, a type error through it is found, and Go to Definition reaches the other file. The engine gathers them
// (/api/ts/project, /api/ts/packages: TsProject.cs); a change on disk (/api/watch) reads again what it touched: one file, the project, or the
// packages after npm install. A project with several configs (a solution's tsconfig.json referring to tsconfig.app.json and
// tsconfig.node.json, a monorepo's packages) is checked by the one that covers the file in the editor: the service has one set of options,
// so they follow the file. A project too large to be given whole has what the file in the editor imports read as it is opened.
import * as monaco from 'monaco-editor';
import { ROOT, compilerOptionsOf, planRefresh, configFor, importsOf, isCodePath } from './tsproject-model.js';

export function setupTsProject() {
    const ts = monaco.languages.typescript;
    const services = [ts.typescriptDefaults, ts.javascriptDefaults];
    // path -> { text, disposables }: the project's own files and the packages' apart (they are read again apart)
    const code = new Map(), packages = new Map();
    // a project file's address as its model has it (encoded, as Uri.toString writes it); a package's as TypeScript writes it when it
    // resolves node_modules/@scope/name from a file of the project
    const codeName = (path) => monaco.Uri.parse(ROOT + path).toString();
    const packageName = (path) => monaco.Uri.parse(ROOT + path).toString(true);
    const state = { js: false, config: null, configs: [], used: null, truncated: false, packagesTruncated: false };

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

    /** The options of a config in force: set only when they differ from the ones in force (the service checks every file again). */
    let inForce = '';
    function use(config) {
        const raw = config?.compilerOptions ?? null;
        // a jsconfig.json is a project of JavaScript: its files are allowed and checked as it says
        const jsconfig = /jsconfig(\.[\w.-]+)?\.json$/i.test(config?.path ?? '');
        const forTs = compilerOptionsOf(raw, 'typescript'), forJs = compilerOptionsOf(jsconfig ? { allowJs: true, ...raw } : raw, 'javascript');
        const key = JSON.stringify([forTs, forJs]);
        state.used = config?.path ?? null;
        if (key === inForce) return;
        inForce = key;
        ts.typescriptDefaults.setCompilerOptions(forTs);
        ts.javascriptDefaults.setCompilerOptions(forJs);
    }
    /** A model's path in the project (a component's hidden script is its component's), or null. */
    const pathOf = (model) => {
        if (!model || model.uri.scheme !== 'pymagic') return null;
        return decodeURIComponent(model.uri.path.replace(/^\//, ''));
    };
    const currentPath = () => {
        for (const ed of monaco.editor.getEditors()) {
            const p = ed.hasTextFocus?.() || monaco.editor.getEditors().length === 1 ? pathOf(ed.getModel()) : null;
            if (p && (isCodePath(p) || /\.(vue|svelte)$/i.test(p))) return p;
        }
        return null;
    };

    async function readProject() {
        const r = await getJson('/api/ts/project');
        if (!r?.js) {
            state.js = false;
            return;
        }
        state.js = true;
        state.config = r.config ?? null;
        state.configs = r.configs ?? (r.config ? [r.config] : []);
        state.truncated = !!r.truncated;
        for (const c of state.configs) if (c.error) console.warn('PyMagic: ' + c.error);
        const at = currentPath();
        use((at && configFor(at, state.configs)) || state.config);
        const seen = new Set();
        for (const f of r.files ?? []) {
            seen.add(f.path);
            put(code, codeName, f.path, f.text);
        }
        for (const path of [...code.keys()]) if (!seen.has(path) && !asked.has(path)) drop(code, path);
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
        for (const path of [...packages.keys()]) if (!seen.has(path) && !askedPackageFiles.has(path)) drop(packages, path);
    }
    async function readFile(path) {
        const res = await fetch('/api/file?path=' + encodeURIComponent(path)).catch(() => null);
        if (res?.ok) { put(code, codeName, path, await res.text()); return true; }
        drop(code, path);
        return false;
    }

    // what the file in the editor imports, when the project or its packages' types were too large to be given whole
    const asked = new Set(), askedPackages = new Set(), askedPackageFiles = new Set();
    async function readImports(path, text, depth = 0) {
        const { files, packages: names } = importsOf(path, text);
        if (state.truncated)
            for (const candidates of files) {
                if (candidates.some((p) => code.has(p))) continue;
                for (const p of candidates) {
                    if (asked.size >= 400) return;
                    if (asked.has(p)) continue;
                    asked.add(p);
                    if (!(await readFile(p))) continue;
                    // and what that one imports, one step further
                    if (depth < 2) await readImports(p, code.get(p)?.text ?? '', depth + 1);
                    break;
                }
            }
        if (state.packagesTruncated)
            for (const name of names) {
                if (askedPackages.has(name)) continue;
                askedPackages.add(name);
                const prefix = `node_modules/${name}/`;
                if ([...packages.keys()].some((p) => p.startsWith(prefix) && /\.d\.[cm]?ts$/.test(p))) continue;
                const r = await getJson('/api/ts/package?name=' + encodeURIComponent(name));
                for (const f of r?.files ?? []) { askedPackageFiles.add(f.path); put(packages, packageName, f.path, f.text); }
            }
    }

    let busy = readProject().then(readPackages).catch(() => { });
    /** The file in the editor: its config in force, and (a large project) its imports read. */
    function follow(model) {
        const path = pathOf(model);
        if (!path || !(isCodePath(path) || /\.(vue|svelte)$/i.test(path))) return;
        busy = busy.then(async () => {
            if (!state.js || model.isDisposed()) return;
            if (state.configs.length > 1) use(configFor(path, state.configs) ?? state.config);
            if (state.truncated || state.packagesTruncated) await readImports(path, model.getValue());
        }).catch(() => { });
    }
    const watch = (ed) => { follow(ed.getModel()); ed.onDidChangeModel(() => follow(ed.getModel())); ed.onDidFocusEditorText?.(() => follow(ed.getModel())); };
    for (const ed of monaco.editor.getEditors()) watch(ed);
    monaco.editor.onDidCreateEditor(watch);

    window.addEventListener('pymagic:diskChanged', (e) => {
        const plan = planRefresh(e.detail);
        busy = busy.then(async () => {
            if (plan.project || (plan.packages && !state.js)) await readProject();
            else if (state.js) for (const f of plan.files) await readFile(f);
            if (plan.packages) { askedPackages.clear(); await readPackages(); }
        }).catch(() => { });
    });
    // for the checks: what the service was given
    return {
        ready: () => busy,
        state: () => ({ ...state, files: code.size, packages: packages.size }),
        has: (path) => code.has(path) || packages.has(path),
    };
}

// The project as the TypeScript service sees it (it sees only the open files otherwise): the project's own JavaScript and TypeScript files,
// its tsconfig.json or jsconfig.json, and the packages' types (node_modules' .d.ts and package.json files) are given to Monaco's
// TypeScript and JavaScript services as files of their own (extra libraries) under the addresses the editor's models have, so an import of
// another file or a package resolves, a type error through it is found, and Go to Definition reaches the other file. The engine gathers them
// (/api/ts/project, /api/ts/packages: TsProject.cs); a change on disk (/api/watch) reads again what it touched: one file, the project, or the
// packages after npm install. A project with several configs (a solution's tsconfig.json referring to tsconfig.app.json and
// tsconfig.node.json, a monorepo's packages) is checked by the one that covers the file in the editor: the service has one set of options,
// so they follow the file. A project too large to be given whole has what the file in the editor imports read as it is opened.
import * as monaco from 'monaco-editor';
import { ROOT, COMPONENT_SHIM, compilerOptionsOf, planRefresh, configFor, importsOf, isCodePath } from './tsproject-model.js';

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
        // a declaration file whose name has a character an address encodes ($types.d.ts, SvelteKit's): also under the name TypeScript
        // looks for when an import names it (./$types), which is the name as written
        const names = [name(path)];
        if (map === code && /\.d\.[mc]?ts$/i.test(path) && packageName(path) !== names[0]) names.push(packageName(path));
        map.set(path, { text, disposables: names.flatMap((n) => services.map((s) => s.addExtraLib(text, n))) });
    }
    function drop(map, path) {
        map.get(path)?.disposables.forEach((d) => d.dispose());
        map.delete(path);
    }
    const getJson = (url) => fetch(url).then((r) => (r.ok ? r.json() : null)).catch(() => null);

    /** The options of a config in force: set only when they differ from the ones in force (the service checks every file again). */
    let inForce = '';
    /** A config's options for each of the two services. */
    function optionsOf(config) {
        const raw = config?.compilerOptions ?? null;
        // a jsconfig.json is a project of JavaScript: its files are allowed and checked as it says
        const jsconfig = /jsconfig(\.[\w.-]+)?\.json$/i.test(config?.path ?? '');
        return { typescript: compilerOptionsOf(raw, 'typescript'), javascript: compilerOptionsOf(jsconfig ? { allowJs: true, ...raw } : raw, 'javascript') };
    }
    function use(config) {
        const { typescript: forTs, javascript: forJs } = optionsOf(config);
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

    // a component imported by a script or by another component (import Card from './Card.vue') is a module to TypeScript, unless the
    // project or the framework's own package says what one is (an env.d.ts with declare module '*.vue', Svelte's types)
    let shim = null;
    function components() {
        const declared = (ext) => {
            const re = new RegExp(`declare\\s+module\\s+['"]\\*\\.${ext}['"]`);
            for (const f of code.values()) if (re.test(f.text)) return true;
            for (const [path, f] of packages) if (path.startsWith(`node_modules/${ext}/`) && re.test(f.text)) return true;
            return false;
        };
        const text = ['vue', 'svelte'].filter((e) => !declared(e)).map((e) => `declare module '*.${e}' { const component: any; export default component; }`).join('\n');
        if ((shim?.text ?? '') === text) return;
        shim?.disposables.forEach((d) => d.dispose());
        shim = text ? { text, disposables: services.map((s) => s.addExtraLib(text, COMPONENT_SHIM)) } : null;
    }
    components();

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
        components();
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
        components();
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

    // ---- an open file of another config than the one in force (two editors side by side, a file whose problems are asked while another
    // is in the editor): the service checks it with the options in force, so it is checked again by a service of its own with its config's
    // options, and those findings stand in place of the service's
    const others = new Map();       // language + options -> { worker, used, libs }
    const mine = new Map();         // a model's address -> the findings set for it here
    const pending = new Set();
    let libsVersion = 0;
    for (const s of services) s.onDidExtraLibsChange(() => { libsVersion++; });
    const defaultsOf = (language) => (language === 'typescript' ? ts.typescriptDefaults : ts.javascriptDefaults);
    /** The options a model's own config asks for, when they are not the ones in force; null when it is checked as it should be. */
    function ownOptions(model) {
        const language = model.getLanguageId();
        if ((language !== 'typescript' && language !== 'javascript') || state.configs.length < 2) return null;
        // (a component's hidden documents are checked as the component)
        const path = pathOf(model)?.replace(/\.(vue|svelte)\.\d+(\.m)?\.(ts|js)$/i, '.$1');
        if (!path || !(isCodePath(path) || /\.(vue|svelte)$/i.test(path))) return null;
        const own = optionsOf(configFor(path, state.configs) ?? state.config)[language];
        return JSON.stringify(own) === JSON.stringify(defaultsOf(language).getCompilerOptions()) ? null : own;
    }
    async function serviceOf(options, language) {
        const key = language + JSON.stringify(options);
        let s = others.get(key);
        if (!s) {
            const worker = monaco.editor.createWebWorker({
                moduleId: 'vs/language/typescript/tsWorker', label: language, keepIdleModels: true,
                createData: { compilerOptions: options, extraLibs: defaultsOf(language).getExtraLibs(), customWorkerPath: undefined, inlayHintsOptions: {} },
            });
            s = { worker, libs: libsVersion };
            others.set(key, s);
        }
        s.used = Date.now();
        const client = await s.worker.withSyncedResources(monaco.editor.getModels().filter((m) => m.getLanguageId() === language).map((m) => m.uri));
        if (s.libs !== libsVersion) {
            s.libs = libsVersion;
            await client.updateExtraLibs(defaultsOf(language).getExtraLibs());
        }
        return client;
    }
    const S = monaco.MarkerSeverity;
    const flat = (t, depth = 0) => (typeof t === 'string' ? t : t ? '  '.repeat(depth) + t.messageText + (t.next ?? []).map((n) => '\n' + flat(n, depth + 1)).join('') : '');
    const keyOf = (list) => JSON.stringify(list.map((k) => [k.startLineNumber, k.startColumn, k.endLineNumber, k.endColumn, k.severity, k.message]));
    async function recheck(model) {
        const uri = model.uri.toString(), language = model.getLanguageId();
        const options = ownOptions(model);
        if (!options) { mine.delete(uri); return; }
        // (the findings there are the ones set here: nothing to do)
        if (mine.get(uri) === keyOf(monaco.editor.getModelMarkers({ resource: model.uri, owner: language })) || pending.has(uri)) return;
        pending.add(uri);
        try {
            const client = await serviceOf(options, language);
            const version = model.getVersionId();
            const off = defaultsOf(language).getDiagnosticsOptions();
            const found = (await Promise.all([
                off.noSyntaxValidation ? [] : client.getSyntacticDiagnostics(uri), off.noSemanticValidation ? [] : client.getSemanticDiagnostics(uri),
                off.noSuggestionDiagnostics ? [] : client.getSuggestionDiagnostics(uri),
            ])).flat().filter((d) => !(off.diagnosticCodesToIgnore ?? []).includes(d.code));
            if (model.isDisposed() || model.getVersionId() !== version || !ownOptions(model)) return;
            const markers = found.map((d) => {
                const a = model.getPositionAt(d.start ?? 0), b = model.getPositionAt((d.start ?? 0) + (d.length ?? 0));
                return {
                    severity: d.category === 1 ? S.Error : d.category === 0 ? S.Warning : d.category === 3 ? S.Info : S.Hint,
                    startLineNumber: a.lineNumber, startColumn: a.column, endLineNumber: b.lineNumber, endColumn: b.column,
                    message: flat(d.messageText), code: String(d.code), tags: [...(d.reportsUnnecessary ? [1] : []), ...(d.reportsDeprecated ? [2] : [])],
                };
            });
            mine.set(uri, keyOf(markers));
            monaco.editor.setModelMarkers(model, language, markers);
        }
        catch { /* the service's own findings stay */ }
        finally { pending.delete(uri); }
    }
    monaco.editor.onDidChangeMarkers((uris) => {
        if (state.configs.length < 2) return;
        for (const uri of uris) {
            const model = monaco.editor.getModel(uri);
            if (model && (model.getLanguageId() === 'typescript' || model.getLanguageId() === 'javascript')) void recheck(model);
        }
    });
    monaco.editor.onWillDisposeModel((model) => mine.delete(model.uri.toString()));
    // a service of another config that nothing has asked for a while is let go (each holds the project's files and its packages' types)
    window.setInterval(() => {
        for (const [key, s] of others) {
            if (Date.now() - s.used < 120000) continue;
            if (monaco.editor.getModels().some((m) => { const o = ownOptions(m); return o && m.getLanguageId() + JSON.stringify(o) === key; })) continue;
            s.worker.dispose();
            others.delete(key);
        }
    }, 30000);

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
        state: () => ({ ...state, files: code.size, packages: packages.size, otherServices: others.size }),
        has: (path) => code.has(path) || packages.has(path),
    };
}

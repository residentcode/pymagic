// The TypeScript service's project, without Monaco: the compiler options a tsconfig.json (or jsconfig.json) asks for, as Monaco's TypeScript
// worker takes them (its enums as numbers, its libraries as file names, paths under the project's own addresses), and which changed files
// ask for the project's files or its packages' types to be read again. tsproject.js gives them to Monaco.

/** The address of a project file as the TypeScript service sees the editor's models (pymagic:/path: Monaco writes no empty authority), which it resolves imports in. */
export const ROOT = 'pymagic:/';
/** The file that says what a component (.vue, .svelte) is when it is imported: a module (tsproject.js gives it to the services). */
export const COMPONENT_SHIM = 'pymagic:/__pymagic_components.d.ts';

const TARGETS = { es3: 0, es5: 1, es6: 2, es2015: 2, es2016: 3, es2017: 4, es2018: 5, es2019: 6, es2020: 7, es2021: 8, es2022: 9, es2023: 10, es2024: 11, esnext: 99 };
const MODULES = { none: 0, commonjs: 1, amd: 2, umd: 3, system: 4, es6: 5, es2015: 5, es2020: 6, es2022: 7, esnext: 99, node16: 100, node18: 101, nodenext: 199, preserve: 200 };
const RESOLUTIONS = { classic: 1, node: 2, node10: 2, node16: 3, nodenext: 99, bundler: 100 };
const JSX = { preserve: 1, react: 2, 'react-native': 3, 'react-jsx': 4, 'react-jsxdev': 5 };
const DETECTION = { legacy: 1, auto: 2, force: 3 };
// what only building writes or reads, not what the editor checks
const LEFT_OUT = new Set(['outDir', 'outFile', 'rootDir', 'declarationDir', 'tsBuildInfoFile', 'composite', 'incremental', 'plugins', 'pathsBase', 'emitDeclarationOnly', 'sourceRoot', 'mapRoot']);

/** Without a tsconfig.json: what a project of today's tools (Vite, Node) is checked with. */
export const DEFAULT_OPTIONS = {
    target: TARGETS.es2022, module: MODULES.esnext, moduleResolution: RESOLUTIONS.bundler, jsx: JSX['react-jsx'],
    allowJs: true, esModuleInterop: true, allowSyntheticDefaultImports: true, skipLibCheck: true, resolveJsonModule: true,
};

// ("./src" and "." are the project's; ".svelte-kit" keeps its dot)
const under = (rel) => ROOT + String(rel ?? '').replace(/^\.(?:\/|$)/, '').replace(/^\/+/, '').replace(/\/+$/, '');

/** A library's name in a tsconfig (DOM, ES2022, dom.iterable) as Monaco's worker names its file (lib.dom.d.ts). */
export const libFile = (name) => `lib.${String(name).toLowerCase()}.d.ts`;

/**
 * The compiler options of a tsconfig's compilerOptions (with what it extends merged in, as /api/ts/project gives them) for the TypeScript
 * service of `language` ('typescript' or 'javascript'). Without a config the defaults; a JavaScript file is type-checked when the config's
 * checkJs says so (or the file's own // @ts-check), as VS Code does.
 */
export function compilerOptionsOf(raw, language = 'typescript') {
    const out = { ...DEFAULT_OPTIONS };
    for (const [k, v] of Object.entries(raw ?? {})) {
        if (LEFT_OUT.has(k) || v === undefined || v === null) continue;
        const text = typeof v === 'string' ? v.toLowerCase() : null;
        if (k === 'target') { if (text in TARGETS) out.target = TARGETS[text]; }
        else if (k === 'module') { if (text in MODULES) out.module = MODULES[text]; }
        else if (k === 'moduleResolution') { if (text in RESOLUTIONS) out.moduleResolution = RESOLUTIONS[text]; }
        else if (k === 'jsx') { if (text in JSX) out.jsx = JSX[text]; }
        else if (k === 'moduleDetection') { if (text in DETECTION) out.moduleDetection = DETECTION[text]; }
        else if (k === 'lib' && Array.isArray(v)) out.lib = v.map(libFile);
        else if (k === 'baseUrl' && typeof v === 'string') out.baseUrl = under(v);
        else if (k === 'typeRoots' && Array.isArray(v)) out.typeRoots = v.map(under);
        // (SvelteKit's: the project and .svelte-kit/types, so that ./$types is found beside a route; the server writes them from the project)
        else if (k === 'rootDirs' && Array.isArray(v)) out.rootDirs = v.map((d) => under(d === '.' ? '' : d));
        else out[k] = v;
    }
    // paths without a baseUrl are the config's own folder's
    if (raw?.paths && !out.baseUrl) out.baseUrl = under(raw.pathsBase ?? '');
    // a bundler's resolution needs a module of today's; an old module of the config keeps node's resolution
    if (out.moduleResolution === RESOLUTIONS.bundler && ![MODULES.esnext, MODULES.es2015, MODULES.es2020, MODULES.es2022, MODULES.preserve].includes(out.module))
        out.moduleResolution = out.module >= MODULES.node16 && out.module <= MODULES.nodenext ? RESOLUTIONS.nodenext : RESOLUTIONS.node;
    // the worker cannot list folders: @types is found where the project's node_modules has it
    if (!out.typeRoots) out.typeRoots = [under('node_modules/@types')];
    out.allowNonTsExtensions = true;
    if (language === 'javascript') out.allowJs = true;
    return out;
}

const CODE = /\.(ts|tsx|mts|cts|js|jsx|mjs|cjs)$/i;
const SKIPPED = /(^|\/)(node_modules|\.git|vendor|\.venv|venv|env|__pycache__|bin|obj|\.pymagic|dist|build|out|\.next|\.nuxt|\.svelte-kit|\.output|coverage|\.turbo|\.cache|storage|public)\//;

/** Whether a project path is one of the code files the TypeScript service is given (not a bundle's, a build's or an environment's). */
export const isCodePath = (path) => CODE.test(path) && !SKIPPED.test(path) && !/\.min\./i.test(path);

/**
 * What a batch of changes on disk (/api/watch's `{kind, path}`) asks of the project: `project` (read the files and the config again: one
 * was added or removed, or a config changed), `packages` (node_modules or package.json changed), and `files` (code files whose text
 * changed, read one by one).
 */
export function planRefresh(events) {
    const plan = { project: false, packages: false, files: [] };
    for (const e of events ?? []) {
        const path = String(e.path ?? '');
        const name = path.split('/').pop();
        if (e.kind === 'all') { plan.project = plan.packages = true; continue; }
        if (e.kind === 'node' || name === 'package.json' && !path.includes('node_modules/')) { plan.packages = true; continue; }
        if (/^(tsconfig|jsconfig)(\.[\w.-]+)?\.json$/i.test(name) && !path.includes('node_modules/')) { plan.project = true; continue; }
        if (!isCodePath(path)) continue;
        if (e.kind === 'changed') { if (!plan.files.includes(path)) plan.files.push(path); }
        else plan.project = true;
    }
    if (plan.project) plan.files = [];
    return plan;
}

// ------------------------------------------------------------ several configs: the one that covers a file

/** A tsconfig pattern (src, src/**\/*, **\/*.test.ts, vite.config.ts), written from the project's folder, as a test of a project path. */
function patternTest(pattern) {
    const p = String(pattern).replace(/^\.\//, '').replace(/\/+$/, '');
    if (p === '' || p === '.') return () => true;
    if (!/[*?]/.test(p)) return (path) => path === p || path.startsWith(p + '/');      // a file, or a folder with all it holds
    const source = p.split('/').map((part) => (part === '**' ? '\u0000' : part.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '[^/]*').replace(/\?/g, '[^/]')))
        .join('/').replace(/\u0000\//g, '(?:.*/)?').replace(/\/\u0000/g, '(?:/.*)?').replace(/\u0000/g, '.*');
    // (a pattern that ends in a folder's name takes what the folder holds)
    const re = new RegExp('^' + source + '(?:/.*)?$');
    return (path) => re.test(path);
}

/** Whether a config (/api/ts/project's configs: { path, dir, include, exclude, files, references }) is the one a project file is checked by. */
export function covers(config, path) {
    if ((config.files ?? []).includes(path)) return true;
    const inDir = !config.dir || path.startsWith(config.dir + '/');
    if ((config.exclude ?? []).some((x) => patternTest(x)(path))) return false;
    if (config.include) return config.include.some((x) => patternTest(x)(path));
    // no include: everything under its folder, unless it names its files (a solution's "files": [])
    return !config.files && inDir && !/(^|\/)node_modules\//.test(path);
}

/**
 * The config a project file is checked by: the nearest folder's above it; there the config a solution's tsconfig.json refers to
 * (tsconfig.app.json for src, tsconfig.node.json for vite.config.ts), else the folder's tsconfig.json or jsconfig.json when it covers
 * the file, else another of the folder's that does. None covers it: the project's own tsconfig.json (null when it has none).
 */
export function configFor(path, configs) {
    const list = configs ?? [];
    const byPath = new Map(list.map((c) => [c.path, c]));
    const main = (c) => /(^|\/)(tsconfig|jsconfig)\.json$/i.test(c.path);
    const dirs = [...new Set(list.map((c) => c.dir ?? ''))].filter((d) => !d || path.startsWith(d + '/')).sort((a, b) => b.length - a.length);
    for (const dir of dirs) {
        const here = list.filter((c) => (c.dir ?? '') === dir);
        for (const c of here.filter(main)) {
            for (const r of c.references ?? []) { const to = byPath.get(r); if (to && covers(to, path)) return to; }
            if (covers(c, path)) return c;
        }
        for (const c of here.filter((x) => !main(x))) if (covers(c, path)) return c;
    }
    return list.find((c) => !c.dir && main(c)) ?? null;
}

// ------------------------------------------------------------ a project too large to be given whole: what a file imports

/** What a file imports: { files: the project paths a relative import may be (in the order TypeScript tries them), packages: the packages' names }. */
export function importsOf(path, text) {
    const dir = path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '';
    const files = [], packages = [];
    const specs = new Set();
    for (const m of String(text).matchAll(/(?:\bfrom\s*|\bimport\s*\(\s*|\brequire\s*\(\s*|\bimport\s+)(["'])([^"'\n]+)\1/g)) specs.add(m[2]);
    for (const spec of specs) {
        if (spec.startsWith('.')) {
            const parts = (dir ? dir.split('/') : []);
            for (const part of spec.split('/')) { if (part === '..') parts.pop(); else if (part && part !== '.') parts.push(part); }
            const base = parts.join('/').replace(/\.(js|jsx|mjs|cjs)$/, '');      // (./a.js is a.ts to TypeScript)
            const own = parts.join('/');
            files.push([...new Set([own, ...['.ts', '.tsx', '.d.ts', '.js', '.jsx', '.mts', '.mjs'].map((e) => base + e), ...['/index.ts', '/index.tsx', '/index.js', '/index.d.ts'].map((e) => own + e)])]
                .filter((p) => CODE.test(p)));
        }
        else if (!/^[a-z]+:/.test(spec) && !spec.startsWith('/') && !spec.startsWith('#') && !spec.startsWith('@/') && !spec.startsWith('~')) {
            const name = spec.startsWith('@') ? spec.split('/').slice(0, 2).join('/') : spec.split('/')[0];
            if (/^(@[\w.-]+\/)?[\w.-]+$/.test(name) && !packages.includes(name)) packages.push(name);
        }
    }
    return { files, packages };
}

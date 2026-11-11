// The TypeScript service's project, without Monaco: the compiler options a tsconfig.json (or jsconfig.json) asks for, as Monaco's TypeScript
// worker takes them (its enums as numbers, its libraries as file names, paths under the project's own addresses), and which changed files
// ask for the project's files or its packages' types to be read again. tsproject.js gives them to Monaco.

/** The address of a project file as the TypeScript service sees the editor's models (pymagic:/path: Monaco writes no empty authority), which it resolves imports in. */
export const ROOT = 'pymagic:/';

const TARGETS = { es3: 0, es5: 1, es6: 2, es2015: 2, es2016: 3, es2017: 4, es2018: 5, es2019: 6, es2020: 7, es2021: 8, es2022: 9, es2023: 10, es2024: 11, esnext: 99 };
const MODULES = { none: 0, commonjs: 1, amd: 2, umd: 3, system: 4, es6: 5, es2015: 5, es2020: 6, es2022: 7, esnext: 99, node16: 100, node18: 101, nodenext: 199, preserve: 200 };
const RESOLUTIONS = { classic: 1, node: 2, node10: 2, node16: 3, nodenext: 99, bundler: 100 };
const JSX = { preserve: 1, react: 2, 'react-native': 3, 'react-jsx': 4, 'react-jsxdev': 5 };
const DETECTION = { legacy: 1, auto: 2, force: 3 };
// what only building writes or reads, not what the editor checks
const LEFT_OUT = new Set(['outDir', 'outFile', 'rootDir', 'rootDirs', 'declarationDir', 'tsBuildInfoFile', 'composite', 'incremental', 'plugins', 'pathsBase', 'emitDeclarationOnly', 'sourceRoot', 'mapRoot']);

/** Without a tsconfig.json: what a project of today's tools (Vite, Node) is checked with. */
export const DEFAULT_OPTIONS = {
    target: TARGETS.es2022, module: MODULES.esnext, moduleResolution: RESOLUTIONS.bundler, jsx: JSX['react-jsx'],
    allowJs: true, esModuleInterop: true, allowSyntheticDefaultImports: true, skipLibCheck: true, resolveJsonModule: true,
};

const under = (rel) => ROOT + String(rel ?? '').replace(/^\.\/?/, '').replace(/^\/+/, '').replace(/\/+$/, '');

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

// Run configurations without the page: what one is, how a stored list is read, and what the run is told for one.
import { splitArguments } from './django-model.js';

/** The kinds of configuration, as PyCharm's: [kind, label]. */
export const KINDS = [['script', 'Python script'], ['module', 'Python module (-m)'], ['django', 'Django server'], ['fastapi', 'FastAPI (uvicorn)'], ['flask', 'Flask server'], ['pytest', 'pytest']];

/** The settings beside name, script, args, cwd and env, with their defaults (strings and switches). */
const DEFAULTS = {
    kind: 'script', module: '', app: '', host: '127.0.0.1', port: '', target: '', reload: true,
    envFiles: '', uv: false, uvPath: '', contentRoots: true, sourceRoots: true, shared: false, openPanel: true,
};

/** A configuration: { name, kind, script (a file of the project), module, app, host, port, target, reload, args (as typed), cwd (a folder of the project,
 * '' for the project itself), env ({ NAME: value }), envFiles (.env files, comma separated), uv, uvPath, contentRoots, sourceRoots, shared, openPanel }. */
export const blank = (name = 'New configuration', kind = 'script') => ({ name, script: kind === 'django' ? 'manage.py' : '', args: '', cwd: '', env: {}, ...DEFAULTS, kind,
    app: kind === 'fastapi' ? 'main:app' : kind === 'flask' ? 'app' : '', port: { django: '8000', fastapi: '8000', flask: '5000' }[kind] ?? '' });

/** The stored text, as a list of well-formed configurations and the name of the chosen one (or null for "the file in the editor"). */
export function read(stored) {
    const list = [];
    const seen = new Set();
    for (let c of Array.isArray(stored?.configurations) ? stored.configurations : []) {
        if (!c || typeof c.name !== 'string' || !c.name.trim() || (typeof c.script !== 'string' && typeof c.kind !== 'string')) continue;
        if (typeof c.script !== 'string') c = { ...c, script: '' };
        const name = c.name.trim();
        if (seen.has(name)) continue;
        seen.add(name);
        const env = {};
        if (c.env && typeof c.env === 'object' && !Array.isArray(c.env))
            for (const [k, v] of Object.entries(c.env)) if (typeof v === 'string') env[k] = v;
        const extra = {};
        for (const [k, d] of Object.entries(DEFAULTS))
            extra[k] = typeof c[k] === typeof d ? c[k] : d;
        if (!KINDS.some(([k]) => k === extra.kind))
            extra.kind = 'script';
        list.push({ name, script: c.script, args: typeof c.args === 'string' ? c.args : '', cwd: typeof c.cwd === 'string' ? c.cwd : '', env, ...extra });
    }
    const chosen = typeof stored?.selected === 'string' && seen.has(stored.selected) ? stored.selected : null;
    return { configurations: list, selected: chosen };
}

/** What the start message of a run carries for this configuration: a file or a module, its arguments, and the run's settings. */
export function startOf(c) {
    const extra = splitArguments(c.args ?? '');
    const host = c.host || '127.0.0.1';
    let start;
    switch (c.kind) {
        case 'module': start = { module: c.module, args: extra }; break;
        case 'django': start = { file: c.script || 'manage.py', args: ['runserver', c.port ? `${host}:${c.port}` : host === '127.0.0.1' ? '8000' : `${host}:8000`, ...(c.reload === false ? ['--noreload'] : []), ...extra] }; break;
        case 'fastapi': start = { module: 'uvicorn', args: [c.app || 'main:app', '--host', host, '--port', c.port || '8000', ...(c.reload !== false ? ['--reload'] : []), ...extra] }; break;
        case 'flask': start = { module: 'flask', args: ['--app', c.app || 'app', 'run', '--host', host, '--port', c.port || '5000', ...(c.reload !== false ? ['--debug'] : []), ...extra] }; break;
        case 'pytest': start = { module: 'pytest', args: [...(c.target ? [c.target] : []), ...extra] }; break;
        default: start = { file: c.script, args: extra };
    }
    const out = { ...start, env: { ...c.env }, cwd: c.cwd };
    const envFiles = (c.envFiles ?? '').split(',').map((s) => s.trim()).filter(Boolean);
    if (envFiles.length) out.envFiles = envFiles;
    if (c.uv) {
        out.uv = true;
        if (c.uvPath) out.uvPath = c.uvPath;
    }
    if (c.contentRoots === false) out.contentRoots = false;
    if (c.sourceRoots === false) out.sourceRoots = false;
    return out;
}

/** The configurations kept with the project's files (Store as project file: .run/pymagic.json, for Git) and the ones kept in .pymagic. */
export function splitShared(list) {
    return { shared: list.filter((c) => c.shared), own: list.filter((c) => !c.shared) };
}

/** "KEY=value" lines to an object (a line without = , an empty name or a comment is left out). */
export function parseEnv(text) {
    const env = {};
    for (const line of text.split(/\r?\n/)) {
        const t = line.trim();
        if (!t || t.startsWith('#')) continue;
        const at = t.indexOf('=');
        if (at <= 0) continue;
        env[t.slice(0, at).trim()] = t.slice(at + 1).trim();
    }
    return env;
}

export function formatEnv(env) {
    return Object.entries(env).map(([k, v]) => `${k}=${v}`).join('\n');
}

/** A name that no configuration has yet: "Name", then "Name 2", "Name 3". */
export function uniqueName(list, base) {
    const taken = new Set(list.map((c) => c.name));
    if (!taken.has(base)) return base;
    for (let n = 2; ; n++) if (!taken.has(`${base} ${n}`)) return `${base} ${n}`;
}

export function toStored(configurations, selected) {
    return { selected, configurations };
}

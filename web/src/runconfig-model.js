// Run configurations without the page: what one is, how a stored list is read, and what the run is told for one.
import { splitArguments } from './django-model.js';

/** The kinds of configuration, as PyCharm's: [kind, label]. */
export const KINDS = [['script', 'Python script'], ['module', 'Python module (-m)'], ['django', 'Django server'], ['djangoTests', 'Django tests'], ['fastapi', 'FastAPI (uvicorn)'], ['flask', 'Flask server'], ['pytest', 'pytest'], ['debugserver', 'Python Debug Server (a program connects back)'],
    ['node', 'Node.js (a .js or .ts file)'], ['npm', 'npm script (package.json: dev, start, …)'], ['browser', 'JavaScript Debug (a page in the browser)'], ['nodeAttach', 'Attach to Node.js (a program running with --inspect)'],
    ['laravel', 'Laravel server (php artisan serve)'], ['artisan', 'Artisan command (php artisan …)'], ['php', 'PHP script'], ['phpTests', 'PHP tests (PHPUnit or Pest)'],
    ['http', 'HTTP request (.http file)'], ['compound', 'Compound (several together)']];
/** The kinds run by Node rather than Python: no interpreter, uv or PYTHONPATH of theirs. */
export const NODE_KINDS = new Set(['node', 'npm', 'browser', 'nodeAttach']);
/** The kinds run by PHP: nothing of Python's either. */
export const PHP_KINDS = new Set(['laravel', 'artisan', 'php', 'phpTests']);

/** The settings beside name, script, args, cwd and env, with their defaults (strings and switches). */
const DEFAULTS = {
    kind: 'script', module: '', app: '', host: '127.0.0.1', port: '', target: '', reload: true,
    envFiles: '', interpreter: '', uv: false, uvPath: '', contentRoots: true, sourceRoots: true, shared: false, openPanel: true,
    // Django: the settings module, a test server with fixtures, the browser opened once the server is up, debugging only the project's code,
    // log files shown in the console, another configuration run first, and --failfast for the tests
    settings: '', testServer: false, fixtures: '', openBrowser: false, justMyCode: true, logFiles: '', before: '', failfast: false,
    // Compound: the names of the configurations it starts together, one per line
    members: '',
    // Python Debug Server: the address the program reaches this computer by (for the snippet), and the folder pairs `local=remote`, one per line
    connectHost: '', mappings: '',
    // HTTP request: the request of the .http file (its script) by its name or its `METHOD url` line, '' for all of them in turn, and the environment
    request: '', environment: '',
    // Node.js and npm: the script of package.json to run, the folder of that package.json ('' for the project's), and Node itself ('' for the PATH's)
    npmScript: '', packageDir: '', node: '',
    // JavaScript Debug: the page to open (a dev server's), and the browser ('' for Edge, else Chrome); packageDir is its web root
    url: '', browserPath: '',
    // Artisan command: the command and its arguments as typed (migrate --seed, make:model Post -m)
    command: '',
    // PHP tests: the runner ('' the project's: Pest when it has it, else PHPUnit); target is a folder, a file or one test as the Tests panel names it
    phpRunner: '',
};

/** A configuration: { name, kind, script (a file of the project), module, app, host, port, target, reload, args (as typed), cwd (a folder of the project,
 * '' for the project itself), env ({ NAME: value }), envFiles (.env files, comma separated), uv, uvPath, contentRoots, sourceRoots, shared, openPanel }. */
export const blank = (name = 'New configuration', kind = 'script') => ({ name, script: kind === 'django' ? 'manage.py' : '', args: '', cwd: '', env: {}, ...DEFAULTS, kind,
    app: kind === 'fastapi' ? 'main:app' : kind === 'flask' ? 'app' : '', port: { django: '8000', fastapi: '8000', flask: '5000', debugserver: '5678', laravel: '8000' }[kind] ?? '' });

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

/** The configurations a compound starts together, one by one in the order named: {members, skipped}. A name that is no configuration, the compound itself, another compound
 * and the tests (they have their own panel) are skipped, each with the reason. `find(name)` gives a configuration by name. */
export function compoundMembers(c, find) {
    const members = [];
    const skipped = [];
    const seen = new Set();
    for (const raw of String(c?.members ?? "").split(/\r?\n/)) {
        const name = raw.trim();
        if (!name || seen.has(name)) continue;
        seen.add(name);
        const m = find(name);
        if (!m) skipped.push({ name, why: 'there is no such configuration' });
        else if (m.name === c.name) skipped.push({ name, why: 'a compound cannot start itself' });
        else if (m.kind === 'compound') skipped.push({ name, why: 'a compound cannot start another compound' });
        else if (m.kind === 'djangoTests') skipped.push({ name, why: 'tests run in the Tests panel' });
        else if (m.kind === 'debugserver') skipped.push({ name, why: 'a debug server needs the debugger' });
        else members.push(m);
    }
    return { members, skipped };
}

/** The kinds of task of Before launch, as the lines of the box start: [prefix, label, placeholder]. A line with none of them names another configuration. */
export const BEFORE_TASKS = [['', 'Run another configuration', 'migrate'], ['manage.py ', 'Run a manage.py command', 'manage.py collectstatic --noinput'], ['browser ', 'Launch the web browser', 'browser http://127.0.0.1:8000/']];

/**
 * What runs before a configuration, in order, from its Before launch lines: {steps, skipped}. A step is {config} (another configuration, after its own
 * Before launch), {manage: [args]} (a command of the project's manage.py: the configuration's own for a Django server, else manage.py) or {browser: url}.
 * A name that is no configuration, a configuration that would run itself again (directly or through another one), a compound, tests and a debug server
 * are skipped with the reason. `find(name)` gives a configuration by name. One configuration's name alone (the field as it was) is one step.
 */
export function beforeSteps(c, find) {
    const steps = [];
    const skipped = [];
    const manage = c?.kind === 'django' && c.script ? c.script : 'manage.py';
    const walk = (of, path) => {
        for (const raw of String(of?.before ?? '').split(/\r?\n/)) {
            const line = raw.trim();
            if (!line || line.startsWith('#')) continue;
            const words = splitArguments(line);
            if (/^manage\.py$/i.test(words[0] ?? '')) {
                if (words.length > 1) steps.push({ manage: words.slice(1), file: manage, label: line });
                continue;
            }
            if (/^browser$/i.test(words[0] ?? '') || /^https?:\/\//i.test(line)) {
                const url = /^https?:\/\//i.test(line) ? line : words[1] ?? '';
                if (/^https?:\/\/\S+$/i.test(url)) steps.push({ browser: url, label: `browser ${url}` });
                else skipped.push({ name: line, why: 'the browser needs an http:// or https:// address' });
                continue;
            }
            const m = find(line);
            if (!m) skipped.push({ name: line, why: 'there is no such configuration' });
            else if (path.includes(m.name)) skipped.push({ name: line, why: 'it would run itself again' });
            else if (m.kind === 'compound') skipped.push({ name: line, why: 'a compound does not finish by itself' });
            else if (m.kind === 'djangoTests') skipped.push({ name: line, why: 'tests run in the Tests panel' });
            else if (m.kind === 'debugserver') skipped.push({ name: line, why: 'a debug server needs the debugger' });
            else if (m.kind === 'http') skipped.push({ name: line, why: 'an HTTP request runs in the HTTP tab' });
            else {
                walk(m, [...path, m.name]);
                steps.push({ config: m, label: m.name });
            }
        }
    };
    walk(c, [c?.name]);
    return { steps, skipped };
}

/** The port of a debug server: a number from 0 (a free one) to 65535, 5678 when empty or not a number. */
export function portOf(text) {
    const t = String(text ?? '').trim();
    const n = Number(t);
    return t !== '' && Number.isInteger(n) && n >= 0 && n <= 65535 ? n : 5678;
}

/** Path mappings typed as lines `local=remote` (also `local => remote`; a line starting with # is a comment): [{local, remote}]. A local folder may be empty or
 * relative (the project's). A line without a remote folder is left out. */
export function parseMappings(text) {
    const out = [];
    for (const raw of String(text ?? '').split(/\r?\n/)) {
        const line = raw.trim();
        if (!line || line.startsWith('#')) continue;
        const m = /^(.*?)\s*(?:=>|=)\s*(.+)$/.exec(line);
        if (m && m[2].trim()) out.push({ local: m[1].trim(), remote: m[2].trim() });
    }
    return out;
}

/** The lines of mappings, as they are typed. */
export function formatMappings(list) {
    return list.map((m) => `${m.local}=${m.remote}`).join('\n');
}

/** What the start message of a run carries for this configuration: a file or a module, its arguments, and the run's settings. */
export function startOf(c) {
    const extra = splitArguments(c.args ?? '');
    const host = c.host || '127.0.0.1';
    let start;
    switch (c.kind) {
        case 'module': start = { module: c.module, args: extra }; break;
        case 'django': {
            const address = c.port ? `${host}:${c.port}` : host === '127.0.0.1' ? '8000' : `${host}:8000`;
            const fixtures = (c.fixtures ?? '').split(/[\s,]+/).filter(Boolean);
            start = c.testServer
                ? { file: c.script || 'manage.py', args: ['testserver', ...fixtures, '--addrport', address, '--noinput', ...extra] }
                : { file: c.script || 'manage.py', args: ['runserver', address, ...(c.reload === false ? ['--noreload'] : []), ...extra] };
            break;
        }
        case 'djangoTests': start = { tests: { ids: c.target ? [c.target] : [], framework: 'django', settings: c.settings || undefined, failfast: !!c.failfast } }; break;
        case 'fastapi': start = { module: 'uvicorn', args: [c.app || 'main:app', '--host', host, '--port', c.port || '8000', ...(c.reload !== false ? ['--reload'] : []), ...extra] }; break;
        case 'flask': start = { module: 'flask', args: ['--app', c.app || 'app', 'run', '--host', host, '--port', c.port || '5000', ...(c.reload !== false ? ['--debug'] : []), ...extra] }; break;
        case 'debugserver': start = { debugServer: { host, port: portOf(c.port), connectHost: (c.connectHost ?? '').trim(), mappings: parseMappings(c.mappings) } }; break;
        case 'pytest': start = { module: 'pytest', args: [...(c.target ? [c.target] : []), ...extra] }; break;
        case 'http': start = { http: { file: c.script, request: c.request ?? '', environment: c.environment ?? '' } }; break;
        case 'node': start = { file: c.script, args: extra, ...(c.node ? { node: c.node } : {}) }; break;
        case 'npm': start = { npmScript: c.npmScript || 'start', ...(c.packageDir ? { packageDir: c.packageDir } : {}), args: extra, ...(c.node ? { node: c.node } : {}) }; break;
        case 'browser': start = { browser: { url: c.url || 'http://localhost:5173/', ...(c.packageDir ? { webRoot: c.packageDir } : {}), ...(c.browserPath ? { browserPath: c.browserPath } : {}) } }; break;
        case 'nodeAttach': start = { nodeAttach: { host: c.host || '127.0.0.1', port: Number(c.port) || 9229, ...(c.packageDir ? { webRoot: c.packageDir } : {}) } }; break;
        case 'laravel': start = { artisan: ['serve', `--host=${host}`, `--port=${c.port || '8000'}`, ...extra] }; break;
        case 'artisan': start = { artisan: [...splitArguments(c.command || 'list'), ...extra] }; break;
        case 'php': start = { file: c.script, args: extra }; break;
        case 'phpTests': start = { tests: { ids: c.target ? [c.target] : [], framework: c.phpRunner || 'auto' } }; break;
        default: start = { file: c.script, args: extra };
    }
    if (NODE_KINDS.has(c.kind) || PHP_KINDS.has(c.kind)) {
        // Node's: its variables, folder and the debugger's choice of code; nothing of Python's
        const out = { ...start, env: { ...c.env }, cwd: c.cwd };
        if (c.justMyCode === false) out.allCode = true;
        return out;
    }
    const out = { ...start, env: { ...c.env, ...(c.settings && (c.kind === 'django' || c.kind === 'djangoTests') ? { DJANGO_SETTINGS_MODULE: c.settings } : {}) }, cwd: c.cwd };
    if (c.justMyCode === false) out.allCode = true;
    const envFiles = (c.envFiles ?? '').split(',').map((s) => s.trim()).filter(Boolean);
    if (envFiles.length) out.envFiles = envFiles;
    if (c.uv) {
        out.uv = true;
        if (c.uvPath) out.uvPath = c.uvPath;
    }
    if (c.interpreter) out.interpreter = c.interpreter;
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

/**
 * The configuration a project gets the first time it is opened (nothing stored, nothing shared), as PyCharm does when it finds Django:
 * a Django server with the project's manage.py and settings module, from /api/django. Null when there is no Django or a configuration is kept already.
 */
export function firstConfiguration(stored, shared, django) {
    if (stored || shared || !django?.found || !django.manage) return null;
    return { ...blank('Django server', 'django'), script: String(django.manage).replace(/\\/g, '/'), settings: django.settingsModule ?? '' };
}

export function toStored(configurations, selected) {
    return { selected, configurations };
}

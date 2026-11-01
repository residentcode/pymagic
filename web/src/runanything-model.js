// Run Anything without the page: what a line typed in the box means, and what it suggests. No DOM here: web/test/runanything.test.mjs checks it.

/** The words of a command line, with "quoted words" kept whole. */
export function splitArgs(text) {
    const out = [];
    const re = /"([^"]*)"|'([^']*)'|(\S+)/g;
    let m;
    while ((m = re.exec(text))) out.push(m[1] ?? m[2] ?? m[3]);
    return out;
}

/**
 * What a line says to do: {kind: 'module', module, args} (python -m), {kind: 'pip', args}, {kind: 'manage', args} (manage.py ...),
 * {kind: 'script', file, args} (a .py file), {kind: 'project', name} (open ...), or {kind: 'name', text} (a run configuration, or nothing).
 */
export function parseCommand(text) {
    const tokens = splitArgs(text.trim());
    if (!tokens.length) return { kind: 'name', text: '' };
    if (/^(python3?|py)$/i.test(tokens[0]) || /^python3?\.exe$/i.test(tokens[0])) tokens.shift();
    if (!tokens.length) return { kind: 'name', text: '' };
    if (tokens[0] === '-m') return tokens[1] ? { kind: 'module', module: tokens[1], args: tokens.slice(2) } : { kind: 'name', text: text.trim() };
    if (tokens[0] === 'pip' || tokens[0] === 'pip3') return { kind: 'pip', args: tokens.slice(1) };
    if (/^(manage|manage\.py|python manage\.py)$/i.test(tokens[0]) || /(^|[\\/])manage\.py$/i.test(tokens[0])) return { kind: 'manage', args: tokens.slice(1) };
    if (/\.py$/i.test(tokens[0])) return { kind: 'script', file: tokens[0].replace(/\\/g, '/'), args: tokens.slice(1) };
    if (/^open$/i.test(tokens[0]) && tokens.length > 1) return { kind: 'project', name: tokens.slice(1).join(' ') };
    return { kind: 'name', text: text.trim() };
}

export const MANAGE_COMMANDS = ['runserver', 'migrate', 'makemigrations', 'createsuperuser', 'shell', 'test', 'collectstatic', 'check', 'showmigrations', 'sqlmigrate',
    'startapp', 'startproject', 'dbshell', 'flush', 'loaddata', 'dumpdata', 'changepassword', 'inspectdb', 'diffsettings', 'sendtestemail', 'squashmigrations', 'compilemessages', 'makemessages'];
export const PIP_COMMANDS = ['install', 'uninstall', 'list', 'show', 'freeze', 'check', 'download', 'index', 'config', 'cache'];

/** How well a name matches what was typed: 0 for no match, higher is better (a start beats the inside, which beats letters in order). */
export function score(name, typed) {
    const n = name.toLowerCase(), t = typed.toLowerCase();
    if (!t) return 1;
    if (n === t) return 100;
    if (n.startsWith(t)) return 80;
    const parts = n.split(/[\s/_.-]+/);
    if (parts.some((p) => p.startsWith(t))) return 60;
    if (n.includes(t)) return 40;
    let k = 0;
    for (const ch of n) if (ch === t[k]) k++;
    return k === t.length ? 20 : 0;
}

/**
 * The suggestions for a line: [{kind, label, detail, fill}] best first. `ctx`: {configs: [names], scripts: [paths of .py files], manage: path of
 * manage.py or null, recents: [{name, path}]}. `fill` is what Tab puts in the box.
 */
export function suggest(text, ctx, limit = 14) {
    const line = text.trim();
    const items = [];
    const add = (kind, label, detail, fill, s) => { if (s > 0) items.push({ kind, label, detail, fill, s }); };
    const cmd = parseCommand(line);
    // after "manage.py " or "pip ", the commands of those
    if (/^(python3?\s+)?(manage(\.py)?|\S*manage\.py)\s+/i.test(line) || (cmd.kind === 'manage' && /\s$/.test(text))) {
        const typed = /\s(\S*)$/.exec(line)?.[1] ?? '';
        const head = line.slice(0, line.length - typed.length);
        const only = cmd.args.length <= 1 ? MANAGE_COMMANDS : [];
        for (const c of only) add('manage', c, 'manage.py', head + c, score(c, typed));
        return rank(items, limit);
    }
    if (/^pip3?\s+/i.test(line)) {
        const typed = /\s(\S*)$/.exec(line)?.[1] ?? '';
        const head = line.slice(0, line.length - typed.length);
        for (const c of cmd.args.length <= 1 ? PIP_COMMANDS : []) add('pip', c, 'pip', head + c, score(c, typed));
        return rank(items, limit);
    }
    if (/^open\s+/i.test(line)) {
        const typed = line.replace(/^open\s+/i, '');
        for (const p of ctx.recents ?? []) add('project', p.name, p.path, 'open ' + p.name, score(p.name, typed));
        return rank(items, limit);
    }
    const typed = line;
    for (const c of ctx.configs ?? []) { const sc = score(c, typed); add('config', c, 'run configuration', c, sc > 0 ? sc + 5 : 0); }
    for (const s of ctx.scripts ?? []) add('script', s, 'script', s, score(s, typed));
    if (ctx.manage) for (const c of MANAGE_COMMANDS) add('manage', `manage.py ${c}`, 'Django', `manage.py ${c}`, score(`manage.py ${c}`, typed) - (typed.includes(' ') ? 0 : 15));
    for (const c of ['pip install', 'pip uninstall', 'pip list', 'pip freeze']) add('pip', c, 'packages', c + ' ', score(c, typed) - 10);
    add('module', 'python -m ', 'run a module', 'python -m ', typed && 'python -m'.startsWith(typed.toLowerCase()) ? 70 : 0);
    if (typed && 'open '.startsWith(typed.toLowerCase())) add('project', 'open …', 'a recent project', 'open ', 70);
    return rank(items, limit);
}

function rank(items, limit) {
    const seen = new Set();
    return items.sort((a, b) => b.s - a.s || a.label.localeCompare(b.label)).filter((i) => { const k = i.kind + '|' + i.label; return !seen.has(k) && seen.add(k); })
        .slice(0, limit).map(({ s, ...rest }) => rest);
}

/** Whether a key sequence was Control pressed and released twice in a row with no other key between, and how fast. */
export function doubleTap(state, event, now, within = 450) {
    if (event.type === 'keydown') {
        if (event.key !== 'Control') state.other = true;
        else if (!event.repeat) state.other = false;
        return false;
    }
    if (event.type === 'keyup' && event.key === 'Control') {
        if (state.other) { state.last = 0; return false; }
        const hit = state.last && now - state.last <= within;
        state.last = hit ? 0 : now;
        return !!hit;
    }
    return false;
}

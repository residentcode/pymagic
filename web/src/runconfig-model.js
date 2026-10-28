// Run configurations without the page: what one is, how a stored list is read, and what the run is told for one.
import { splitArguments } from './django-model.js';

/** A configuration: { name, script (a file of the project), args (as typed), cwd (a folder of the project, '' for the project itself), env ({ NAME: value }) }. */
export const blank = (name = 'New configuration') => ({ name, script: '', args: '', cwd: '', env: {} });

/** The stored text, as a list of well-formed configurations and the name of the chosen one (or null for "the file in the editor"). */
export function read(stored) {
    const list = [];
    const seen = new Set();
    for (const c of Array.isArray(stored?.configurations) ? stored.configurations : []) {
        if (!c || typeof c.name !== 'string' || !c.name.trim() || typeof c.script !== 'string') continue;
        const name = c.name.trim();
        if (seen.has(name)) continue;
        seen.add(name);
        const env = {};
        if (c.env && typeof c.env === 'object' && !Array.isArray(c.env))
            for (const [k, v] of Object.entries(c.env)) if (typeof v === 'string') env[k] = v;
        list.push({ name, script: c.script, args: typeof c.args === 'string' ? c.args : '', cwd: typeof c.cwd === 'string' ? c.cwd : '', env });
    }
    const chosen = typeof stored?.selected === 'string' && seen.has(stored.selected) ? stored.selected : null;
    return { configurations: list, selected: chosen };
}

/** What the start message of a run carries for this configuration. */
export function startOf(c) {
    return { file: c.script, args: splitArguments(c.args), env: { ...c.env }, cwd: c.cwd };
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

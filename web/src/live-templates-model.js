// Live templates (Settings > Templates), as data: the built-in groups (live-templates-data.js and the language server's Python, postfix and
// Django template ones), what the person changed (settings.templates: the ones switched off, the built-in ones edited, their own templates and
// groups, the key that expands), where a template applies, and which one the text before the cursor names. No DOM and no Monaco here.
import { GROUPS } from './live-templates-data.js';

/** Where a template can apply: [id, name, the editor's languages]. "other" is every file. */
export const CONTEXTS = [
    ['python', 'Python', ['python']],
    ['template', 'Django and Jinja templates', ['django-html']],
    ['javascript', 'JavaScript and TypeScript', ['javascript', 'typescript']],
    ['typescript', 'TypeScript', ['typescript']],
    ['html', 'HTML', ['html', 'django-html', 'ejs', 'handlebars', 'twig', 'razor', 'blade', 'liquid']],
    ['xml', 'XML', ['xml']],
    ['css', 'CSS, SCSS and Less', ['css', 'scss', 'less']],
    ['sql', 'SQL', ['sql', 'mysql', 'pgsql']],
    ['shell', 'Shell script', ['shell']],
    ['http', 'HTTP request', ['http']],
    ['php', 'PHP', ['php']],
    ['blade', 'Blade', ['blade']],
    ['json', 'JSON', ['json']],
    ['yaml', 'YAML', ['yaml']],
    ['other', 'Everywhere', []],
];
const CONTEXT_IDS = new Set(CONTEXTS.map((c) => c[0]));

export const EXPAND_KEYS = [['tab', 'Tab'], ['enter', 'Enter'], ['space', 'Space']];
const KEY_IDS = new Set(EXPAND_KEYS.map((k) => k[0]));
export const keyName = (id) => EXPAND_KEYS.find((k) => k[0] === id)?.[1] ?? 'Tab';

/** The names of a template's contexts, as the page says them. */
export const contextNames = (ids) => CONTEXTS.filter((c) => ids.includes(c[0])).map((c) => c[1]);

/** Whether a template is one for a file of this language. */
export function applicable(t, languageId) {
    if (t.languages) return t.languages.includes(languageId);     // a template of the project, by the languages its scope names
    return t.contexts.some((id) => id === 'other' || CONTEXTS.find((c) => c[0] === id)?.[2].includes(languageId));
}

// ------------------------------------------------------------ the text of a template

const NAMED = /\$([A-Za-z_]\w*)\$/g;
const HAS_NAMED = new RegExp(NAMED.source);
const literal = (s) => s.replace(/[\\$}]/g, '\\$&');

/**
 * What a place's expression can be (Edit Variables): [what is typed, what it gives, the editor's snippet for it, a sample for the page].
 * The last five take the name of another place and follow what is typed there.
 */
export const EXPRESSIONS = [
    ['fileName()', 'the file\'s name', '${TM_FILENAME}', 'views.py'],
    ['fileNameWithoutExtension()', 'the file\'s name without its extension', '${TM_FILENAME_BASE}', 'views'],
    ['date()', 'today\'s date', '${CURRENT_YEAR}-${CURRENT_MONTH}-${CURRENT_DATE}', '2026-01-31'],
    ['time()', 'the time now', '${CURRENT_HOUR}:${CURRENT_MINUTE}', '12:00'],
    ['year()', 'this year', '${CURRENT_YEAR}', '2026'],
    ['lineNumber()', 'the line\'s number', '${TM_LINE_NUMBER}', '1'],
    ['clipboard()', 'the clipboard\'s text', '${CLIPBOARD}', 'clipboard'],
    ['capitalize(name)', 'another place, its first letter a capital', 'capitalize', null],
    ['upperCase(name)', 'another place in capitals', 'upcase', null],
    ['lowerCase(name)', 'another place in small letters', 'downcase', null],
    ['camelCase(name)', 'another place as camelCase', 'camelcase', null],
    ['pascalCase(name)', 'another place as PascalCase', 'pascalcase', null],
    ['snakeCase(name)', 'another place as snake_case'], ['capitalizeAndUnderscore(name)', 'another place as CAPITALS_WITH_UNDERSCORES'],
    ['lowercaseAndDash(name)', 'another place as lower-case-with-dashes'], ['spaceSeparated(name)', 'another place as words with spaces'],
    ['underscoresToCamelCase(name)', 'another place, its _x as X'], ['underscoresToSpaces(name)', 'another place, its underscores as spaces'],
    ['spacesToUnderscores(name)', 'another place, its spaces as underscores'], ['decapitalize(name)', 'another place, its first letter small'],
    ['firstWord(name)', 'the first word of another place'], ['substringBefore(name, ".")', 'another place up to a text'],
    ['regularExpression(name, "search", "replacement")', 'another place with every match of a regular expression replaced ($1 is its first group)'],
    ['complete()', 'completion is offered when the place is reached'],
];
// the functions that follow another place through a search and its replacement (the editor's ${1/search/replacement/flags}):
// [search, replacement, flags, the same for the page's preview]
const WORDS = (s) => s.replace(/([a-z0-9])([A-Z])/g, '$1 $2').split(/[\s_-]+/).filter(Boolean);
const FOLLOW = {
    snakeCase: ['([a-z0-9])([A-Z])|([\\s-]+)|([A-Z])', '$1${2:+_}${2:/downcase}${3:+_}${4:/downcase}', 'g', (s) => WORDS(s).join('_').toLowerCase()],
    capitalizeAndUnderscore: ['([a-z0-9])([A-Z])|([\\s-]+)|(.)', '${1:/upcase}${2:+_}$2${3:+_}${4:/upcase}', 'g', (s) => WORDS(s).join('_').toUpperCase()],
    lowercaseAndDash: ['([a-z0-9])([A-Z])|([\\s_]+)|([A-Z])', '$1${2:+-}${2:/downcase}${3:+-}${4:/downcase}', 'g', (s) => WORDS(s).join('-').toLowerCase()],
    spaceSeparated: ['([a-z0-9])([A-Z])|([_-]+)', '$1${2:+ }$2${3:+ }', 'g', (s) => WORDS(s).join(' ')],
    underscoresToCamelCase: ['_+([A-Za-z0-9])', '${1:/upcase}', 'g', (s) => s.replace(/_+([A-Za-z0-9])/g, (m, c) => c.toUpperCase())],
    underscoresToSpaces: ['_', ' ', 'g', (s) => s.replaceAll('_', ' ')],
    spacesToUnderscores: [' ', '_', 'g', (s) => s.replaceAll(' ', '_')],
    decapitalize: ['^(.)', '${1:/downcase}', '', (s) => s.charAt(0).toLowerCase() + s.slice(1)],
    firstWord: ['^\\s*(\\S+).*$', '$1', '', (s) => s.trim().split(/\s+/)[0] ?? ''],
};
/** A call's arguments: [{ name } or { text }] (names of places, "quoted" texts), or null when they are not that. */
function argumentsOf(text) {
    const out = [];
    const re = /\s*(?:"((?:[^"\\]|\\.)*)"|'((?:[^'\\]|\\.)*)'|([A-Za-z_]\w*))\s*(,|$)/y;
    if (!text.trim()) return out;
    let m, at = 0;
    while (at < text.length && (m = (re.lastIndex = at, re.exec(text)))) {
        out.push(m[3] ? { name: m[3] } : { text: (m[1] ?? m[2]).replace(/\\(["'])/g, '$1') });
        at = re.lastIndex;
        if (m[4] !== ',') return at >= text.length ? out : null;
    }
    return null;
}
const SAMPLE = {
    capitalize: (s) => s.charAt(0).toUpperCase() + s.slice(1), upcase: (s) => s.toUpperCase(), downcase: (s) => s.toLowerCase(),
    camelcase: (s) => s.replace(/[_\-\s]+(\w)/g, (m, c) => c.toUpperCase()).replace(/^\w/, (c) => c.toLowerCase()),
    pascalcase: (s) => s.replace(/[_\-\s]+(\w)/g, (m, c) => c.toUpperCase()).replace(/^\w/, (c) => c.toUpperCase()),
};

/** An expression read: { snippet, sample } of a value, { transform, of } of one following another place, null for none or an unknown one. */
function expression(expr) {
    const e = (expr ?? '').trim();
    if (!e) return null;
    const wide = /^(\w+)\(([\s\S]*)\)$/.exec(e);
    const args = wide && argumentsOf(wide[2]);
    if (!args) return null;
    const of = args[0]?.name;
    if (wide[1] === 'complete' || wide[1] === 'completeSmart') return args.length ? null : { complete: true };
    if (FOLLOW[wide[1]]) {
        const [search, replace, flags, sample] = FOLLOW[wide[1]];
        return args.length === 1 && of ? { search, replace, flags, of, change: sample, format: true } : null;
    }
    if (wide[1] === 'substringBefore') {
        const sep = args[1]?.text;
        if (args.length !== 2 || !of || !sep) return null;
        return { search: '^([\\s\\S]*?)' + sep.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '[\\s\\S]*$', replace: '$1', flags: '', of, change: (s) => (s.includes(sep) ? s.slice(0, s.indexOf(sep)) : s) };
    }
    if (wide[1] === 'regularExpression') {
        const search = args[1]?.text, replace = args[2]?.text;
        if (args.length !== 3 || !of || !search || replace === undefined) return null;
        try { new RegExp(search); } catch { return null; }
        return { search, replace, flags: 'g', of, change: (s) => s.replace(new RegExp(search, 'g'), replace) };
    }
    const call = /^(\w+)\(\s*(\w*)\s*\)$/.exec(e);
    if (!call) return null;
    for (const [form, , snippet, sample] of EXPRESSIONS) {
        if (snippet === undefined) continue;
        if (form.slice(0, form.indexOf('(')) !== call[1]) continue;
        if (sample === null) return call[2] ? { transform: snippet, of: call[2] } : null;
        return call[2] ? null : { snippet, sample };
    }
    return null;
}

/** The names of a text's places, in the order they first come ($END$ and $SELECTION$ are not places to fill). */
export function placesOf(body) {
    const names = [];
    for (const m of body.matchAll(NAMED)) if (m[1] !== 'END' && m[1] !== 'SELECTION' && !names.includes(m[1])) names.push(m[1]);
    return names;
}

/**
 * A template written with $name$ places ($END$, $SELECTION$), as the editor's snippet: ${1:name}, $0, ${TM_SELECTED_TEXT}. A place's variable
 * (vars[name]: { expr, def, skip }) gives it another default, a value (the file's name, the date...) that is typed over or, with skip, just
 * written, or makes it follow another place. With <paramref name="plain"/> the text is given as it would be written, nothing typed.
 */
export function fromNamedForm(text, vars = {}, plain = false, info = null) {
    const slash = (s) => s.replace(/\//g, '\\/');
    const order = [];
    const index = (name) => {
        let n = order.indexOf(name);
        if (n < 0) { order.push(name); n = order.length - 1; }
        return n + 1;
    };
    const esc = plain ? (s) => s : literal;
    const shown = (name, seen = []) => {      // a place's text with nothing typed, for the plain form
        const v = vars[name], e = expression(v?.expr);
        if (e?.sample) return e.sample;
        if (e?.transform && !seen.includes(name)) return SAMPLE[e.transform](shown(e.of, [...seen, name]));
        if (e?.search && !seen.includes(name)) return e.change(shown(e.of, [...seen, name]));
        if (e?.complete) return v?.def || '';
        return v?.def || name;
    };
    let out = '', last = 0;
    for (const m of text.matchAll(NAMED)) {
        out += esc(text.slice(last, m.index));
        last = m.index + m[0].length;
        const name = m[1];
        if (name === 'END') out += plain ? '' : '$0';
        else if (name === 'SELECTION') out += plain ? '' : '${TM_SELECTED_TEXT}';
        else if (plain) out += shown(name);
        else {
            const v = vars[name], e = expression(v?.expr);
            if (e?.transform && e.of !== name) out += '${' + index(e.of) + '/(.*)/${1:/' + e.transform + '}/}';
            else if (e?.search && e.of !== name) out += '${' + index(e.of) + '/' + slash(e.search) + '/' + (e.format ? e.replace : slash(e.replace)) + '/' + e.flags + '}';
            else if (e?.complete) {
                // (nothing written: completion is asked at the place when it is reached; live-templates.js)
                const n = index(name);
                if (info && !info.complete.includes(n)) info.complete.push(n);
                out += v?.def ? '${' + n + ':' + literal(v.def) + '}' : '${' + n + '}';
            }
            else if (e?.snippet && v.skip) out += e.snippet;
            else out += '${' + index(name) + ':' + (e?.snippet ?? literal(v?.def || name)) + '}';
        }
    }
    return out + esc(text.slice(last));
}

const indented = (body) => body.replace(/^( {4})+/gm, (m) => '\t'.repeat(m.length / 4));

/** The snippet the editor inserts for a template's text: $name$ places when it has some, else the snippet it already is ($1, ${1:name}, $0). */
export function toSnippet(body, vars) {
    return HAS_NAMED.test(body) ? fromNamedForm(indented(body), vars ?? {}) : indented(body);
}

/** The places of a template (the numbers of its snippet's stops) where completion is to be offered: its variables' complete(). */
export function completeStops(body, vars) {
    const info = { complete: [] };
    if (HAS_NAMED.test(body)) fromNamedForm(indented(body), vars ?? {}, false, info);
    return info.complete;
}

/** A template's text as plain text, its places by their names: what the page and a completion's side panel show. */
export function preview(body, vars) {
    const text = HAS_NAMED.test(body) ? fromNamedForm(indented(body), vars ?? {}, true)
        : indented(body).replace(/\$\{\d+:([^}]*)\}/g, '$1').replace(/\$\{TM_SELECTED_TEXT\}/g, '').replace(/\$\{?\d+\}?/g, '').replace(/\\([\\$}])/g, '$1');
    return text.replaceAll('\t', '    ');
}

// ------------------------------------------------------------ what the person changed

const FIELDS = ['abbr', 'description', 'body', 'contexts', 'lineStart', 'expand', 'vars', 'reformat'];
const text = (v, max) => (typeof v === 'string' ? v.slice(0, max) : '');
const contextsOf = (v) => (Array.isArray(v) ? v.filter((c) => CONTEXT_IDS.has(c)) : []);
/** The variables of a template's places, only what says something: { name: { expr, def, skip } }. */
function varsOf(v) {
    const out = {};
    for (const [name, one] of Object.entries(v && typeof v === 'object' && !Array.isArray(v) ? v : {})) {
        if (!/^[A-Za-z_]\w*$/.test(name) || !one || typeof one !== 'object') continue;
        const expr = text(one.expr, 200).trim(), def = text(one.def, 200);
        if (expr || def) out[name] = { expr, def, skip: one.skip === true };
    }
    return out;
}

/** The settings' templates as they may be used: anything of the wrong kind dropped (a broken file changes nothing). */
export function cleanConf(raw) {
    const r = raw && typeof raw === 'object' && !Array.isArray(raw) ? raw : {};
    const changed = {};
    for (const [id, c] of Object.entries(r.changed && typeof r.changed === 'object' ? r.changed : {})) {
        if (!c || typeof c !== 'object') continue;
        const one = {};
        if (typeof c.abbr === 'string' && c.abbr.trim()) one.abbr = c.abbr.trim().slice(0, 60);
        if (typeof c.description === 'string') one.description = c.description.slice(0, 200);
        if (typeof c.body === 'string') one.body = c.body.slice(0, 20000);
        if (Array.isArray(c.contexts)) one.contexts = contextsOf(c.contexts);
        if (typeof c.lineStart === 'boolean') one.lineStart = c.lineStart;
        if (typeof c.expand === 'string' && (c.expand === '' || KEY_IDS.has(c.expand))) one.expand = c.expand;
        if (c.vars && typeof c.vars === 'object') one.vars = varsOf(c.vars);
        if (typeof c.reformat === 'boolean') one.reformat = c.reformat;
        if (Object.keys(one).length) changed[id] = one;
    }
    const own = (Array.isArray(r.own) ? r.own : []).filter((t) => t && typeof t === 'object' && typeof t.id === 'string' && t.id.startsWith('own/')).map((t) => ({
        id: t.id, group: text(t.group, 60) || 'user', abbr: text(t.abbr, 60).trim(), description: text(t.description, 200), body: text(t.body, 20000),
        contexts: contextsOf(t.contexts), lineStart: t.lineStart === true, expand: KEY_IDS.has(t.expand) ? t.expand : '',
        vars: varsOf(t.vars), reformat: t.reformat === true,
    }));
    return {
        expand: KEY_IDS.has(r.expand) ? r.expand : 'tab',
        off: [...new Set((Array.isArray(r.off) ? r.off : []).filter((id) => typeof id === 'string'))],
        changed, own,
        groups: [...new Set((Array.isArray(r.groups) ? r.groups : []).filter((g) => typeof g === 'string' && g.trim()).map((g) => g.trim().slice(0, 60)))],
    };
}

/** The built-in templates: the data's groups and the language server's ({ live, template, postfix } of pymagic/templates, null before it answers). */
export function builtIns(server) {
    const out = [];
    for (const g of GROUPS)
        for (const t of g.templates)
            out.push({ id: `${g.name}/${t.abbr}`, group: g.name, abbr: t.abbr, description: t.description, body: t.body, contexts: g.contexts, lineStart: g.lineStart, expand: '', vars: {}, reformat: false });
    for (const t of server?.live ?? [])
        out.push({ id: `Python/${t.prefix}`, group: 'Python', abbr: t.prefix, description: t.description, body: t.body, contexts: ['python'], lineStart: true, expand: '', vars: {}, reformat: false,
            server: 'live', note: [t.classBody ? 'Offered in the body of a class.' : '', t.import ? `Adds the import of ${t.import}.` : ''].filter(Boolean).join(' ') });
    for (const t of server?.template ?? [])
        out.push({ id: `Django Templates/${t.prefix}`, group: 'Django Templates', abbr: t.prefix, description: t.description, body: t.body, contexts: ['template'], lineStart: false, expand: '', vars: {}, reformat: false,
            server: 'template', note: 'Offered in the text of a template, outside its tags.' });
    for (const t of server?.postfix ?? [])
        out.push({ id: `Python postfix/${t.key}`, group: 'Python postfix', abbr: '.' + t.key, description: t.description, body: t.example, contexts: ['python'], lineStart: false, expand: '', vars: {}, reformat: false,
            server: 'postfix', note: 'A postfix template, typed after an expression and a dot: it can be switched on and off.' });
    return out;
}

const same = (a, b) => (Array.isArray(a) ? a.length === b.length && a.every((x) => b.includes(x))
    : a && typeof a === 'object' ? JSON.stringify(a) === JSON.stringify(b) : a === b);

/** Every template as it is now: the built-in ones with the person's changes, then their own. Each has on, builtIn, changed and (a built-in one) original. */
export function allTemplates(server, conf, project = []) {
    const off = new Set(conf.off);
    const list = builtIns(server).map((t) => {
        const c = t.server === 'postfix' ? null : conf.changed[t.id];
        return { ...t, ...(c ?? {}), original: t, builtIn: true, changed: !!c, on: !off.has(t.id) };
    });
    for (const t of conf.own) list.push({ ...t, builtIn: false, changed: false, on: !off.has(t.id) });
    list.push(...project);
    return list;
}

/** The groups in the page's order (by name, the person's own empty ones too): [{ name, own, templates }]. */
export function groupsOf(templates, conf) {
    const map = new Map();
    for (const t of templates) {
        if (!map.has(t.group)) map.set(t.group, { name: t.group, own: !t.builtIn, templates: [] });
        const g = map.get(t.group);
        g.templates.push(t);
        if (t.builtIn) g.own = false;
    }
    for (const name of conf.groups) if (!map.has(name)) map.set(name, { name, own: true, templates: [] });
    return [...map.values()].sort((a, b) => a.name.localeCompare(b.name, 'en', { sensitivity: 'base' }));
}

/** A built-in template the language server offers itself (its completion knows the statement and the import): one whose abbreviation and place are its own. */
export function servedByServer(t) {
    if (!t.server) return false;
    if (t.server === 'postfix' || t.server === 'project') return true;
    return t.abbr === t.original.abbr && same(t.contexts, t.original.contexts);
}

/** The key that expands a template: its own, or the one of the settings. */
export const expandKey = (t, conf) => t.expand || conf.expand;

// the changes, each giving the new settings (the old ones untouched)

export function setOn(conf, ids, on) {
    const off = new Set(conf.off);
    for (const id of ids) { if (on) off.delete(id); else off.add(id); }
    return { ...conf, off: [...off] };
}

/** A template edited: a built-in one keeps only what differs from how it comes, one of the person's own is replaced. */
export function edited(conf, t, patch) {
    if (!t.builtIn)
        return { ...conf, own: conf.own.map((o) => (o.id === t.id ? { ...o, ...patch } : o)) };
    if (t.server === 'postfix') return conf;
    const now = { ...t, ...patch }, diff = {};
    for (const f of FIELDS) if (!same(now[f], t.original[f])) diff[f] = now[f];
    const changed = { ...conf.changed };
    if (Object.keys(diff).length) changed[t.id] = diff; else delete changed[t.id];
    return { ...conf, changed };
}

/** Built-in templates as they come again (edits gone, switched on). */
export function restored(conf, ids) {
    const changed = { ...conf.changed };
    for (const id of ids) delete changed[id];
    return setOn({ ...conf, changed }, ids, true);
}

let counter = 0;
const newId = (conf) => {
    let id;
    do id = `own/${Date.now().toString(36)}${(counter++).toString(36)}`; while (conf.own.some((t) => t.id === id));
    return id;
};

/** A template of the person's own added to a group (a copy of <paramref name="from"/> when given); [the settings, its id]. */
export function added(conf, group, from) {
    const id = newId(conf);
    const t = from
        ? { id, group, abbr: from.abbr.replace(/^\./, ''), description: from.description, body: from.server === 'postfix' ? '' : from.body, contexts: [...from.contexts], lineStart: !!from.lineStart, expand: from.expand || '', vars: { ...(from.vars ?? {}) }, reformat: !!from.reformat }
        : { id, group, abbr: '', description: '', body: '', contexts: [], lineStart: false, expand: '', vars: {}, reformat: false };
    return [{ ...conf, own: [...conf.own, t], groups: conf.groups.includes(group) ? conf.groups : [...conf.groups, group] }, id];
}

export function removed(conf, ids) {
    const gone = new Set(ids);
    return { ...conf, own: conf.own.filter((t) => !gone.has(t.id)), off: conf.off.filter((id) => !gone.has(id)) };
}

/** A new group of the person's own, named so that no group has the name; [the settings, the name]. */
export function groupAdded(conf, taken, wanted = 'New group') {
    let name = wanted, n = 2;
    while (taken.includes(name)) name = `${wanted} ${n++}`;
    return [{ ...conf, groups: [...conf.groups, name] }, name];
}

export function groupRenamed(conf, from, to) {
    return { ...conf, groups: conf.groups.map((g) => (g === from ? to : g)), own: conf.own.map((t) => (t.group === from ? { ...t, group: to } : t)) };
}

/** A group of the person's own removed, its templates with it. */
export function groupRemoved(conf, name) {
    const ids = conf.own.filter((t) => t.group === name).map((t) => t.id);
    return { ...removed(conf, ids), groups: conf.groups.filter((g) => g !== name) };
}

// ------------------------------------------------------------ the template the text before the cursor names

const WORD = /[A-Za-z0-9_]/;

/**
 * The template whose abbreviation ends the text before the cursor, for the key pressed: the longest one that is on, applies to the language
 * and expands with this key. An abbreviation starting with a letter must start a word; one for the start of a line must have only blanks before it.
 */
export function matchAt(templates, conf, languageId, before, key) {
    return matchesAt(templates, conf, languageId, before, key)[0] ?? null;
}

/** Every such template: more than one when templates share the abbreviation (the editor then asks which). */
export function matchesAt(templates, conf, languageId, before, key) {
    let best = [];
    for (const t of templates) {
        if (!t.on || !t.abbr || t.server === 'postfix' || !before.endsWith(t.abbr)) continue;
        if (best.length && best[0].abbr.length > t.abbr.length) continue;
        if (expandKey(t, conf) !== key || !applicable(t, languageId)) continue;
        const head = before.slice(0, before.length - t.abbr.length);
        if (WORD.test(t.abbr[0]) && head && WORD.test(head[head.length - 1])) continue;
        if (t.lineStart && head.trim()) continue;
        if (best.length && best[0].abbr.length < t.abbr.length) best = [];
        best.push(t);
    }
    return best;
}

/**
 * The project's own templates (.pymagic/snippets.json, read by codegen-model.js's readSnippets) as templates: each for the languages its scope
 * names. Python and the text of Django templates are the language server's to offer (as its own templates are); the rest are the editor's.
 */
export function projectTemplates(snippets) {
    const out = [];
    (snippets ?? []).forEach((s, i) => {
        if (!s.prefix) return;
        const scope = (s.scope ?? '').toLowerCase();
        const named = scope.split(',').map((x) => x.trim()).filter(Boolean);
        const one = { group: 'This project', abbr: s.prefix, description: s.description || s.name || '', body: s.body, contexts: [], lineStart: false, expand: '', vars: {}, reformat: false,
            builtIn: false, changed: false, on: true, project: true };
        const servers = /django|jinja|template/.test(scope) ? ['django-html'] : !named.length || named.includes('python') ? ['python'] : [];
        if (servers.length) out.push({ ...one, id: `project/${i}/server`, languages: servers, server: 'project', lineStart: servers[0] === 'python' });
        const rest = named.filter((l) => !servers.includes(l) && !/django|jinja|template/.test(l));
        if (rest.length) out.push({ ...one, id: `project/${i}`, languages: rest });
    });
    return out;
}

/** The templates the editor's own completion offers for a language (the language server's are its own to offer). */
export function offered(templates, languageId, before) {
    return templates.filter((t) => t.on && t.abbr && !servedByServer(t) && applicable(t, languageId) && !(t.lineStart && before.trim()));
}

/**
 * The language server's completion items with the person's changes: a built-in template switched off is left out, one whose text or description
 * was edited gets them, and one that moved (another abbreviation or place) is left to the editor's own completion.
 */
export function serverItems(items, templates, languageId) {
    const byId = new Map(templates.filter((t) => t.server).map((t) => [t.id, t]));
    const out = [];
    for (const i of items) {
        const sort = i.sortText ?? '';
        const id = !i.isSnippet ? null
            : sort === 'z9' + i.label ? `Python postfix/${i.label}`
            : sort === '4' + i.label ? `${languageId === 'python' ? 'Python' : 'Django Templates'}/${i.label}` : null;
        const t = id && byId.get(id);
        if (!t) { out.push(i); continue; }
        if (!t.on || !servedByServer(t)) continue;
        if (t.changed && t.server !== 'postfix')
            out.push({ ...i, detail: t.description, ...(t.body !== t.original.body || Object.keys(t.vars ?? {}).length ? { insertText: toSnippet(t.body, t.vars), documentation: '```\n' + preview(t.body, t.vars) + '\n```' } : {}) });
        else out.push(i);
    }
    return out;
}

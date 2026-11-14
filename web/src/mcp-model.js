// What the MCP server's tools work out without the editor (web/src/mcp-tools.js carries them out): checking a path, cutting long texts, replacing
// text, the tree of a folder, finding a symbol by name, the lines of matches and problems. Kept apart so web/test/mcp.test.mjs checks it.
import { globMatch } from './schema-model.js';

/** A path the agent gave, as the page's (relative, with /); throws for one outside the project. '' is the project's folder. */
export function projectPath(p, { allowRoot = false } = {}) {
    const s = String(p ?? '').replace(/\\/g, '/').replace(/^\.\/+/, '').replace(/\/+$/, '');
    if (!s || s === '.') {
        if (allowRoot) return '';
        throw new Error('a path in the project is needed');
    }
    if (/^[a-zA-Z]:/.test(s) || s.startsWith('/') || s.split('/').some((part) => part === '..'))
        throw new Error(`${p} is not a path inside the project (give it from the project folder, without .. or a drive)`);
    return s.replace(/\/{2,}/g, '/');
}

/** An absolute path under the project's root, made relative; others left for projectPath to refuse. */
export function relativeToRoot(p, root) {
    const s = String(p ?? '').replace(/\\/g, '/');
    const r = String(root ?? '').replace(/\\/g, '/').replace(/\/+$/, '');
    if (r && s.toLowerCase().startsWith(r.toLowerCase() + '/')) return s.slice(r.length + 1);
    if (r && s.toLowerCase() === r.toLowerCase()) return '';
    return s;
}

/** At most `max` lines of a text; `mode` START keeps the last ones, END the first ones, MIDDLE both ends, NONE all. */
export function truncateLines(text, max, mode = 'END') {
    const lines = text.split('\n');
    if (lines.length > 1 && lines[lines.length - 1] === '') lines.pop();   // a last newline ends the last line, it is not one more
    if (mode === 'NONE' || !(max > 0) || lines.length <= max) return { text, truncated: false };
    const left = lines.length - max;
    const cut = `<<${left} line${left === 1 ? '' : 's'} left out>>`;
    if (mode === 'START') return { text: cut + '\n' + lines.slice(left).join('\n'), truncated: true };
    if (mode === 'MIDDLE') {
        const head = Math.ceil(max / 2), tail = max - head;
        return { text: [...lines.slice(0, head), cut, ...lines.slice(lines.length - tail)].join('\n'), truncated: true };
    }
    return { text: lines.slice(0, max).join('\n') + '\n' + cut, truncated: true };
}

/** Every (or the first) occurrence of `oldText` replaced: { text, count }. */
export function replaceText(text, oldText, newText, { all = true, caseSensitive = true } = {}) {
    if (!oldText) throw new Error('oldText is empty');
    const hay = caseSensitive ? text : text.toLowerCase();
    const needle = caseSensitive ? oldText : oldText.toLowerCase();
    let out = '', from = 0, count = 0;
    for (;;) {
        const at = hay.indexOf(needle, from);
        if (at < 0) break;
        out += text.slice(from, at) + newText;
        from = at + oldText.length;
        count++;
        if (!all) break;
    }
    return { text: out + text.slice(from), count };
}

/** The project's files whose name has the keyword, shortest path first. */
export function filesByName(files, keyword, limit = 1000) {
    const k = String(keyword).toLowerCase();
    const found = files.filter((f) => f.slice(f.lastIndexOf('/') + 1).toLowerCase().includes(k));
    found.sort((a, b) => a.length - b.length || a.localeCompare(b));
    return { files: found.slice(0, limit), more: Math.max(0, found.length - limit) };
}

/** The project's files whose path matches a glob (under `under` when given: the glob is then from that folder). */
export function filesByGlob(files, glob, under = '', limit = 1000) {
    const base = under ? under.replace(/\/+$/, '') + '/' : '';
    const found = files.filter((f) => (!base || f.startsWith(base)) && globMatch(glob, f.slice(base.length)));
    return { files: found.slice(0, limit), more: Math.max(0, found.length - limit) };
}

/** A folder of the project as a tree: dirs and files are the project's paths; at most `maxDepth` levels under `dir`. */
export function treeText(dirs, files, dir = '', maxDepth = 5) {
    const base = dir ? dir + '/' : '';
    const children = new Map();   // folder -> { dirs:Set, files:[] }
    const at = (d) => { if (!children.has(d)) children.set(d, { dirs: new Set(), files: [] }); return children.get(d); };
    const parent = (p) => p.includes('/') ? p.slice(0, p.lastIndexOf('/')) : '';
    for (const d of dirs) if (!base || d.startsWith(base)) { at(parent(d)).dirs.add(d); at(d); }
    for (const f of files) if (!base || f.startsWith(base)) at(parent(f)).files.push(f);
    if (dir && !children.has(dir)) throw new Error(`${dir} is not a folder of the project`);
    const out = [(dir || '.') + '/'];
    const name = (p) => p.slice(p.lastIndexOf('/') + 1);
    const walk = (d, indent, depth) => {
        const c = children.get(d);
        if (!c) return;
        const items = [...[...c.dirs].sort().map((x) => [x, true]), ...c.files.sort().map((x) => [x, false])];
        items.forEach(([p, isDir], i) => {
            const last = i === items.length - 1;
            out.push(indent + (last ? '└── ' : '├── ') + name(p) + (isDir ? '/' : ''));
            if (isDir) {
                if (depth + 1 < maxDepth) walk(p, indent + (last ? '    ' : '│   '), depth + 1);
                else if (children.get(p) && (children.get(p).dirs.size || children.get(p).files.length)) out.push(indent + (last ? '    ' : '│   ') + '└── …');
            }
        });
    };
    walk(dir, '', 0);
    return out.join('\n');
}

/** A DocumentSymbol by its name, or Class.method: the first one found, depth first. */
export function findSymbol(symbols, name) {
    const parts = String(name).split('.');
    const search = (list, i) => {
        for (const s of list ?? []) {
            if (s.name === parts[i]) {
                if (i === parts.length - 1) return s;
                const inner = search(s.children, i + 1);
                if (inner) return inner;
            }
        }
        if (i === 0) for (const s of list ?? []) { const inner = search(s.children, 0); if (inner) return inner; }
        return null;
    };
    return search(symbols, 0);
}

const SEVERITY = { 1: 'error', 2: 'warning', 3: 'weak warning', 4: 'hint' };
/** LSP diagnostics (or Monaco's markers, made alike) as the tool's answer: one per line, file:line:column severity: message [code]. */
export function problemsText(file, problems, errorsOnly = false) {
    const kept = problems.filter((p) => !errorsOnly || p.severity === 1);
    if (!kept.length) return `No ${errorsOnly ? 'errors' : 'problems'} in ${file}.`;
    return kept.map((p) => `${file}:${p.line}:${p.column} ${SEVERITY[p.severity] ?? 'problem'}: ${p.message}${p.code ? ` [${p.code}]` : ''}`).join('\n');
}

/** The search's answer (WorkspaceSearch's files and matches) as lines: file:line: the line's text with the match between ||. */
export function matchesText(result, limit = 1000) {
    const lines = [];
    let total = 0;
    for (const f of result.files ?? []) {
        for (const m of f.matches ?? []) {
            total++;
            if (lines.length >= limit) continue;
            const t = m.text ?? '';
            const marked = t.slice(0, m.column) + '||' + t.slice(m.column, m.column + m.length) + '||' + t.slice(m.column + m.length);
            lines.push(`${f.path}:${m.line + 1}: ${marked.trim()}`);
        }
    }
    if (!lines.length) return 'Nothing found.';
    const more = total - lines.length + (result.truncated ? 1 : 0);
    return lines.join('\n') + (more > 0 ? `\n(${result.truncated ? 'more than ' : ''}${total - lines.length} more not shown)` : '');
}

/** A search's include globs from the folder and the file mask. */
export function includeOf(directory, fileMask) {
    const dir = directory ? projectPath(directory, { allowRoot: true }) : '';
    const masks = String(fileMask ?? '').split(',').map((m) => m.trim()).filter(Boolean);
    if (!dir) return masks.join(',');
    if (!masks.length) return `${dir}/**`;
    return masks.map((m) => `${dir}/**/${m.replace(/^\*\*\//, '')}`).join(',');
}

/** A hover's contents (markdown, a MarkedString, or a list) as plain text. */
export function hoverText(hover) {
    const c = hover?.contents;
    if (!c) return '';
    const one = (x) => typeof x === 'string' ? x : x?.value ?? '';
    return (Array.isArray(c) ? c.map(one) : [one(c)]).join('\n\n').trim();
}

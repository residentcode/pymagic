// The notebook file format (nbformat 4) as plain functions: reading, editing cells, turning the kernel's messages into
// outputs, and writing the file back the way Jupyter writes it (keys sorted, one space of indent, a final newline), so
// opening and saving a notebook changes nothing but what was edited. No DOM here: web/test/notebook.test.mjs checks it.

/** Reads the text of an .ipynb file. Returns { nb } or { error }. */
export function parseNotebook(text) {
    let nb;
    try { nb = JSON.parse(text); } catch (e) { return { error: `not a notebook: ${e.message}` }; }
    if (!nb || typeof nb !== 'object' || !Array.isArray(nb.cells)) return { error: 'not a notebook: there is no list of cells' };
    if ((nb.nbformat ?? 0) < 4) return { error: `notebook format ${nb.nbformat ?? '?'} is too old (version 4 and later are supported)` };
    nb.metadata ??= {};
    for (const c of nb.cells) {
        c.metadata ??= {};
        if (c.cell_type === 'code') { c.outputs ??= []; if (c.execution_count === undefined) c.execution_count = null; }
    }
    return { nb };
}

/** JSON with sorted keys, one space of indent and no \u escapes for letters: what nbformat writes. */
export function serializeNotebook(nb) {
    const sort = (v) => {
        if (Array.isArray(v)) return v.map(sort);
        if (v && typeof v === 'object') return Object.fromEntries(Object.keys(v).sort().map((k) => [k, sort(v[k])]));
        return v;
    };
    return JSON.stringify(sort(nb), null, 1) + '\n';
}

// ------------------------------------------------------------ cell text

/** A cell's source is a string or a list of lines; this is always the whole text. */
export function sourceOf(cell) {
    return Array.isArray(cell.source) ? cell.source.join('') : (cell.source ?? '');
}

/** The list-of-lines form the file uses: every line but the last keeps its newline. */
export function splitLines(text) {
    if (text === '') return [];
    const lines = text.split('\n').map((l, i, all) => (i < all.length - 1 ? l + '\n' : l));
    if (lines[lines.length - 1] === '') lines.pop();
    return lines;
}

export function setSource(cell, text) {
    cell.source = splitLines(text);
}

// ------------------------------------------------------------ editing the list of cells

function newId() {
    const b = new Uint8Array(4);
    (globalThis.crypto ?? { getRandomValues: (a) => a.map(() => Math.floor(Math.random() * 256)) }).getRandomValues(b);
    return Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
}

/** A new empty cell; it gets an id when the notebook's format is new enough to have them (4.5). */
export function newCell(nb, type = 'code', text = '') {
    const cell = { cell_type: type, metadata: {}, source: splitLines(text) };
    if (type === 'code') { cell.execution_count = null; cell.outputs = []; }
    if ((nb.nbformat_minor ?? 0) >= 5) cell.id = newId();
    return cell;
}

export function insertCell(nb, index, type = 'code', text = '') {
    const cell = newCell(nb, type, text);
    nb.cells.splice(Math.max(0, Math.min(index, nb.cells.length)), 0, cell);
    return cell;
}

export function deleteCell(nb, index) {
    if (index < 0 || index >= nb.cells.length) return null;
    return nb.cells.splice(index, 1)[0];
}

/** Moves a cell by `by` places (-1 up, 1 down); returns its new index (or the old one if it cannot move). */
export function moveCell(nb, index, by) {
    const to = index + by;
    if (index < 0 || index >= nb.cells.length || to < 0 || to >= nb.cells.length) return index;
    const [cell] = nb.cells.splice(index, 1);
    nb.cells.splice(to, 0, cell);
    return to;
}

export function changeType(nb, cell, type) {
    if (cell.cell_type === type) return;
    cell.cell_type = type;
    if (type === 'code') { cell.execution_count = null; cell.outputs = []; }
    else { delete cell.execution_count; delete cell.outputs; }
    if (type === 'raw') cell.metadata = cell.metadata ?? {};
}

export function clearOutputs(nb) {
    for (const c of nb.cells) if (c.cell_type === 'code') { c.outputs = []; c.execution_count = null; }
}

/** The largest execution count in the notebook, so the next run continues the numbering. */
export function lastExecutionCount(nb) {
    return nb.cells.reduce((m, c) => Math.max(m, c.execution_count ?? 0), 0);
}

// ------------------------------------------------------------ outputs

/**
 * Applies one message of the kernel to a cell's outputs (nbformat's shapes):
 *   stream {name, text} -> joins the previous stream output of the same name
 *   result {data, execution_count} -> execute_result      display {data} -> display_data
 *   error {ename, evalue, traceback} -> error
 */
export function applyKernelMessage(cell, m) {
    const outputs = cell.outputs;
    switch (m.type) {
        case 'stream': {
            const last = outputs[outputs.length - 1];
            if (last && last.output_type === 'stream' && last.name === m.name) {
                last.text = splitLines(sourceOf({ source: last.text }) + m.text);
            } else outputs.push({ name: m.name, output_type: 'stream', text: splitLines(m.text) });
            break;
        }
        case 'result':
            outputs.push({ data: m.data, execution_count: m.execution_count ?? null, metadata: {}, output_type: 'execute_result' });
            break;
        case 'display':
            outputs.push({ data: m.data, metadata: {}, output_type: 'display_data' });
            break;
        case 'error':
            outputs.push({ ename: m.ename, evalue: m.evalue, output_type: 'error', traceback: m.traceback ?? [] });
            break;
    }
}

const MIME_ORDER = ['image/png', 'image/jpeg', 'image/svg+xml', 'text/html', 'text/markdown', 'text/latex', 'application/json', 'text/plain'];

/**
 * What to show for one output: { kind: 'text' | 'error' | 'image' | 'html' | 'markdown' | 'json' | 'latex', ... }.
 * Text may come as a string or a list of lines.
 */
export function describeOutput(o) {
    const text = (v) => (Array.isArray(v) ? v.join('') : v ?? '');
    switch (o.output_type) {
        case 'stream': return { kind: 'text', stream: o.name, text: text(o.text) };
        case 'error': return { kind: 'error', ename: o.ename, evalue: o.evalue, text: (o.traceback ?? []).join('\n') || `${o.ename}: ${o.evalue}` };
        case 'execute_result':
        case 'display_data': {
            const data = o.data ?? {};
            for (const mime of MIME_ORDER) {
                if (data[mime] === undefined) continue;
                if (mime.startsWith('image/') && mime !== 'image/svg+xml') return { kind: 'image', mime, data: text(data[mime]).replace(/\s+/g, '') };
                if (mime === 'image/svg+xml') return { kind: 'html', html: text(data[mime]) };
                if (mime === 'text/html') return { kind: 'html', html: text(data[mime]) };
                if (mime === 'text/markdown') return { kind: 'markdown', text: text(data[mime]) };
                if (mime === 'text/latex') return { kind: 'latex', text: text(data[mime]) };
                if (mime === 'application/json') return { kind: 'json', text: JSON.stringify(data[mime], null, 2) };
                return { kind: 'text', stream: 'result', text: text(data[mime]) };
            }
            return { kind: 'text', stream: 'result', text: '' };
        }
        default: return { kind: 'text', stream: 'stdout', text: '' };
    }
}

const ANSI = /\u001b\[([0-9;]*)m/g;
const COLORS = { 30: 'black', 31: 'red', 32: 'green', 33: 'yellow', 34: 'blue', 35: 'magenta', 36: 'cyan', 37: 'white' };

/** Splits text with ANSI colour codes (tracebacks have them) into [{ text, cls }]; cls is e.g. "ansi-red ansi-bold". */
export function ansiSegments(text) {
    const out = [];
    let classes = new Set();
    let at = 0;
    const push = (s) => { if (s) out.push({ text: s, cls: [...classes].join(' ') }); };
    for (const m of text.matchAll(ANSI)) {
        push(text.slice(at, m.index));
        at = m.index + m[0].length;
        for (const code of (m[1] === '' ? '0' : m[1]).split(';').map(Number)) {
            if (code === 0) classes = new Set();
            else if (code === 1) classes.add('ansi-bold');
            else if (COLORS[code]) { for (const c of [...classes]) if (/^ansi-(?!bold)/.test(c)) classes.delete(c); classes.add(`ansi-${COLORS[code]}`); }
            else if (code >= 90 && code <= 97) { for (const c of [...classes]) if (/^ansi-(?!bold)/.test(c)) classes.delete(c); classes.add(`ansi-${COLORS[code - 60]}`); }
            else if (code === 39) { for (const c of [...classes]) if (/^ansi-(?!bold)/.test(c)) classes.delete(c); }
        }
    }
    push(text.slice(at));
    return out;
}

/** Text with carriage returns the way a terminal shows it: a progress bar that rewrites its line leaves only the last version. */
export function collapseCarriageReturns(text) {
    return text.replace(/\r\n/g, '\n').split('\n').map((line) => {
        const parts = line.split('\r');
        for (let i = parts.length - 1; i >= 0; i--) if (parts[i] !== '') return parts[i];
        return '';
    }).join('\n');
}

// ------------------------------------------------------------ the notebook as one Python file (for completion, hover and diagnostics)

/** `!shell` lines and `%magic` lines are not Python: in the file the language server sees they become `pass`. */
export function maskMagics(code) {
    return code.split('\n').map((line) => {
        const body = line.trimStart();
        return body.startsWith('!') || body.startsWith('%') ? line.slice(0, line.length - body.length) + 'pass' : line;
    }).join('\n');
}

/**
 * All code cells one after the other as one Python file, so a name defined in an earlier cell is known in a later one.
 * Returns { text, cells: [{ index, start, lines }] }: `index` is the cell's place in the notebook, `start` its first
 * line in the file (0-based) and `lines` how many lines it takes there (an empty cell takes one).
 */
export function virtualDocument(nb) {
    const parts = [];
    const cells = [];
    let line = 0;
    nb.cells.forEach((cell, index) => {
        if (cell.cell_type !== 'code') return;
        const code = maskMagics(sourceOf(cell).replace(/\r\n?/g, '\n'));
        const lines = code.split('\n').length;
        parts.push(code);
        cells.push({ index, start: line, lines });
        line += lines;
    });
    return { text: parts.join('\n'), cells };
}

/** The cell a line of the virtual file belongs to, and the line inside that cell; null when there is none. */
export function cellOfLine(doc, line) {
    for (const c of doc.cells) if (line >= c.start && line < c.start + c.lines) return { index: c.index, line: line - c.start };
    return null;
}

/** The line in the virtual file of a line (0-based) in the cell with this notebook index; null when it is not a code cell. */
export function virtualLine(doc, cellIndex, line) {
    const c = doc.cells.find((x) => x.index === cellIndex);
    return c ? c.start + line : null;
}

// ------------------------------------------------------------ merging and splitting cells

/**
 * Joins the cell at `index` with the one below it (what Jupyter's "Merge cell below" does): the sources are joined with a blank line,
 * the result keeps the kind, the id and the tags of the first, and a code cell loses its outputs (they no longer belong to its code).
 * Returns the merged cell, or null when there is no cell below.
 */
export function mergeCells(nb, index) {
    if (index < 0 || index + 1 >= nb.cells.length) return null;
    const [a, b] = [nb.cells[index], nb.cells[index + 1]];
    const first = sourceOf(a), second = sourceOf(b);
    setSource(a, first === '' ? second : second === '' ? first : `${first.replace(/\n+$/, '')}\n\n${second}`);
    if (a.cell_type === 'code') { a.outputs = []; a.execution_count = null; }
    nb.cells.splice(index + 1, 1);
    return a;
}

/**
 * Cuts the cell at `index` in two at a character offset of its text: the first part stays, the rest becomes a new cell of the same kind below it
 * (the newline at the cut goes). The new cell has no tags and no outputs; the first keeps its tags and loses its outputs.
 * Returns the new cell, or null for a bad index.
 */
export function splitCell(nb, index, offset) {
    const cell = nb.cells[index];
    if (!cell) return null;
    const text = sourceOf(cell);
    const at = Math.max(0, Math.min(offset, text.length));
    const head = text.slice(0, at).replace(/\n$/, ''), tail = text.slice(at).replace(/^\n/, '');
    setSource(cell, head);
    if (cell.cell_type === 'code') { cell.outputs = []; cell.execution_count = null; }
    return insertCell(nb, index + 1, cell.cell_type, tail);
}

/** The character offset in a text of a Monaco-style position (1-based line and column). */
export function offsetOfPosition(text, lineNumber, column) {
    const lines = text.split('\n');
    let offset = 0;
    for (let i = 0; i < lineNumber - 1 && i < lines.length; i++) offset += lines[i].length + 1;
    return Math.min(text.length, offset + column - 1);
}

// ------------------------------------------------------------ cell tags (metadata.tags)

export function tagsOf(cell) {
    const t = cell.metadata?.tags;
    return Array.isArray(t) ? t.filter((x) => typeof x === 'string') : [];
}

/** A comma- or space-separated list as tags: trimmed, no empty ones, no repeats. */
export function parseTags(text) {
    return [...new Set(text.split(/[,\s]+/).map((t) => t.trim()).filter(Boolean))];
}

/** Sets the tags of a cell; an empty list removes the key (as Jupyter does), other metadata stays. */
export function setTags(cell, tags) {
    cell.metadata ??= {};
    if (tags.length) cell.metadata.tags = [...tags]; else delete cell.metadata.tags;
}

// ------------------------------------------------------------ headings: sections and the structure

/** The headings of a markdown text: [{ level, name, line }] (line is 0-based in the text); `#` lines inside a code fence are not headings. */
export function headingsOfText(text) {
    const out = [];
    let fence = null;
    text.split('\n').forEach((raw, line) => {
        const f = /^\s*(```+|~~~+)/.exec(raw);
        if (f) { if (!fence) fence = f[1][0]; else if (fence === f[1][0]) fence = null; return; }
        if (fence) return;
        const m = /^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$/.exec(raw);
        if (m && m[2]) out.push({ level: m[1].length, name: m[2], line });
    });
    return out;
}

/** The level of the heading a markdown cell starts with (its first heading, when only blank lines come before it); 0 when it has none. */
export function cellHeadingLevel(cell) {
    if (cell.cell_type !== 'markdown') return 0;
    const text = sourceOf(cell);
    const h = headingsOfText(text)[0];
    if (!h) return 0;
    return text.split('\n').slice(0, h.line).every((l) => l.trim() === '') ? h.level : 0;
}

/**
 * The cells of the section a markdown heading cell opens: from it to the cell before the next heading cell of the same or a higher level.
 * Returns { start, end } (end exclusive), or null when the cell at `index` does not start with a heading.
 */
export function sectionOf(nb, index) {
    const level = cellHeadingLevel(nb.cells[index] ?? {});
    if (!level) return null;
    let end = index + 1;
    while (end < nb.cells.length) {
        const l = cellHeadingLevel(nb.cells[end]);
        if (l && l <= level) break;
        end++;
    }
    return { start: index, end };
}

/**
 * The headings of all markdown cells as the nested symbols of the Structure window. A position's `line` is the index of the cell (the unit a
 * notebook moves in) and its `character` the heading's line inside the cell; a range runs to the last cell before the next heading of the
 * same or a higher level, so "follow the cursor" finds the section of the selected cell. A heading is a child of the nearest earlier larger one.
 */
export function notebookSymbols(nb) {
    const flat = [];
    nb.cells.forEach((cell, index) => {
        if (cell.cell_type !== 'markdown') return;
        for (const h of headingsOfText(sourceOf(cell))) flat.push({ ...h, cell: index });
    });
    const roots = [];
    const stack = [];
    flat.forEach((h, i) => {
        const next = flat.slice(i + 1).find((x) => x.level <= h.level);
        const last = next ? (next.cell === h.cell ? h.cell : next.cell - 1) : nb.cells.length - 1;
        const at = { line: h.cell, character: h.line };
        const symbol = { name: h.name, detail: '', kind: 'md-heading', level: h.level, cell: h.cell, range: { start: at, end: { line: last, character: 0 } }, selectionRange: { start: at, end: at }, children: [] };
        while (stack.length && stack[stack.length - 1].level >= h.level) stack.pop();
        (stack.length ? stack[stack.length - 1].children : roots).push(symbol);
        stack.push(symbol);
    });
    return roots;
}

// ------------------------------------------------------------ the Variables window: objects opened lazily

/** The key of a path (the first step names a variable, the others are keys the kernel listed): what the open rows and the loaded members are kept under. */
export const pathKey = (path) => JSON.stringify(path);

/**
 * The rows of the Variables window: every variable, and under each one that is open (its key in `open`) the members the kernel
 * sent for it (`members`: key -> { list } while loaded, { loading } or { error }). A row is { path, key, depth, name, type, size,
 * value, expandable, isOpen, loading, error }.
 */
export function variableRows(vars, open, members) {
    const rows = [];
    const add = (item, path, depth) => {
        const key = pathKey(path);
        const isOpen = !!item.expandable && open.has(key);
        const got = members.get(key);
        rows.push({ path, key, depth, name: item.name, type: item.type ?? '', size: item.size ?? '', value: item.value ?? '', expandable: !!item.expandable, isOpen, loading: isOpen && !!got?.loading, error: isOpen ? got?.error ?? '' : '' });
        if (isOpen && got?.list) for (const child of got.list) add(child, path.concat([child.key]), depth + 1);
    };
    for (const v of vars) add(v, [['v', v.name]], 0);
    return rows;
}

/** The open paths, shallowest first (a parent is asked for before its members). */
export function pathsToRefresh(open) {
    return [...open].map((k) => JSON.parse(k)).sort((a, b) => a.length - b.length);
}

// ------------------------------------------------------------ debugging a cell

/**
 * The name of the file that stands for a cell while it is debugged (it is never written to disk): the kernel runs the cell under
 * this name, the breakpoints of the cell are set on it, and a stop in it is shown in the cell. `rel` is the notebook's own path.
 */
export function cellDebugFile(rel, index) {
    const slug = rel.replace(/[\\/]/g, '__').replace(/\./g, '_');
    return `.pymagic/notebooks/${slug}.cell${index + 1}.py`;
}

/** Which cell a file name made by cellDebugFile stands for: { slug, index } or null. */
export function parseCellDebugFile(file) {
    const m = /^\.pymagic\/notebooks\/(.+)\.cell(\d+)\.py$/.exec(String(file).replace(/\\/g, '/'));
    return m ? { slug: m[1], index: Number(m[2]) - 1 } : null;
}

/** The slug cellDebugFile puts in a file name for a notebook. */
export const notebookSlug = (rel) => rel.replace(/[\\/]/g, '__').replace(/\./g, '_');

// ------------------------------------------------------------ the Jupyter console of a notebook

/** The lines typed into the console, newest last: a line is added unless it repeats the previous one; at most `limit` are kept. */
export function pushHistory(history, text, limit = 200) {
    if (text.trim() === '' || history[history.length - 1] === text) return history;
    history.push(text);
    if (history.length > limit) history.splice(0, history.length - limit);
    return history;
}

/** Up (-1) and Down (+1) in the console's history: { index, text } of the entry to show; `index` === history.length is the empty line below the newest. */
export function stepHistory(history, index, by, draft = '') {
    const next = Math.max(0, Math.min(history.length, index + by));
    return { index: next, text: next === history.length ? draft : history[next] };
}

/** Whether Enter in the console should add a line instead of running: an open bracket or string, a trailing backslash, a block that is not ended by a blank line. */
export function needsMoreInput(text) {
    const lines = text.replace(/\r\n?/g, '\n').split('\n');
    const last = lines[lines.length - 1];
    if (lines.length > 1 && last.trim() === '') return false;        // a blank line ends a block
    let depth = 0, quote = null;
    for (let i = 0; i < text.length; i++) {
        const c = text[i];
        if (quote) {
            if (c === '\\') i++;
            else if (text.startsWith(quote, i)) { i += quote.length - 1; quote = null; }
        } else if (c === '#') { while (i < text.length && text[i] !== '\n') i++; }
        else if (c === '"' || c === "'") quote = text.startsWith(c.repeat(3), i) ? c.repeat(3) : c;
        else if ('([{'.includes(c)) depth++;
        else if (')]}'.includes(c)) depth = Math.max(0, depth - 1);
    }
    if (quote && quote.length === 3) return true;
    if (depth > 0) return true;
    const t = text.trimEnd();
    if (t.endsWith('\\')) return true;
    if (lines.length === 1) return t.endsWith(':');
    return /^\s+\S/.test(last) || t.endsWith(':');                   // inside an indented body: more lines may follow until a blank one
}

// ------------------------------------------------------------ Jupyter servers (the selector of the notebook)

export const BUILT_IN_SERVER = { id: 'builtin', name: 'Built-in kernel', url: '', token: '', kernel: '' };

/**
 * A server the user typed: { id, name, url, token, kernel } or { error }. The address may be what Jupyter prints on start
 * (http://localhost:8888/?token=abc): the token is taken from it and the address is kept without it.
 */
export function normalizeServer(input) {
    let raw = String(input.url ?? '').trim();
    if (!raw) return { error: 'Type the address of the Jupyter server, e.g. http://localhost:8888' };
    if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(raw)) raw = 'http://' + raw;
    let u;
    try { u = new URL(raw); } catch { return { error: `"${raw}" is not an address` }; }
    if (u.protocol !== 'http:' && u.protocol !== 'https:') return { error: 'A Jupyter server is reached by http:// or https://' };
    const token = String(input.token ?? '').trim() || u.searchParams.get('token') || '';
    const path = u.pathname.replace(/\/(tree|lab|notebooks?)(\/.*)?$/, '').replace(/\/+$/, '');
    const url = `${u.protocol}//${u.host}${path}`;
    const name = String(input.name ?? '').trim() || u.host;
    return { id: input.id ?? url, name, url, token, kernel: String(input.kernel ?? '').trim() };
}

/** The saved servers from their JSON text (anything that is not a valid server is dropped). */
export function parseServers(text) {
    let list;
    try { list = JSON.parse(text ?? '[]'); } catch { return []; }
    if (!Array.isArray(list)) return [];
    return list.map((s) => normalizeServer(s || {})).filter((s) => !s.error);
}

/** The list with this server added (or replaced, when one with the same id is there). */
export function addServer(list, server) {
    return [...list.filter((s) => s.id !== server.id), server];
}

// ------------------------------------------------------------ a notebook as one HTML page (Open in browser)

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

function inlineHtml(parts) {
    return parts.map((p) => {
        switch (p.t) {
            case 'bold': return `<strong>${esc(p.text)}</strong>`;
            case 'italic': return `<em>${esc(p.text)}</em>`;
            case 'code': return `<code>${esc(p.text)}</code>`;
            case 'link': return `<a href="${esc(p.href)}" rel="noopener noreferrer">${esc(p.text)}</a>`;
            case 'image': return /^(https?:|data:image\/(png|jpeg|gif|webp);base64,)/i.test(p.href) ? `<img src="${esc(p.href)}" alt="${esc(p.text)}">` : esc(p.text);
            default: return esc(p.text);
        }
    }).join('');
}

function blocksHtml(blocks) {
    return blocks.map((b) => {
        switch (b.type) {
            case 'heading': return `<h${b.level}>${inlineHtml(b.inline)}</h${b.level}>`;
            case 'paragraph': return `<p>${inlineHtml(b.inline)}</p>`;
            case 'code': return `<pre class="code">${esc(b.text)}</pre>`;
            case 'quote': return `<blockquote>${blocksHtml(b.blocks)}</blockquote>`;
            case 'rule': return '<hr>';
            case 'list': { const tag = b.ordered ? 'ol' : 'ul'; return `<${tag}>${b.items.map((i) => `<li>${inlineHtml(i)}</li>`).join('')}</${tag}>`; }
            default: return '';
        }
    }).join('\n');
}

function outputHtml(o, blocksOf) {
    const d = describeOutput(o);
    switch (d.kind) {
        case 'text':
        case 'error': {
            const cls = d.kind === 'error' ? 'out error' : `out ${d.stream ?? 'result'}`;
            const body = ansiSegments(collapseCarriageReturns(d.text)).map((s) => (s.cls ? `<span class="${esc(s.cls)}">${esc(s.text)}</span>` : esc(s.text))).join('');
            return `<pre class="${cls}">${body}</pre>`;
        }
        case 'image': return `<img class="out" src="data:${esc(d.mime)};base64,${esc(d.data)}" alt="output">`;
        // the page of a notebook is served from the host's own address: the HTML of an output stays in a frame with no rights at all (no scripts, no forms)
        case 'html': return `<iframe class="out html" sandbox="" srcdoc="${esc(d.html)}"></iframe>`;
        case 'markdown': return `<div class="out md">${blocksHtml(blocksOf(d.text))}</div>`;
        case 'json': return `<pre class="out result">${esc(d.text)}</pre>`;
        default: return `<pre class="out result">${esc(d.text ?? '')}</pre>`;
    }
}

const EXPORT_CSS = `body{font:15px/1.5 system-ui,Segoe UI,sans-serif;max-width:60rem;margin:2rem auto;padding:0 1rem;color:#1d2330;background:#fff}
h1.title{font-size:1.1rem;color:#667;border-bottom:1px solid #dde;padding-bottom:.4rem}
.cell{margin:1rem 0}.in{display:flex;gap:.6rem}.prompt{flex:none;width:4.5rem;text-align:right;color:#307fc1;font:12px/1.7 Consolas,monospace}
pre{margin:0;font:13px/1.45 Consolas,Menlo,monospace;white-space:pre-wrap;word-break:break-word}
.in pre{flex:1;background:#f5f6f8;border:1px solid #dde;border-radius:3px;padding:.4rem .6rem}
.outputs{margin-left:5.1rem}.out{margin:.25rem 0}.out.stderr,.out.error{background:#fdecea}
.out.html{width:100%;border:0;min-height:3rem}img.out{max-width:100%}
.ansi-bold{font-weight:bold}.ansi-red{color:#c62828}.ansi-green{color:#2e7d32}.ansi-yellow{color:#9a6700}.ansi-blue{color:#1565c0}.ansi-magenta{color:#8e24aa}.ansi-cyan{color:#00838f}
blockquote{border-left:3px solid #ccd;margin:.5rem 0;padding-left:1rem;color:#556}pre.code{background:#f5f6f8;padding:.4rem .6rem;border-radius:3px}
code{background:#eef0f4;padding:0 .25em;border-radius:3px}`;

/**
 * The notebook as one self-contained HTML page: markdown cells formatted, code and its outputs as they are saved (images inline).
 * There is no script in it, and the HTML of an output is in a frame with no rights. `blocksOf` is markdown-lite's reader
 * (passed in, so that this file has no imports).
 */
export function notebookToHtml(nb, { title = 'Notebook', blocksOf }) {
    const cells = nb.cells.map((cell) => {
        const src = sourceOf(cell);
        if (cell.cell_type === 'markdown') return `<div class="cell md">${blocksHtml(blocksOf(src))}</div>`;
        if (cell.cell_type === 'raw') return `<div class="cell raw"><pre>${esc(src)}</pre></div>`;
        const count = cell.execution_count == null ? ' ' : cell.execution_count;
        const outs = (cell.outputs ?? []).map((o) => outputHtml(o, blocksOf)).join('\n');
        return `<div class="cell code"><div class="in"><span class="prompt">In [${esc(count)}]:</span><pre>${esc(src)}</pre></div>${outs ? `<div class="outputs">${outs}</div>` : ''}</div>`;
    });
    return `<!doctype html>\n<html lang="en"><head><meta charset="utf-8"><title>${esc(title)}</title><meta name="viewport" content="width=device-width,initial-scale=1"><style>${EXPORT_CSS}</style></head>\n<body><h1 class="title">${esc(title)}</h1>\n${cells.join('\n')}\n</body></html>\n`;
}

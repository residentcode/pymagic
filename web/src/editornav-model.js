// The editor navigation's logic that needs no page: where the next method starts, a block's first and last lines,
// the path of definitions to a line, where method separators go, the places to come back to, and a regex under the cursor.
// LSP symbol kinds: 5 class, 6 method, 9 constructor (__init__), 12 function.
const DEF_KINDS = new Set([5, 6, 9, 12]);
/** The classes, methods and functions of a documentSymbol answer, nested ones too, each with its depth. */
export function definitions(symbols, depth = 0, out = []) {
    for (const s of symbols ?? []) {
        if (DEF_KINDS.has(s.kind))
            out.push({ name: s.name, kind: s.kind, depth, start: s.range.start.line + 1, end: s.range.end.line + 1, nameLine: s.selectionRange.start.line + 1 });
        definitions(s.children, DEF_KINDS.has(s.kind) ? depth + 1 : depth, out);
    }
    return out.sort((a, b) => a.start - b.start);
}
/** The line of the next (dir 1) or previous (dir -1) definition from a line, or null. */
export function nextDefinition(defs, line, dir) {
    const lines = defs.map((d) => d.nameLine);
    return dir > 0 ? lines.find((l) => l > line) ?? null : [...lines].reverse().find((l) => l < line) ?? null;
}
/** The definitions that contain a line, outermost first: the breadcrumbs. */
export function pathTo(defs, line) {
    return defs.filter((d) => d.start <= line && line <= d.end).sort((a, b) => a.depth - b.depth);
}
/** The lines a method separator goes above: each definition after the first of its level in the file, not nested in a function. */
export function separatorLines(defs) {
    const out = [];
    for (const d of defs) {
        const parent = defs.filter((p) => p.depth === d.depth - 1 && p.start < d.start && d.end <= p.end).pop();
        if (parent && parent.kind !== 5)
            continue;   // a function inside a function
        const first = defs.find((o) => o.depth === d.depth && (parent ? parent.start < o.start && o.end <= parent.end : true));
        if (first !== d)
            out.push(d.start);
    }
    return out;
}
const indentOf = (text) => text.length - text.trimStart().length;
const blank = (text) => text.trim() === '' || text.trim().startsWith('#');
/** The block a line is in, as Python's indentation makes it: the line that opens it (ending with ":") and its last line. */
export function blockOf(lineText, line, lineCount) {
    let at = line;
    while (at > 1 && blank(lineText(at)))
        at--;
    const own = indentOf(lineText(at));
    let start = null;
    for (let l = at - 1; l >= 1; l--) {
        const t = lineText(l);
        if (blank(t))
            continue;
        if (indentOf(t) < own && t.trimEnd().endsWith(':')) {
            start = l;
            break;
        }
        if (indentOf(t) < own)
            break;
    }
    if (start === null)
        return null;
    const opener = indentOf(lineText(start));
    let end = start;
    for (let l = start + 1; l <= lineCount; l++) {
        const t = lineText(l);
        if (blank(t))
            continue;
        if (indentOf(t) <= opener)
            break;
        end = l;
    }
    return { start, end };
}
/** A place added to the recent ones: newest first, one per file and nearby line, at most `max`. */
export function addPlace(places, place, max = 30, near = 3) {
    const rest = places.filter((p) => !(p.rel === place.rel && Math.abs(p.line - place.line) <= near));
    return [place, ...rest].slice(0, max);
}
const RE_CALL = /\bre\.(compile|match|fullmatch|search|findall|finditer|sub|subn|split)\s*\(\s*$/;
/** The regular expression of a `re.compile(r"...")`-like call when the cursor is inside its first string, as Python reads it:
 * {pattern, flags, start, end} (columns 1-based, the literal's quotes included), else null. */
export function regexAt(lineText, column) {
    const re = /([rRbBuU]{0,2})('''|"""|'|")/g;
    let m;
    while ((m = re.exec(lineText))) {
        const prefix = m[1], quote = m[2];
        const open = m.index, bodyStart = m.index + m[0].length;
        const close = lineText.indexOf(quote, bodyStart + (quote.length === 1 ? 0 : 0));
        let end = close;
        if (quote.length === 1 && !/r/i.test(prefix)) {
            // a backslash escapes the quote in a plain string
            end = bodyStart;
            while (end < lineText.length && lineText[end] !== quote)
                end += lineText[end] === '\\' ? 2 : 1;
        }
        if (end < 0 || end >= lineText.length)
            return null;
        re.lastIndex = end + quote.length;
        if (column - 1 < open || column - 1 > end + quote.length - 1)
            continue;
        if (!RE_CALL.test(lineText.slice(0, open)))
            return null;
        const raw = lineText.slice(bodyStart, end);
        const pattern = /r/i.test(prefix) ? raw : raw.replace(/\\(.)/g, (_, c) => ({ n: '\n', t: '\t', '\\': '\\', "'": "'", '"': '"' }[c] ?? '\\' + c));
        const after = lineText.slice(end + quote.length);
        const flags = [...after.matchAll(/re\.(IGNORECASE|I|MULTILINE|M|DOTALL|S|VERBOSE|X|ASCII|A)\b/g)].map((f) => ({ I: 'IGNORECASE', M: 'MULTILINE', S: 'DOTALL', X: 'VERBOSE', A: 'ASCII' }[f[1]] ?? f[1]));
        return { pattern, flags, start: open + 1, end: end + quote.length + 1 };
    }
    return null;
}

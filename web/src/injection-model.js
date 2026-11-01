// Language injections in Python strings: which strings hold SQL, JSON, HTML or a regular expression (by a `# language=sql` comment, by the
// call they are the argument of, or for SQL by its keywords), the text such a string stands for, and the way back. No DOM here:
// web/test/injection.test.mjs checks it.

/** The strings and comments of a Python source: {strings: [{start, bodyStart, bodyEnd, end, prefix, quote, triple}], comments: [{start, end, text}]}. */
export function scanPython(text) {
    const strings = [], comments = [];
    let i = 0;
    while (i < text.length) {
        const ch = text[i];
        if (ch === '#') {
            let j = i;
            while (j < text.length && text[j] !== '\n') j++;
            comments.push({ start: i, end: j, text: text.slice(i + 1, j) });
            i = j;
            continue;
        }
        const prefixed = /^([A-Za-z]{1,2})(?=["'])/.exec(text.slice(i, i + 3));
        const startsHere = ch === '"' || ch === "'" || (prefixed && /^(r|b|u|f|rb|br|rf|fr)$/i.test(prefixed[1]) && (i === 0 || !/[\w]/.test(text[i - 1])));
        if (!startsHere) { i++; continue; }
        const prefix = ch === '"' || ch === "'" ? '' : prefixed[1].toLowerCase();
        const q0 = i + prefix.length;
        const quote = text.startsWith(text[q0].repeat(3), q0) ? text[q0].repeat(3) : text[q0];
        const bodyStart = q0 + quote.length;
        let j = bodyStart;
        const raw = prefix.includes('r');
        let closed = false;
        while (j < text.length) {
            if (text[j] === '\\') { j += 2; continue; }
            if (text.startsWith(quote, j)) { closed = true; break; }
            if (quote.length === 1 && text[j] === '\n') break;
            j++;
        }
        if (!closed) { i = bodyStart; continue; }
        strings.push({ start: i, bodyStart, bodyEnd: j, end: j + quote.length, prefix, quote, triple: quote.length === 3, raw });
        i = j + quote.length;
    }
    return { strings, comments };
}

const SQL_CALLS = /(?:^|\.)(execute|executemany|executescript|read_sql|read_sql_query|read_sql_table|from_statement|exec_driver_sql|raw|text|mogrify)$/;
const RE_CALLS = /^re\.(compile|match|search|fullmatch|sub|subn|findall|finditer|split)$/;
// a statement that no sentence starts with ...
const SQL_STRONG = /^\s*(insert\s+into|delete\s+from|create\s+(?:or\s+replace\s+)?(?:temp(?:orary)?\s+)?(?:table|view|index|unique\s+index|trigger)|alter\s+table|drop\s+(?:table|view|index)|truncate\s+table|replace\s+into|pragma\s+\w+|with\s+\w+\s+as\s*\()/i;
// ... and words a sentence can start with, which need a second keyword of SQL after them
const SQL_WEAK = /^\s*(select\b(?=[\s\S]*\b(from|where|join|union|limit|order\s+by|group\s+by)\b)|update\b(?=[\s\S]*\bset\b))/i;

const LANGUAGES = { sql: 'sql', json: 'json', html: 'html', regexp: 'regex', regex: 'regex', re: 'regex' };

/** The injections of a source: [{language, start, end (the body), raw, fstring, triple, quote, why}], in order. */
export function findInjections(text) {
    const { strings, comments } = scanPython(text);
    const lineStart = (o) => text.lastIndexOf('\n', o - 1) + 1;
    const found = [];
    for (const s of strings) {
        if (s.prefix.includes('b')) continue;
        const body = text.slice(s.bodyStart, s.bodyEnd);
        let language = null, why = '';
        // 1. a comment: on the line the string starts on, or alone on the line above
        const ls = lineStart(s.start);
        const same = comments.find((c) => c.start >= ls && c.start < s.start + 1 && lineStart(c.start) === ls) ?? comments.find((c) => lineStart(c.start) === ls && c.start > s.end);
        const prevEnd = ls - 1;
        const above = prevEnd > 0 ? comments.find((c) => c.end === prevEnd && text.slice(lineStart(c.start), c.start).trim() === '') : null;
        for (const c of [same, above]) {
            const m = c && /\blang(?:uage)?\s*=\s*([A-Za-z]+)/i.exec(c.text);
            if (m && LANGUAGES[m[1].toLowerCase()]) { language = LANGUAGES[m[1].toLowerCase()]; why = 'comment'; break; }
        }
        // 2. the call the string is the first argument of
        if (!language) {
            const call = /([A-Za-z_][\w.]*)\s*\(\s*$/.exec(text.slice(Math.max(0, s.start - 120), s.start));
            if (call) {
                if (SQL_CALLS.test(call[1])) { language = 'sql'; why = 'call'; }
                else if (RE_CALLS.test(call[1])) { language = 'regex'; why = 'call'; }
                else if (call[1] === 'json.loads') { language = 'json'; why = 'call'; }
            }
        }
        // 3. SQL by its keywords
        if (!language && (SQL_STRONG.test(body) || SQL_WEAK.test(body))) { language = 'sql'; why = 'keywords'; }
        if (language) found.push({ language, start: s.bodyStart, end: s.bodyEnd, raw: s.raw, fstring: s.prefix.includes('f'), triple: s.triple, quote: s.quote, why });
    }
    return found;
}

/** The text of a body with each `{expression}` of an f-string made of `x`s of the same length (positions stay), and `{{` left as the braces they are. */
export function maskHoles(body, fstring) {
    if (!fstring) return { text: body, holes: [] };
    let out = '';
    const holes = [];
    for (let i = 0; i < body.length;) {
        if (body[i] === '{' && body[i + 1] === '{') { out += '{{'; i += 2; continue; }
        if (body[i] === '{') {
            let depth = 1, j = i + 1;
            while (j < body.length && depth) { if (body[j] === '{') depth++; else if (body[j] === '}') depth--; j++; }
            holes.push([i, j]);
            out += 'x'.repeat(j - i);
            i = j;
            continue;
        }
        out += body[i];
        i++;
    }
    return { text: out, holes };
}

const ESCAPES = { n: '\n', t: '\t', r: '\r', '\\': '\\', "'": "'", '"': '"' };

/** What a string's body says, with its escapes read (a raw string's is as it is). null when it cannot be read back safely. */
export function unescapeBody(body, raw) {
    if (raw) return body;
    let out = '';
    for (let i = 0; i < body.length; i++) {
        if (body[i] !== '\\') { out += body[i]; continue; }
        const n = body[i + 1];
        if (n === '\n') { i++; continue; }
        if (n in ESCAPES) { out += ESCAPES[n]; i++; continue; }
        return null;                                      // \x41, \N{...}, \d: left alone
    }
    return out;
}

/** The body for a text: escapes put back (for a raw string, only when the text has nothing that would need one); null when it cannot be. */
export function escapeBody(text, { raw, quote, triple }) {
    if (raw) return text.includes(quote[0]) || text.endsWith('\\') ? null : text;
    let out = text.replace(/\\/g, '\\\\');
    if (triple) {
        // only a run of three quotes (or one at the end) would close the string
        const q = quote[0];
        out = out.replace(new RegExp(`${q}{3,}`, 'g'), (run) => ('\\' + q).repeat(run.length));
        if (out.endsWith(q) && !out.endsWith('\\' + q)) out = out.slice(0, -1) + '\\' + q;
    } else {
        out = out.split(quote[0]).join('\\' + quote[0]).replace(/\r?\n/g, '\\n');
    }
    return out;
}

/** What to call the language in a title. */
export const NAMES = { sql: 'SQL', json: 'JSON', html: 'HTML', regex: 'Regular expression' };

// ------------------------------------------------------------------------------------------------------------------ regular expressions

/**
 * The parts of a regular expression, for colouring: [{start, end, kind}] with kind `escape`, `class` (a [..] set), `group` (the
 * parentheses and what opens a group), `quantifier`, `anchor`, `alternation`.
 */
export function regexTokens(pattern) {
    const out = [];
    let i = 0;
    while (i < pattern.length) {
        const ch = pattern[i];
        if (ch === '\\') { out.push({ start: i, end: Math.min(pattern.length, i + 2), kind: 'escape' }); i += 2; continue; }
        if (ch === '[') {
            let j = i + 1;
            if (pattern[j] === '^') j++;
            if (pattern[j] === ']') j++;
            while (j < pattern.length && pattern[j] !== ']') j += pattern[j] === '\\' ? 2 : 1;
            out.push({ start: i, end: Math.min(pattern.length, j + 1), kind: 'class' });
            i = j + 1;
            continue;
        }
        if (ch === '(') {
            const open = /^\((\?(?:P<\w+>|P=\w+\)|[:=!#]|<[=!]|[aiLmsux-]+(?::|\))))?/.exec(pattern.slice(i));
            out.push({ start: i, end: i + open[0].length, kind: 'group' });
            i += open[0].length;
            continue;
        }
        if (ch === ')') { out.push({ start: i, end: i + 1, kind: 'group' }); i++; continue; }
        if (ch === '*' || ch === '+' || ch === '?') { out.push({ start: i, end: i + (pattern[i + 1] === '?' || pattern[i + 1] === '+' ? 2 : 1), kind: 'quantifier' }); i += pattern[i + 1] === '?' || pattern[i + 1] === '+' ? 2 : 1; continue; }
        if (ch === '{') {
            const q = /^\{\d*(,\d*)?\}\??/.exec(pattern.slice(i));
            if (q) { out.push({ start: i, end: i + q[0].length, kind: 'quantifier' }); i += q[0].length; continue; }
        }
        if (ch === '^' || ch === '$') { out.push({ start: i, end: i + 1, kind: 'anchor' }); i++; continue; }
        if (ch === '|') { out.push({ start: i, end: i + 1, kind: 'alternation' }); i++; continue; }
        i++;
    }
    return out;
}

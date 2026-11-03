// Language injections in Python and JavaScript strings: which strings hold SQL, JSON, HTML, CSS or a regular expression (by a
// `# language=sql` or `// language=sql` comment, by the call they are the argument of or the tag of a template, or for SQL by its keywords),
// a string built from parts taken as one, the text such a string stands for, and the way back. No DOM here: web/test/injection.test.mjs
// checks it.

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

const LANGUAGES = { sql: 'sql', json: 'json', html: 'html', css: 'css', regexp: 'regex', regex: 'regex', re: 'regex' };

// JavaScript: the calls whose first argument is SQL, a regular expression or JSON, and the tags of a template that say its language
const JS_SQL_CALLS = /(?:^|\.)(query|execute|prepare|raw|sql|unsafe)$/;
const JS_TAGS = { sql: 'sql', html: 'html', css: 'css', json: 'json' };

/** After the `${` of a template: the offset just after its `}`, past the braces, strings and templates inside. */
function skipHole(text, j) {
    let depth = 1;
    while (j < text.length && depth) {
        const c = text[j];
        if (c === '"' || c === "'" || c === '`') {
            j++;
            while (j < text.length && text[j] !== c) {
                if (text[j] === '\\') j += 2;
                else if (c === '`' && text[j] === '$' && text[j + 1] === '{') j = skipHole(text, j + 2);
                else j++;
            }
            j++;
            continue;
        }
        if (c === '{') depth++;
        else if (c === '}') depth--;
        j++;
    }
    return j;
}

/**
 * The strings and comments of a JavaScript or TypeScript source, as scanPython gives them: '...', "..." and `...` (a template, whose `${...}`
 * are skipped), `//` and `/* *\/` comments. A regular expression literal is not told apart from a division (a quote in one can mislead it).
 */
export function scanJs(text) {
    const strings = [], comments = [];
    let i = 0;
    while (i < text.length) {
        const ch = text[i];
        if (ch === '/' && text[i + 1] === '/') {
            let j = i;
            while (j < text.length && text[j] !== '\n') j++;
            comments.push({ start: i, end: j, text: text.slice(i + 2, j) });
            i = j;
            continue;
        }
        if (ch === '/' && text[i + 1] === '*') {
            const close = text.indexOf('*/', i + 2);
            const end = close < 0 ? text.length : close + 2;
            comments.push({ start: i, end, text: text.slice(i + 2, close < 0 ? text.length : close) });
            i = end;
            continue;
        }
        if (ch !== '"' && ch !== "'" && ch !== '`') { i++; continue; }
        const bodyStart = i + 1;
        let j = bodyStart, closed = false;
        while (j < text.length) {
            if (text[j] === '\\') { j += 2; continue; }
            if (text[j] === ch) { closed = true; break; }
            if (ch === '`' && text[j] === '$' && text[j + 1] === '{') { j = skipHole(text, j + 2); continue; }
            if (ch !== '`' && text[j] === '\n') break;
            j++;
        }
        if (!closed) { i = bodyStart; continue; }
        strings.push({ start: i, bodyStart, bodyEnd: j, end: j + 1, prefix: '', quote: ch, triple: ch === '`', raw: false, template: ch === '`' });
        i = j + 1;
    }
    return { strings, comments };
}

// between two parts of a string built with `+`: the plus signs, and names, attributes, calls and subscripts between them
const PLUS_GAP = /^\s*\+\s*(?:[\w.$]+(?:\([^()'"`]*\))?(?:\[[^\]'"`]*\])?\s*\+\s*)*$/;

/**
 * Whether two strings are parts of one, and with a value between them: `"a " + x + " b"`, `"a" + "b"`, and in Python the strings written
 * one after the other ("a" "b", also over lines inside brackets). `between` is the text between them, its comments taken out.
 */
function joinedBy(between, python, inBrackets) {
    const plain = between.replace(/\\\r?\n/g, ' ');
    if (python && /^\s*$/.test(plain) && (inBrackets || !plain.includes('\n'))) return { joined: true, value: false };
    if (PLUS_GAP.test(plain)) return { joined: true, value: /[\w$]/.test(plain) };
    return { joined: false };
}

/** For each string's start: the offset of the bracket it is inside of (the innermost not closed before it), or -1. Strings and comments do not count. */
function openBrackets(text, strings, comments) {
    const skip = [...strings.map((s) => [s.start, s.end]), ...comments.map((c) => [c.start, c.end])].sort((a, b) => a[0] - b[0]);
    const open = new Map();
    const stack = [];
    let k = 0, next = 0;
    const starts = strings.map((s) => s.start);
    for (let i = 0; i < text.length; i++) {
        while (next < starts.length && starts[next] === i) { open.set(i, stack.length ? stack[stack.length - 1] : -1); next++; }
        while (k < skip.length && skip[k][1] <= i) k++;
        if (k < skip.length && skip[k][0] <= i) { i = skip[k][1] - 1; continue; }
        const c = text[i];
        if (c === '(' || c === '[' || c === '{') stack.push(i);
        else if (c === ')' || c === ']' || c === '}') stack.pop();
    }
    return open;
}

/**
 * The injections of a source: [{language, start, end (the body), raw, fstring, template, triple, quote, why, chain}], in order. `kind` is
 * 'python' or 'javascript' (TypeScript too). A string built from parts (`"SELECT " + cols + " FROM t"`, or strings one after the other) is
 * one fragment: its language is found from the whole, every part is given it, and `chain` ({parts: [{start, end, fstring, template}],
 * values: [whether a value stands between part k and k+1]}) is the same object on each of them.
 */
export function findInjections(text, kind = 'python') {
    const python = kind !== 'javascript';
    const { strings, comments } = python ? scanPython(text) : scanJs(text);
    const lineStart = (o) => text.lastIndexOf('\n', o - 1) + 1;
    const usable = strings.filter((s) => !s.prefix.includes('b'));
    const opens = openBrackets(text, usable, comments);
    // the strings in chains of parts
    const chains = [];
    for (const s of usable) {
        const last = chains[chains.length - 1];
        if (last) {
            const prev = last.strings[last.strings.length - 1];
            let between = text.slice(prev.end, s.start);
            for (const c of comments) if (c.start >= prev.end && c.end <= s.start) between = between.replace(text.slice(c.start, c.end), ' ');
            const j = joinedBy(between, python, opens.get(s.start) >= 0);
            if (j.joined) { last.strings.push(s); last.values.push(j.value); continue; }
        }
        chains.push({ strings: [s], values: [] });
    }
    const found = [];
    for (const { strings: parts, values } of chains) {
        const first = parts[0], lastPart = parts[parts.length - 1];
        const whole = parts.map((p, k) => text.slice(p.bodyStart, p.bodyEnd) + (k < values.length && values[k] ? 'x' : '')).join('');
        let language = null, why = '';
        // 1. a comment: on the line the string starts (or its last part ends) on, or alone on the line above
        const ls = lineStart(first.start);
        const same = comments.find((c) => c.start >= ls && c.start < first.start + 1 && lineStart(c.start) === ls)
            ?? comments.find((c) => lineStart(c.start) === ls && c.start > first.end)
            ?? comments.find((c) => lineStart(c.start) === lineStart(lastPart.end) && c.start >= lastPart.end);
        const aboveLine = (from) => (from - 1 > 0 ? comments.find((c) => c.end === from - 1 && text.slice(lineStart(c.start), c.start).trim() === '') : null);
        // the line above the string's, or above the line the bracket around it opens on: `d = (` over the parts of a string
        const opener = opens.get(first.start) ?? -1;
        const above = aboveLine(ls) ?? (opener >= 0 ? aboveLine(lineStart(opener)) : null);
        for (const c of [same, above]) {
            const m = c && /\blang(?:uage)?\s*=\s*([A-Za-z]+)/i.exec(c.text);
            if (m && LANGUAGES[m[1].toLowerCase()]) { language = LANGUAGES[m[1].toLowerCase()]; why = 'comment'; break; }
        }
        // 2. the call the string is the first argument of, or (JavaScript) the tag of a template: sql`...`, html`...`
        if (!language) {
            const before = text.slice(Math.max(0, first.start - 120), first.start);
            const call = /([A-Za-z_$][\w.$]*)\s*\(\s*$/.exec(before);
            const tag = !python && first.template ? /([A-Za-z_$][\w$]*)\s*$/.exec(before) : null;
            if (tag && JS_TAGS[tag[1].toLowerCase()]) { language = JS_TAGS[tag[1].toLowerCase()]; why = 'tag'; }
            else if (call && python) {
                if (SQL_CALLS.test(call[1])) { language = 'sql'; why = 'call'; }
                else if (RE_CALLS.test(call[1])) { language = 'regex'; why = 'call'; }
                else if (call[1] === 'json.loads') { language = 'json'; why = 'call'; }
            } else if (call) {
                if (JS_SQL_CALLS.test(call[1])) { language = 'sql'; why = 'call'; }
                else if (call[1] === 'RegExp' || call[1] === 'new RegExp') { language = 'regex'; why = 'call'; }
                else if (call[1] === 'JSON.parse') { language = 'json'; why = 'call'; }
            }
        }
        // 3. SQL by its keywords, in the whole of a string built from parts
        if (!language && (SQL_STRONG.test(whole) || SQL_WEAK.test(whole))) { language = 'sql'; why = 'keywords'; }
        if (!language) continue;
        const chain = parts.length > 1 ? { parts: parts.map((p) => ({ start: p.bodyStart, end: p.bodyEnd, fstring: p.prefix.includes('f'), template: !!p.template })), values } : undefined;
        for (const s of parts)
            found.push({ language, start: s.bodyStart, end: s.bodyEnd, raw: s.raw, fstring: s.prefix.includes('f'), template: !!s.template, triple: s.triple, quote: s.quote, why, ...(chain ? { chain } : {}) });
    }
    return found;
}

/**
 * The text a fragment stands for, for its colours: the bodies of its parts one after the other (their holes masked), an `x` where a value
 * is put between two of them. `pieces` say where each part's text is: [{at (in the text), start (in the source), length}].
 */
export function fragmentText(source, inj) {
    const parts = inj.chain?.parts ?? [{ start: inj.start, end: inj.end, fstring: inj.fstring, template: inj.template }];
    let text = '';
    const pieces = [];
    parts.forEach((p, k) => {
        pieces.push({ at: text.length, start: p.start, length: p.end - p.start });
        text += maskHoles(source.slice(p.start, p.end), p.fstring, p.template).text;
        if (inj.chain?.values[k]) text += 'x';
    });
    return { text, pieces };
}

/** The ranges of the source [start, end] that a range of a fragment's text stands on (a token can run over two parts). */
export function sourceRanges(pieces, from, to) {
    const out = [];
    for (const p of pieces) {
        const a = Math.max(from, p.at), b = Math.min(to, p.at + p.length);
        if (b > a) out.push([p.start + (a - p.at), p.start + (b - p.at)]);
    }
    return out;
}

/**
 * The text of a body with each `{expression}` of an f-string (or `${expression}` of a JavaScript template) made of `x`s of the same length
 * (positions stay), and `{{` left as the braces they are.
 */
export function maskHoles(body, fstring, template = false) {
    if (template) {
        let out = '';
        const holes = [];
        for (let i = 0; i < body.length;) {
            if (body[i] === '\\') { out += body.slice(i, i + 2); i += 2; continue; }
            if (body[i] === '$' && body[i + 1] === '{') {
                const j = skipHole(body, i + 2);
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
// a JavaScript template has two more
const TEMPLATE_ESCAPES = { ...ESCAPES, '`': '`', $: '$' };

/** What a string's body says, with its escapes read (a raw string's is as it is). null when it cannot be read back safely. */
export function unescapeBody(body, raw, template = false) {
    if (raw) return body;
    const escapes = template ? TEMPLATE_ESCAPES : ESCAPES;
    let out = '';
    for (let i = 0; i < body.length; i++) {
        if (body[i] !== '\\') { out += body[i]; continue; }
        const n = body[i + 1];
        if (n === '\n') { i++; continue; }
        if (n in escapes) { out += escapes[n]; i++; continue; }
        return null;                                      // \x41, \N{...}, \d: left alone
    }
    return out;
}

/** The body for a text: escapes put back (for a raw string, only when the text has nothing that would need one); null when it cannot be. */
export function escapeBody(text, { raw, quote, triple, template }) {
    if (raw) return text.includes(quote[0]) || text.endsWith('\\') ? null : text;
    let out = text.replace(/\\/g, '\\\\');
    if (template) {
        // a template ends at its backtick, and ${ would start a value
        out = out.replace(/`/g, '\\`').replace(/\$\{/g, '\\${');
    } else if (triple) {
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
export const NAMES = { sql: 'SQL', json: 'JSON', html: 'HTML', css: 'CSS', regex: 'Regular expression' };

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

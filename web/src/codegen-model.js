// Code generation that needs no page: Surround With, Unwrap / Remove, Emmet abbreviations, file templates and the project's own snippets.
// Snippet texts use Monaco's syntax: $1, ${2:name}, $0 for where the cursor ends.

const indentOf = (text) => text.length - text.trimStart().length;
const blank = (text) => text.trim() === '';
const escapeSnippet = (text) => text.replace(/[\\$}]/g, (c) => '\\' + c);

/** What Surround With offers: [id, label] for lines, and for a part of one line. */
export const SURROUND_LINES = [
    ['if', 'if'], ['ifelse', 'if / else'], ['while', 'while'], ['for', 'for'], ['try', 'try / except'], ['tryfinally', 'try / finally'], ['with', 'with'],
];
export const SURROUND_EXPRESSION = [['par', '(expr)'], ['not', 'not (expr)'], ['str', 'str(expr)'], ['len', 'len(expr)'], ['fstring', 'f"{expr}"']];

/** The snippet that replaces whole lines with them inside a statement: the lines move one level in, the statement takes their first indentation. */
export function surroundLines(lines, kind, unit = '    ') {
    const used = lines.filter((l) => !blank(l));
    if (used.length === 0)
        return null;
    const base = Math.min(...used.map(indentOf));
    const pad = used.find((l) => indentOf(l) === base).slice(0, base);
    const body = lines.map((l) => (blank(l) ? '' : pad + unit + escapeSnippet(l.slice(base)))).join('\n');
    const head = {
        if: 'if ${1:condition}:', ifelse: 'if ${1:condition}:', while: 'while ${1:condition}:', for: 'for ${1:item} in ${2:items}:',
        try: 'try:', tryfinally: 'try:', with: 'with ${1:expression} as ${2:target}:',
    }[kind];
    if (!head)
        return null;
    const tail = {
        ifelse: `\n${pad}else:\n${pad}${unit}\${0:pass}`,
        try: `\n${pad}except \${1:Exception} as \${2:e}:\n${pad}${unit}\${0:raise}`,
        tryfinally: `\n${pad}finally:\n${pad}${unit}\${0:pass}`,
    }[kind] ?? '';
    return `${pad}${head}\n${body}${tail}`;
}

/** The snippet that replaces a selected expression. */
export function surroundExpression(text, kind) {
    const e = escapeSnippet(text);
    return { par: `(${e})$0`, not: `not (${e})$0`, str: `str(${e})$0`, len: `len(${e})$0`, fstring: `f"{${e}}"$0` }[kind] ?? null;
}

const OPENERS = /^(async\s+)?(if|elif|else|for|while|with|try|except|finally)\b.*:\s*(#.*)?$/;
const FOLLOWERS = { if: ['elif', 'else'], elif: ['elif', 'else'], for: ['else'], while: ['else'], try: ['except', 'else', 'finally'], except: ['except', 'else', 'finally'] };
const keywordOf = (text) => OPENERS.exec(text.trim())?.[2] ?? null;

/** The last line of the clause that opens at `line`: the lines indented under it. */
function clauseEnd(lineText, line, lineCount) {
    const own = indentOf(lineText(line));
    let end = line;
    for (let l = line + 1; l <= lineCount; l++) {
        const t = lineText(l);
        if (blank(t))
            continue;
        if (indentOf(t) <= own)
            break;
        end = l;
    }
    return end;
}

/** The clauses of the statement a clause belongs to (if / elif / else, try / except / finally, for / else), as {keyword, start, end}. */
function statementOf(lineText, line, lineCount) {
    const own = indentOf(lineText(line));
    // back to the clause that starts the statement
    let first = line;
    while (!['if', 'for', 'while', 'with', 'try'].includes(keywordOf(lineText(first)))) {
        let l = first - 1;
        while (l >= 1 && (blank(lineText(l)) || indentOf(lineText(l)) > own))
            l--;
        if (l < 1 || indentOf(lineText(l)) !== own || !keywordOf(lineText(l)))
            break;
        first = l;
    }
    const clauses = [];
    let at = first;
    while (at <= lineCount) {
        const keyword = keywordOf(lineText(at));
        const end = clauseEnd(lineText, at, lineCount);
        clauses.push({ keyword, start: at, end });
        let next = end + 1;
        while (next <= lineCount && blank(lineText(next)))
            next++;
        if (next > lineCount || indentOf(lineText(next)) !== own || !(FOLLOWERS[keyword] ?? []).includes(keywordOf(lineText(next))))
            break;
        at = next;
    }
    return clauses;
}

/** A clause's body moved out by one level (the lines under its header). */
function dedentedBody(lineText, clause) {
    const header = indentOf(lineText(clause.start));
    const lines = [];
    for (let l = clause.start + 1; l <= clause.end; l++)
        lines.push(lineText(l));
    const used = lines.filter((t) => !blank(t));
    const shift = used.length ? Math.min(...used.map(indentOf)) - header : 0;
    return lines.map((t) => (blank(t) ? '' : t.slice(shift)));
}

/** The header of the innermost clause that holds a line (the line itself when it is one and `self` is true), or null. */
function enclosingHeader(lineText, line, lineCount, self) {
    if (self && keywordOf(lineText(line)))
        return line;
    const own = blank(lineText(line)) ? Infinity : indentOf(lineText(line));
    for (let l = line - 1; l >= 1; l--) {
        const t = lineText(l);
        if (blank(t) || indentOf(t) >= own)
            continue;
        return keywordOf(t) && clauseEnd(lineText, l, lineCount) >= line ? l : null;   // a def or class ends the search
    }
    return null;
}

/** What Unwrap / Remove offers at a line: the statements around it, innermost first, as {label, start, end, lines} where lines replace start..end. */
export function unwrapOptions(lineText, line, lineCount) {
    const out = [];
    for (let header = enclosingHeader(lineText, line, lineCount, true); header !== null;) {
        const clauses = statementOf(lineText, header, lineCount);
        const mine = clauses.find((c) => c.start === header) ?? clauses[0];
        const first = clauses[0], last = clauses[clauses.length - 1];
        out.push({ label: `Unwrap '${mine.keyword}...'`, start: first.start, end: last.end, lines: dedentedBody(lineText, mine) });
        const rest = clauses.filter((c) => c !== mine);
        const tryLeftBare = first.keyword === 'try' && !rest.some((c) => c.keyword === 'except' || c.keyword === 'finally');
        if (mine !== first && !tryLeftBare) {
            const kept = [];
            for (const c of rest)
                for (let l = c.start; l <= c.end; l++)
                    kept.push(lineText(l));
            out.push({ label: `Remove '${mine.keyword}...'`, start: first.start, end: last.end, lines: kept });
        }
        header = enclosingHeader(lineText, first.start, lineCount, false);
    }
    return out;
}

// ------------------------------------------------------------ Emmet

const VOID = new Set(['area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'source', 'track', 'wbr']);
const INLINE = new Set(['a', 'abbr', 'b', 'button', 'code', 'em', 'i', 'label', 'option', 'small', 'span', 'strong', 'sub', 'sup', 'td', 'th', 'li', 'title', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'p', 'dt', 'dd', 'textarea']);
const DEFAULT_ATTRS = { a: [['href', '']], img: [['src', ''], ['alt', '']], input: [['type', 'text'], ['name', '']], link: [['rel', 'stylesheet'], ['href', '']], script: [['src', '']], form: [['action', '']], label: [['for', '']] };
/** The tag an element with no name gets inside a parent. */
const IMPLIED_CHILD = { ul: 'li', ol: 'li', table: 'tr', tbody: 'tr', thead: 'tr', tr: 'td', select: 'option', dl: 'dt' };
const KNOWN = new Set([...VOID, ...INLINE, 'div', 'section', 'article', 'aside', 'header', 'footer', 'main', 'nav', 'ul', 'ol', 'table', 'thead', 'tbody', 'tr', 'form', 'select', 'script', 'style', 'html', 'head', 'body', 'pre', 'blockquote', 'figure', 'figcaption', 'dl', 'fieldset', 'legend', 'video', 'audio', 'canvas', 'iframe', 'template', 'details', 'summary', 'svg']);
const HTML5 = '<!DOCTYPE html>\n<html lang="${1:en}">\n<head>\n\t<meta charset="UTF-8">\n\t<meta name="viewport" content="width=device-width, initial-scale=1.0">\n\t<title>${2:Document}</title>\n</head>\n<body>\n\t$0\n</body>\n</html>';

/** The abbreviation that ends at the cursor: back to a space outside brackets, or to the end of an HTML tag. */
export function abbreviationBefore(text) {
    let depth = 0, i = text.length;
    while (i > 0) {
        const c = text[i - 1];
        if (c === ']' || c === '}' || c === ')')
            depth++;
        else if (c === '[' || c === '{' || c === '(') {
            if (depth === 0)
                break;
            depth--;
        }
        else if (depth === 0 && /\s/.test(c))
            break;
        i--;
    }
    let abbr = text.slice(i);
    const tagEnd = abbr.lastIndexOf('<') >= 0 ? abbr.indexOf('>', abbr.lastIndexOf('<')) : -1;
    if (tagEnd >= 0)
        abbr = abbr.slice(tagEnd + 1);
    return abbr;
}

/** Parses an abbreviation into a tree of {name, id, classes, attrs, text, count, children}; null when it is not one. */
function parse(abbr) {
    let i = 0;
    const peek = () => abbr[i];
    function element() {
        if (peek() === '(') {
            i++;
            const group = sequence();
            if (peek() !== ')')
                throw new Error('unclosed group');
            i++;
            const node = { group, count: 1 };
            multiplier(node);
            return node;
        }
        const node = { name: '', id: null, classes: [], attrs: [], text: null, count: 1, children: [] };
        const name = /^[A-Za-z][\w:-]*/.exec(abbr.slice(i));
        if (name) {
            node.name = name[0];
            i += name[0].length;
        }
        for (;;) {
            const c = peek();
            if (c === '#' || c === '.') {
                const m = /^[\w$-]+/.exec(abbr.slice(i + 1));
                if (!m)
                    throw new Error('empty name');
                i += 1 + m[0].length;
                if (c === '#')
                    node.id = m[0];
                else
                    node.classes.push(m[0]);
            }
            else if (c === '[') {
                const close = abbr.indexOf(']', i);
                if (close < 0)
                    throw new Error('unclosed attributes');
                for (const m of abbr.slice(i + 1, close).matchAll(/([\w:@-]+)(?:=("[^"]*"|'[^']*'|[^\s\]]*))?/g))
                    node.attrs.push([m[1], m[2] === undefined ? '' : m[2].replace(/^["']|["']$/g, '')]);
                i = close + 1;
            }
            else if (c === '{') {
                const close = abbr.indexOf('}', i);
                if (close < 0)
                    throw new Error('unclosed text');
                node.text = abbr.slice(i + 1, close);
                i = close + 1;
            }
            else
                break;
        }
        if (!node.name && !node.id && !node.classes.length && !node.attrs.length && node.text === null)
            throw new Error('no element');
        multiplier(node);
        return node;
    }
    function multiplier(node) {
        if (peek() === '*') {
            const m = /^\d+/.exec(abbr.slice(i + 1));
            if (!m)
                throw new Error('no count');
            node.count = Number(m[0]);
            i += 1 + m[0].length;
        }
    }
    // siblings joined by +, each with its children after >; ^ climbs out of the innermost level
    function sequence() {
        const top = [];
        const stack = [top];   // the lists that take the next element
        let lastNode = null;
        for (;;) {
            const node = element();
            stack[stack.length - 1].push(node);
            lastNode = node;
            const op = peek();
            if (op === '+') {
                i++;
                continue;
            }
            if (op === '>') {
                i++;
                if (lastNode.group)
                    throw new Error('a group takes no children');
                stack.push(lastNode.children);
                continue;
            }
            if (op === '^') {
                while (peek() === '^') {
                    i++;
                    if (stack.length > 1)
                        stack.pop();
                }
                continue;
            }
            return top;
        }
    }
    try {
        const tree = sequence();
        return i === abbr.length ? tree : null;
    }
    catch {
        return null;
    }
}

/** The HTML for an abbreviation such as `ul>li.item$*3` or `!`, as a snippet with a tab stop in each empty place; null when it is not one. */
export function expandAbbreviation(abbr, unit = '\t') {
    if (abbr === '!' || abbr === 'html:5')
        return HTML5.replaceAll('\t', unit);
    if (!abbr || !/^[A-Za-z.#([{]/.test(abbr))
        return null;
    const tree = parse(abbr);
    if (!tree)
        return null;
    // a lone word is an abbreviation only when it is an HTML tag
    if (tree.length === 1 && !tree[0].group && tree[0].count === 1 && /^[A-Za-z][\w-]*$/.test(abbr) && !KNOWN.has(abbr.toLowerCase()))
        return null;
    let stop = 0;
    const number = (text, index, count) => text.replace(/(\$+)(@-)?/g, (_, d, back) => String(back ? count - index + 1 : index).padStart(d.length, '0'));
    const field = (value) => (value === '' ? `\${${++stop}}` : escapeSnippet(value));
    function render(nodes, parentTag, depth) {
        const out = [];
        for (const node of nodes) {
            for (let n = 1; n <= node.count; n++) {
                if (node.group) {
                    out.push(...render(node.group, parentTag, depth));
                    continue;
                }
                const tag = node.name || IMPLIED_CHILD[parentTag] || (INLINE.has(parentTag) ? 'span' : 'div');
                const attrs = [];
                if (node.id !== null)
                    attrs.push(['id', number(node.id, n, node.count)]);
                if (node.classes.length)
                    attrs.push(['class', node.classes.map((c) => number(c, n, node.count)).join(' ')]);
                const given = node.attrs.map(([k, v]) => [k, number(v, n, node.count)]);
                for (const [k, v] of DEFAULT_ATTRS[tag] ?? [])
                    if (!given.some(([g]) => g === k) && !attrs.some(([g]) => g === k))
                        attrs.push([k, v]);
                attrs.push(...given);
                const open = `<${tag}${attrs.map(([k, v]) => ` ${k}="${field(v)}"`).join('')}>`;
                const pad = unit.repeat(depth);
                if (VOID.has(tag)) {
                    out.push(pad + open);
                    continue;
                }
                const text = node.text === null ? '' : escapeSnippet(number(node.text, n, node.count));
                if (node.children.length) {
                    const inner = render(node.children, tag, depth + 1);
                    out.push(pad + open + text, ...inner, `${pad}</${tag}>`);
                }
                else
                    out.push(`${pad}${open}${text || `\${${++stop}}`}</${tag}>`);
            }
        }
        return out;
    }
    return render(tree, '', 0).join('\n');
}

// ------------------------------------------------------------ file templates

/** What a new file starts with, when the project gives no template of its own (.pymagic/fileTemplates.json). */
export const DEFAULT_FILE_TEMPLATES = [
    { name: 'Python test', pattern: 'test_*.py', body: 'import pytest\n\n\ndef test_${NAME_SUFFIX}():\n    pass\n' },
    { name: 'Python package', pattern: '__init__.py', body: '' },
    { name: 'Python file', pattern: '*.py', body: '' },
    { name: 'HTML file', pattern: '*.html', body: '<!DOCTYPE html>\n<html lang="en">\n<head>\n    <meta charset="UTF-8">\n    <title>${NAME}</title>\n</head>\n<body>\n\n</body>\n</html>\n' },
];

const globToRegExp = (glob) => new RegExp('^' + glob.split('*').map((s) => s.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('.*') + '$', 'i');

/** The text of a new file: the first template whose pattern fits its name, with ${NAME}, ${FILE_NAME}, ${DATE}, ${TIME}, ${YEAR}, ${USER} and ${PROJECT_NAME} filled in. */
export function newFileText(fileName, templates, vars = {}) {
    const t = (templates ?? []).find((x) => x.pattern && globToRegExp(x.pattern).test(fileName));
    if (!t || !t.body)
        return '';
    const name = fileName.replace(/\.[^.]*$/, '');
    const all = { FILE_NAME: fileName, NAME: name, NAME_SUFFIX: name.replace(/^test_/, '') || 'name', ...vars };
    return t.body.replace(/\$\{([A-Z_]+)\}/g, (whole, key) => (key in all ? String(all[key]) : whole));
}

/** The project's file templates from the text of .pymagic/fileTemplates.json (a list of {name, pattern, body}); the defaults when there is none or it does not read. */
export function readFileTemplates(text) {
    try {
        const list = JSON.parse(text);
        if (Array.isArray(list) && list.every((t) => t && typeof t.pattern === 'string' && typeof t.body === 'string'))
            return list;
    }
    catch { /* the defaults */ }
    return DEFAULT_FILE_TEMPLATES;
}

// ------------------------------------------------------------ the project's own snippets (.pymagic/snippets.json, VS Code's form)

/** The snippets of a snippets.json text as a list of {name, prefix, description, body}. */
export function readSnippets(text) {
    let obj;
    try {
        obj = JSON.parse(text || '{}');
    }
    catch {
        return null;
    }
    return Object.entries(obj ?? {}).filter(([, v]) => v && typeof v === 'object' && 'body' in v).map(([name, v]) => ({
        name,
        prefix: Array.isArray(v.prefix) ? v.prefix[0] ?? '' : v.prefix ?? '',
        description: v.description ?? '',
        body: Array.isArray(v.body) ? v.body.join('\n') : String(v.body),
    }));
}

/** The snippets.json text of a list of snippets; a name taken twice gets a number. */
export function writeSnippets(list) {
    const out = {};
    for (const s of list) {
        let name = s.name || s.prefix || 'snippet', n = 2;
        while (name in out)
            name = `${s.name || s.prefix} ${n++}`;
        out[name] = { prefix: s.prefix, description: s.description, body: s.body.split('\n') };
    }
    return JSON.stringify(out, null, 2) + '\n';
}

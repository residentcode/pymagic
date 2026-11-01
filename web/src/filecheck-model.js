// Problems in the files that are not Python: TOML and YAML syntax, .env files, Dockerfiles, Markdown links, and the regular
// expressions of Python's re module. Each check returns [{line, start, end, severity, code, message}] (1-based line and columns).
// No DOM here: web/test/filecheck.test.mjs checks it.

/** What kind of file a project path is, or null when nothing here checks it. */
export function kindOf(rel) {
    const name = rel.split('/').pop().toLowerCase();
    if (name.endsWith('.toml')) return 'toml';
    if (name.endsWith('.yml') || name.endsWith('.yaml')) return 'yaml';
    if (name === '.env' || name.startsWith('.env.') || name.endsWith('.env')) return 'env';
    if (name === 'dockerfile' || name.startsWith('dockerfile.') || name.endsWith('.dockerfile')) return 'dockerfile';
    if (name.endsWith('.md') || name.endsWith('.markdown')) return 'markdown';
    if (name.endsWith('.py') || name.endsWith('.pyi')) return 'python';
    return null;
}

export function check(kind, text, context = {}) {
    switch (kind) {
        case 'toml': return tomlProblems(text);
        case 'yaml': return yamlProblems(text);
        case 'env': return envProblems(text);
        case 'dockerfile': return dockerfileProblems(text);
        case 'markdown': return markdownProblems(text, context);
        case 'python': return pythonRegexProblems(text);
        default: return [];
    }
}

const problem = (line, start, end, severity, code, message) => ({ line, start, end: Math.max(end, start + 1), severity, code, message });

// ------------------------------------------------------------------------------------------------------------------ TOML

/**
 * The text with every string's content and every comment blanked (strings keep their quotes, newlines stay), so structure can be
 * read without a string's brackets or equals signs getting in the way. `open` is where a string was never closed.
 */
function maskToml(text) {
    let out = '';
    const open = [];
    let i = 0, line = 1, col = 1;
    const joined = new Set();                  // 0-based lines whose end is inside a multi-line string
    let inMultiline = false;
    const put = (ch) => { out += ch; if (ch === '\n') { if (inMultiline) joined.add(line - 1); line++; col = 1; } else col++; };
    while (i < text.length) {
        const ch = text[i];
        if (ch === '#') {
            while (i < text.length && text[i] !== '\n') { put(' '); i++; }
            continue;
        }
        if (ch === '"' || ch === "'") {
            const triple = text.startsWith(ch.repeat(3), i);
            const startLine = line, startCol = col;
            const quote = triple ? ch.repeat(3) : ch;
            for (const _ of quote) { put(ch); i++; }
            inMultiline = triple;
            let closed = false;
            while (i < text.length) {
                if (text.startsWith(quote, i)) {
                    // in a triple string, up to two more quotes may end it ("""a"""" is a" and the end)
                    if (triple) while (text.startsWith(quote + ch, i)) { put('s'); i++; }
                    inMultiline = false;
                    for (const _ of quote) { put(ch); i++; }
                    closed = true;
                    break;
                }
                if (!triple && text[i] === '\n') break;
                if (ch === '"' && text[i] === '\\' && i + 1 < text.length && text[i + 1] !== '\n') { put('s'); i++; }
                put(text[i] === '\n' ? '\n' : 's');
                i++;
            }
            inMultiline = false;
            if (!closed) open.push({ line: startLine, col: startCol, triple });
            continue;
        }
        put(ch);
        i++;
    }
    return { masked: out, open, joined };
}

const TOML_VALUE = /^(true|false|[+-]?(inf|nan)|[+-]?(0|[1-9][0-9_]*)(\.[0-9][0-9_]*)?([eE][+-]?[0-9_]+)?|0x[0-9a-fA-F_]+|0o[0-7_]+|0b[01_]+|\d{4}-\d\d-\d\d([Tt ]\d\d:\d\d(:\d\d(\.\d+)?)?([Zz]|[+-]\d\d:\d\d)?)?|\d\d:\d\d(:\d\d(\.\d+)?)?)$/;

export function tomlProblems(text) {
    const found = [];
    const { masked, open, joined } = maskToml(text);
    for (const o of open)
        found.push(problem(o.line, o.col, o.col + (o.triple ? 3 : 1), 'error', 'toml-string', o.triple ? 'This multi-line string is never closed' : 'This string is not closed on its line'));
    const lines = text.split('\n');
    const mlines = masked.split('\n');
    const tables = new Set();
    let keys = new Set();
    let n = 0;
    while (n < mlines.length) {
        const first = mlines[n];
        if (!first.trim()) { n++; continue; }
        // a statement runs on while brackets are open
        let depth = 0, end = n, stmt = '';
        for (; end < mlines.length; end++) {
            stmt += (end > n ? '\n' : '') + mlines[end];
            for (const ch of mlines[end]) depth += ch === '[' || ch === '{' ? 1 : ch === ']' || ch === '}' ? -1 : 0;
            if (depth <= 0 && !joined.has(end)) break;
        }
        const startCol = first.length - first.trimStart().length + 1;
        const lineNo = n + 1;
        if (depth > 0) {
            const header = first.trimStart().startsWith('[') && !first.includes('=');
            found.push(problem(lineNo, startCol, first.trimEnd().length + 1, 'error', header ? 'toml-header' : 'toml-brackets', header ? 'Not a valid table header' : 'A bracket opened here is never closed'));
            if (header) { n++; continue; }
            break;
        }
        const code = stmt.trim();
        const original = lines.slice(n, end + 1).join('\n');
        if (code.startsWith('[')) {
            const m = /^(\[\[?)\s*([^\[\]]+?)\s*(\]\]?)$/.exec(code);
            const array = code.startsWith('[[');
            if (!m || (m[1].length !== m[3].length) || !/^[A-Za-z0-9_\-."'s ]+$/.test(m[2]) || /(^\.|\.\.|\.$)/.test(m[2].replace(/\s/g, ''))) {
                found.push(problem(lineNo, startCol, first.trimEnd().length + 1, 'error', 'toml-header', 'Not a valid table header'));
            } else if (!array) {
                const name = normalizeKey(original.trim().slice(1, -1));
                if (tables.has(name)) found.push(problem(lineNo, startCol, first.trimEnd().length + 1, 'error', 'toml-duplicate-table', `The table [${name}] is already defined`));
                tables.add(name);
                keys = new Set();
            } else {
                keys = new Set();
            }
        } else {
            const eq = code.indexOf('=');
            if (eq < 0) {
                found.push(problem(lineNo, startCol, first.trimEnd().length + 1, 'error', 'toml-no-equals', 'Expected key = value'));
            } else {
                const keyMasked = code.slice(0, eq).trim();
                const value = code.slice(eq + 1).trim();
                const keyStart = first.indexOf(code[0]) + 1;
                if (!keyMasked || !/^[A-Za-z0-9_\-."'s ]+$/.test(keyMasked) || /(^\.|\.\.|\.$)/.test(keyMasked.replace(/\s/g, ''))) {
                    found.push(problem(lineNo, keyStart, keyStart + Math.max(1, eq), 'error', 'toml-key', 'Not a valid key'));
                } else {
                    const key = normalizeKey(original.slice(0, original.indexOf('=')));
                    if (keys.has(key)) found.push(problem(lineNo, keyStart, keyStart + keyMasked.length, 'error', 'toml-duplicate-key', `The key ${key} is already set in this table`));
                    keys.add(key);
                }
                if (!value) {
                    found.push(problem(lineNo, startCol, first.trimEnd().length + 1, 'error', 'toml-no-value', 'The key has no value'));
                } else if (!/^["'\[{]/.test(value) && !TOML_VALUE.test(value)) {
                    const at = first.indexOf(value[0], first.indexOf('=') + 1) + 1;
                    found.push(problem(lineNo, at, first.trimEnd().length + 1, 'error', 'toml-value', 'Not a TOML value (strings need quotes; true and false are lower case)'));
                }
            }
        }
        n = end + 1;
    }
    return found;
}

const normalizeKey = (s) => s.trim().replace(/\s*\.\s*/g, '.').replace(/^"([^"]*)"$/, '$1');

// ------------------------------------------------------------------------------------------------------------------ YAML

export function yamlProblems(text) {
    const found = [];
    const lines = text.split('\n').map((l) => l.replace(/\r$/, ''));
    let stack = [];                                   // [{indent, keys:Set}], the mappings being read
    let blockIndent = -1;                             // inside a | or > block scalar: lines indented deeper than this are text
    for (let i = 0; i < lines.length; i++) {
        const raw = lines[i];
        if (!raw.trim()) continue;
        const indentOf = raw.length - raw.trimStart().length;
        if (blockIndent >= 0) {
            if (indentOf > blockIndent) continue;
            blockIndent = -1;
        }
        if (/^(---|\.\.\.)(\s|$)/.test(raw)) { stack = []; continue; }
        if (raw.trimStart().startsWith('#')) continue;
        const lead = /^[ \t]*/.exec(raw)[0];
        if (lead.includes('\t')) {
            found.push(problem(i + 1, 1, lead.length + 1, 'error', 'yaml-tab', 'YAML cannot be indented with tabs'));
            continue;
        }
        let col = indentOf, rest = raw.slice(indentOf), item = false;
        while (/^-(\s+|$)/.test(rest)) {
            item = true;
            const dash = /^-\s*/.exec(rest)[0].length;
            col += dash;
            rest = rest.slice(dash);
        }
        if (item && !rest) continue;
        const m = /^("(?:[^"\\]|\\.)*"|'(?:[^']|'')*'|[^\s#'"\[\]{},&*!|>%@`:?-][^#]*?|-[^\s#][^#]*?)\s*:(\s+|$)(.*)$/.exec(rest);
        if (m) {
            const key = m[1].replace(/^(["'])(.*)\1$/, '$2');
            if (item) { while (stack.length && stack[stack.length - 1].indent >= col) stack.pop(); }
            else { while (stack.length && stack[stack.length - 1].indent > col) stack.pop(); }
            if (!stack.length || stack[stack.length - 1].indent < col) stack.push({ indent: col, keys: new Set() });
            const top = stack[stack.length - 1];
            if (top.keys.has(key)) found.push(problem(i + 1, col + 1, col + 1 + m[1].length, 'warning', 'yaml-duplicate-key', `The key ${key} appears twice in this mapping (the later one wins)`));
            top.keys.add(key);
            const value = m[3].trim();
            if (/^[|>][+-]?\d*\s*(#.*)?$/.test(value)) { blockIndent = indentOf + (item ? 1 : 0); if (item) blockIndent = col - 1; continue; }
            checkYamlValue(value, i, lines, found, rest.length - m[3].length + col);
        } else if (item) {
            checkYamlValue(rest.trim(), i, lines, found, col);
        }
    }
    return found;
}

/** A quoted scalar that is never closed, or a flow collection whose brackets never balance. */
function checkYamlValue(value, i, lines, found, col) {
    if (!value) return;
    const first = value[0];
    if (first === '"' || first === "'") {
        let rest = value.slice(1), j = i;
        for (;;) {
            const closeAt = first === '"' ? closingDouble(rest) : closingSingle(rest);
            if (closeAt >= 0) return;
            j++;
            if (j >= lines.length) break;
            rest += '\n' + lines[j];
        }
        found.push(problem(i + 1, col + 1, col + 1 + value.length, 'error', 'yaml-quote', 'This quoted string is never closed'));
    } else if (first === '[' || first === '{') {
        let depth = 0, j = i, scan = value;
        for (;;) {
            let quote = '';
            for (let k = 0; k < scan.length; k++) {
                const ch = scan[k];
                if (quote) { if (ch === quote) quote = ''; else if (quote === '"' && ch === '\\') k++; continue; }
                if (ch === '"' || ch === "'") quote = ch;
                else if (ch === '#' && (k === 0 || /\s/.test(scan[k - 1]))) break;
                else if (ch === '[' || ch === '{') depth++;
                else if (ch === ']' || ch === '}') depth--;
            }
            if (depth <= 0) { if (depth < 0) found.push(problem(i + 1, col + 1, col + 1 + value.length, 'error', 'yaml-brackets', 'A closing bracket has no opening one')); return; }
            j++;
            if (j >= lines.length) break;
            scan = lines[j];
        }
        found.push(problem(i + 1, col + 1, col + 2, 'error', 'yaml-brackets', 'A bracket opened here is never closed'));
    }
}
function closingDouble(s) { for (let k = 0; k < s.length; k++) { if (s[k] === '\\') k++; else if (s[k] === '"') return k; } return -1; }
function closingSingle(s) { for (let k = 0; k < s.length; k++) { if (s[k] === "'") { if (s[k + 1] === "'") k++; else return k; } } return -1; }

// ------------------------------------------------------------------------------------------------------------------ .env

export function envProblems(text) {
    const found = [];
    const seen = new Map();
    const lines = text.split('\n').map((l) => l.replace(/\r$/, ''));
    for (let i = 0; i < lines.length; i++) {
        const raw = lines[i];
        const line = raw.trim();
        if (!line || line.startsWith('#')) continue;
        const body = line.replace(/^export\s+/, '');
        const lead = raw.indexOf(body) + 1;
        const eq = body.indexOf('=');
        if (eq < 0) {
            found.push(problem(i + 1, lead, lead + body.length, 'error', 'env-no-equals', 'Expected NAME=value'));
            continue;
        }
        const name = body.slice(0, eq).trim();
        if (!/^[A-Za-z_][A-Za-z0-9_.-]*$/.test(name)) {
            found.push(problem(i + 1, lead, lead + Math.max(1, eq), 'error', 'env-name', 'Not a variable name (letters, digits and underscores, not starting with a digit)'));
        } else if (seen.has(name)) {
            found.push(problem(i + 1, lead, lead + name.length, 'warning', 'env-duplicate', `${name} is already set on line ${seen.get(name)}`));
        } else seen.set(name, i + 1);
        let value = body.slice(eq + 1).trim();
        const quote = value[0];
        if (quote === '"' || quote === "'") {
            // a quoted value may run over several lines
            let rest = value.slice(1), j = i;
            while (!(quote === '"' ? closingDouble(rest) >= 0 : rest.includes(quote)) && j + 1 < lines.length) { j++; rest += '\n' + lines[j]; }
            if (!(quote === '"' ? closingDouble(rest) >= 0 : rest.includes(quote)))
                found.push(problem(i + 1, lead + eq + 1, lead + body.length, 'error', 'env-quote', 'This quoted value is never closed'));
            else i = j;
        } else if (/\s/.test(value.replace(/\s+#.*$/, '')) && !/^\$\(/.test(value)) {
            const at = lead + eq + 1;
            found.push(problem(i + 1, at, lead + body.length, 'warning', 'env-space', 'A value with spaces should be quoted'));
        }
    }
    return found;
}

// ------------------------------------------------------------------------------------------------------------------ Dockerfile

const INSTRUCTIONS = new Set(['FROM', 'RUN', 'CMD', 'LABEL', 'MAINTAINER', 'EXPOSE', 'ENV', 'ADD', 'COPY', 'ENTRYPOINT', 'VOLUME', 'USER', 'WORKDIR', 'ARG', 'ONBUILD', 'STOPSIGNAL', 'HEALTHCHECK', 'SHELL']);

export function dockerfileProblems(text) {
    const found = [];
    const lines = text.split('\n').map((l) => l.replace(/\r$/, ''));
    let seenFrom = false, stages = 0;
    const lastOf = {};
    const aliases = new Set();                        // stage names (FROM python AS build): a later FROM build is not an image
    for (let i = 0; i < lines.length; i++) {
        const first = lines[i];
        if (!first.trim() || first.trim().startsWith('#')) continue;
        // a trailing backslash continues the instruction on the next line (comment lines may sit between)
        let body = first, end = i;
        while (/\\\s*$/.test(body) && end + 1 < lines.length) {
            end++;
            if (lines[end].trim().startsWith('#')) { body = body.replace(/\\\s*$/, '') + '\\'; continue; }
            body = body.replace(/\\\s*$/, ' ') + lines[end].trim();
        }
        const m = /^(\s*)([A-Za-z]+)(\s+(.*))?$/.exec(body);
        const lead = (m?.[1].length ?? 0) + 1;
        if (!m) { found.push(problem(i + 1, 1, first.length + 1, 'error', 'docker-syntax', 'Expected an instruction such as FROM or RUN')); i = end; continue; }
        const word = m[2].toUpperCase();
        const args = (m[4] ?? '').trim();
        if (!INSTRUCTIONS.has(word)) {
            found.push(problem(i + 1, lead, lead + m[2].length, 'error', 'docker-unknown', `${m[2]} is not a Dockerfile instruction`));
            i = end;
            continue;
        }
        if (!args && word !== 'HEALTHCHECK') found.push(problem(i + 1, lead, lead + m[2].length, 'error', 'docker-args', `${word} needs arguments`));
        if (word === 'FROM') {
            seenFrom = true;
            stages++;
            const image = args.replace(/^--\S+\s+/, '').split(/\s+/)[0];
            const hasTag = /@sha256:/.test(image) || image.split('/').pop().includes(':');
            const alias = /\s+as\s+(\S+)\s*$/i.exec(args);
            if (alias) aliases.add(alias[1].toLowerCase());
            if (image && image !== 'scratch' && !image.includes('$') && !aliases.has(image.toLowerCase()) && !hasTag) {
                const at = lead + m[2].length + 1 + (m[3] ?? '').indexOf(image);
                found.push(problem(i + 1, at, at + image.length, 'warning', 'docker-latest', `${image} has no tag, so it means :latest and changes under you`));
            } else if (/:latest$/.test(image)) {
                const at = lead + m[2].length + 1 + (m[3] ?? '').indexOf(image);
                found.push(problem(i + 1, at, at + image.length, 'warning', 'docker-latest', `${image} changes under you; pin a version`));
            }
        } else if (!seenFrom && word !== 'ARG') {
            found.push(problem(i + 1, lead, lead + m[2].length, 'error', 'docker-first', 'A Dockerfile starts with FROM (only ARG may come before it)'));
            seenFrom = true;
        }
        if (word === 'CMD' || word === 'ENTRYPOINT') {
            if (lastOf[word + stages] !== undefined) found.push(problem(i + 1, lead, lead + m[2].length, 'warning', 'docker-repeat', `Only the last ${word} counts; line ${lastOf[word + stages]} has one too`));
            lastOf[word + stages] = i + 1;
        }
        if (word === 'COPY' || word === 'ADD') {
            const parts = args.replace(/--\S+\s*/g, '').trim();
            const list = parts.startsWith('[') ? null : parts.split(/\s+/).filter(Boolean);
            if (list && list.length < 2) found.push(problem(i + 1, lead, first.length + 1, 'error', 'docker-copy', `${word} needs a source and a destination`));
            if (word === 'ADD' && list && !list.some((p) => /^https?:\/\//.test(p) || /\.(tar|tgz|tar\.gz|tar\.bz2|tar\.xz|zip)$/i.test(p)))
                found.push(problem(i + 1, lead, lead + 3, 'info', 'docker-add', 'COPY says what is meant here: ADD also unpacks archives and fetches URLs'));
        }
        if (word === 'EXPOSE') {
            for (const p of args.split(/\s+/)) {
                if (p && !p.includes('$') && !/^\d{1,5}(-\d{1,5})?(\/(tcp|udp|sctp))?$/i.test(p)) {
                    const at = first.indexOf(p, lead + 5) + 1;
                    found.push(problem(i + 1, at, at + p.length, 'error', 'docker-port', `${p} is not a port (8000 or 8000/tcp)`));
                }
            }
        }
        if (word === 'WORKDIR' && args && !/^[/$"']|^[A-Za-z]:/.test(args)) found.push(problem(i + 1, lead, first.length + 1, 'info', 'docker-workdir', 'A relative WORKDIR depends on the one before it; an absolute path is clearer'));
        i = end;
    }
    if (!seenFrom && lines.some((l) => l.trim() && !l.trim().startsWith('#'))) found.push(problem(1, 1, 2, 'error', 'docker-no-from', 'There is no FROM'));
    return found;
}

// ------------------------------------------------------------------------------------------------------------------ Markdown

export const headingSlug = (title) => title.trim().toLowerCase().replace(/`/g, '').replace(/[^\p{L}\p{N}\s_-]/gu, '').replace(/\s/g, '-');

/** The anchors a Markdown text has (GitHub's: a heading's lower-cased words joined by hyphens, -1, -2 for repeats). */
export function headingAnchors(text) {
    const anchors = new Set(), counts = new Map();
    let fence = '';
    for (const line of text.split('\n')) {
        const f = /^\s*(```|~~~)/.exec(line);
        if (f) { fence = fence ? (fence === f[1] ? '' : fence) : f[1]; continue; }
        if (fence) continue;
        const h = /^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$/.exec(line);
        if (!h) continue;
        const slug = headingSlug(h[1].replace(/\[([^\]]*)\]\([^)]*\)/g, '$1'));
        const n = counts.get(slug) ?? 0;
        counts.set(slug, n + 1);
        anchors.add(n ? `${slug}-${n}` : slug);
    }
    return anchors;
}

/** `context`: {rel: the file's project path, files: Set of the project's file paths (null: unknown, so no file is reported)}. */
export function markdownProblems(text, context = {}) {
    const found = [];
    const own = headingAnchors(text);
    const dir = (context.rel ?? '').split('/').slice(0, -1);
    let fence = '';
    const lines = text.split('\n');
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        const f = /^\s*(```|~~~)/.exec(line);
        if (f) { fence = fence ? (fence === f[1] ? '' : fence) : f[1]; continue; }
        if (fence) continue;
        // inline code is not a link
        const code = line.replace(/`[^`]*`/g, (s) => ' '.repeat(s.length));
        const re = /!?\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+(?:"[^"]*"|'[^']*'))?\s*\)/g;
        let m;
        while ((m = re.exec(code))) {
            const target = m[1];
            if (/^([a-z][a-z0-9+.-]*:|\/\/|\{\{|\$)/i.test(target)) continue;
            const at = m.index + m[0].indexOf(target) + 1;
            const hash = target.indexOf('#');
            const path = decodeURIComponentSafe(hash >= 0 ? target.slice(0, hash) : target).replace(/\?.*$/, '');
            const anchor = hash >= 0 ? decodeURIComponentSafe(target.slice(hash + 1)).toLowerCase() : '';
            if (!path) {
                if (anchor && !own.has(anchor))
                    found.push(problem(i + 1, at, at + target.length, 'warning', 'md-anchor', `No heading here makes the anchor #${anchor}`));
                continue;
            }
            if (!context.files) continue;
            const resolved = resolveRel(path.startsWith('/') ? [] : dir, path);
            if (resolved === null) { found.push(problem(i + 1, at, at + target.length, 'warning', 'md-link', `${path} is outside the project`)); continue; }
            const isDir = [...context.files].some((p) => p.startsWith(resolved + '/'));
            if (resolved && !context.files.has(resolved) && !isDir)
                found.push(problem(i + 1, at, at + target.length, 'warning', 'md-link', `${path} is not in the project`));
        }
    }
    return found;
}

function decodeURIComponentSafe(s) { try { return decodeURIComponent(s); } catch { return s; } }

/** `from` (a list of folders) plus a relative path, or null when it climbs out of the project. */
function resolveRel(from, path) {
    const parts = [...from];
    for (const seg of path.split('/')) {
        if (!seg || seg === '.') continue;
        if (seg === '..') { if (!parts.length) return null; parts.pop(); } else parts.push(seg);
    }
    return parts.join('/');
}

// ------------------------------------------------------------------------------------------------------------------ regular expressions

const RE_FUNCTIONS = 'compile|match|search|fullmatch|sub|subn|findall|finditer|split';

/**
 * The regular expressions of `re.compile("...")`, `re.sub(r"...", ...)` and the like on one line, with the column the pattern starts at.
 * Only plain strings are looked at: an f-string, a triple-quoted string, or an escaped one (without the r prefix) has text the pattern
 * is not.
 */
export function regexLiterals(line) {
    const out = [];
    const re = new RegExp(`\\bre\\.(${RE_FUNCTIONS})\\(\\s*([rR]?)(["'])`, 'g');
    let m;
    while ((m = re.exec(line))) {
        const raw = m[2] !== '', quote = m[3];
        const start = m.index + m[0].length;
        if (line.startsWith(quote + quote, start - 1) && line.startsWith(quote.repeat(3), start - 1)) continue;
        let end = start;
        while (end < line.length && line[end] !== quote) { if (line[end] === '\\') end += raw ? 2 : 2; else end++; }
        if (end >= line.length) continue;
        const pattern = line.slice(start, end);
        if (!raw && pattern.includes('\\')) continue;
        out.push({ pattern, col: start + 1, fullmatch: m[1] === 'fullmatch' });
    }
    return out;
}

export function pythonRegexProblems(text) {
    const found = [];
    const lines = text.split('\n');
    for (let i = 0; i < lines.length; i++) {
        if (!lines[i].includes('re.')) continue;
        for (const lit of regexLiterals(lines[i]))
            for (const p of regexProblems(lit.pattern, { anchored: lit.fullmatch }))
                found.push(problem(i + 1, lit.col + p.index, lit.col + p.index + Math.max(1, p.length), p.severity ?? 'warning', p.code, p.message));
    }
    return found;
}

const SPECIAL_OUTSIDE = new Set('\\^$.|?*+()[]{}'.split(''));
const SPECIAL_INSIDE = new Set('\\]^-['.split(''));

/**
 * Problems in one pattern: [{index, length, code, message, severity?}]. A redundant escape (`\-` outside a class), a character twice
 * in a class, an empty group, and an alternative that can never match because an earlier one already did.
 * `anchored` (fullmatch, or a pattern starting with ^ or ending in $) takes the last away: with an anchor, a later alternative is reached.
 */
export function regexProblems(pattern, options = {}) {
    const found = [];
    const verbose = !!options.verbose;
    const anchored = options.anchored || pattern.startsWith('^') || pattern.endsWith('$') || /\(\?[a-zA-Z]*x/.test(pattern);
    let i = 0;
    const alternatives = [];                  // top-level alternatives: [{from, to}]
    let altFrom = 0, depth = 0;
    const stack = [];
    while (i < pattern.length) {
        const ch = pattern[i];
        if (ch === '\\') {
            const next = pattern[i + 1];
            if (next !== undefined && !/[A-Za-z0-9]/.test(next) && !SPECIAL_OUTSIDE.has(next) && !(verbose && (next === ' ' || next === '#')) && next !== ' ' && next !== '#' && next !== '\\')
                found.push({ index: i, length: 2, code: 're-escape', message: `The escape \\${next} is redundant outside a character class` });
            i += 2;
            continue;
        }
        if (ch === '[') {
            const result = readClass(pattern, i);
            found.push(...result.problems);
            i = result.end;
            continue;
        }
        if (ch === '(') {
            stack.push(i);
            depth++;
            const empty = /^\((\?:|\?P<\w+>|\?i:|\?s:|\?m:)?\)/.exec(pattern.slice(i));
            if (empty) found.push({ index: i, length: empty[0].length, code: 're-empty-group', message: 'This group is empty' });
            i++;
            continue;
        }
        if (ch === ')') { stack.pop(); depth = Math.max(0, depth - 1); i++; continue; }
        if (ch === '|' && depth === 0) {
            alternatives.push({ from: altFrom, to: i });
            altFrom = i + 1;
        }
        i++;
    }
    if (alternatives.length) {
        alternatives.push({ from: altFrom, to: pattern.length });
        const literal = (a) => {
            const s = pattern.slice(a.from, a.to);
            return /^[A-Za-z0-9_ ]+$/.test(s) ? s : null;
        };
        for (let j = 1; j < alternatives.length; j++) {
            const later = literal(alternatives[j]);
            if (later === null) continue;
            for (let k = 0; k < j; k++) {
                const earlier = literal(alternatives[k]);
                if (earlier === null) continue;
                if (earlier === later) {
                    found.push({ index: alternatives[j].from, length: later.length, code: 're-alternative', message: `The alternative ${later} repeats an earlier one` });
                    break;
                }
                if (!anchored && later.startsWith(earlier)) {
                    found.push({ index: alternatives[j].from, length: later.length, code: 're-alternative', message: `The alternative ${later} is never used: ${earlier} matches first (put the longer one first)` });
                    break;
                }
            }
        }
    }
    return found.sort((a, b) => a.index - b.index);
}

/** A character class from `[` at `from`: {end (index after its `]`), problems}. */
function readClass(pattern, from) {
    const problems = [];
    let i = from + 1;
    if (pattern[i] === '^') i++;
    const seen = new Map();
    const start = i;
    while (i < pattern.length) {
        const ch = pattern[i];
        if (ch === ']' && i > start) { i++; return { end: i, problems }; }
        let unit, width = 1;
        if (ch === '\\') {
            const next = pattern[i + 1];
            width = 2;
            if (next !== undefined && !/[A-Za-z0-9]/.test(next) && !SPECIAL_INSIDE.has(next) && next !== ' ' && next !== '#' && next !== '\\')
                problems.push({ index: i, length: 2, code: 're-escape', message: `The escape \\${next} is redundant in a character class` });
            unit = /[A-Za-z0-9]/.test(next ?? '') ? null : (next ?? '');
        } else unit = ch;
        // a range a-z is one unit; its ends are not repeats of single characters
        if (pattern[i + width] === '-' && pattern[i + width + 1] !== undefined && pattern[i + width + 1] !== ']') {
            const to = pattern[i + width + 1] === '\\' ? 3 : 2;
            i += width + to;
            continue;
        }
        if (unit !== null && unit !== undefined) {
            if (seen.has(unit)) problems.push({ index: i, length: width, code: 're-class-repeat', message: `${unit === ' ' ? 'The space' : `The character ${unit}`} is in this class twice` });
            else seen.set(unit, i);
        }
        i += width;
    }
    return { end: pattern.length, problems };
}

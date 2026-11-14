// Indentation that does not follow the { } blocks, in JavaScript and TypeScript (Express's too), CSS, SCSS, Less and JSON: a line 1 unit in from
// the line that opened its block, a closing } at that line's indentation (JSON's [ ] as blocks too). The unit is the file's own (2 or 4 spaces,
// or a tab). Only what is certain is told, as for PHP (php-format-model.js): a line inside an open ( or [, a statement's continuation (a chained
// .then(), a condition on two lines, a selector list), a comment, a switch's cases, the inside of a template literal, and a file with JSX or
// with its braces not balanced (being typed) are left alone. fixIndentation moves the wrong lines, each with what goes with it.

/** The kind of checking of a Monaco language id, or null. */
export function braceKind(languageId) {
    return { javascript: 'js', typescript: 'js', css: 'css', scss: 'scss', less: 'scss', json: 'json' }[languageId] ?? null;
}

const REGEX_BEFORE = new Set(['(', ',', '=', ':', '[', '!', '&', '|', '?', '{', '}', ';', '+', '-', '*', '%', '<', '>', '~', '^', '=>']);
const REGEX_WORDS = new Set(['return', 'typeof', 'case', 'do', 'else', 'in', 'of', 'new', 'delete', 'void', 'throw', 'instanceof', 'yield', 'await']);

/** The tokens that matter: brackets, ; and , (type: the character), words, and strings/comments/regexes as one token each. { type, start, end }. */
export function tokenize(text, kind) {
    const out = [];
    const n = text.length;
    let i = 0, last = null;   // the last significant token, for a / that starts a regex
    const push = (type, start, end) => { out.push({ type, start, end }); if (type !== 'comment') last = type === 'word' ? text.slice(start, end) : type; };
    function stringEnd(q, from) {
        let j = from;
        while (j < n && text[j] !== q) { if (text[j] === '\\') j++; else if (text[j] === '\n' && kind !== 'js') break; j++; }
        return Math.min(j + 1, n);
    }
    function templateEnd(from) {   // after the opening `: to after the closing `, through ${ … } with their own strings and braces
        let j = from;
        while (j < n && text[j] !== '`') {
            if (text[j] === '\\') { j += 2; continue; }
            if (text[j] === '$' && text[j + 1] === '{') {
                let depth = 1;
                j += 2;
                while (j < n && depth > 0) {
                    const c = text[j];
                    if (c === '{') depth++;
                    else if (c === '}') depth--;
                    else if (c === '"' || c === "'") { j = stringEnd(c, j + 1); continue; }
                    else if (c === '`') { j = templateEnd(j + 1); continue; }
                    j++;
                }
                continue;
            }
            j++;
        }
        return Math.min(j + 1, n);
    }
    while (i < n) {
        const c = text[i];
        if (c === ' ' || c === '\t' || c === '\r' || c === '\n') { i++; continue; }
        if (c === '/' && text[i + 1] === '*') { const e = text.indexOf('*/', i + 2); const end = e < 0 ? n : e + 2; push('comment', i, end); i = end; continue; }
        if (c === '/' && text[i + 1] === '/' && kind !== 'css') { let e = text.indexOf('\n', i); if (e < 0) e = n; push('comment', i, e); i = e; continue; }
        if (c === '"' || c === "'") { const end = stringEnd(c, i + 1); push('string', i, end); i = end; continue; }
        if (c === '`' && kind === 'js') { const end = templateEnd(i + 1); push('string', i, end); i = end; continue; }
        if (c === '/' && kind === 'js' && (last === null || REGEX_BEFORE.has(last) || REGEX_WORDS.has(last))) {
            let j = i + 1, inClass = false;
            while (j < n && text[j] !== '\n') {
                if (text[j] === '\\') { j += 2; continue; }
                if (text[j] === '[') inClass = true;
                else if (text[j] === ']') inClass = false;
                else if (text[j] === '/' && !inClass) break;
                j++;
            }
            j++;
            while (j < n && /[a-z]/i.test(text[j])) j++;
            push('regex', i, j); i = j; continue;
        }
        // url(…) unquoted in a stylesheet: one string (its // is not a comment)
        if (kind !== 'js' && kind !== 'json' && /^url\(\s*[^'"\s)]/i.test(text.slice(i, i + 6))) { let e = text.indexOf(')', i); if (e < 0) e = n; push('string', i, e + 1); i = e + 1; continue; }
        if (/[A-Za-z_$@\d.#-]/.test(c)) { let j = i + 1; while (j < n && /[\w$@.#-]/.test(text[j])) j++; push('word', i, j); i = j; continue; }
        if (c === '=' && text[i + 1] === '>') { push('=>', i, i + 2); i += 2; continue; }
        push(c, i, i + 1);
        i++;
    }
    return out;
}

const columns = (indent) => { let c = 0; for (const ch of indent) c += ch === '\t' ? 4 - (c % 4) : 1; return c; };

/** The file's indentation unit in columns (the commonest step in from one line to the next: 2, 4 or 8; a tab is 4), 4 when there is none. */
export function indentUnit(text) {
    const counts = new Map();
    let prev = 0;
    for (const line of text.split('\n')) {
        if (!line.trim()) continue;
        const now = columns(/^[ \t]*/.exec(line)[0]);
        const step = now - prev;
        if (step === 2 || step === 4 || step === 8) counts.set(step, (counts.get(step) ?? 0) + 1);
        prev = now;
    }
    let best = 4, most = 0;
    for (const [step, count] of counts) if (count > most) { best = step; most = count; }
    return best;
}

/** The scan: { found: [{ line (1-based), actual, expected, message }], checked: [{ line (0-based), depth }], movable: Set of 0-based lines }. */
function scan(text, kind, limit, givenUnit = 0) {
    const none = { found: [], checked: [], movable: new Set() };
    if (text.length > 1_000_000 || !kind) return none;
    // JSX: markup in the code, its text not JavaScript's (an apostrophe is not a quote there): not told
    if (kind === 'js' && /<\/[A-Za-z][\w.:-]*\s*>|<[A-Za-z][\w.]*(?:\s[^<>]*)?\/>/.test(text)) return none;
    // a bundle (webpack's, browserify's): generated, its modules' bodies at the left edge, not edited by hand
    if (kind === 'js' && /\/\*{6}\/|__webpack_require__|webpackBootstrap|\[function\(require,module,exports\)\{/.test(text)) return none;
    const tokens = tokenize(text, kind);
    const lineStarts = [0];
    for (let i = 0; i < text.length; i++) if (text[i] === '\n') lineStarts.push(i + 1);
    const lineOf = (pos) => { let lo = 0, hi = lineStarts.length - 1; while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (lineStarts[mid] <= pos) lo = mid; else hi = mid - 1; } return lo; };
    const indentAt = (line) => columns(/^[ \t]*/.exec(text.slice(lineStarts[line], lineStarts[line + 1] ?? text.length))[0]);
    const tabs = (() => { let t = 0, s = 0; for (const l of text.split('\n')) { if (l.startsWith('\t')) t++; else if (l.startsWith(' ')) s++; } return t > s; })();
    const describe = (cols) => (cols === 0 ? 'none' : tabs && cols % 4 === 0 ? `${cols / 4} tab${cols === 4 ? '' : 's'}` : `${cols} space${cols === 1 ? '' : 's'}`);
    const blockOpen = (t) => t === '{' || (kind === 'json' && t === '[');
    const blockClose = (t) => t === '}' || (kind === 'json' && t === ']');

    const stack = [], checks = [], checked = [], movable = new Set();
    let item = false, ended = true, sawSwitch = false, startLine = -1, balanced = true, statementLine = 0, firstType = null;
    let afterControl = false, controlLine = -1, prevWord = null;
    const lineText = (l) => text.slice(lineStarts[l], lineStarts[l + 1] ?? text.length).trim();
    for (let ti = 0; ti < tokens.length; ti++) {
        const tok = tokens[ti];
        const { type, start } = tok;
        const line = lineOf(start);
        // where the statement (or the selector, or the JSON item) this token is in started: after a ; or a block, after a , between an object's
        // properties, and on the line after a brace-less if (…) / for (…) / while (…) / else (`if (a)` then `for (…) {` on the next line)
        if ((ended || item || (afterControl && line !== controlLine)) && type !== 'comment' && stack[stack.length - 1]?.kind !== '(') statementLine = line;
        if (type !== 'comment') { item = false; afterControl = false; }
        if (line !== startLine && !text.slice(lineStarts[line], start).trim()) {
            startLine = line;
            firstType = type;
            movable.add(line);
            let block = null;
            for (let k = stack.length - 1; k >= 0; k--) if (stack[k].kind === '{') { block = stack[k]; break; }
            const inside = stack.slice(stack.indexOf(block) + 1);
            const closing = blockClose(type);
            // a stylesheet's prefixed property and the plain one lined up under it (-webkit-clip-path: …; then clip-path: …) are left as they are
            const prefixed = kind !== 'js' && kind !== 'json' && line > 0 && /^-(?:webkit|moz|ms|o)-/.test(lineText(line - 1))
                && lineText(line - 1).replace(/^-(?:webkit|moz|ms|o)-/, '').split(':')[0] === lineText(line).split(':')[0];
            if (type !== 'comment' && !inside.length && !block?.loose && !(block?.switch && !closing) && (closing || ended) && !prefixed) {
                const blocks = stack.filter((e) => e.kind === '{').length;
                checked.push({ line, depth: closing ? blocks - 1 : blocks });
                checks.push({ line, base: block ? block.base : 0, inBlock: !!block && !block.flat, closing, actual: indentAt(line), tabbed: /^\t+(?=\S)/.test(text.slice(lineStarts[line], lineStarts[line + 1] ?? text.length)) });
            }
        }
        // a string, comment or regex over several lines: the lines it goes on in are not moved
        if (type === 'string' || type === 'comment' || type === 'regex') {
            const endLine = lineOf(Math.max(start, tok.end - 1));
            for (let l = line + 1; l <= endLine; l++) movable.add(-1 - l);   // marked: not movable (see below)
            if (type === 'comment') continue;
        }
        if (blockOpen(type)) {
            // the block's lines go in from the line its statement started on (a selector list over lines, a class whose `implements B {` is on
            // the next line, a condition on two lines closed by `b) {`), or from the { line itself when the { is in a call's arguments
            // (`.then(() => {` on a line of its own) or starts its line (after `=`, or Allman style)
            const inCall = stack[stack.length - 1]?.kind === '(';
            const ownLine = startLine === line && firstType === '{';
            // two blocks opened on one line (`get x() { try {`): the outer one's lines (`catch {`) stand with the inner one's }
            for (let k = stack.length - 1; k >= 0; k--) if (stack[k].kind === '{') { if (stack[k].line === line) stack[k].flat = true; break; }
            // left alone: a block on a line starting with | & ? : (a union's object type, a ternary's object), laid out in ways of their own; and
            // the outermost (function () { … })() of a file (UMD, a bundle's wrapper), whose body is often left at the left edge
            // a function in parentheses ((function () { … })(), webpack's /***/ (function(module, exports) { … }) whose body is often left at the
            // left edge; and an object passed to a call (`call(a, {`), whose properties are laid out in ways of their own (a function's body in a
            // call, `=> {` or `) {`, is told)
            const before = tokens[ti - 1] ? text.slice(tokens[ti - 1].start, tokens[ti - 1].end) : '';
            const loose = kind === 'js' && ((['|', '&', '?', ':'].includes(firstType) && startLine === line)
                || /^(?:\/\*.*?\*\/\s*)*[;!(]/.test(lineText(statementLine))
                || /^(?:\/\*.*?\*\/\s*)*\(\s*(?:async\s+)?(?:function\b|\()/.test(lineText(line))
                || (inCall && ['(', ',', ':', '['].includes(before)));
            // (never further in than the { line itself: without semicolons a statement can seem to have started on an earlier, deeper line)
            stack.push({ kind: '{', line, base: inCall || ownLine ? indentAt(line) : Math.min(indentAt(statementLine), indentAt(line)), switch: sawSwitch, isSwitch: sawSwitch, loose, outer: statementLine });
        }
        else if (type === '(' || type === '[') stack.push({ kind: '(', line, control: type === '(' && /^(?:if|for|while|with)$/.test(prevWord ?? '') });
        else if (type === ')' || (type === ']' && kind !== 'json')) {
            const top = stack[stack.length - 1];
            if (top?.kind === '(') { stack.pop(); if (top.control) { afterControl = true; controlLine = line; } } else balanced = false;
        }
        let blockEnd = false;
        if (blockClose(type)) {
            while (stack.length && stack[stack.length - 1].kind === '(') { stack.pop(); balanced = false; }
            const open = stack.pop();
            if (!open) balanced = false;
            blockEnd = open?.kind === '{';
            if (open?.kind === '{') statementLine = open.outer;   // back in the statement that held the block
        }
        const word = type === 'word' ? text.slice(start, tok.end) : null;
        if (word === 'switch') sawSwitch = true;
        if (kind === 'js' && (word === 'else' || word === 'do')) { afterControl = true; controlLine = line; }
        // a } followed by : ? . , ) ] or an operator goes on (an object in a ternary, `}).then(`): not the end of a statement
        let next = ti + 1;
        while (tokens[next]?.type === 'comment') next++;
        const goesOn = kind === 'js' && blockEnd && tokens[next] && (':?.,)]+-*/&|=<>'.includes(text[tokens[next].start])
            || /^(?:from|as|satisfies|instanceof|in|of|extends|implements)$/.test(text.slice(tokens[next].start, tokens[next].end)));
        const top = stack[stack.length - 1];
        // a switch's case x: and default: end their line's statement
        const caseEnd = type === ':' && top?.kind === '{' && top.isSwitch;
        ended = type === ';' || blockOpen(type) || (blockEnd && !goesOn) || caseEnd || (kind === 'json' && type === ',');
        item = type === ',' && top?.kind === '{';
        if (ended) sawSwitch = false;
        prevWord = word;
    }
    if (!balanced || stack.length) return none;   // braces not balanced: being typed, or not this language
    // the unit: how far in from their block's line the lines of blocks are, most often (2, 4 or 8 columns; a tab is 4); else from the file
    const steps = new Map();
    for (const c of checks) { const d = c.actual - c.base; if (c.inBlock && !c.closing && (d === 2 || d === 4 || d === 8)) steps.set(d, (steps.get(d) ?? 0) + 1); }
    let unit = steps.size ? 0 : indentUnit(text), most = 0;
    for (const [d, count] of steps) if (count > most) { unit = d; most = count; }
    if (tabs) unit = 4;   // a file indented with tabs: one tab
    if (givenUnit) unit = givenUnit;   // (Fix indentation's later passes: the unit the file had before)
    const found = [];
    let wrong = 0;
    for (const c of checks) {
        // (a line indented with tabs alone steps in by one tab, in a file whose other lines have spaces too)
        const expected = c.closing || !c.inBlock ? c.base : c.base + (c.tabbed ? 4 : unit);
        if (c.actual === expected) continue;
        wrong++;
        if (found.length < limit) found.push({ line: c.line + 1, actual: c.actual, expected, message: `Indentation is ${describe(c.actual)}, expected ${describe(expected)} (Fix indentation: Alt+Enter)` });
    }
    // most lines off: the file's own layout (a data table with its items at the left edge), not mistakes
    if (checks.length >= 20 && wrong > checks.length * 0.3) return none;
    for (const l of [...movable]) if (l < 0) { movable.delete(l); movable.delete(-1 - l); }
    return { found, checked, movable, unit };
}

/** The lines whose indentation does not follow their { } blocks: [{ line (1-based), actual, expected (columns), message }]. */
export function braceIndentProblems(text, kind, limit = 200) {
    return scan(text, kind, limit).found;
}

/** The text with the wrong lines' indentation fixed (the lines that go with each moved along), nothing else changed. */
export function fixBraceIndentation(text, kind) {
    const tabs = (() => { let t = 0, s = 0; for (const l of text.split('\n')) { if (l.startsWith('\t')) t++; else if (l.startsWith(' ')) s++; } return t > s; })();
    const eol = text.includes('\r\n') ? '\r\n' : '\n';
    const lines = text.split(/\r?\n/);
    const unit = scan(text, kind, 1).unit;
    for (let pass = 0; pass < 2000; pass++) {
        const { found, checked, movable } = scan(lines.join('\n'), kind, 1, unit);
        if (!found.length) break;
        const p = found[0];
        const i = checked.findIndex((c) => c.line === p.line - 1);
        let end = lines.length - 1;
        for (let k = i + 1; k < checked.length; k++) if (checked[k].depth <= checked[i].depth) { end = checked[k].line - 1; break; }
        const delta = p.expected - p.actual;
        for (let l = p.line - 1; l <= end; l++) {
            if (!movable.has(l) || !lines[l].trim()) continue;
            const old = /^[ \t]*/.exec(lines[l])[0];
            const cols = Math.max(0, columns(old) + delta);
            lines[l] = (tabs ? '\t'.repeat(Math.floor(cols / 4)) + ' '.repeat(cols % 4) : ' '.repeat(cols)) + lines[l].slice(old.length);
        }
    }
    return lines.join(eol);
}

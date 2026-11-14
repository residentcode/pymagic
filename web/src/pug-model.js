// Pug's indentation errors while typing, as Pug's own lexer has them (its indentation is its syntax: a wrong one does not compile): a line
// indented with tabs and spaces both ("you can use tabs or spaces but not both"), a file indenting with the other one than it started with, and a
// line coming back to a level none of the lines above it is at ("Inconsistent indentation"). Inside a block of text (p. script. style. and a
// tag with its own . at the end), a comment block (// or //- with lines under it), a filter (:markdown) and a block of code (a lone -) the lines
// are free, and so are the lines of attributes spread over several lines (a( … )). No DOM here. [{ start, end, message }] as htmlcheck-model.js.

const TEXT_BLOCK = /^(?:[\w#.:-]+|\+[\w-]+)(?:\((?:[^()'"]|'[^']*'|"[^"]*")*\))?(?:&attributes\([^)]*\))?\.\s*$/;

/** The parentheses a line opens and does not close (quoted text left out). */
function parenDepth(line) {
    let depth = 0, quote = null;
    for (let i = 0; i < line.length; i++) {
        const c = line[i];
        if (quote) { if (c === '\\') i++; else if (c === quote) quote = null; continue; }
        if (c === '"' || c === "'" || c === '`') quote = c;
        else if (c === '(') depth++;
        else if (c === ')') depth--;
    }
    return depth;
}

export function pugProblems(text) {
    const found = [];
    const lines = text.split('\n');
    let offset = 0, stack = [0], indentWith = null, freeAbove = -1, parens = 0;
    for (const raw of lines) {
        const start = offset;
        offset += raw.length + 1;
        const line = raw.replace(/\r$/, '');
        if (!line.trim()) continue;
        const lead = /^[ \t]*/.exec(line)[0];
        const n = lead.length;
        // the lines of an attribute list over several lines
        if (parens > 0) { parens += parenDepth(line); continue; }
        // the lines of a block of text, a comment, a filter or code: anything further in than its first line
        if (freeAbove >= 0) { if (n > freeAbove) continue; freeAbove = -1; }
        const at = { start, end: start + Math.max(1, n) };
        if (lead.includes('\t') && lead.includes(' ')) {
            found.push({ ...at, message: 'Invalid indentation: tabs and spaces both (Pug takes one or the other)' });
            continue;
        }
        if (n) {
            const kind = lead[0] === '\t' ? 'tabs' : 'spaces';
            if (indentWith && kind !== indentWith) {
                found.push({ ...at, message: `Invalid indentation: ${kind} here, the file is indented with ${indentWith}` });
                continue;
            }
            indentWith ??= kind;
        }
        const top = stack[stack.length - 1];
        if (n > top) stack.push(n);
        else if (n < top) {
            while (stack.length > 1 && stack[stack.length - 1] > n) stack.pop();
            if (stack[stack.length - 1] !== n) {
                const unit = indentWith === 'tabs' ? 'tab' : 'space';
                const levels = [stack[stack.length - 1], top].filter((v, i, a) => a.indexOf(v) === i).map((v) => `${v} ${unit}${v === 1 ? '' : 's'}`);
                found.push({ ...at, message: `Inconsistent indentation: ${n} ${unit}${n === 1 ? '' : 's'}, the lines above are at ${levels.join(' or ')}` });
                stack.push(n);   // as the line stands, for the lines under it
            }
        }
        const body = line.slice(n);
        if (TEXT_BLOCK.test(body) || /^\/\/-?/.test(body) || /^:[\w-]+/.test(body) || /^-\s*$/.test(body)) freeAbove = n;
        else parens = Math.max(0, parenDepth(body));
    }
    return found;
}

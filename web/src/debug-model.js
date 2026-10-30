// The debugger's logic that needs no page: which names a line shows values for, the calls Smart Step Into offers,
// and the expression under the mouse.
const KEYWORDS = new Set(['False', 'None', 'True', 'and', 'as', 'assert', 'async', 'await', 'break', 'class', 'continue', 'def', 'del', 'elif',
    'else', 'except', 'finally', 'for', 'from', 'global', 'if', 'import', 'in', 'is', 'lambda', 'nonlocal', 'not', 'or', 'pass', 'raise',
    'return', 'try', 'while', 'with', 'yield', 'match', 'case', 'print', 'len', 'range', 'str', 'int', 'float', 'list', 'dict', 'set', 'tuple',
    'bool', 'isinstance', 'super', 'type']);
/** The code of a line without its comment and the text of its strings (names inside them are not variables). */
export function codeOf(line) {
    let out = '', quote = null;
    for (let i = 0; i < line.length; i++) {
        const c = line[i];
        if (quote) {
            if (c === '\\') { i++; out += '  '; continue; }
            if (c === quote) { quote = null; out += c; } else out += ' ';
            continue;
        }
        if (c === '#') break;
        if (c === '"' || c === "'") quote = c;
        out += c;
    }
    return out;
}
/** The names of a line in the order they first appear, without keywords, attributes (after a dot) and keyword arguments. */
export function namesOf(line) {
    const code = codeOf(line), names = [];
    for (const m of code.matchAll(/[A-Za-z_][A-Za-z0-9_]*/g)) {
        const name = m[0], before = code.slice(0, m.index).trimEnd(), after = code.slice(m.index + name.length);
        if (KEYWORDS.has(name) || before.endsWith('.') || /^\d/.test(name))
            continue;
        if (/^\s*=(?!=)/.test(after) && /[(,]\s*$/.test(before))
            continue;   // f(key=...): the keyword is not a variable
        if (!names.includes(name))
            names.push(name);
    }
    return names;
}
/** For the lines of a frame from its first line to the stopped one: [{line, names}] of the lines that have names. */
export function inlineLines(lineText, first, current) {
    const result = [];
    for (let line = Math.max(1, first); line <= current; line++) {
        const text = lineText(line);
        if (text === null)
            break;
        const names = namesOf(text);
        if (names.length)
            result.push({ line, names });
    }
    return result;
}
/** The text shown at the end of a line: "a = 1, b = [2]" for the names that have a value, cut at `max` characters. */
export function inlineText(names, values, max = 120) {
    const parts = names.filter((n) => Object.prototype.hasOwnProperty.call(values, n)).map((n) => `${n} = ${values[n]}`);
    const text = parts.join(', ');
    return text.length > max ? text.slice(0, max - 1) + '…' : text;
}
/** The calls of a line that Smart Step Into can enter, by name in the order they are written: `a.b(c(1))` gives b, c. */
export function callsOn(line) {
    const code = codeOf(line), calls = [];
    for (const m of code.matchAll(/([A-Za-z_][A-Za-z0-9_]*)\s*\(/g)) {
        const name = m[1];
        if (['if', 'elif', 'while', 'for', 'return', 'and', 'or', 'not', 'in', 'is', 'print', 'def', 'class', 'lambda', 'with', 'assert', 'yield', 'await', 'except'].includes(name))
            continue;
        if (code.slice(0, m.index).trimEnd().endsWith('def') || code.slice(0, m.index).trimEnd().endsWith('class'))
            continue;
        if (!calls.includes(name))
            calls.push(name);
    }
    return calls;
}
/** The dotted expression at a column (1-based) of a line, ending at the word under the column: `self.items.count` at "items" gives `self.items`. */
export function expressionAt(line, column) {
    const i = column - 1;
    if (i < 0 || i >= line.length || !/[A-Za-z0-9_]/.test(line[i]))
        return null;
    let end = i;
    while (end < line.length && /[A-Za-z0-9_]/.test(line[end]))
        end++;
    let start = i;
    while (start > 0 && /[A-Za-z0-9_.]/.test(line[start - 1]))
        start--;
    const text = line.slice(start, end).replace(/^\.+/, '');
    if (!text || /^\d/.test(text) || KEYWORDS.has(text))
        return null;
    return text;
}
/** A breakpoint on a `def` line is a function breakpoint: {function, codeLine} where codeLine is the first decorator's line. */
export function functionBreakpoint(lineText, line) {
    const m = /^(\s*)(?:async\s+)?def\s+([A-Za-z_][A-Za-z0-9_]*)/.exec(lineText(line) ?? '');
    if (!m)
        return null;
    let codeLine = line;
    for (let l = line - 1; l >= 1; l--) {
        const text = lineText(l) ?? '';
        if (text.startsWith(m[1] + '@'))
            codeLine = l;
        else if (text.trim() !== '' && !text.trim().startsWith('#'))
            break;
    }
    return { function: m[2], codeLine };
}
/** The expression of a member shown under a value in the Variables pane, or null when it has none (a set item, a return value). */
export function childExpression(parent, parentType, name) {
    if (!parent || /\(\)$/.test(parent))
        return null;
    if (['dict', 'OrderedDict', 'defaultdict', 'Counter', 'list', 'tuple', 'deque'].includes(parentType))
        return `${parent}[${name}]`;
    if (['set', 'frozenset'].includes(parentType))
        return null;
    return /^[A-Za-z_][A-Za-z0-9_]*$/.test(name) ? `${parent}.${name}` : null;
}
/** The saved watches as [{expression, paused}]; older versions saved plain strings. */
export function readWatches(saved) {
    return (Array.isArray(saved) ? saved : []).map((w) => (typeof w === 'string' ? { expression: w, paused: false } : { expression: String(w.expression ?? ''), paused: !!w.paused }))
        .filter((w) => w.expression);
}
/** A copy of a list with one item moved by `by` places, kept inside the list. */
export function moveItem(list, index, by) {
    const to = Math.max(0, Math.min(list.length - 1, index + by));
    const copy = [...list];
    const [item] = copy.splice(index, 1);
    copy.splice(to, 0, item);
    return copy;
}
/** The call stack as text, innermost first, for the clipboard. */
export function stackText(frames) {
    return frames.map((f) => `${f.name}, ${f.file}:${f.line}`).join('\n');
}

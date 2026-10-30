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
/** The call stack as text, innermost first, for the clipboard. */
export function stackText(frames) {
    return frames.map((f) => `${f.name}, ${f.file}:${f.line}`).join('\n');
}

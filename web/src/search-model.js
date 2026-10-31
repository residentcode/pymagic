// The parts of Find in files and Go to symbol that are plain data: the list of recent searches, a pasted text of several lines as a pattern,
// and the kinds symbols can be filtered by.

/** The recent searches with <paramref name="q"/> first, each once, at most <paramref name="max"/>. */
export function rememberSearch(list, q, max = 15) {
    const text = (q ?? '').trim();
    if (!text)
        return list.slice(0, max);
    return [q, ...list.filter((x) => x !== q)].slice(0, max);
}

/** A text of several lines as a regular expression that finds it as written, whatever line breaks the file has (a last line break left out). */
export function multilinePattern(text) {
    return text.replace(/\r?\n$/, '').split(/\r?\n/).map((l) => l.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('\\r?\\n');
}

/** The kinds Go to symbol filters by, with the LSP symbol kinds each takes in. */
export const SYMBOL_FILTERS = [
    ['All', null],
    ['Classes', [5]],
    ['Functions', [12]],
    ['Methods', [6, 9, 7]],
    ['Variables', [13, 8]],
    ['Constants', [14]],
    ['Modules', [2]],
];

/** The symbols of the kinds a filter takes in (all of them for 'All'). */
export function filterSymbols(symbols, filter) {
    const kinds = SYMBOL_FILTERS.find(([name]) => name === filter)?.[1];
    return kinds ? symbols.filter((s) => kinds.includes(s.kind)) : symbols;
}

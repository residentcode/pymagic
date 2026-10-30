// Code vision's logic that needs no page: which symbols get lenses, the text of a lens, and who wrote most of a range.
// LSP symbol kinds: 5 class, 6 method, 12 function.
const LENS_KINDS = new Set([5, 6, 12]);
/** The classes, methods and functions of a documentSymbol answer, nested ones too, in order. */
export function lensSymbols(symbols) {
    const out = [];
    const walk = (list) => {
        for (const s of list ?? []) {
            if (LENS_KINDS.has(s.kind))
                out.push(s);
            walk(s.children);
        }
    };
    walk(symbols);
    return out;
}
const plural = (n, one) => `${n} ${one}${n === 1 ? '' : 's'}`;
/** "3 usages", "no usages"; "1 inheritor". */
export function usagesText(n) { return n === 0 ? 'no usages' : plural(n, 'usage'); }
export function inheritorsText(n) { return plural(n, 'inheritor'); }
/** The author of most lines of a range of a blame (one entry per line, 0-based), with how many others wrote some:
 * "Jane Doe +2"; null when no line of it is committed. */
export function mainAuthor(blame, first, last) {
    const counts = new Map();
    for (let i = first; i <= last && i < blame.length; i++) {
        const b = blame[i];
        if (!b || /^0+$/.test(b.hash ?? ''))
            continue;
        counts.set(b.author, (counts.get(b.author) ?? 0) + 1);
    }
    if (counts.size === 0)
        return null;
    const [top] = [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
    return counts.size > 1 ? `${top[0]} +${counts.size - 1}` : top[0];
}

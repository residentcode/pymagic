// What the Profiler panel shows: rows from the host's /api/profile, filtered and sorted. No DOM here, so
// web/test/profile.test.mjs can check it.

/** "812 µs", "42 ms", "1.30 s", "2 min 5 s". */
export function formatTime(seconds) {
    if (!Number.isFinite(seconds) || seconds <= 0) return '0';
    if (seconds < 0.001) return `${Math.max(1, Math.round(seconds * 1e6))} µs`;
    if (seconds < 1) return `${seconds >= 0.01 ? Math.round(seconds * 1000) : (seconds * 1000).toFixed(1)} ms`;
    if (seconds < 60) return `${seconds.toFixed(2)} s`;
    const minutes = Math.floor(seconds / 60);
    return `${minutes} min ${Math.round(seconds - minutes * 60)} s`;
}

/** A function's label: its name, with the file's base name for code that is not in the project. */
export function labelOf(fn) {
    if (fn.builtin) return fn.name.replace(/^<built-in method (.*)>$/, '$1').replace(/^<method '(.*)' of '(.*)' objects>$/, '$2.$1');
    return fn.name;
}

export function locationOf(fn) {
    if (fn.builtin) return 'built-in';
    return `${fn.rel ?? fn.file.split(/[\\/]/).pop()}:${fn.line}`;
}

const SORTS = {
    cumulative: (a, b) => b.cumulative - a.cumulative || b.own - a.own,
    own: (a, b) => b.own - a.own || b.cumulative - a.cumulative,
    calls: (a, b) => b.calls - a.calls || b.own - a.own,
    name: (a, b) => labelOf(a).localeCompare(labelOf(b)) || a.line - b.line,
};

/**
 * The rows to list. `projectOnly` keeps functions of the project (not libraries, not built-ins), `filter` is a text
 * the name or file contains, `sort` is one of cumulative / own / calls / name. Each row gets the percentages of the total.
 */
export function rowsOf(data, { projectOnly = true, filter = '', sort = 'cumulative' } = {}) {
    if (!data) return [];
    const needle = filter.trim().toLowerCase();
    const total = data.total > 0 ? data.total : 1;
    return data.functions
        .filter((f) => !projectOnly || (!f.builtin && f.rel))
        .filter((f) => !needle || labelOf(f).toLowerCase().includes(needle) || (f.rel ?? f.file).toLowerCase().includes(needle))
        .map((f) => ({ ...f, selfPercent: (100 * f.own) / total, cumulativePercent: (100 * f.cumulative) / total }))
        .sort(SORTS[sort] ?? SORTS.cumulative);
}

/** The functions of the project that spent the most time themselves, for the one-line summary. */
export function hottest(data) {
    const rows = rowsOf(data, { projectOnly: true, sort: 'own' });
    return rows.length ? rows[0] : null;
}

/** Two function entries are the same function when file, line and name agree. */
export const sameFunction = (a, b) => a.file === b.file && a.line === b.line && a.name === b.name;

/**
 * The call graph around one function, as boxes and arrows to draw: its callers in a column on the left, the function in
 * the middle, the functions it called on the right (the most expensive first, at most `max` on a side). Each arrow says
 * how many calls and how much time went along it; each box has its x, y (top left), width and height.
 */
export function callGraph(fn, { max = 8, width = 220, height = 34, gap = 14, column = 320 } = {}) {
    const side = (list) => [...list].sort((a, b) => b.cumulative - a.cumulative).slice(0, max);
    const callers = side(fn.callers ?? []), callees = side(fn.callees ?? []);
    const rows = Math.max(callers.length, callees.length, 1);
    const totalHeight = rows * height + (rows - 1) * gap;
    const columnOf = (list, x) => {
        const top = (totalHeight - (list.length * height + Math.max(0, list.length - 1) * gap)) / 2;
        return list.map((f, i) => ({ fn: f, x, y: top + i * (height + gap), width, height }));
    };
    const center = { fn, x: column, y: (totalHeight - height) / 2, width, height };
    const left = columnOf(callers, 0), right = columnOf(callees, column * 2);
    const edges = [
        ...left.map((b) => ({ from: b, to: center, calls: b.fn.calls, time: b.fn.cumulative })),
        ...right.map((b) => ({ from: center, to: b, calls: b.fn.calls, time: b.fn.cumulative })),
    ];
    return { boxes: [...left, center, ...right], edges, width: column * 2 + width, height: totalHeight,
        more: { callers: Math.max(0, (fn.callers ?? []).length - callers.length), callees: Math.max(0, (fn.callees ?? []).length - callees.length) } };
}

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

/**
 * The whole program's call graph at once, as PyCharm's profiler draws it: the functions that took the most time (with callees; at most `max`,
 * the project's only when `projectOnly`) in columns by how deep they are called, the entry points on the left, each arrow a call with its
 * count and time. A call back to a function on its left or in its own column (recursion through others) is an arrow `back`; a call of a
 * function by itself is not drawn but counted on its box (`self`). In a column the boxes are ordered after the boxes they are called from,
 * so the arrows cross less. `hidden` says how many functions were left out.
 */
export function wholeGraph(data, { projectOnly = true, max = 60, width = 200, height = 34, gapX = 110, gapY = 12 } = {}) {
    const key = (f) => `${f.file}|${f.line}|${f.name}`;
    const usable = (data?.functions ?? []).filter((f) => !projectOnly || (!f.builtin && f.rel));
    const nodes = [...usable].sort((a, b) => b.cumulative - a.cumulative || b.own - a.own).slice(0, max);
    const index = new Map(nodes.map((f, i) => [key(f), i]));
    const self = nodes.map(() => 0);
    const links = new Map();      // "i>j" -> {from, to, calls, time}
    nodes.forEach((f, i) => {
        for (const c of f.callees ?? []) {
            const j = index.get(key(c));
            if (j === undefined) continue;
            if (j === i) { self[i] += c.calls ?? 0; continue; }
            const id = `${i}>${j}`;
            const had = links.get(id);
            if (had) { had.calls += c.calls ?? 0; had.time += c.cumulative ?? 0; }
            else links.set(id, { from: i, to: j, calls: c.calls ?? 0, time: c.cumulative ?? 0 });
        }
    });
    const out = nodes.map(() => []), into = nodes.map(() => []);
    for (const l of links.values()) { out[l.from].push(l); into[l.to].push(l); }

    // the arrows that close a cycle are found by a walk from the entry points (no caller among the boxes), the most expensive first
    const state = nodes.map(() => 0);   // 0 not seen, 1 on the walk, 2 done
    const order = [];                   // the boxes finished, callees before callers
    const walk = (start) => {
        const stack = [[start, 0]];
        state[start] = 1;
        while (stack.length) {
            const top = stack[stack.length - 1];
            const [i, k] = top;
            if (k < out[i].length) {
                top[1]++;
                const l = out[i][k];
                if (state[l.to] === 1) l.back = true;
                else if (state[l.to] === 0) { state[l.to] = 1; stack.push([l.to, 0]); }
            } else { state[i] = 2; order.push(i); stack.pop(); }
        }
    };
    const byCost = nodes.map((_, i) => i);
    for (const i of byCost.filter((i) => !into[i].length)) if (!state[i]) walk(i);
    for (const i of byCost) if (!state[i]) walk(i);

    // the column of a box: one more than the deepest of its callers (over the arrows that go forward)
    const layer = nodes.map(() => 0);
    for (const i of [...order].reverse()) for (const l of out[i]) if (!l.back) layer[l.to] = Math.max(layer[l.to], layer[i] + 1);
    const columns = [];
    nodes.forEach((_, i) => (columns[layer[i]] ??= []).push(i));
    // in a column, the boxes after the mean place of their callers (then of their callees), twice over
    const place = nodes.map(() => 0);
    const number = () => columns.forEach((col) => col.forEach((i, r) => { place[i] = r; }));
    number();
    const mean = (list, end) => (list.length ? list.reduce((s, l) => s + place[l[end]], 0) / list.length : null);
    for (let pass = 0; pass < 2; pass++) {
        for (const col of columns.slice(1)) { const m = new Map(col.map((i) => [i, mean(into[i].filter((l) => !l.back), 'from') ?? place[i]])); col.sort((a, b) => m.get(a) - m.get(b)); col.forEach((i, r) => { place[i] = r; }); }
        for (const col of columns.slice(0, -1).reverse()) { const m = new Map(col.map((i) => [i, mean(out[i].filter((l) => !l.back), 'to') ?? place[i]])); col.sort((a, b) => m.get(a) - m.get(b)); col.forEach((i, r) => { place[i] = r; }); }
    }
    number();
    const tallest = Math.max(1, ...columns.map((c) => c.length));
    const totalHeight = tallest * height + (tallest - 1) * gapY;
    const boxes = nodes.map((fn, i) => {
        const col = columns[layer[i]];
        const top = (totalHeight - (col.length * height + (col.length - 1) * gapY)) / 2;
        return { fn, x: layer[i] * (width + gapX), y: top + place[i] * (height + gapY), width, height, column: layer[i], self: self[i] };
    });
    const edges = [...links.values()].map((l) => ({ from: boxes[l.from], to: boxes[l.to], calls: l.calls, time: l.time, back: !!l.back || layer[l.to] <= layer[l.from] }));
    return { boxes, edges, width: columns.length * width + (columns.length - 1) * gapX, height: totalHeight, hidden: usable.length - nodes.length };
}

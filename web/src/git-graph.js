// The Log tab's graph of commits: which column each commit sits in and which lines run between rows.
// Pure functions, no page: `new Lanes()` takes commits newest first (git log --topo-order), a page at a time.

/** Columns of the commit graph. `add(commit)` returns the row to draw. */
export class Lanes {
    constructor() { this.lanes = []; }

    add(commit) {
        const lanes = this.lanes;
        const before = lanes.slice();
        const cols = [];
        lanes.forEach((h, i) => { if (h === commit.hash) cols.push(i); });
        let col;
        if (cols.length) col = cols[0];
        else { col = lanes.indexOf(null); if (col < 0) col = lanes.length; }
        for (const i of cols) lanes[i] = null;
        const parentCols = [];
        (commit.parents ?? []).forEach((p, n) => {
            const at = lanes.indexOf(p);
            if (at >= 0) { parentCols.push(at); return; }
            let slot;
            if (n === 0) slot = col;
            else { slot = lanes.indexOf(null); if (slot < 0) slot = lanes.length; }
            lanes[slot] = p;
            parentCols.push(slot);
        });
        while (lanes.length && lanes[lanes.length - 1] === null) lanes.pop();
        const after = lanes.slice();
        const width = Math.max(before.length, after.length, col + 1);
        return { hash: commit.hash, col, cols, before, after, parentCols, width };
    }
}

/** The rows of a whole list at once. */
export function layout(commits) {
    const l = new Lanes();
    return commits.map((c) => l.add(c));
}

export const LANE_COLORS = ['#4aa3ff', '#e5a50a', '#3fb950', '#c678dd', '#ff6b6b', '#2fc4c4', '#d19a66', '#9aa5b1'];
export const laneColor = (col) => LANE_COLORS[col % LANE_COLORS.length];

/**
 * What to draw for a row, in a box `rowHeight` high with `step` pixels per column:
 * { lines: [{x1, y1, x2, y2, color}], dot: {x, y, color} }.
 */
export function rowShapes(row, step = 14, rowHeight = 20) {
    const x = (c) => 8 + c * step;
    const mid = rowHeight / 2;
    const lines = [];
    row.before.forEach((h, i) => {
        if (h === null || h === undefined) return;
        if (row.cols.includes(i)) lines.push({ x1: x(i), y1: 0, x2: x(row.col), y2: mid, color: laneColor(i) });
        else lines.push({ x1: x(i), y1: 0, x2: x(i), y2: rowHeight, color: laneColor(i) });
    });
    for (const pc of row.parentCols) lines.push({ x1: x(row.col), y1: mid, x2: x(pc), y2: rowHeight, color: laneColor(pc) });
    return { lines, dot: { x: x(row.col), y: mid, color: laneColor(row.col) } };
}

/** "HEAD -> main, tag: v1, origin/main" as badges: [{kind: 'head'|'branch'|'remote'|'tag', name, current}]. */
export function parseRefs(refs, remotes = []) {
    if (!refs) return [];
    return refs.split(',').map((r) => r.trim()).filter(Boolean).map((r) => {
        if (r.startsWith('tag: ')) return { kind: 'tag', name: r.slice(5), current: false };
        if (r.startsWith('HEAD -> ')) return { kind: 'branch', name: r.slice(8), current: true };
        if (r === 'HEAD') return { kind: 'head', name: 'HEAD', current: true };
        const remote = r.includes('/') && remotes.some((n) => r.startsWith(n + '/'));
        return { kind: remote ? 'remote' : 'branch', name: r, current: false };
    });
}

/** The query string for /api/git/log from the filter fields (empty fields are left out). */
export function logQuery(f, limit, skip) {
    const q = new URLSearchParams();
    q.set('limit', String(limit));
    q.set('skip', String(skip));
    if (f.path) q.set('path', f.path);
    if (f.branch === '*') q.set('all', 'true');
    else if (f.branch) q.set('branch', f.branch);
    for (const k of ['author', 'text', 'since', 'until']) if (f[k]?.trim()) q.set(k, f[k].trim());
    return q.toString();
}

/** The graph is drawn only when every commit of the history is shown: a filter on people, words or dates leaves holes in it. */
export const graphFits = (f) => !f.path && !f.author?.trim() && !f.text?.trim() && !f.since && !f.until;

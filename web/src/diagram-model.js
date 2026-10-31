// The layout of a diagram, without a page: boxes in rows, each box above the boxes that point at it (a base above the classes that
// derive from it, an imported module above the ones that import it, a related model above the model with the relation), the rows
// ordered so that connected boxes stand near each other.

export const BOX = { minWidth: 120, maxWidth: 280, head: 26, line: 16, members: 12, charWidth: 7, gapX: 36, gapY: 70, pad: 20 };

/** Each node's row: 0 for the ones that point at nothing, else one more than the highest row they point at (cycles cut). */
export function rows(nodes, edges) {
    const rank = new Map(nodes.map((n) => [n.id, 0]));
    const out = new Map(nodes.map((n) => [n.id, []]));
    for (const e of edges)
        if (out.has(e.from) && rank.has(e.to) && e.from !== e.to)
            out.get(e.from).push(e.to);
    // longest path by relaxation; as many passes as nodes at most, so a cycle cannot loop for ever
    for (let pass = 0; pass < nodes.length; pass++) {
        let changed = false;
        for (const [from, targets] of out)
            for (const to of targets)
                if (rank.get(from) < rank.get(to) + 1 && rank.get(to) + 1 < nodes.length) {
                    rank.set(from, rank.get(to) + 1);
                    changed = true;
                }
        if (!changed)
            break;
    }
    return rank;
}

/** The size of a box: wide enough for its name and members (up to a limit), tall enough for the members shown. */
export function boxSize(node) {
    const shown = Math.min(node.members.length, BOX.members) + (node.members.length > BOX.members ? 1 : 0);
    const longest = Math.max(node.label.length + 2, ...node.members.slice(0, BOX.members).map((m) => m.length));
    return { width: Math.max(BOX.minWidth, Math.min(BOX.maxWidth, longest * BOX.charWidth + BOX.pad)), height: BOX.head + shown * BOX.line + (shown ? 8 : 0) };
}

/** Where each box goes: { boxes: Map id -> {x, y, width, height, row}, width, height } (row 0 at the top). */
export function layout(nodes, edges) {
    const rank = rows(nodes, edges);
    const byRow = new Map();
    for (const n of [...nodes].sort((a, b) => a.label.localeCompare(b.label))) {
        const r = rank.get(n.id);
        if (!byRow.has(r))
            byRow.set(r, []);
        byRow.get(r).push(n);
    }
    const order = [...byRow.keys()].sort((a, b) => a - b);
    // each row after the first is ordered by where the boxes it points at stand (the barycentre)
    const position = new Map();
    for (const r of order) {
        const list = byRow.get(r);
        if (r > 0) {
            const centre = (n) => {
                const targets = edges.filter((e) => e.from === n.id && position.has(e.to)).map((e) => position.get(e.to));
                return targets.length ? targets.reduce((a, b) => a + b, 0) / targets.length : Infinity;
            };
            list.sort((a, b) => centre(a) - centre(b) || a.label.localeCompare(b.label));
        }
        list.forEach((n, i) => position.set(n.id, i));
    }
    const boxes = new Map();
    let y = BOX.pad, width = 0;
    const rowWidths = order.map((r) => byRow.get(r).reduce((w, n) => w + boxSize(n).width, 0) + (byRow.get(r).length - 1) * BOX.gapX);
    const widest = Math.max(0, ...rowWidths);
    order.forEach((r, i) => {
        let x = BOX.pad + (widest - rowWidths[i]) / 2;
        let tallest = 0;
        for (const n of byRow.get(r)) {
            const size = boxSize(n);
            boxes.set(n.id, { x, y, ...size, row: r });
            x += size.width + BOX.gapX;
            tallest = Math.max(tallest, size.height);
        }
        width = Math.max(width, x - BOX.gapX + BOX.pad);
        y += tallest + BOX.gapY;
    });
    return { boxes, width: Math.max(width, 200), height: y - BOX.gapY + BOX.pad };
}

/** The line of an arrow: from the top of the box that points to the bottom of the one pointed at (or the side, in one row). */
export function arrow(from, to) {
    if (from.y > to.y + to.height)
        return { x1: from.x + from.width / 2, y1: from.y, x2: to.x + to.width / 2, y2: to.y + to.height };
    if (to.y > from.y + from.height)
        return { x1: from.x + from.width / 2, y1: from.y + from.height, x2: to.x + to.width / 2, y2: to.y };
    const leftToRight = from.x < to.x;
    return { x1: leftToRight ? from.x + from.width : from.x, y1: from.y + from.height / 2, x2: leftToRight ? to.x : to.x + to.width, y2: to.y + to.height / 2 };
}

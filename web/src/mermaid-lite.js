// A reader and a layout for the two Mermaid diagrams that most READMEs hold: flowcharts (`graph TD`, `flowchart LR`) and sequence diagrams, drawn
// at once without the Mermaid library; the other kinds are reported as unsupported here, and the preview draws them with the library (mermaid.js). No DOM here:
// web/test/mermaid.test.mjs checks it; mdpreview.js draws the layout as SVG.

const unquote = (s) => s.trim().replace(/^"(.*)"$/s, '$1').replace(/<br\s*\/?>/gi, ' ').trim();

/** The diagram a text describes: {type: 'flow', direction, nodes, edges} | {type: 'sequence', participants, steps} | {type: 'unsupported', kind}. */
export function parseMermaid(text) {
    const lines = text.replace(/\r\n?/g, '\n').split('\n').map((l) => l.replace(/%%.*$/, '').trim()).filter(Boolean);
    const head = lines[0] ?? '';
    let m;
    if ((m = /^(?:graph|flowchart)\s*(TB|TD|BT|RL|LR)?\s*$/i.exec(head))) return parseFlow(lines.slice(1), (m[1] ?? 'TD').toUpperCase().replace('TB', 'TD'));
    if (/^sequenceDiagram\b/.test(head)) return parseSequence(lines.slice(1));
    return { type: 'unsupported', kind: head.split(/\s+/)[0] || 'empty' };
}

const NODE = /^([A-Za-z_][\w-]*)(\(\[.*?\]\)|\(\(.*?\)\)|\[\[.*?\]\]|\[\(.*?\)\]|\[.*?\]|\(.*?\)|\{.*?\})?/s;
const ARROW = /^\s*(?:--\s+([^>]*?)\s+-->|--\s+([^-]*?)\s+---|(-\.+->|-\.-|==+>|===+|--+>|--+(?!-)|---+)(?:\s*\|([^|]*)\|)?)\s*/;

function shapeOf(rest) {
    if (!rest) return { shape: 'box', label: null };
    if (rest.startsWith('((')) return { shape: 'circle', label: unquote(rest.slice(2, -2)) };
    if (rest.startsWith('([')) return { shape: 'stadium', label: unquote(rest.slice(2, -2)) };
    if (rest.startsWith('[(')) return { shape: 'database', label: unquote(rest.slice(2, -2)) };
    if (rest.startsWith('[[')) return { shape: 'box', label: unquote(rest.slice(2, -2)) };
    if (rest[0] === '[') return { shape: 'box', label: unquote(rest.slice(1, -1)) };
    if (rest[0] === '(') return { shape: 'round', label: unquote(rest.slice(1, -1)) };
    return { shape: 'diamond', label: unquote(rest.slice(1, -1)) };
}

function parseFlow(lines, direction) {
    const nodes = new Map();
    const edges = [];
    const touch = (id, rest) => {
        const s = shapeOf(rest);
        const have = nodes.get(id);
        if (!have) nodes.set(id, { id, label: s.label ?? id, shape: s.shape });
        else if (rest) { have.label = s.label ?? have.label; have.shape = s.shape; }
        return id;
    };
    for (const raw of lines) {
        if (/^(subgraph\b|end$|classDef\b|class\b|style\b|linkStyle\b|click\b|direction\b)/.test(raw)) continue;
        for (const statement of raw.split(';')) {
            let rest = statement.trim();
            if (!rest) continue;
            // "A & B --> C" lists are not read: the first node of each group only
            let at = NODE.exec(rest);
            if (!at) continue;
            let previous = touch(at[1], at[2]);
            rest = rest.slice(at[0].length);
            while (rest.trim()) {
                const arrow = ARROW.exec(rest);
                if (!arrow) break;
                rest = rest.slice(arrow[0].length);
                at = NODE.exec(rest);
                if (!at) break;
                const to = touch(at[1], at[2]);
                const kind = arrow[3] ?? (arrow[1] !== undefined ? '-->' : '---');
                edges.push({ from: previous, to, label: unquote(arrow[4] ?? arrow[1] ?? arrow[2] ?? ''), dashed: /\./.test(kind), thick: kind.startsWith('=='), arrow: />$/.test(kind) });
                previous = to;
                rest = rest.slice(at[0].length);
            }
        }
    }
    return { type: 'flow', direction, nodes: [...nodes.values()], edges };
}

function parseSequence(lines) {
    const participants = [];
    const steps = [];
    const known = (id, label) => { let p = participants.find((x) => x.id === id); if (!p) { p = { id, label: label ?? id }; participants.push(p); } else if (label) p.label = label; return p; };
    for (const line of lines) {
        let m;
        if ((m = /^(?:participant|actor)\s+(\S+)(?:\s+as\s+(.+))?$/.exec(line))) { known(m[1], m[2] && unquote(m[2])); continue; }
        if ((m = /^(\S+?)\s*(-->>|->>|--x|-x|-->|->|--\)|-\))\s*([+-]?)(\S+?)\s*:\s*(.*)$/.exec(line))) {
            known(m[1]); known(m[4]);
            steps.push({ type: 'message', from: m[1], to: m[4], text: unquote(m[5]), dashed: m[2].startsWith('--'), arrow: !/x$/.test(m[2]) });
            continue;
        }
        if ((m = /^Note\s+(?:over|left of|right of)\s+([^:]+):\s*(.*)$/i.exec(line))) {
            const ids = m[1].split(',').map((s) => s.trim());
            for (const id of ids) known(id);
            steps.push({ type: 'note', over: ids, text: unquote(m[2]) });
        }
    }
    return { type: 'sequence', participants, steps };
}

/** Where each node of a flowchart goes: layers along the direction by the longest path from the sources, the nodes of a layer spread across it. */
export function layoutFlow(diagram, measure = (label) => ({ w: Math.max(64, label.length * 7.4 + 28), h: 38 })) {
    const GAP_A = 64, GAP_B = 34;        // between layers, between nodes of a layer
    const ids = diagram.nodes.map((n) => n.id);
    const out = new Map(ids.map((id) => [id, []]));
    for (const e of diagram.edges) if (out.has(e.from) && out.has(e.to)) out.get(e.from).push(e.to);
    // the layer of a node: one more than its deepest predecessor, with back edges (cycles) not counted
    const layer = new Map(ids.map((id) => [id, 0]));
    const state = new Map();
    const back = new Set();
    const visit = (id) => {
        state.set(id, 1);
        for (const to of out.get(id)) {
            if (state.get(to) === 1) { back.add(id + '>' + to); continue; }
            if (!state.has(to)) visit(to);
        }
        state.set(id, 2);
    };
    const hasIn = new Set(diagram.edges.map((e) => e.to));
    for (const id of ids.filter((x) => !hasIn.has(x))) if (!state.has(id)) visit(id);
    for (const id of ids) if (!state.has(id)) visit(id);
    for (let pass = 0; pass < ids.length; pass++) {
        let changed = false;
        for (const e of diagram.edges) {
            if (back.has(e.from + '>' + e.to) || !out.has(e.from) || !out.has(e.to)) continue;
            if (layer.get(e.to) < layer.get(e.from) + 1) { layer.set(e.to, layer.get(e.from) + 1); changed = true; }
        }
        if (!changed) break;
    }
    const vertical = diagram.direction === 'TD' || diagram.direction === 'BT';
    const sizes = new Map(diagram.nodes.map((n) => [n.id, measure(n.label)]));
    const layers = [];
    for (const id of ids) (layers[layer.get(id)] ??= []).push(id);
    const compact = layers.filter(Boolean);
    // the extent of a layer across, and the depth of each along
    const across = compact.map((l) => l.reduce((sum, id) => sum + (vertical ? sizes.get(id).w : sizes.get(id).h), 0) + GAP_B * (l.length - 1));
    const widest = Math.max(0, ...across);
    const depth = compact.map((l) => Math.max(...l.map((id) => (vertical ? sizes.get(id).h : sizes.get(id).w))));
    const placed = new Map();
    let along = 0;
    compact.forEach((l, k) => {
        let pos = (widest - across[k]) / 2;
        for (const id of l) {
            const s = sizes.get(id);
            const w = s.w, h = s.h;
            if (vertical) placed.set(id, { x: pos, y: along + (depth[k] - h) / 2, w, h });
            else placed.set(id, { x: along + (depth[k] - w) / 2, y: pos, w, h });
            pos += (vertical ? w : h) + GAP_B;
        }
        along += depth[k] + GAP_A;
    });
    let nodes = diagram.nodes.map((n) => ({ ...n, ...placed.get(n.id) }));
    const total = { w: vertical ? widest : along - GAP_A, h: vertical ? along - GAP_A : widest };
    if (diagram.direction === 'BT') nodes = nodes.map((n) => ({ ...n, y: total.h - n.y - n.h }));
    if (diagram.direction === 'RL') nodes = nodes.map((n) => ({ ...n, x: total.w - n.x - n.w }));
    const byId = new Map(nodes.map((n) => [n.id, n]));
    const edges = diagram.edges.filter((e) => byId.has(e.from) && byId.has(e.to)).map((e) => {
        const a = byId.get(e.from), b = byId.get(e.to);
        const ca = { x: a.x + a.w / 2, y: a.y + a.h / 2 }, cb = { x: b.x + b.w / 2, y: b.y + b.h / 2 };
        return { ...e, from: ca, to: cb, a, b };
    });
    return { nodes, edges, width: Math.max(total.w, 1), height: Math.max(total.h, 1) };
}

/** Where the line between two box centres leaves and enters the boxes. */
export function clip(box, towards) {
    const c = { x: box.x + box.w / 2, y: box.y + box.h / 2 };
    const dx = towards.x - c.x, dy = towards.y - c.y;
    if (dx === 0 && dy === 0) return c;
    const t = Math.min(dx !== 0 ? (box.w / 2) / Math.abs(dx) : Infinity, dy !== 0 ? (box.h / 2) / Math.abs(dy) : Infinity);
    return { x: c.x + dx * t, y: c.y + dy * t };
}

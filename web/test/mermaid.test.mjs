// Checks of web/src/mermaid-lite.js: `node web/test/mermaid.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseMermaid, layoutFlow, clip } from '../src/mermaid-lite.js';

test('a flowchart: nodes, shapes, labels and the arrows between them', () => {
    const d = parseMermaid('graph TD\n  A[Start] --> B{Is it?}\n  B -->|yes| C(Done)\n  B -- no --> D((End))\n  C -.-> D\n  D ==> E[(db)]\n');
    assert.equal(d.type, 'flow');
    assert.equal(d.direction, 'TD');
    assert.deepEqual(d.nodes.map((n) => `${n.id}:${n.shape}:${n.label}`), ['A:box:Start', 'B:diamond:Is it?', 'C:round:Done', 'D:circle:End', 'E:database:db']);
    assert.deepEqual(d.edges.map((e) => `${e.from}>${e.to}${e.label ? '|' + e.label : ''}${e.dashed ? ' dashed' : ''}${e.thick ? ' thick' : ''}`), ['A>B', 'B>C|yes', 'B>D|no', 'C>D dashed', 'D>E thick']);
});

test('a chain on one line, a node named again keeps its first label, comments and styles are skipped', () => {
    const d = parseMermaid('flowchart LR\n  %% a comment\n  A[One] --> B --> C\n  B[Second]\n  style A fill:#f00\n  subgraph S\n    C --> A\n  end\n');
    assert.equal(d.direction, 'LR');
    assert.deepEqual(d.nodes.map((n) => `${n.id}:${n.label}`), ['A:One', 'B:Second', 'C:C']);
    assert.deepEqual(d.edges.map((e) => `${e.from}>${e.to}`), ['A>B', 'B>C', 'C>A']);
});

test('layout: layers follow the longest path, a cycle does not hang it', () => {
    const d = parseMermaid('graph TD\n A --> B\n A --> C\n B --> D\n C --> D\n D --> A\n');
    const l = layoutFlow(d);
    const at = Object.fromEntries(l.nodes.map((n) => [n.id, n]));
    assert.ok(at.A.y < at.B.y && at.B.y < at.D.y && at.A.y < at.C.y);
    assert.equal(at.B.y, at.C.y);
    assert.ok(at.B.x !== at.C.x);
    assert.equal(l.edges.length, 5);
    // left to right swaps the axes
    const h = layoutFlow(parseMermaid('graph LR\n A --> B\n'));
    assert.ok(h.nodes[0].x < h.nodes[1].x && h.nodes[0].y === h.nodes[1].y);
    // bottom to top turns it over
    const up = layoutFlow(parseMermaid('graph BT\n A --> B\n'));
    assert.ok(up.nodes[0].y > up.nodes[1].y);
});

test('a line between boxes is cut at their edges', () => {
    const box = { x: 0, y: 0, w: 100, h: 40 };
    assert.deepEqual(clip(box, { x: 50, y: 200 }), { x: 50, y: 40 });
    assert.deepEqual(clip(box, { x: 300, y: 20 }), { x: 100, y: 20 });
});

test('a sequence diagram: participants, messages, notes', () => {
    const d = parseMermaid('sequenceDiagram\n  participant A as Alice\n  A->>B: Hello\n  B-->>A: Hi back\n  Note over A,B: done\n  A-xB: lost\n');
    assert.equal(d.type, 'sequence');
    assert.deepEqual(d.participants.map((p) => `${p.id}:${p.label}`), ['A:Alice', 'B:B']);
    assert.deepEqual(d.steps.map((s) => `${s.type}:${s.from ?? s.over}:${s.to ?? ''}:${s.text}${s.dashed ? ' dashed' : ''}${s.arrow === false ? ' lost' : ''}`), ['message:A:B:Hello', 'message:B:A:Hi back dashed', 'note:A,B::done', 'message:A:B:lost lost']);
});

test('other kinds are reported', () => {
    assert.deepEqual(parseMermaid('pie title Pets\n "Dogs": 3\n'), { type: 'unsupported', kind: 'pie' });
    assert.deepEqual(parseMermaid(''), { type: 'unsupported', kind: 'empty' });
});

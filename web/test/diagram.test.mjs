// Checks of web/src/diagram-model.js: `node web/test/diagram.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/diagram-model.js', import.meta.url), 'utf8');
const { rows, boxSize, layout, arrow, BOX, layoutAcross, neighbourhood } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const node = (id, members = []) => ({ id, label: id, members });

test('a base stands above what derives from it, at any depth; a cycle does not loop', () => {
    const nodes = [node('Cube'), node('Square'), node('Shape'), node('Other')];
    const edges = [{ from: 'Cube', to: 'Square' }, { from: 'Square', to: 'Shape' }];
    const r = rows(nodes, edges);
    assert.deepEqual([r.get('Shape'), r.get('Square'), r.get('Cube'), r.get('Other')], [0, 1, 2, 0]);
    const cyc = rows([node('a'), node('b')], [{ from: 'a', to: 'b' }, { from: 'b', to: 'a' }]);
    assert.ok(cyc.get('a') < 2 && cyc.get('b') < 2);
});

test('boxes are as wide as their longest line, within limits, and show at most a dozen members', () => {
    assert.equal(boxSize(node('A')).width, BOX.minWidth);
    assert.equal(boxSize(node('A', ['x'.repeat(200)])).width, BOX.maxWidth);
    const many = boxSize(node('A', Array.from({ length: 30 }, (_, i) => `m${i}`)));
    assert.equal(many.height, BOX.head + 13 * BOX.line + 8);
    assert.equal(boxSize(node('A')).height, BOX.head);
});

test('rows go down the page, centred, with the boxes of a row side by side; arrows run from the top of one to the bottom of the other', () => {
    const nodes = [node('Shape'), node('Square'), node('Circle')];
    const edges = [{ from: 'Square', to: 'Shape' }, { from: 'Circle', to: 'Shape' }];
    const { boxes, width, height } = layout(nodes, edges);
    const shape = boxes.get('Shape'), square = boxes.get('Square'), circle = boxes.get('Circle');
    assert.equal(shape.row, 0);
    assert.ok(square.y > shape.y + shape.height && circle.y === square.y);
    assert.ok(circle.x < square.x, 'alphabetical when the barycentres are the same');
    assert.ok(Math.abs(shape.x + shape.width / 2 - width / 2) < 1, 'the top row is centred');
    assert.ok(height > square.y + square.height);
    const a = arrow(square, shape);
    assert.deepEqual([a.y1, a.y2], [square.y, shape.y + shape.height]);
    const side = arrow({ x: 0, y: 0, width: 10, height: 10 }, { x: 50, y: 0, width: 10, height: 10 });
    assert.deepEqual(side, { x1: 10, y1: 5, x2: 50, y2: 5 });
});

test('left to right: the rows become columns, the base first, the boxes of a column one under the other', () => {
    const nodes = [node('Shape'), node('Square'), node('Circle')];
    const edges = [{ from: 'Square', to: 'Shape' }, { from: 'Circle', to: 'Shape' }];
    const { boxes, width, height } = layoutAcross(nodes, edges);
    const shape = boxes.get('Shape'), square = boxes.get('Square'), circle = boxes.get('Circle');
    assert.ok(square.x > shape.x + shape.width && circle.x === square.x);
    assert.ok(circle.y < square.y, 'the order of the row kept');
    assert.ok(width > square.x + square.width && height > square.y + square.height);
});

test('one model and its neighbours', () => {
    const d = { nodes: ['A', 'B', 'C', 'D'].map((x) => node(x)), edges: [{ from: 'A', to: 'B' }, { from: 'C', to: 'A' }, { from: 'C', to: 'D' }] };
    const n = neighbourhood(d, 'A');
    assert.deepEqual(n.nodes.map((x) => x.id), ['A', 'B', 'C']);
    assert.deepEqual(n.edges.length, 2);
});

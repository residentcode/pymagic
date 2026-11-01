import test from 'node:test';
import assert from 'node:assert/strict';
import { Lanes, layout, rowShapes, parseRefs, logQuery, graphFits } from '../src/git-graph.js';

const c = (hash, ...parents) => ({ hash, parents });

test('a straight history stays in column 0', () => {
    const rows = layout([c('c', 'b'), c('b', 'a'), c('a')]);
    assert.deepEqual(rows.map((r) => r.col), [0, 0, 0]);
    assert.deepEqual(rows[0].after, ['b']);
    assert.deepEqual(rows[2].after, []);
});

test('a merge opens a second column and the branch closes it again', () => {
    // m merges f (feature) into main; both come from base
    const rows = layout([c('m', 'x', 'f'), c('f', 'base'), c('x', 'base'), c('base')]);
    assert.deepEqual(rows.map((r) => r.col), [0, 1, 0, 1]);
    assert.deepEqual(rows[0].parentCols, [0, 1]);
    assert.deepEqual(rows[0].after, ['x', 'f']);
    // x's first parent 'base' is already waiting in column 1 (from f): the line crosses over
    assert.deepEqual(rows[2].parentCols, [1]);
    // base is wanted by one lane (column 1), and takes that one
    assert.equal(rows[3].col, 1);
    assert.deepEqual(rows[3].cols, [1]);
});

test('a second branch tip joins the lane waiting for the same parent', () => {
    const rows = layout([c('a', 'r'), c('b', 'r'), c('r')]);
    assert.deepEqual(rows.map((r) => r.col), [0, 1, 0]);
    assert.deepEqual(rows[2].cols, [0]);
    assert.deepEqual(rows[2].after, []);
});

test('pages continue where the last one stopped', () => {
    const all = [c('d', 'c'), c('c', 'b', 'e'), c('e', 'a'), c('b', 'a'), c('a')];
    const whole = layout(all);
    const l = new Lanes();
    const parts = [...all.slice(0, 2), ...all.slice(2)].map((x) => l.add(x));
    assert.deepEqual(parts, whole);
});

test('shapes: a dot, a line down to each parent, lines through for other lanes', () => {
    const rows = layout([c('m', 'x', 'f'), c('f', 'base')]);
    const s0 = rowShapes(rows[0], 10, 20);
    assert.deepEqual(s0.dot, { x: 8, y: 10, color: s0.dot.color });
    assert.equal(s0.lines.length, 2);
    const s1 = rowShapes(rows[1], 10, 20);
    // lane 0 (x) runs through, f arrives at its dot in column 1 and continues to base
    assert.ok(s1.lines.some((l) => l.x1 === 8 && l.x2 === 8 && l.y1 === 0 && l.y2 === 20));
    assert.ok(s1.lines.some((l) => l.x1 === 18 && l.y1 === 0 && l.x2 === 18 && l.y2 === 10));
});

test('ref badges: current branch, tags, remotes', () => {
    const r = parseRefs('HEAD -> main, tag: v1, origin/main, feature', ['origin']);
    assert.deepEqual(r.map((x) => `${x.kind}:${x.name}:${x.current}`), ['branch:main:true', 'tag:v1:false', 'remote:origin/main:false', 'branch:feature:false']);
    assert.deepEqual(parseRefs('', []), []);
    assert.equal(parseRefs('HEAD', [])[0].kind, 'head');
});

test('the query leaves out empty fields and asks for all branches with *', () => {
    assert.equal(logQuery({ branch: '', author: ' ', text: '' }, 60, 0), 'limit=60&skip=0');
    assert.equal(logQuery({ branch: '*', author: 'Ann', text: 'fix bug', since: '2026-01-02' }, 60, 60), 'limit=60&skip=60&all=true&author=Ann&text=fix+bug&since=2026-01-02');
    assert.equal(logQuery({ branch: 'dev' }, 10, 0), 'limit=10&skip=0&branch=dev');
});

test('the graph is drawn for every filter but the one that follows the current file', () => {
    assert.equal(graphFits({ branch: '*' }), true);
    assert.equal(graphFits({ author: 'x' }), true);
    assert.equal(graphFits({ paths: 'src' }), true);
    assert.equal(graphFits({ path: 'a.py' }), false);
    assert.equal(logQuery({ paths: ' src/app ' }, 10, 0), 'limit=10&skip=0&paths=src%2Fapp');
});

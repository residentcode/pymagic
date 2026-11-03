// Checks of web/src/profile-model.js: `node web/test/profile.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// web/package.json does not say "type": "module", so Node would read src/*.js as CommonJS; load the file as a module instead
const source = readFileSync(new URL('../src/profile-model.js', import.meta.url), 'utf8');
const { formatTime, labelOf, locationOf, rowsOf, hottest, sameFunction, callGraph, wholeGraph } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const fn = (name, own, cumulative, extra = {}) => ({ name, own, cumulative, calls: 1, line: 1, file: '/p/app.py', rel: 'app.py', builtin: false, callers: [], ...extra });
const data = {
    total: 10,
    functions: [
        fn('main', 0.5, 10),
        fn('hot', 6, 7, { calls: 300, line: 20 }),
        fn('cold', 0.1, 0.1, { calls: 5, line: 40 }),
        fn('lib', 2, 2, { file: '/p/.venv/lib/x.py', rel: null }),
        fn('<built-in method builtins.sum>', 1.4, 1.4, { file: '~', rel: null, builtin: true, calls: 9000 }),
    ],
};

test('formatTime: microseconds, milliseconds, seconds, minutes', () => {
    assert.equal(formatTime(0), '0');
    assert.equal(formatTime(0.0000042), '4 µs');
    assert.equal(formatTime(0.0042), '4.2 ms');
    assert.equal(formatTime(0.042), '42 ms');
    assert.equal(formatTime(1.3), '1.30 s');
    assert.equal(formatTime(125), '2 min 5 s');
    assert.equal(formatTime(NaN), '0');
});

test('rowsOf keeps the project by default and sorts by cumulative time', () => {
    const rows = rowsOf(data);
    assert.deepEqual(rows.map((r) => r.name), ['main', 'hot', 'cold']);
});

test('rowsOf can include libraries and built-ins', () => {
    const rows = rowsOf(data, { projectOnly: false, sort: 'own' });
    assert.deepEqual(rows.map((r) => r.name), ['hot', 'lib', '<built-in method builtins.sum>', 'main', 'cold']);
});

test('rowsOf sorts by calls and by name', () => {
    assert.equal(rowsOf(data, { projectOnly: false, sort: 'calls' })[0].name, '<built-in method builtins.sum>');
    assert.deepEqual(rowsOf(data, { sort: 'name' }).map((r) => r.name), ['cold', 'hot', 'main']);
});

test('rowsOf filters by name or file, ignoring case', () => {
    assert.deepEqual(rowsOf(data, { filter: 'HO' }).map((r) => r.name), ['hot']);
    assert.deepEqual(rowsOf(data, { filter: 'app.py' }).length, 3);
    assert.deepEqual(rowsOf(data, { filter: 'nothing' }), []);
});

test('percentages are of the total time', () => {
    const hot = rowsOf(data).find((r) => r.name === 'hot');
    assert.equal(hot.selfPercent, 60);
    assert.equal(hot.cumulativePercent, 70);
});

test('no data, no rows; a zero total does not divide by zero', () => {
    assert.deepEqual(rowsOf(null), []);
    const rows = rowsOf({ total: 0, functions: [fn('a', 0, 0)] });
    assert.equal(rows[0].selfPercent, 0);
});

test('hottest is the project function with the most own time', () => {
    assert.equal(hottest(data).name, 'hot');
    assert.equal(hottest({ total: 1, functions: [] }), null);
});

test('labels and locations', () => {
    assert.equal(labelOf(fn('f', 0, 0)), 'f');
    assert.equal(labelOf({ name: '<built-in method builtins.sum>', builtin: true }), 'builtins.sum');
    assert.equal(labelOf({ name: "<method 'append' of 'list' objects>", builtin: true }), 'list.append');
    assert.equal(locationOf(fn('f', 0, 0, { line: 7 })), 'app.py:7');
    assert.equal(locationOf(fn('f', 0, 0, { rel: null, file: 'C:\\lib\\x.py', line: 3 })), 'x.py:3');
    assert.equal(locationOf({ builtin: true }), 'built-in');
});

test('sameFunction compares file, line and name', () => {
    assert.ok(sameFunction(fn('a', 0, 0), fn('a', 5, 5)));
    assert.ok(!sameFunction(fn('a', 0, 0), fn('a', 0, 0, { line: 2 })));
});

test('callGraph: callers on the left, the function in the middle, callees on the right, the costliest first', () => {
    const f = (name, cumulative, calls = 1) => ({ name, cumulative, calls, file: 'a.py', line: 1 });
    const fn = { ...f('outer', 5), callers: [f('main', 5)], callees: [f('fast', 1, 10), f('slow', 3, 30), f('mid', 2)] };
    const g = callGraph(fn, { max: 2, width: 100, height: 20, gap: 10, column: 200 });
    assert.deepEqual(g.boxes.map((b) => [b.fn.name, b.x, b.y]), [['main', 0, 15], ['outer', 200, 15], ['slow', 400, 0], ['mid', 400, 30]]);
    assert.deepEqual(g.edges.map((e) => `${e.from.fn.name}->${e.to.fn.name}:${e.calls}`), ['main->outer:1', 'outer->slow:30', 'outer->mid:1']);
    assert.deepEqual(g.more, { callers: 0, callees: 1 });
    assert.equal(g.width, 500);
    assert.equal(g.height, 50);
    assert.deepEqual(callGraph({ ...f('alone', 1) }).boxes.map((b) => b.fn.name), ['alone']);
});

test('wholeGraph: every function in columns by how deep it is called, a cycle drawn back, the cheapest left out', () => {
    // main -> parse -> token, main -> run -> walk -> run (recursion through walk), walk -> walk (itself), main -> lib (not the project's)
    const f = (name, line, cumulative, callees = [], extra = {}) => ({ name, line, cumulative, own: cumulative / 2, calls: 1, file: '/p/app.py', rel: 'app.py', builtin: false, callees, ...extra });
    const c = (name, line, calls, cumulative) => ({ name, line, calls, cumulative, file: '/p/app.py' });
    const data = {
        total: 10,
        functions: [
            f('main', 1, 10, [c('parse', 10, 1, 3), c('run', 20, 1, 6), { name: 'lib', line: 1, calls: 1, cumulative: 1, file: '/v/lib.py' }]),
            f('parse', 10, 3, [c('token', 15, 40, 2)]),
            f('token', 15, 2),
            f('run', 20, 6, [c('walk', 30, 5, 5)]),
            f('walk', 30, 5, [c('run', 20, 4, 4), c('walk', 30, 7, 1)]),
            f('lib', 1, 1, [], { file: '/v/lib.py', rel: null }),
            f('tiny', 50, 0.001),
        ],
    };
    const g = wholeGraph(data, { projectOnly: true, width: 100, height: 20, gapX: 50, gapY: 10 });
    const at = Object.fromEntries(g.boxes.map((b) => [b.fn.name, b.column]));
    assert.deepEqual(at, { main: 0, parse: 1, run: 1, token: 2, walk: 2, tiny: 0 });
    const edges = g.edges.map((e) => `${e.from.fn.name}->${e.to.fn.name}:${e.calls}${e.back ? ' back' : ''}`).sort();
    assert.deepEqual(edges, ['main->parse:1', 'main->run:1', 'parse->token:40', 'run->walk:5', 'walk->run:4 back']);
    assert.equal(g.boxes.find((b) => b.fn.name === 'walk').self, 7);
    assert.equal(g.width, 3 * 100 + 2 * 50);
    // in a column the boxes follow their callers: token and walk stand as parse and run do
    const y = Object.fromEntries(g.boxes.map((b) => [b.fn.name, b.y]));
    assert.equal(y.parse < y.run, y.token < y.walk);
    assert.equal(g.hidden, 0);
    // with libraries, and at most 3 boxes: the costliest
    const small = wholeGraph(data, { projectOnly: false, max: 3 });
    assert.deepEqual(small.boxes.map((b) => b.fn.name).sort(), ['main', 'run', 'walk']);
    assert.equal(small.hidden, 4);
    assert.deepEqual(wholeGraph(null).boxes, []);
});

// Checks of web/src/profile-model.js: `node web/test/profile.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// web/package.json does not say "type": "module", so Node would read src/*.js as CommonJS; load the file as a module instead
const source = readFileSync(new URL('../src/profile-model.js', import.meta.url), 'utf8');
const { formatTime, labelOf, locationOf, rowsOf, hottest, sameFunction } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

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

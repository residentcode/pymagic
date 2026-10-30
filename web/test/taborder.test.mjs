// Checks of web/src/taborder.js: `node web/test/taborder.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// web/package.json does not say "type": "module", so Node would read src/*.js as CommonJS; load the file as a module instead
const source = readFileSync(new URL('../src/taborder.js', import.meta.url), 'utf8');
const { dropIndex, staysPut, moveTo, moveBy, pinFirst, clampToGroup, tabLimitVictims } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const abc = ['a.py', 'b.py', 'c.py'];

test('dropIndex: before the first middle, between middles, after the last', () => {
    const mids = [50, 150, 250];
    assert.equal(dropIndex(mids, 10), 0);
    assert.equal(dropIndex(mids, 50), 0);
    assert.equal(dropIndex(mids, 51), 1);
    assert.equal(dropIndex(mids, 200), 2);
    assert.equal(dropIndex(mids, 900), 3);
    assert.equal(dropIndex([], 5), 0);
});

test('moveTo: to the front, to the end, one place either way', () => {
    assert.deepEqual(moveTo(abc, 'c.py', 0), ['c.py', 'a.py', 'b.py']);
    assert.deepEqual(moveTo(abc, 'a.py', 3), ['b.py', 'c.py', 'a.py']);
    assert.deepEqual(moveTo(abc, 'a.py', 2), ['b.py', 'a.py', 'c.py']);
    assert.deepEqual(moveTo(abc, 'c.py', 1), ['a.py', 'c.py', 'b.py']);
});

test('moveTo: dropping a tab on either side of itself changes nothing and returns the same array', () => {
    for (const index of [1, 2]) assert.equal(moveTo(abc, 'b.py', index), abc);
    assert.equal(moveTo(abc, 'a.py', 0), abc);
    assert.equal(moveTo(abc, 'c.py', 3), abc);
    assert.ok(staysPut(abc, 'b.py', 1) && staysPut(abc, 'b.py', 2) && !staysPut(abc, 'b.py', 0) && !staysPut(abc, 'b.py', 3));
});

test('moveTo: out-of-range indexes are clamped, unknown tabs leave the order alone, the input is not changed', () => {
    assert.deepEqual(moveTo(abc, 'b.py', -4), ['b.py', 'a.py', 'c.py']);
    assert.deepEqual(moveTo(abc, 'b.py', 99), ['a.py', 'c.py', 'b.py']);
    assert.equal(moveTo(abc, 'zzz.py', 0), abc);
    assert.deepEqual(abc, ['a.py', 'b.py', 'c.py']);
});

test('moveBy: one place left and right, stopping at the ends', () => {
    assert.deepEqual(moveBy(abc, 'b.py', -1), ['b.py', 'a.py', 'c.py']);
    assert.deepEqual(moveBy(abc, 'b.py', 1), ['a.py', 'c.py', 'b.py']);
    assert.equal(moveBy(abc, 'a.py', -1), abc);
    assert.equal(moveBy(abc, 'c.py', 1), abc);
    assert.deepEqual(moveBy(abc, 'a.py', 5), ['b.py', 'c.py', 'a.py']);
    assert.equal(moveBy(['only.py'], 'only.py', 1).length, 1);
    assert.equal(moveBy(abc, 'zzz.py', 1), abc);
});

test('pinFirst: the pinned tabs come first in their own order, the rest keep theirs', () => {
    const tabs = ['a.py', 'b.py', 'c.py', 'd.py'];
    assert.deepEqual(pinFirst(tabs, new Set(['c.py', 'a.py'])), ['a.py', 'c.py', 'b.py', 'd.py']);
    assert.equal(pinFirst(tabs, new Set(['a.py'])), tabs);
    assert.equal(pinFirst(tabs, new Set()), tabs);
});

test('clampToGroup: a pinned tab stays among the pinned ones, another stays after them', () => {
    const tabs = ['p1.py', 'p2.py', 'a.py', 'b.py'];
    const pinned = new Set(['p1.py', 'p2.py']);
    assert.equal(clampToGroup(tabs, pinned, 'p1.py', 4), 2);
    assert.equal(clampToGroup(tabs, pinned, 'p2.py', 0), 0);
    assert.equal(clampToGroup(tabs, pinned, 'b.py', 0), 2);
    assert.equal(clampToGroup(tabs, pinned, 'a.py', 4), 4);
    assert.deepEqual(moveTo(tabs, 'b.py', clampToGroup(tabs, pinned, 'b.py', 1)), ['p1.py', 'p2.py', 'b.py', 'a.py']);
});

test('paths that share a prefix are kept apart', () => {
    const tabs = ['pkg/a.py', 'pkg/a.pyi', 'pkg'];
    assert.deepEqual(moveTo(tabs, 'pkg', 0), ['pkg', 'pkg/a.py', 'pkg/a.pyi']);
    assert.deepEqual(moveBy(tabs, 'pkg/a.pyi', -1), ['pkg/a.pyi', 'pkg/a.py', 'pkg']);
});

test('tabLimitVictims: past the limit the tabs used longest ago go, not a pinned, active or changed one', () => {
    const tabs = ['a', 'b', 'c', 'd', 'e'];
    const mru = ['e', 'd', 'c', 'b', 'a'];
    assert.deepEqual(tabLimitVictims(tabs, mru, new Set(), 'e', 3), ['a', 'b']);
    assert.deepEqual(tabLimitVictims(tabs, mru, new Set(['a']), 'e', 3), ['b', 'c']);
    assert.deepEqual(tabLimitVictims(tabs, mru, new Set(), 'e', 3, (r) => r === 'b'), ['a', 'c']);
    assert.deepEqual(tabLimitVictims(tabs, mru, new Set(), 'e', 0), []);
    assert.deepEqual(tabLimitVictims(tabs, mru, new Set(), 'e', 5), []);
});

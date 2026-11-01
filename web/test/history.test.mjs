import test from 'node:test';
import assert from 'node:assert/strict';
import { ago, clock, size, matchLines, oldRange, selectionHistory, interleave } from '../src/history-model.js';

const at = (y, mo, d, h = 0, mi = 0, s = 0) => new Date(y, mo - 1, d, h, mi, s).getTime();

test('how long ago, in words', () => {
    const now = at(2026, 5, 10, 12, 0, 0);
    assert.equal(ago(now, now - 5_000), 'just now');
    assert.equal(ago(now, now - 60_000), 'a minute ago');
    assert.equal(ago(now, now - 7 * 60_000), '7 minutes ago');
    assert.equal(ago(now, now - 60 * 60_000), 'an hour ago');
    assert.equal(ago(now, now - 5 * 3600_000), '5 hours ago');
    assert.equal(ago(now, now - 26 * 3600_000), 'yesterday');
    assert.equal(ago(now, now - 5 * 86_400_000), '5 days ago');
    assert.equal(ago(now, now + 10_000), 'just now');   // a clock a little ahead is not the future
});

test('the time of day, with the day when it is not today', () => {
    const now = at(2026, 5, 10, 12, 0, 0);
    assert.equal(clock(now, at(2026, 5, 10, 9, 5, 7)), '09:05:07');
    assert.equal(clock(now, at(2026, 5, 9, 23, 59, 1)), 'yesterday 23:59:01');
    assert.equal(clock(now, at(2026, 3, 3, 18, 2, 44)), '3 Mar 18:02:44');
    assert.equal(clock(now, at(2025, 12, 31, 8, 0, 0)), '31 Dec 2025 08:00:00');
});

test('sizes', () => {
    assert.equal(size(812), '812 bytes');
    assert.equal(size(3500), '3.4 KB');
});

const lines = (...l) => l.join('\n');

test('matchLines: the lines two texts share, in order', () => {
    assert.deepEqual(matchLines(['a', 'b', 'c'], ['a', 'x', 'c']), [[0, 0], [2, 2]]);
    assert.deepEqual(matchLines(['a', 'b', 'c'], ['b', 'c', 'd']), [[1, 0], [2, 1]]);
    assert.deepEqual(matchLines([], ['a']), []);
    assert.deepEqual(matchLines(['a', 'x', 'y', 'b'], ['a', 'b']), [[0, 0], [3, 1]]);
    // a moved block is matched once
    assert.deepEqual(matchLines(['1', '2', '3', '4'], ['3', '4', '1', '2']).length, 2);
});

test('oldRange: where chosen lines stood in an older text', () => {
    // current: a b c d e / old: a b X d e  (line c was X)
    const pairs = matchLines(['a', 'b', 'c', 'd', 'e'], ['a', 'b', 'X', 'd', 'e']);
    assert.deepEqual(oldRange(pairs, 2, 2, 5), { from: 2, to: 3 });
    assert.deepEqual(oldRange(pairs, 1, 3, 5), { from: 1, to: 4 });
    // lines added since: the chosen line did not exist; the range is empty between its neighbours
    const added = matchLines(['a', 'NEW', 'b'], ['a', 'b']);
    assert.deepEqual(oldRange(added, 1, 1, 2), { from: 1, to: 1 });
    // changed at the end
    assert.deepEqual(oldRange(matchLines(['a', 'b'], ['a']), 1, 1, 1), { from: 1, to: 1 });
});

test('selectionHistory: only the versions in which the chosen lines were different', () => {
    const now = lines('def f():', '    return 3', '', 'print(f())');
    const versions = [
        { id: '4', time: 't4', text: lines('def f():', '    return 3', '', 'print(f())', '# footer') },     // only a line elsewhere changed
        { id: '3', time: 't3', text: lines('def f():', '    return 2', '', 'print(f())') },
        { id: '2', time: 't2', text: lines('# header', 'def f():', '    return 2', '', 'print(f())') },    // the same lines, moved down by a header
        { id: '1', time: 't1', text: lines('def f():', '    return 1', '', 'print(f())') },
    ];
    const found = selectionHistory(now, 1, 1, versions);
    assert.deepEqual(found.map((v) => [v.id, v.text, v.previous]), [['3', '    return 2', '    return 3'], ['1', '    return 1', '    return 2']]);
    // the whole function: version 3 and 1 differ in the body line only, but the chosen block holds it
    assert.deepEqual(selectionHistory(now, 0, 1, versions).map((v) => v.id), ['3', '1']);
    // nothing was ever different
    assert.deepEqual(selectionHistory(now, 3, 3, versions), []);
});

test('interleave: labels stand among the versions by time', () => {
    const rows = interleave([{ id: '0003', f: 'v3' }, { id: '0001', f: 'v1' }], [{ id: '0002', name: 'L' }, { id: '0004', name: 'M' }]);
    assert.deepEqual(rows.map((r) => `${r.type}:${r.id}`), ['label:0004', 'version:0003', 'label:0002', 'version:0001']);
});

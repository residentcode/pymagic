// Checks of web/src/hunks.js: `node web/test/hunks.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// web/package.json does not say "type": "module", so Node would read src/*.js as CommonJS; load the file as a module instead
const source = readFileSync(new URL('../src/hunks.js', import.meta.url), 'utf8');
const h = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const line = (kind, text) => ({ kind, text });
const diff = {
    id: 'abc',
    hunks: [
        { oldStart: 3, newStart: 3, section: '', lines: [line(' ', 'a'), line('-', 'b'), line('+', 'B'), line('+', 'B2'), line(' ', 'c')] },
        { oldStart: 20, newStart: 21, section: '', lines: [line(' ', 'x'), line('+', 'y'), line(' ', 'z')] },
    ],
};

test('the changed lines of a hunk are the added and removed ones', () => {
    assert.deepEqual(h.changedLines(diff.hunks[0]), [1, 2, 3]);
    assert.deepEqual(h.changedLines(diff.hunks[1]), [1]);
});

test('selectAll and selectNone', () => {
    const all = h.selectAll(diff);
    assert.deepEqual(h.counts(diff, all), { chosen: 4, total: 4 });
    assert.equal(h.stateOf(diff, all), 'all');
    assert.equal(all.id, 'abc');
    const none = h.selectNone(diff);
    assert.deepEqual(h.counts(diff, none), { chosen: 0, total: 4 });
    assert.equal(h.stateOf(diff, none), 'none');
});

test('one line out of the selection makes it partial', () => {
    const sel = h.selectAll(diff);
    h.toggleLine(diff, sel, 0, 2);
    assert.equal(h.stateOf(diff, sel), 'partial');
    assert.deepEqual(h.counts(diff, sel), { chosen: 3, total: 4 });
    assert.equal(h.hunkState(diff, sel, 0), 'partial');
    assert.equal(h.hunkState(diff, sel, 1), 'all');
    h.toggleLine(diff, sel, 0, 2);
    assert.equal(h.stateOf(diff, sel), 'all');
});

test('an unchanged line cannot be toggled, nor one that is not there', () => {
    const sel = h.selectNone(diff);
    h.toggleLine(diff, sel, 0, 0);
    h.toggleLine(diff, sel, 0, 99);
    h.toggleLine(diff, sel, 5, 0);
    assert.equal(h.stateOf(diff, sel), 'none');
});

test('toggling a hunk takes all of it, or none when it was all taken', () => {
    const sel = h.selectNone(diff);
    h.toggleHunk(diff, sel, 1);
    assert.equal(h.hunkState(diff, sel, 1), 'all');
    assert.equal(h.stateOf(diff, sel), 'partial');
    h.toggleHunk(diff, sel, 1);
    assert.equal(h.hunkState(diff, sel, 1), 'none');
    h.toggleLine(diff, sel, 0, 1);
    h.toggleHunk(diff, sel, 0);                 // some of it taken: the rest is taken
    assert.equal(h.hunkState(diff, sel, 0), 'all');
});

test('the payload names the diff and leaves empty hunks out', () => {
    const sel = h.selectNone(diff);
    h.toggleLine(diff, sel, 0, 3);
    h.toggleLine(diff, sel, 0, 1);
    h.toggleLine(diff, sel, 1, 1);
    h.toggleLine(diff, sel, 1, 1);              // taken and given back
    assert.deepEqual(h.payload(sel), { id: 'abc', hunks: { 0: [1, 3] } });
});

test('a diff without changes is never "all"', () => {
    const empty = { id: 'e', hunks: [] };
    assert.equal(h.stateOf(empty, h.selectAll(empty)), 'none');
});

test('numbering gives the line numbers of both versions', () => {
    assert.deepEqual(h.numbering(diff.hunks[0]), [[3, 3], [4, null], [null, 4], [null, 5], [5, 6]]);
});

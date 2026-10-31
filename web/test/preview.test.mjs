import test from 'node:test';
import assert from 'node:assert/strict';
import { diffLines, blocksOf, textWithChosen, changedSpan, wordSpans, hunksOf } from '../src/preview.js';

const OLD = 'import a\nx = old_name(1)\ny = 2\nz = 3\nw = old_name(2)\n';
const NEW = 'import a\nx = new_name(1)\ny = 2\nz = 3\nw = new_name(2)\n';

test('each run of changed lines is one change', () => {
    const lines = diffLines(OLD, NEW);
    assert.equal(blocksOf(lines).length, 2);
    assert.deepEqual(blocksOf(lines)[0].map((i) => lines[i].kind), ['-', '+']);
});

test('only the chosen changes are made', () => {
    const lines = diffLines(OLD, NEW);
    assert.equal(textWithChosen(lines, new Set([0, 1])), NEW);
    assert.equal(textWithChosen(lines, new Set()), OLD);
    assert.equal(textWithChosen(lines, new Set([1])), 'import a\nx = old_name(1)\ny = 2\nz = 3\nw = new_name(2)\n');
});

test('the words that changed in a line', () => {
    assert.deepEqual(changedSpan('x = old_name(1)', 'x = new_name(1)'), { old: [4, 12], new: [4, 12] });
    // widened to the whole word
    assert.deepEqual(changedSpan('total = count', 'total = counter'), { old: [8, 13], new: [8, 15] });
    assert.equal(changedSpan('abc', 'xyz'), null);
    const lines = diffLines(OLD, NEW);
    const spans = wordSpans(lines);
    const removed = lines.findIndex((l) => l.kind === '-');
    assert.deepEqual(spans.get(removed), [4, 12]);
});

test('hunks say where their lines start', () => {
    const lines = diffLines(OLD, NEW);
    const hunks = hunksOf(lines, 0);
    assert.equal(lines[hunks[1].from].kind, '-');
});

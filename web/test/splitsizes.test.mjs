// Checks of web/src/splitsizes.js: `node web/test/splitsizes.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// web/package.json does not say "type": "module", so Node would read src/*.js as CommonJS; load the file as a module instead
const source = readFileSync(new URL('../src/splitsizes.js', import.meta.url), 'utf8');
const { splitWeights, resizePair } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

test('splitWeights: as many shares as editors, kept ones first', () => {
    assert.deepEqual(splitWeights([], 1), [1]);
    assert.deepEqual(splitWeights([2, 1], 2), [2, 1]);
    assert.deepEqual(splitWeights([1, 3, 2], 2), [1, 3]);
});

test('splitWeights: a new editor gets the average share, a wrong value counts as 1', () => {
    assert.deepEqual(splitWeights([1, 3], 3), [1, 3, 2]);
    assert.deepEqual(splitWeights([0, -1, 'x', NaN], 4), [1, 1, 1, 1]);
    assert.deepEqual(splitWeights(null, 2), [1, 1]);
});

test('resizePair: only the two beside the divider change, and their total stays', () => {
    assert.deepEqual(resizePair([1, 1, 2], 0, 0.25), [0.5, 1.5, 2]);
    assert.deepEqual(resizePair([1, 1, 2], 1, 0.5), [1, 1.5, 1.5]);
});

test('resizePair: each keeps at least 15%, and a divider that is not there changes nothing', () => {
    const round = (ws) => ws.map((w) => Math.round(w * 100) / 100);
    assert.deepEqual(round(resizePair([1, 1], 0, 0)), [0.3, 1.7]);
    assert.deepEqual(round(resizePair([1, 1], 0, 2)), [1.7, 0.3]);
    assert.deepEqual(resizePair([1, 1], 1, 0.5), [1, 1]);
    assert.deepEqual(resizePair([1, 1], 0, NaN), [1, 1]);
});

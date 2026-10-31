// Checks of web/src/coverage-model.js: `node web/test/coverage.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/coverage-model.js', import.meta.url), 'utf8');
const { mergeSuites, treeTotals, lineState, lineInfo, nextUncovered, reportHtml } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

// shapes.py as pm_tests.py reports it after sign(5): line 2's branch went to 3 only, 4 never ran
const shapes = { executable: [1, 2, 3, 4, 6, 7], executed: [1, 2, 3, 6], branches: { 2: [[3, 4], [3]], 6: [[7, 0], [0]] } };
const run = (files, label = 'a') => ({ label, files });

test('a line is hit, partly covered (a branch way not taken) or missed', () => {
    assert.equal(lineState(shapes, 1), 'hit');
    assert.equal(lineState(shapes, 2), 'part');
    assert.equal(lineState(shapes, 4), 'miss');
    assert.equal(lineState(shapes, 5), null);
    assert.equal(lineInfo(shapes, 2), 'Ran; branches: 1 of 2 taken; never went to line 4');
    assert.equal(lineInfo(shapes, 6), 'Ran; branches: 1 of 2 taken; never went to line 7');
    assert.equal(lineInfo(shapes, 4), 'Not run');
    assert.equal(lineInfo({ ...shapes, branches: { 2: [[3, 0], [3]] } }, 2), 'Ran; branches: 1 of 2 taken; never went to leaving the function');
});

test('next and previous uncovered lines skip the covered ones and stop at the ends', () => {
    assert.equal(nextUncovered(shapes, 1, 1), 2);
    assert.equal(nextUncovered(shapes, 2, 1), 4);
    assert.equal(nextUncovered(shapes, 6, 1), 7);
    assert.equal(nextUncovered(shapes, 7, 1), null);
    assert.equal(nextUncovered(shapes, 7, -1), 6);
    assert.equal(nextUncovered(shapes, 2, -1), null);
});

test('suites merge: a line or a branch way counts when any run took it, the newest run says what can run', () => {
    const newest = run({ 'shapes.py': shapes, 'gone.py': { executable: [1], executed: [] } }, 'newest');
    const older = run({ 'shapes.py': { executable: [1, 2, 3, 4, 9], executed: [1, 2, 4, 9], branches: { 2: [[3, 4], [4]] } } }, 'older');
    const merged = mergeSuites([newest, older]);
    const f = merged.files['shapes.py'];
    assert.deepEqual(f.executed, [1, 2, 3, 4, 6]);      // 9 is not a line of the newest file
    assert.deepEqual(f.branches['2'], [[3, 4], [3, 4]]);
    assert.equal(lineState(f, 2), 'hit');
    assert.equal(merged.label, 'newest');
    assert.equal(mergeSuites([newest]), newest);
    assert.equal(mergeSuites([]), null);
});

test('totals per file and per folder, the project at ""', () => {
    const totals = treeTotals(run({ 'app/a.py': shapes, 'app/sub/b.py': { executable: [1, 2], executed: [1, 2] }, 'c.py': { executable: [], executed: [] } }));
    assert.equal(totals['app/a.py'].hit, 4);
    assert.equal(totals['app/sub'].lines, 2);
    assert.deepEqual([totals.app.lines, totals.app.hit, totals.app.branches, totals.app.taken], [8, 6, 4, 2]);
    assert.equal(totals[''].percent, 75);
    assert.equal(totals['c.py'], undefined);
});

test('the report lists the files, least covered first, and marks the lines of the sources it has', () => {
    const html = reportHtml(run({ 'a.py': shapes, 'b.py': { executable: [1], executed: [1] } }), { 'a.py': 'def sign(x):\n    if x > 0:\n        return 1\n    return -1\n\ndef f():\n    pass <b>\n' });
    assert.ok(html.indexOf('>a.py</a>') < html.indexOf('>b.py</a>'));
    assert.match(html, /71\.4% of 7 lines ran \(5\), 2 of 4 branch ways taken, in 2 files/);
    assert.match(html, /<tr class="part" title="Ran; branches: 1 of 2 taken; never went to line 4"><td class="n">2<\/td>/);
    assert.match(html, /<tr class="miss" title="Not run"><td class="n">4<\/td>/);
    assert.match(html, /pass &lt;b&gt;/);
    assert.ok(!html.includes('id="f-b-py"'));
});

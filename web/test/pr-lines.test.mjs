// Checks of web/src/pr-model.js `patchRows` and `reviewChoices`: a patch as rows with real line numbers, and what a review can say on each forge.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { patchRows, reviewChoices } from '../src/pr-model.js';

const PATCH = '@@ -1,2 +1,3 @@\n def total():\n-    return 0\n+    return 1\n+    # more\n@@ -10,2 +20,2 @@\n keep\n-old\n+new\n\\ No newline at end of file\n';

test('a patch becomes rows that carry the line numbers of the file, and a comment can go on the rows with a new number', () => {
    const rows = patchRows(PATCH);
    assert.deepEqual(rows.map((r) => r.kind), ['hunk', 'ctx', 'del', 'add', 'add', 'hunk', 'ctx', 'del', 'add']);
    assert.deepEqual(rows.map((r) => r.newNo), [null, 1, null, 2, 3, null, 20, null, 21]);
    assert.deepEqual(rows.map((r) => r.oldNo), [null, 1, 2, null, null, null, 10, 11, null]);
    assert.equal(rows[2].text, '    return 0');
    assert.equal(rows[4].text, '    # more');
});

test('a patch with no hunk, an empty one and a new file give what they have', () => {
    assert.deepEqual(patchRows(''), []);
    assert.deepEqual(patchRows(undefined), []);
    assert.deepEqual(patchRows('no hunk here\n'), []);
    const added = patchRows('@@ -0,0 +1 @@\n+x = 1\n');
    assert.deepEqual(added.map((r) => [r.kind, r.newNo]), [['hunk', null], ['add', 1]]);
});

test('GitHub takes approvals, requests for changes and comments; GitLab approvals and taking them back', () => {
    const github = reviewChoices('github');
    assert.deepEqual(github.map((c) => c.value), ['approve', 'request_changes', 'comment']);
    assert.deepEqual(github.map((c) => c.needsWords), [false, true, true]);
    const gitlab = reviewChoices('gitlab');
    assert.deepEqual(gitlab.map((c) => c.value), ['approve', 'unapprove']);
    assert.ok(gitlab.every((c) => !c.needsWords));
});

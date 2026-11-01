// Checks of web/src/pr-model.js: `node web/test/pr.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { baseChoices, changeText, checksText, newProblem, nounOf, refOf, splitPatch, stateText, statusLetter, subjectOf, tokenHint, whoWants } from '../src/pr-model.js';

test('numbers and nouns of the two forges', () => {
    assert.equal(nounOf('github'), 'pull request');
    assert.equal(nounOf('gitlab'), 'merge request');
    assert.equal(refOf('github', 7), '#7');
    assert.equal(refOf('gitlab', 3), '!3');
});

test('checks, states and who wants what', () => {
    assert.deepEqual(checksText('failure'), { text: 'failed', cls: 'bad' });
    assert.deepEqual(checksText('success'), { text: 'passed', cls: 'ok' });
    assert.deepEqual(checksText('pending'), { text: 'running', cls: 'wait' });
    assert.deepEqual(checksText('none'), { text: 'no checks', cls: 'none' });
    assert.deepEqual(checksText('what'), { text: 'unknown', cls: 'none' });
    assert.equal(stateText({ state: 'open', draft: true }), 'draft');
    assert.equal(stateText({ state: 'open', draft: false }), 'open');
    assert.equal(stateText({ state: 'merged' }), 'merged');
    assert.equal(whoWants({ author: 'ada', source: 'cart', target: 'main' }), 'ada wants to merge cart into main');
    assert.equal(subjectOf('cart: the model\n\nbody'), 'cart: the model');
    assert.equal(changeText({ additions: 2, deletions: 1 }), '+2 −1');
    assert.equal(statusLetter('added'), 'A');
    assert.equal(statusLetter('modified'), 'M');
});

test('a patch becomes the two sides of a diff, the hunk headers on both', () => {
    const { original, modified } = splitPatch('@@ -1,2 +1,3 @@\n def total():\n-    return 0\n+    return 1\n+    # more\n\\ No newline at end of file\n');
    assert.equal(original, '@@ -1,2 +1,3 @@\ndef total():\n    return 0');
    assert.equal(modified, '@@ -1,2 +1,3 @@\ndef total():\n    return 1\n    # more');
    assert.deepEqual(splitPatch(''), { original: '', modified: '' });
    assert.deepEqual(splitPatch(undefined), { original: '', modified: '' });
    assert.equal(splitPatch('@@ -0,0 +1 @@\n+x = 1\n').original, '@@ -0,0 +1 @@');
});

test('what is wrong with the form of a new request', () => {
    assert.equal(newProblem({ title: ' ', head: 'a', base: 'main' }), 'Give it a title.');
    assert.match(newProblem({ title: 't', head: null, base: 'main' }), /no current branch/);
    assert.match(newProblem({ title: 't', head: 'a', base: '' }), /Choose the branch/);
    assert.match(newProblem({ title: 't', head: 'main', base: 'main' }), /switch to another branch/);
    assert.equal(newProblem({ title: 't', head: 'a', base: 'main' }), '');
});

test('the branches to merge into: the usual ones first, the current one never', () => {
    assert.deepEqual(baseChoices({ branch: 'cart', bases: ['zeta', 'cart', 'HEAD', 'develop', 'main'] }), ['main', 'develop', 'zeta']);
    assert.deepEqual(baseChoices({ branch: 'x' }), []);
});

test('the token field says where the token is kept and where it goes', () => {
    assert.match(tokenHint({ hasToken: false, host: 'github.com', apiBase: 'https://api.github.com' }), /only sent to https:\/\/api\.github\.com/);
    assert.match(tokenHint({ hasToken: true, host: 'gitlab.com' }), /pullrequests\.private\.json/);
});

import test from 'node:test';
import assert from 'node:assert/strict';
import { findConflicts, settle, settleAll, conflictLines } from '../src/merge-model.js';

const two = ['a = 1', '<<<<<<< HEAD', 'b = 2', '=======', 'b = 3', '>>>>>>> feature', 'c = 4', '<<<<<<< HEAD', 'd = 1', '=======', 'd = 2', '>>>>>>> feature', ''].join('\n');

test('the conflicts of a file, with their sides and labels', () => {
    const found = findConflicts(two);
    assert.equal(found.length, 2);
    assert.equal(found[0].ours, 'b = 2\n');
    assert.equal(found[0].theirs, 'b = 3\n');
    assert.equal(found[0].base, null);
    assert.equal(found[0].oursLabel, 'HEAD');
    assert.equal(found[0].theirsLabel, 'feature');
    assert.equal(two.slice(found[1].start, found[1].end), '<<<<<<< HEAD\nd = 1\n=======\nd = 2\n>>>>>>> feature\n');
});

test('each way of settling one conflict', () => {
    assert.equal(settle(two, 0, 'ours'), 'a = 1\nb = 2\nc = 4\n<<<<<<< HEAD\nd = 1\n=======\nd = 2\n>>>>>>> feature\n');
    assert.equal(settle(two, 1, 'theirs'), 'a = 1\n<<<<<<< HEAD\nb = 2\n=======\nb = 3\n>>>>>>> feature\nc = 4\nd = 2\n');
    assert.equal(settle(two, 0, 'both'), 'a = 1\nb = 2\nb = 3\nc = 4\n<<<<<<< HEAD\nd = 1\n=======\nd = 2\n>>>>>>> feature\n');
    assert.equal(settle(two, 0, 'bothReversed'), 'a = 1\nb = 3\nb = 2\nc = 4\n<<<<<<< HEAD\nd = 1\n=======\nd = 2\n>>>>>>> feature\n');
});

test('all of them at once, and nothing left to find', () => {
    assert.equal(settleAll(two, 'theirs'), 'a = 1\nb = 3\nc = 4\nd = 2\n');
    assert.equal(findConflicts(settleAll(two, 'ours')).length, 0);
});

test('diff3 style keeps the common ancestor', () => {
    const text = 'x\n<<<<<<< HEAD\nours\n||||||| base\nold\n=======\ntheirs\n>>>>>>> other\ny\n';
    const [c] = findConflicts(text);
    assert.equal(c.ours, 'ours\n');
    assert.equal(c.base, 'old\n');
    assert.equal(c.theirs, 'theirs\n');
    assert.equal(settle(text, 0, 'base'), 'x\nold\ny\n');
    assert.equal(settle(two, 0, 'base'), two); // no ancestor in this file: nothing happens
});

test('an empty side, windows line endings, and a file that does not end in a newline', () => {
    const empty = 'a\n<<<<<<< HEAD\n=======\nnew\n>>>>>>> t\n';
    assert.equal(settle(empty, 0, 'ours'), 'a\n');
    assert.equal(settle(empty, 0, 'theirs'), 'a\nnew\n');
    const crlf = 'a\r\n<<<<<<< HEAD\r\nb\r\n=======\r\nc\r\n>>>>>>> t\r\nd\r\n';
    assert.equal(settle(crlf, 0, 'both'), 'a\r\nb\r\nc\r\nd\r\n');
    const last = 'a\n<<<<<<< HEAD\nb\n=======\nc\n>>>>>>> t';
    assert.equal(settle(last, 0, 'ours'), 'a\nb\n');
});

test('what is not a conflict is left alone', () => {
    assert.equal(findConflicts('title\n=======\ntext\n').length, 0);               // an underline in a document
    assert.equal(findConflicts('<<<<<<< HEAD\nonly one side\n').length, 0);         // never closed
    assert.equal(findConflicts('x = "<<<<<<< not at the start"\n').length, 0);
    assert.equal(findConflicts('<<<<<<<<\n=======\n>>>>>>>\n').length, 0);         // eight characters is not a marker
    assert.equal(settle('plain\n', 0, 'ours'), 'plain\n');
});

test('the line each conflict begins on', () => {
    assert.deepEqual(conflictLines(two), [2, 8]);
});

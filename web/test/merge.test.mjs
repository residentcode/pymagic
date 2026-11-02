import test from 'node:test';
import assert from 'node:assert/strict';
import { findConflicts, settle, settleAll, conflictLines, sideView, sideLines, settlePicked } from '../src/merge-model.js';

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

test('one side of the whole file lines up with the result, conflict by conflict', () => {
    const three = ['a = 1', '<<<<<<< HEAD', 'b = 2', 'b2 = 2', '=======', 'b = 3', '>>>>>>> feature', 'c = 4', '<<<<<<< HEAD', '=======', 'd = 2', '>>>>>>> feature', ''].join('\n');
    const ours = sideView(three, 'ours');
    assert.equal(ours.text, 'a = 1\nb = 2\nb2 = 2\nc = 4\n');
    assert.deepEqual(ours.ranges, [{ index: 0, line: 2, count: 2 }, { index: 1, line: 5, count: 0 }]);
    const theirs = sideView(three, 'theirs');
    assert.equal(theirs.text, 'a = 1\nb = 3\nc = 4\nd = 2\n');
    assert.deepEqual(theirs.ranges, [{ index: 0, line: 2, count: 1 }, { index: 1, line: 4, count: 1 }]);
    // nothing left: the text itself, and no ranges
    assert.deepEqual(sideView('x\n', 'ours'), { text: 'x\n', ranges: [] });
    assert.deepEqual(sideLines('p\nq\nr'), ['p\n', 'q\n', 'r']);
});

test('a conflict settled with the lines picked from each side: ours first, in order', () => {
    const text = ['<<<<<<< HEAD', 'o1', 'o2', 'o3', '=======', 't1', 't2', '>>>>>>> feature', 'end', ''].join('\n');
    assert.equal(settlePicked(text, 0, [2, 0], [1]), 'o1\no3\nt2\nend\n');
    assert.equal(settlePicked(text, 0, [], [0]), 't1\nend\n');
    assert.equal(settlePicked(text, 0, [1, 1, 9], []), 'o2\nend\n');           // a line twice or out of range: once, or not at all
    assert.equal(settlePicked(text, 0, [], []), text);                          // nothing picked: nothing changes
    assert.equal(settlePicked(text, 3, [0], []), text);                         // no such conflict
    // Windows endings stay, and a side whose last line has no ending gets one when more follows
    const crlf = '<<<<<<< HEAD\r\nx\r\n=======\r\ny\r\n>>>>>>> b\r\nz\r\n';
    assert.equal(settlePicked(crlf, 0, [0], [0]), 'x\r\ny\r\nz\r\n');
});

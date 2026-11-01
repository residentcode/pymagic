// Checks of the name finder behind Tab in the notebook console (web/src/notebook-model.js `completionTarget`, `commonPrefix`).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { commonPrefix, completionTarget } from '../src/notebook-model.js';

test('the name under the cursor is the dotted run before it, and only the part after the last dot is replaced', () => {
    assert.deepEqual(completionTarget('lat', 3), { prefix: 'lat', word: 'lat', wordStart: 0 });
    assert.deepEqual(completionTarget('print(late.app', 14), { prefix: 'late.app', word: 'app', wordStart: 11 });
    assert.deepEqual(completionTarget('a.b.', 4), { prefix: 'a.b.', word: '', wordStart: 4 });
    assert.deepEqual(completionTarget('x = df.he and more', 9), { prefix: 'df.he', word: 'he', wordStart: 7 });
    assert.deepEqual(completionTarget('abc', 2), { prefix: 'ab', word: 'ab', wordStart: 0 });
});

test('there is nothing to complete after a space or a bracket, in a number, or behind a call', () => {
    assert.equal(completionTarget('', 0), null);
    assert.equal(completionTarget('x = ', 4), null);
    assert.equal(completionTarget('f(', 2), null);
    assert.equal(completionTarget('1.5', 3), null);
    assert.equal(completionTarget('9lives', 6), null);
    assert.equal(completionTarget('f().', 4), null, 'finding the words of a call would run it');
    assert.equal(completionTarget('a[0].x', 6), null);
    assert.equal(completionTarget('"text".up', 9), null);
});

test('the words that all fit share a start', () => {
    assert.equal(commonPrefix(['remove', 'reverse']), 're');
    assert.equal(commonPrefix(['append']), 'append');
    assert.equal(commonPrefix(['lambda', 'late']), 'la');
    assert.equal(commonPrefix(['a', 'b']), '');
    assert.equal(commonPrefix([]), '');
});

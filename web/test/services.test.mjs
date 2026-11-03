// Checks of web/src/services-model.js: `node web/test/services.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { elapsed, withOutput } from '../src/services-model.js';

test('how long, in words', () => {
    assert.equal(elapsed(0), '0 s');
    assert.equal(elapsed(12_400), '12 s');
    assert.equal(elapsed(59_600), '60 s'.replace('60 s', '1 min'));
    assert.equal(elapsed(3 * 60_000), '3 min');
    assert.equal(elapsed((2 * 60 + 5) * 60_000), '2 h 5 min');
    assert.equal(elapsed(-5), '0 s');
});

test('the output of a service: colour codes out, the end kept', () => {
    assert.equal(withOutput('', '\x1b[32mok\x1b[0m\n'), 'ok\n');
    assert.equal(withOutput('ab', 'cd'), 'abcd');
    assert.equal(withOutput(undefined, 'hello', 3), 'llo');
    assert.equal(withOutput('x', null), 'x');
});

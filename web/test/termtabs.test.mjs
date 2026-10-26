// Checks of web/src/termtabs.js: `node web/test/termtabs.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// web/package.json does not say "type": "module", so Node would read src/*.js as CommonJS; load the file as a module instead
const source = readFileSync(new URL('../src/termtabs.js', import.meta.url), 'utf8');
const { nextName, afterClose, cleanName } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

test('nextName: the lowest free number, so a closed terminal\'s number comes back', () => {
    assert.equal(nextName([]), 'Terminal 1');
    assert.equal(nextName(['Terminal 1']), 'Terminal 2');
    assert.equal(nextName(['Terminal 1', 'Terminal 3']), 'Terminal 2');
    assert.equal(nextName(['Terminal 2', 'Terminal 3']), 'Terminal 1');
});

test('nextName: renamed terminals do not count, and a name typed like a default does', () => {
    assert.equal(nextName(['tests', 'server']), 'Terminal 1');
    assert.equal(nextName(['Terminal 1', 'Terminal 10']), 'Terminal 2');
    assert.equal(nextName(['Terminal 1', 'Terminal 2']), 'Terminal 3');
});

test('afterClose: closing another terminal leaves the open one open', () => {
    assert.equal(afterClose([1, 2, 3], 1, 2), 2);
    assert.equal(afterClose([1, 2, 3], 3, 2), 2);
});

test('afterClose: closing the open terminal opens its right-hand neighbour, or the left one at the end', () => {
    assert.equal(afterClose([1, 2, 3], 2, 2), 3);
    assert.equal(afterClose([1, 2, 3], 1, 1), 2);
    assert.equal(afterClose([1, 2, 3], 3, 3), 2);
});

test('afterClose: nothing is left after the last one, and an unknown open one falls back to a neighbour', () => {
    assert.equal(afterClose([7], 7, 7), null);
    assert.equal(afterClose([1, 2], 1, null), 2);
    assert.equal(afterClose([1, 2], 9, 9), 1);
});

test('cleanName: spaces folded and trimmed, at most 40 characters, nothing typed keeps the old name', () => {
    assert.equal(cleanName('  tests   watch  '), 'tests watch');
    assert.equal(cleanName('x'.repeat(50)), 'x'.repeat(40));
    assert.equal(cleanName('   '), null);
    assert.equal(cleanName(''), null);
    assert.equal(cleanName(undefined), null);
});

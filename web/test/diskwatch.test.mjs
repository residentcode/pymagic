// Checks of web/src/diskwatch.js: `node web/test/diskwatch.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// web/package.json does not say "type": "module", so Node would read src/*.js as CommonJS; load the file as a module instead
const source = readFileSync(new URL('../src/diskwatch.js', import.meta.url), 'utf8');
const { planBatch, parseEvent } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const ev = (...texts) => texts.map(parseEvent);

test('the content of an open file changed: look at it, the tree stays', () => {
    assert.deepEqual(planBatch(ev('changed|a.py', 'changed|a.py'), ['a.py', 'b.py']), { tree: false, files: ['a.py'] });
});

test('a log file written again and again changes nothing in the tree', () => {
    assert.deepEqual(planBatch(ev('changed|run.log', 'changed|run.log'), ['a.py']), { tree: false, files: [] });
});

test('a new, removed or renamed file: the tree is read again', () => {
    assert.deepEqual(planBatch(ev('created|pkg/new.py'), ['a.py']), { tree: true, files: [] });
    assert.deepEqual(planBatch(ev('deleted|pkg', 'changed|a.py'), ['a.py']), { tree: true, files: ['a.py'] });
});

test('too many changes to tell: everything is read again', () => {
    assert.deepEqual(planBatch(ev('all|*'), ['a.py', 'b.py']), { tree: true, files: ['a.py', 'b.py'] });
    assert.deepEqual(parseEvent('created|dir/with|bar.py'), { kind: 'created', path: 'dir/with|bar.py' });
});

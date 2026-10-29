// Checks of web/src/uripath.js: `node web/test/uripath.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// web/package.json does not say "type": "module", so Node would read src/*.js as CommonJS; load the file as a module instead
const source = readFileSync(new URL('../src/uripath.js', import.meta.url), 'utf8');
const { relUnderRoot } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

test('relUnderRoot: a file of the project gives its path from the project folder', () => {
    assert.equal(relUnderRoot('file:///C:/proj/shop/views.py', 'file:///C:/proj/'), 'shop/views.py');
    assert.equal(relUnderRoot('file:///C:/proj/main.py', 'file:///C:/proj'), 'main.py');
});

test('relUnderRoot: the same address written another way is the same file', () => {
    assert.equal(relUnderRoot('file:///c%3A/proj/My%20App/a.py', 'file:///C:/proj/'), 'My App/a.py');
    assert.equal(relUnderRoot('file:///C:/PROJ/a.py', 'file:///c:/proj/'), 'a.py');
});

test('relUnderRoot: an address outside the project, the folder itself, or one that climbs out is not in it', () => {
    assert.equal(relUnderRoot('file:///C:/other/a.py', 'file:///C:/proj/'), null);
    assert.equal(relUnderRoot('file:///C:/project2/a.py', 'file:///C:/proj/'), null);
    assert.equal(relUnderRoot('file:///C:/proj/', 'file:///C:/proj/'), null);
    assert.equal(relUnderRoot('file:///C:/proj/../x.py', 'file:///C:/proj/'), null);
    assert.equal(relUnderRoot('file:///C:/proj/%E0%A4%A', 'file:///C:/proj/'), null);
    assert.equal(relUnderRoot(undefined, 'file:///C:/proj/'), null);
});

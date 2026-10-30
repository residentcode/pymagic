// Checks of web/src/fileactions-model.js: `node web/test/fileactions.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/fileactions-model.js', import.meta.url), 'utf8');
const { pythonFilesUnder, includeFor, isTestFile, FILE_TYPES } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

test('pythonFilesUnder: a folder reaches its own Python files and those of its subfolders, not of a folder with a longer name', () => {
    const files = ['app/a.py', 'app/sub/b.pyi', 'app/readme.md', 'apps/c.py', 'main.py'];
    assert.deepEqual(pythonFilesUnder(files, 'app'), ['app/a.py', 'app/sub/b.pyi']);
    assert.deepEqual(pythonFilesUnder(files, 'main.py'), ['main.py']);
    assert.deepEqual(pythonFilesUnder(files, ''), ['app/a.py', 'app/sub/b.pyi', 'apps/c.py', 'main.py']);
});

test('includeFor: Find in Files of a folder searches under it, of the project folder everywhere', () => {
    assert.equal(includeFor('app/api'), 'app/api/**');
    assert.equal(includeFor(''), '');
});

test('isTestFile: the names pytest and Django collect', () => {
    for (const name of ['tests/test_api.py', 'api_test.py', 'shop/tests.py', 'test.py']) assert.ok(isTestFile(name), name);
    for (const name of ['testing.py', 'contest.py', 'tests/conftest.py', 'test_data.json']) assert.ok(!isTestFile(name), name);
});

test('FILE_TYPES: every type has a label and an id once', () => {
    const ids = FILE_TYPES.map(([id]) => id);
    assert.equal(new Set(ids).size, ids.length);
    assert.ok(ids.includes('python') && ids.includes('plaintext'));
});

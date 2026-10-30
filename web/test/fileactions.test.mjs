// Checks of web/src/fileactions-model.js: `node web/test/fileactions.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/fileactions-model.js', import.meta.url), 'utf8');
const { pythonFilesUnder, includeFor, isTestFile, FILE_TYPES, usageFiles, testPartners, rememberCopy, copyLabel } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

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

test('usageFiles: the usages by file with their lines, those outside the project left out', () => {
    const loc = (uri, line, from, to) => ({ uri, range: { start: { line, character: from }, end: { line, character: to } } });
    const relOf = (uri) => (uri.startsWith('p/') ? uri.slice(2) : null);
    const files = usageFiles([loc('p/main.py', 2, 5, 10), loc('p/main.py', 0, 7, 12), loc('lib/x.py', 0, 0, 1)], relOf, { 'main.py': 'import pkg.b\n\nfrom pkg.b import V\n' });
    assert.deepEqual(files, [{ path: 'main.py', matches: [
        { line: 0, column: 7, length: 5, text: 'import pkg.b', replacement: null },
        { line: 2, column: 5, length: 5, text: 'from pkg.b import V', replacement: null },
    ] }]);
});

test('testPartners: a module finds its tests anywhere, a test its module', () => {
    const files = ['shop/cart.py', 'tests/test_cart.py', 'cart_test.py', 'shop/other.py'];
    assert.deepEqual(testPartners('shop/cart.py', files), ['tests/test_cart.py', 'cart_test.py']);
    assert.deepEqual(testPartners('tests/test_cart.py', files), ['shop/cart.py']);
    assert.deepEqual(testPartners('shop/other.py', files), []);
});

test('rememberCopy and copyLabel: the newest first, each once, at most 15; a label on one line', () => {
    let h = [];
    for (const t of ['a', 'b', 'a', '']) h = rememberCopy(h, t);
    assert.deepEqual(h, ['a', 'b']);
    for (let i = 0; i < 20; i++) h = rememberCopy(h, `x${i}`);
    assert.equal(h.length, 15);
    assert.equal(h[0], 'x19');
    assert.equal(copyLabel('def f():\n    return 1\n'), 'def f(): return 1  (3 lines)');
    assert.equal(copyLabel('y'.repeat(80)).length, 60);
});

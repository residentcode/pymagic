// Checks of web/src/tracelink-model.js: `node web/test/tracelink.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/tracelink-model.js', import.meta.url), 'utf8');
const { traceMentions, projectPathOf, linkPieces } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const ROOT = 'C:\\Users\\me\\proj';

test('the places of a traceback and of a pytest line are found', () => {
    const text = 'Traceback (most recent call last):\n  File "/workspace/app/main.py", line 12, in <module>\n    run()\napp/tests/test_a.py:7: AssertionError\n';
    const m = traceMentions(text);
    assert.deepEqual(m.map((x) => [x.path, x.line]), [['/workspace/app/main.py', 12], ['app/tests/test_a.py', 7]]);
    assert.equal(text.slice(m[0].start, m[0].end), 'File "/workspace/app/main.py", line 12');
});

test('a path of the container is taken back to the project through where the interpreter has it', () => {
    assert.equal(projectPathOf('/workspace/app/main.py', ROOT, '/workspace'), 'app/main.py');
    assert.equal(projectPathOf('/app/x.py', ROOT, '/app'), 'x.py');
    assert.equal(projectPathOf('/mnt/c/Users/me/proj/a/b.py', ROOT, '/mnt/c/Users/me/proj'), 'a/b.py');
    assert.equal(projectPathOf('/workspacey/x.py', ROOT, '/workspace'), null);
    assert.equal(projectPathOf('/usr/local/lib/python3.12/json/__init__.py', ROOT, '/workspace'), null);
    assert.equal(projectPathOf('/workspace/../etc/x.py', ROOT, '/workspace'), null);
});

test('a path of this computer, in either slash and any case of the drive, is the project too', () => {
    assert.equal(projectPathOf('C:\\Users\\me\\proj\\app\\main.py', ROOT, null), 'app/main.py');
    assert.equal(projectPathOf('c:/users/me/proj/app/main.py', ROOT, null), 'app/main.py');
    assert.equal(projectPathOf('C:\\Python312\\Lib\\json\\__init__.py', ROOT, null), null);
    assert.equal(projectPathOf('tests/test_a.py', ROOT, null), 'tests/test_a.py');
    assert.equal(projectPathOf('<string>', ROOT, null), null);
});

test('only the mentions that are project files become links, the rest of the text stays', () => {
    const text = '  File "/workspace/a.py", line 3, in f\n  File "/usr/lib/python3.12/runpy.py", line 9, in g\n';
    const pieces = linkPieces(text, ROOT, '/workspace');
    assert.equal(pieces.map((p) => p.text).join(''), text);
    assert.deepEqual(pieces.filter((p) => p.rel).map((p) => [p.text, p.rel, p.line]), [['File "/workspace/a.py", line 3', 'a.py', 3]]);
    assert.equal(linkPieces('nothing to see', ROOT, null).some((p) => p.rel), false);
});

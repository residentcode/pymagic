// Checks of web/src/spell-model.js: `node web/test/spell.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { splitWords, worthAsking, pythonWords, textWords, spellKind, likeOriginal } from '../src/spell-model.js';

const words = (list) => list.map((w) => `${w.kind}:${w.word}`);

test('identifiers are split at underscores, capitals and digits', () => {
    assert.deepEqual(splitWords('parse_httpRequest').map((w) => w.word), ['parse', 'http', 'Request']);
    assert.deepEqual(splitWords('parseHTTPResponse').map((w) => w.word), ['parse', 'HTTP', 'Response']);
    assert.deepEqual(splitWords('value2go').map((w) => w.word), ['value', 'go']);
    assert.deepEqual(splitWords("don't").map((w) => w.word), ["don't"]);
});

test('what is worth asking about', () => {
    assert.ok(worthAsking('recieve', 3));
    assert.ok(!worthAsking('ab', 3));
    assert.ok(!worthAsking('HTTP', 3));            // an acronym
    assert.ok(!worthAsking('kwargs', 3));          // a word of programming
    assert.ok(!worthAsking('Django', 3));
});

test('Python: the prose of comments and strings, and the names a file defines', () => {
    const text = [
        'import os',
        '# Recieve the mesage here',
        'class Recieved:',
        '    def fetch_mesage(self, nubmer, *args, **kwargs):',
        '        """Returns the fucntion result."""',
        '        label = "hello wrold"',
        '        key = "some_key"',
        '        return os.path.join("a/b/c", f"{label} adn more")',
        '',
    ].join('\n');
    const found = pythonWords(text);
    assert.deepEqual(words(found).filter((w) => /rec|mes|nub|fuc|wro|adn/i.test(w)).sort(), [
        'comment:Recieve', 'comment:mesage', 'name:Recieved', 'name:mesage', 'name:nubmer', 'string:adn', 'string:fucntion', 'string:wrold',
    ].sort());
    // positions are of the word itself
    for (const w of found) assert.equal(text.slice(w.start, w.end), w.word);
});

test('Python: code, paths, comments that are directives and injected languages are left alone', () => {
    const text = [
        'x = 1  # noqa: E501 recieve',
        'y = "SELECT nme FROM tabel WHERE id = 1"',
        '# language=json',
        'z = \'{"nmae": 1}\'',
        'p = "C:/some/pth/to/fle.txt"',
        'q = "http://exmaple.org/pge"',
        'k = "singleword"',
        'n = b"tihs is bytes"',
        'import numpy as np',
    ].join('\n');
    assert.deepEqual(pythonWords(text).map((w) => w.word), []);
});

test('Markdown and text: prose only', () => {
    const text = 'A paragrpah with `inlinecode` and a [link](http://exmaple.org/x).\n\n```\ncode wiht typos\n```\n\n- a list itme\n\n    indented code wiht typos\n';
    assert.deepEqual(textWords(text).map((w) => w.word), ['paragrpah', 'with', 'and', 'link', 'list', 'itme']);
    for (const w of textWords(text)) assert.equal(text.slice(w.start, w.end), w.word);
});

test('which files, and replacements written like the word', () => {
    assert.equal(spellKind('src/a.py'), 'python');
    assert.equal(spellKind('README.md'), 'text');
    assert.equal(spellKind('notes.TXT'), 'text');
    assert.equal(spellKind('a.js'), null);
    assert.equal(likeOriginal('Recieve', 'receive'), 'Receive');
    assert.equal(likeOriginal('RECIEVE', 'receive'), 'RECEIVE');
    assert.equal(likeOriginal('recieve', 'receive'), 'receive');
});

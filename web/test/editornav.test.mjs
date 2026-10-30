// Checks of web/src/editornav-model.js: `node web/test/editornav.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/editornav-model.js', import.meta.url), 'utf8');
const { definitions, nextDefinition, pathTo, separatorLines, blockOf, addPlace, regexAt } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const sym = (name, kind, start, end, children = []) => ({ name, kind, range: { start: { line: start - 1 }, end: { line: end - 1 } }, selectionRange: { start: { line: start - 1 } }, children });
// class Shape (1-8) with area (2-3) and size (5-8, with a nested helper 6-7); a function move (10-12); a variable
const symbols = [sym('Shape', 5, 1, 8, [sym('area', 6, 2, 3), sym('size', 6, 5, 8, [sym('helper', 12, 6, 7)])]), sym('move', 12, 10, 12), sym('X', 13, 14, 14)];
const defs = definitions(symbols);

test('definitions: classes, methods and functions in order with their depth, not variables', () => {
    assert.deepEqual(defs.map((d) => `${d.name}@${d.depth}`), ['Shape@0', 'area@1', 'size@1', 'helper@2', 'move@0']);
});

test('nextDefinition: the next or previous definition line, or null at the ends', () => {
    assert.equal(nextDefinition(defs, 3, 1), 5);
    assert.equal(nextDefinition(defs, 5, -1), 2);
    assert.equal(nextDefinition(defs, 10, 1), null);
    assert.equal(nextDefinition(defs, 1, -1), null);
});

test('pathTo: the definitions around a line, outermost first', () => {
    assert.deepEqual(pathTo(defs, 6).map((d) => d.name), ['Shape', 'size', 'helper']);
    assert.deepEqual(pathTo(defs, 9).map((d) => d.name), []);
});

test('separatorLines: above each definition but the first of its level, not inside a function', () => {
    assert.deepEqual(separatorLines(defs), [5, 10]);
});

test('blockOf: the line that opens the block of a line, and its last line', () => {
    const text = ['def f(x):', '    if x:', '        a = 1', '', '        b = 2', '    return a', 'y = 1'];
    const at = (l) => text[l - 1] ?? '';
    assert.deepEqual(blockOf(at, 3, text.length), { start: 2, end: 5 });
    assert.deepEqual(blockOf(at, 6, text.length), { start: 1, end: 6 });
    assert.equal(blockOf(at, 7, text.length), null);
});

test('addPlace: newest first, one per file and nearby line, at most max', () => {
    let places = [];
    places = addPlace(places, { rel: 'a.py', line: 10 });
    places = addPlace(places, { rel: 'b.py', line: 1 });
    places = addPlace(places, { rel: 'a.py', line: 12 });
    assert.deepEqual(places.map((p) => `${p.rel}:${p.line}`), ['a.py:12', 'b.py:1']);
    assert.equal(addPlace(places, { rel: 'c.py', line: 1 }, 2).length, 2);
});

test('regexAt: the pattern of a re call when the cursor is in its first string, as Python reads it', () => {
    const line = "m = re.match(r'(?P<year>\\d{4})-\\d+', text, re.I | re.MULTILINE)";
    const found = regexAt(line, line.indexOf('year'));
    assert.equal(found.pattern, '(?P<year>\\d{4})-\\d+');
    assert.deepEqual(found.flags, ['IGNORECASE', 'MULTILINE']);
    assert.equal(regexAt(line, 2), null);   // not in the string
    assert.equal(regexAt("print('a+b')", 9), null);   // not a re call
    const plain = "re.compile('a\\\\d\\n')";
    assert.equal(regexAt(plain, 14).pattern, 'a\\d\n');   // a plain string's escapes are read
});

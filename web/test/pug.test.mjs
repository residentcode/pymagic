// Pug's indentation errors (web/src/pug-model.js), as Pug's lexer reports them.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { pugProblems } from '../src/pug-model.js';

const lines = (text) => pugProblems(text).map((p) => `${text.slice(0, p.start).split('\n').length}: ${p.message}`);

test('Express\'s layout and index views: no finding', () => {
    const layout = 'doctype html\nhtml\n  head\n    title= title\n    link(rel=\'stylesheet\', href=\'/stylesheets/style.css\')\n  body\n    block content\n';
    const index = "extends layout\n\nblock content\n  h1= title\n  p Welcome to #{title}\n  if users.length\n    ul\n      each user in users\n        li= user.name\n  else\n    p No users.\n";
    assert.deepEqual(lines(layout), []);
    assert.deepEqual(lines(index), []);
});

test('coming back to a level none of the lines above is at', () => {
    assert.deepEqual(lines('div\n    p a\n  p b\n'), ['3: Inconsistent indentation: 2 spaces, the lines above are at 0 spaces or 4 spaces']);
    assert.deepEqual(lines('ul\n  li a\n    span x\n   li b\n'), ['4: Inconsistent indentation: 3 spaces, the lines above are at 2 spaces or 4 spaces']);
    // going further in by any amount is Pug's (each level its own)
    assert.deepEqual(lines('div\n   p a\n      span x\n   p b\n'), []);
});

test('tabs and spaces', () => {
    assert.deepEqual(lines('div\n\t p a\n'), ['2: Invalid indentation: tabs and spaces both (Pug takes one or the other)']);
    assert.deepEqual(lines('div\n\tp a\nsection\n  p b\n'), ['4: Invalid indentation: spaces here, the file is indented with tabs']);
});

test('free lines: text blocks, comments, filters, code, attributes over lines', () => {
    const ok = [
        'script.\n  if (a) {\n      b();\n   }\np x\n',
        'p.\n  Some text\n     indented as it likes\ndiv\n',
        'div(class="a").\n   text\n  more\n',
        '//\n  a comment\n     over lines\np x\n',
        '//- hidden\n     notes\np x\n',
        ':markdown-it\n  # Title\n     text\np x\n',
        '-\n  var a = 1\n     var b = 2\np= a\n',
        'a(\n  href="/x"\n     class="y"\n) Link\np x\n',
        "p It ends with a period.\n  span x\n",
    ];
    for (const t of ok) assert.deepEqual(lines(t), [], t);
    // after the block (a line no further in than its first): checked again; one further in is still the script's text, as Pug reads it
    assert.deepEqual(lines('div\n  script.\n    x()\n p y\n'), ['4: Inconsistent indentation: 1 space, the lines above are at 0 spaces or 2 spaces']);
    assert.deepEqual(lines('div\n  script.\n    x()\n   p y\n'), []);
});

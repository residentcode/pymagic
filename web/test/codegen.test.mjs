// Checks of web/src/codegen-model.js: `node web/test/codegen.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/codegen-model.js', import.meta.url), 'utf8');
const { surroundLines, surroundExpression, unwrapOptions, abbreviationBefore, expandAbbreviation, newFileText, readFileTemplates, DEFAULT_FILE_TEMPLATES, readSnippets, writeSnippets } =
    await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const at = (text) => {
    const lines = text.split('\n');
    return [(l) => lines[l - 1] ?? '', lines.length];
};
// what a snippet becomes once its placeholders take their default text
const plain = (snippet) => snippet.replace(/\$\{\d+:([^}]*)\}/g, '$1').replace(/\$\{?\d+\}?/g, '').replace(/\\([\\$}])/g, '$1');

test('surround: lines move one level in under the statement, which takes their indentation', () => {
    const s = surroundLines(['    x = 1', '', '    print(x)'], 'if');
    assert.equal(plain(s), '    if condition:\n        x = 1\n\n        print(x)');
    assert.equal(plain(surroundLines(['a()'], 'try')), 'try:\n    a()\nexcept Exception as e:\n    raise');
    assert.equal(plain(surroundLines(['a()'], 'tryfinally')), 'try:\n    a()\nfinally:\n    pass');
    assert.equal(plain(surroundLines(['a()'], 'ifelse')), 'if condition:\n    a()\nelse:\n    pass');
    assert.equal(plain(surroundLines(['a()'], 'with')), 'with expression as target:\n    a()');
    assert.equal(plain(surroundLines(['a()'], 'for')), 'for item in items:\n    a()');
    assert.equal(surroundLines(['', '  '], 'if'), null);
});

test('surround: the selected text is kept literally ($ and } escaped in the snippet)', () => {
    const s = surroundLines(['price = "$5 {x}"'], 'while');
    assert.ok(s.includes('"\\$5 {x\\}"'));
    assert.equal(surroundExpression('a + b', 'not'), 'not (a + b)$0');
    assert.equal(surroundExpression('x', 'fstring'), 'f"{x}"$0');
});

test('unwrap: the if around a line is replaced by its body, one level out; an else can be unwrapped or removed', () => {
    const [t, n] = at('def f():\n    if a:\n        x = 1\n        y = 2\n    else:\n        z = 3\n    done()');
    const inIf = unwrapOptions(t, 3, n);
    assert.deepEqual(inIf.map((o) => o.label), ["Unwrap 'if...'"]);
    assert.deepEqual(inIf[0], { label: "Unwrap 'if...'", start: 2, end: 6, lines: ['    x = 1', '    y = 2'] });
    const inElse = unwrapOptions(t, 6, n);
    assert.deepEqual(inElse.map((o) => o.label), ["Unwrap 'else...'", "Remove 'else...'"]);
    assert.deepEqual(inElse[0].lines, ['    z = 3']);
    assert.deepEqual(inElse[1].lines, ['    if a:', '        x = 1', '        y = 2']);
});

test('unwrap: statements around statements are offered innermost first; a def stops the search', () => {
    const [t, n] = at('def f():\n    for i in r:\n        with open(p) as fh:\n            use(fh)\n    return 1');
    const o = unwrapOptions(t, 4, n);
    assert.deepEqual(o.map((x) => x.label), ["Unwrap 'with...'", "Unwrap 'for...'"]);
    assert.deepEqual(o[0].lines, ['        use(fh)']);
    assert.deepEqual(o[1], { label: "Unwrap 'for...'", start: 2, end: 4, lines: ['    with open(p) as fh:', '        use(fh)'] });
});

test('unwrap: try keeps its body; an except is removed only when another handler is left', () => {
    const [t, n] = at('try:\n    run()\nexcept KeyError:\n    a()\nexcept ValueError:\n    b()\nfinally:\n    c()');
    assert.deepEqual(unwrapOptions(t, 2, n)[0], { label: "Unwrap 'try...'", start: 1, end: 8, lines: ['run()'] });
    const inExcept = unwrapOptions(t, 4, n);
    assert.deepEqual(inExcept.map((x) => x.label), ["Unwrap 'except...'", "Remove 'except...'"]);
    assert.deepEqual(inExcept[1].lines, ['try:', '    run()', 'except ValueError:', '    b()', 'finally:', '    c()']);
    const [t2, n2] = at('try:\n    run()\nexcept KeyError:\n    a()');
    assert.deepEqual(unwrapOptions(t2, 4, n2).map((x) => x.label), ["Unwrap 'except...'"]);
    assert.deepEqual(unwrapOptions(...at('x = 1'), 1), []);
});

test('emmet: children, siblings, climbing, counts with numbering, ids, classes, attributes and text', () => {
    assert.equal(plain(expandAbbreviation('ul>li.item$*2')), '<ul>\n\t<li class="item1"></li>\n\t<li class="item2"></li>\n</ul>');
    assert.equal(plain(expandAbbreviation('div#main.a.b>p{Hi}+span')), '<div id="main" class="a b">\n\t<p>Hi</p>\n\t<span></span>\n</div>');
    assert.equal(plain(expandAbbreviation('div>p^h1')), '<div>\n\t<p></p>\n</div>\n<h1></h1>');
    assert.equal(plain(expandAbbreviation('a[href=/x]{Go}')), '<a href="/x">Go</a>');
    assert.equal(plain(expandAbbreviation('.box')), '<div class="box"></div>');
    assert.equal(plain(expandAbbreviation('ul>.x')), '<ul>\n\t<li class="x"></li>\n</ul>');
    assert.equal(plain(expandAbbreviation('img')), '<img src="" alt="">');
    assert.equal(plain(expandAbbreviation('(dt+dd)*2')), '<dt></dt>\n<dd></dd>\n<dt></dt>\n<dd></dd>');
    assert.equal(plain(expandAbbreviation('li{n$$}*2')), '<li>n01</li>\n<li>n02</li>');
    assert.ok(expandAbbreviation('!').startsWith('<!DOCTYPE html>'));
    // the empty places are tab stops in order
    assert.equal(expandAbbreviation('p+p'), '<p>${1}</p>\n<p>${2}</p>');
});

test('emmet: words that are not HTML tags and broken abbreviations do not expand', () => {
    assert.equal(expandAbbreviation('hello'), null);
    assert.equal(expandAbbreviation('div>'), null);
    assert.equal(expandAbbreviation('a[href'), null);
    assert.equal(expandAbbreviation('{{'), null);
    assert.equal(expandAbbreviation(''), null);
});

test('emmet: the abbreviation before the cursor stops at a space outside brackets and at an HTML tag', () => {
    assert.equal(abbreviationBefore('    ul>li*3'), 'ul>li*3');
    assert.equal(abbreviationBefore('x a[title="a b"]'), 'a[title="a b"]');
    assert.equal(abbreviationBefore('<div>p.x'), 'p.x');
    assert.equal(abbreviationBefore('p{some text}'), 'p{some text}');
});

test('file templates: the first fitting pattern, with its variables filled in', () => {
    assert.equal(newFileText('test_shapes.py', DEFAULT_FILE_TEMPLATES), 'import pytest\n\n\ndef test_shapes():\n    pass\n');
    assert.equal(newFileText('main.py', DEFAULT_FILE_TEMPLATES), '');
    assert.ok(newFileText('index.html', DEFAULT_FILE_TEMPLATES).includes('<title>index</title>'));
    const own = [{ name: 'Header', pattern: '*.py', body: '# ${FILE_NAME} by ${USER}, ${YEAR} ${UNKNOWN}\n' }];
    assert.equal(newFileText('a.py', own, { USER: 'me', YEAR: 2026 }), '# a.py by me, 2026 ${UNKNOWN}\n');
    assert.equal(newFileText('notes.txt', own), '');
    assert.equal(readFileTemplates('not json'), DEFAULT_FILE_TEMPLATES);
    assert.deepEqual(readFileTemplates(JSON.stringify(own)), own);
});

test('snippets: read and written in VS Code\'s form, lines kept, names made unique', () => {
    const list = readSnippets('{"Print": {"prefix": ["pr"], "body": ["print($1)", "$0"], "description": "a print"}}');
    assert.deepEqual(list, [{ name: 'Print', prefix: 'pr', description: 'a print', body: 'print($1)\n$0', scope: '' }]);
    // a template snippet keeps its scope; a Python one writes none
    assert.equal(readSnippets(writeSnippets([{ name: 'card', prefix: 'card', description: '', body: '<div>', scope: 'django-html' }]))[0].scope, 'django-html');
    assert.ok(!('scope' in JSON.parse(writeSnippets(list)).Print));
    const text = writeSnippets([...list, { name: 'Print', prefix: 'p2', description: '', body: 'x' }]);
    assert.deepEqual(Object.keys(JSON.parse(text)), ['Print', 'Print 2']);
    assert.deepEqual(readSnippets(text)[0], list[0]);
    assert.equal(readSnippets('{broken'), null);
    assert.deepEqual(readSnippets(''), []);
});

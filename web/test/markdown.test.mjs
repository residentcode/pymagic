// Checks of web/src/markdown-lite.js: `node web/test/markdown.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// web/package.json does not say "type": "module", so Node would read src/*.js as CommonJS; load the file as a module instead
const source = readFileSync(new URL('../src/markdown-lite.js', import.meta.url), 'utf8');
const { blocksOf, inlineOf, safeHref } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const types = (text) => blocksOf(text).map((b) => b.type);

test('headings, paragraphs, rules', () => {
    const b = blocksOf('# Title\n\nfirst line\nsecond line\n\n---\n\n### Third ###');
    assert.deepEqual(b.map((x) => x.type), ['heading', 'paragraph', 'rule', 'heading']);
    assert.equal(b[0].level, 1);
    assert.equal(b[3].level, 3);
    assert.deepEqual(b[3].inline, [{ t: 'text', text: 'Third' }]);
    assert.deepEqual(b[1].inline, [{ t: 'text', text: 'first line second line' }]);
});

test('fenced code keeps its text and language', () => {
    const [b] = blocksOf('```python\nx = 1\n\nprint(x)\n```');
    assert.deepEqual(b, { type: 'code', lang: 'python', text: 'x = 1\n\nprint(x)' });
    assert.deepEqual(types('~~~\nplain\n~~~\nafter'), ['code', 'paragraph']);
    assert.equal(blocksOf('```\nnever closed\nstill code')[0].text, 'never closed\nstill code');
});

test('lists: bullets, numbers, continuation lines', () => {
    const [bullets] = blocksOf('- one\n- two\n  more of two\n* three');
    assert.equal(bullets.ordered, false);
    assert.equal(bullets.items.length, 3);
    assert.equal(bullets.items[1].map((x) => x.text).join(''), 'two more of two');
    const [numbers] = blocksOf('1. a\n2) b');
    assert.equal(numbers.ordered, true);
    assert.equal(numbers.items.length, 2);
    assert.deepEqual(types('- a\n\n1. b'), ['list', 'list']);
});

test('quotes hold blocks', () => {
    const [q] = blocksOf('> # in quote\n> text');
    assert.equal(q.type, 'quote');
    assert.deepEqual(q.blocks.map((x) => x.type), ['heading', 'paragraph']);
});

test('inline: bold, italic, code, links', () => {
    assert.deepEqual(inlineOf('a **b** *c* _d_ `e` [f](https://x.org/y)'), [
        { t: 'text', text: 'a ' }, { t: 'bold', text: 'b' }, { t: 'text', text: ' ' }, { t: 'italic', text: 'c' }, { t: 'text', text: ' ' },
        { t: 'italic', text: 'd' }, { t: 'text', text: ' ' }, { t: 'code', text: 'e' }, { t: 'text', text: ' ' }, { t: 'link', text: 'f', href: 'https://x.org/y' },
    ]);
});

test('inline: underscores inside words and escapes stay text', () => {
    assert.deepEqual(inlineOf('snake_case_name'), [{ t: 'text', text: 'snake_case_name' }]);
    assert.deepEqual(inlineOf('2 * 3 * 4'), [{ t: 'text', text: '2 * 3 * 4' }]);
    assert.deepEqual(inlineOf('\\*not italic\\*'), [{ t: 'text', text: '*not italic*' }]);
    assert.deepEqual(inlineOf('`a*b*c`'), [{ t: 'code', text: 'a*b*c' }]);
});

test('images', () => {
    assert.deepEqual(inlineOf('![logo](pics/a.png)'), [{ t: 'image', text: 'logo', href: 'pics/a.png' }]);
});

test('links to scripts are not links', () => {
    assert.ok(inlineOf('[click](javascript:alert(1))').every((x) => x.t === 'text' && !x.href));
    assert.equal(inlineOf('[click](javascript:alert(1))')[0].text, 'click');
    assert.deepEqual(inlineOf('[x](data:text/html,hi)'), [{ t: 'text', text: 'x' }]);
    assert.ok(safeHref('https://a.b') && safeHref('mailto:a@b.c') && safeHref('docs/readme.md') && safeHref('#section'));
    assert.ok(!safeHref('JavaScript:1') && !safeHref(' vbscript:x'));
});

test('markup in the text is just text', () => {
    const [p] = blocksOf('<script>alert(1)</script> and <b>bold</b>');
    assert.equal(p.inline.map((x) => x.text).join(''), '<script>alert(1)</script> and <b>bold</b>');
    assert.ok(p.inline.every((x) => x.t === 'text'));
});

test('an empty cell has no blocks', () => {
    assert.deepEqual(blocksOf(''), []);
    assert.deepEqual(blocksOf('\n\n  \n'), []);
});

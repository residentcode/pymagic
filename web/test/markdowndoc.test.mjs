// Checks of web/src/markdown-doc.js: `node web/test/markdowndoc.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { blocksDoc, inlineDoc, headingsOf, plain, slugOf, markdownSymbols, formatTables } from '../src/markdown-doc.js';

const shape = (blocks) => blocks.map((b) => `${b.type}@${b.line}`);

test('inline: emphasis, strike, code, links, images, autolinks, breaks', () => {
    assert.deepEqual(inlineDoc('a **b *c* d** ~~e~~ `f`').map((n) => n.t), ['text', 'bold', 'text', 'strike', 'text', 'code']);
    assert.equal(plain(inlineDoc('a **b *c* d** ~~e~~ `f`')), 'a b c d e f');
    assert.deepEqual(inlineDoc('[the *docs*](https://x.org/a "T")'), [{ t: 'link', href: 'https://x.org/a', title: 'T', children: [{ t: 'text', text: 'the ' }, { t: 'italic', children: [{ t: 'text', text: 'docs' }] }] }]);
    assert.deepEqual(inlineDoc('![alt](img/a.png)'), [{ t: 'image', text: 'alt', href: 'img/a.png', title: undefined }]);
    assert.deepEqual(inlineDoc('<https://x.org> and https://y.org/z.').map((n) => n.t === 'link' ? n.href : n.text), ['https://x.org', ' and ', 'https://y.org/z', '.']);
    assert.deepEqual(inlineDoc('line one  \nline two').map((n) => n.t), ['text', 'br', 'text']);
    assert.deepEqual(inlineDoc('snake_case_word and 2*3*4'), [{ t: 'text', text: 'snake_case_word and 2' }, { t: 'italic', children: [{ t: 'text', text: '3' }] }, { t: 'text', text: '4' }]);
    assert.equal(plain(inlineDoc('\\*not\\* emphasis')), '*not* emphasis');
    // a link to javascript: is only its text
    assert.ok(!inlineDoc('[x](javascript:alert(1))').some((n) => n.t === 'link'));
});

test('blocks with their lines', () => {
    const text = '# Title\n\nA paragraph\nover two lines.\n\n---\n\n```py\ncode\n```\n\n> quoted\n> text\n\nSetext\n======\n\n    indented\n';
    const blocks = blocksDoc(text);
    assert.deepEqual(shape(blocks), ['heading@1', 'paragraph@3', 'rule@6', 'code@8', 'quote@12', 'heading@15', 'code@18']);
    assert.equal(blocks[3].lang, 'py');
    assert.equal(blocks[3].text, 'code');
    assert.equal(plain(blocks[1].inline), 'A paragraph over two lines.');
    assert.equal(blocks[5].level, 1);
    assert.deepEqual(shape(blocks[4].blocks), ['paragraph@12']);
});

test('lists: nesting, ordered, task boxes, paragraphs in an item', () => {
    const text = '- one\n- two\n  - nested a\n  - nested b\n- three\n\n1. first\n2. second\n\n- [ ] todo\n- [x] done\n';
    const [bullets, ordered, tasks] = blocksDoc(text);
    assert.equal(bullets.type, 'list');
    assert.equal(bullets.items.length, 3);
    assert.deepEqual(bullets.items[1].blocks.map((b) => b.type), ['paragraph', 'list']);
    assert.equal(bullets.items[1].blocks[1].items.length, 2);
    assert.equal(ordered.ordered, true);
    assert.equal(ordered.start, 1);
    assert.deepEqual(tasks.items.map((i) => i.task), [false, true]);
    assert.equal(plain(tasks.items[0].blocks[0].inline), 'todo');
    // a second paragraph in an item
    const loose = blocksDoc('- a\n\n  more of a\n- b\n')[0];
    assert.equal(loose.items.length, 2);
    assert.deepEqual(loose.items[0].blocks.map((b) => b.type), ['paragraph', 'paragraph']);
});

test('tables: header, alignment, rows, escaped pipes, and a lone pipe line is no table', () => {
    const [t] = blocksDoc('| Name | Qty | Note |\n|:-----|----:|:----:|\n| pen | 2 | a \\| b |\n| book | 12 | |\n');
    assert.equal(t.type, 'table');
    assert.deepEqual(t.align, ['left', 'right', 'center']);
    assert.deepEqual(t.head.map(plain), ['Name', 'Qty', 'Note']);
    assert.deepEqual(t.rows.map((r) => r.map(plain)), [['pen', '2', 'a | b'], ['book', '12', '']]);
    assert.deepEqual(blocksDoc('a | b\nnot a table\n').map((b) => b.type), ['paragraph']);
});

test('headings for the Structure view get GitHub anchors, repeats numbered, code ignored', () => {
    const text = '# Intro\n\n## Setup\n\n```\n# not a heading\n```\n\n## Setup\n\n### The `code` part!\n';
    assert.deepEqual(headingsOf(text).map((h) => [h.level, h.text, h.line, h.id]), [[1, 'Intro', 1, 'intro'], [2, 'Setup', 3, 'setup'], [2, 'Setup', 9, 'setup-1'], [3, 'The code part!', 11, 'the-code-part']]);
    assert.equal(slugOf('Hello, World & More'), 'hello-world--more');
});

test('the Structure view of a Markdown file: headings nested by level', () => {
    const symbols = markdownSymbols('# A\n\n## B\n\n### C\n\n## D\n\n# E\n');
    const show = (list) => list.map((s) => s.name + (s.children.length ? `(${show(s.children)})` : ''));
    assert.deepEqual(show(symbols), ['A(B(C),D)', 'E']);
    assert.equal(symbols[0].children[0].children[0].selectionRange.start.line, 4);
    // a deeper first heading is a root
    assert.deepEqual(show(markdownSymbols('### Deep\n\n# Top\n')), ['Deep', 'Top']);
});

test('tables are laid out with their alignment kept, code fences left alone', () => {
    const text = '| Name | Qty |  Note |\n|:--|--:|:-:|\n| pen | 2 | a \\| b |\n| notebook | 12 | |\n\n```\n| a | b |\n|--|--|\n```\n';
    const expected = '| Name     | Qty |  Note  |\n| :------- | --: | :----: |\n| pen      |   2 | a \\| b |\n| notebook |  12 |        |\n\n```\n| a | b |\n|--|--|\n```\n';
    assert.equal(formatTables(text), expected);
    // formatting what is formatted changes nothing; windows line breaks stay
    assert.equal(formatTables(expected), expected);
    assert.equal(formatTables('a | b\r\n-|-\r\n1 | 2\r\n'), '| a   | b   |\r\n| --- | --- |\r\n| 1   | 2   |\r\n');
    assert.equal(formatTables('no table here\n| just a pipe\n'), 'no table here\n| just a pipe\n');
});

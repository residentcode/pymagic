// Handlebars, EJS and Astro for the syntax check and the indentation marks (web/src/markup-kinds-model.js).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { handlebarsParts, handlebarsProblems, ejsParts, ejsProblems, astroText, astroProblems } from '../src/markup-kinds-model.js';
import { htmlIndentProblems, fixHtmlIndentation } from '../src/html-indent-model.js';

const said = (list) => list.map((p) => p.message);
const told = (text, kind) => htmlIndentProblems(text, kind).map((p) => `${p.line}: ${p.actual} -> ${p.expected}`);

test('Handlebars: the blocks, and the elements around them', () => {
    const text = '<ul>\n{{#each items as |item|}}\n  <li class="{{#if item.on}}on{{/if}}">{{item.name}} {{{item.html}}}</li>\n{{else}}\n  <li>none</li>\n{{/each}}\n</ul>\n{{> footer year=2026}}\n{{!-- <div> not read --}}\n{{! <b> }}\n';
    const { text: markup, blocks } = handlebarsParts(text);
    assert.equal(markup.length, text.length);
    assert.ok(!markup.includes('{{') && !markup.includes('<div>') && markup.includes('<li class="'));
    assert.deepEqual(blocks.map((b) => `${b.kind} ${b.name ?? (b.last ? 'last' : 'more')}`), ['open each', 'open if', 'close if', 'else last', 'close each']);
    assert.deepEqual(said(handlebarsProblems(text)), []);
    // each branch may open its own element, as long as each closes what it opened
    assert.deepEqual(said(handlebarsProblems('{{#if a}}\n<div class="a">\n{{else if b}}\n<div class="b">\n{{else}}\n<div>\n{{/if}}\n</div>\n')), []);
    // an element left open, a closing tag with nothing to close, Mustache's sections and a raw block
    assert.equal(handlebarsProblems('<section>{{name}}').length, 1);
    assert.match(said(handlebarsProblems('{{^items}}\n<p>none</p></span>\n{{/items}}\n'))[0], /<\/span>/);
    assert.deepEqual(said(handlebarsProblems('{{{{raw}}}}\n<div> {{not}} \n{{{{/raw}}}}\n<p>x</p>\n')), []);
    assert.deepEqual(said(handlebarsProblems('{{#> layout}}\n<p>x</p>\n{{/layout}}\n{{#*inline "row"}}<b>x</b>{{/inline}}\n')), []);
});

test('EJS: a scriptlet that leaves a brace open is a block', () => {
    const text = '<ul>\n<% items.forEach(function (item) { %>\n  <li><%= item.name %> <%- item.html %></li>\n<% }) %>\n</ul>\n<% if (user) { %>\n  <p>Hello</p>\n<% } else if (guest) { %>\n  <p>Guest</p>\n<% } else { %>\n  <p>?</p>\n<% } %>\n<%# <div> not read %>\n<%_ const s = "{"; _%>\n';
    const { text: markup, blocks } = ejsParts(text);
    assert.equal(markup.length, text.length);
    assert.ok(!markup.includes('<%') && !markup.includes('<div>'));
    assert.deepEqual(blocks.map((b) => b.kind + (b.kind === 'else' ? (b.last ? ' last' : ' more') : '')), ['open', 'close', 'open', 'else more', 'else last', 'close']);
    assert.deepEqual(said(ejsProblems(text)), []);
    assert.deepEqual(said(ejsProblems('<% if (a) { %>\n<div>\n<% } else { %>\n<div class="b">\n<% } %>\n</div>\n')), []);
    assert.equal(ejsProblems('<div>\n<% if (a) { %>\n<p>x</span>\n<% } %>\n</div>\n').length, 1);
    assert.equal(ejsProblems('<section>\n<p>text</p>\n').length, 1);
});

test('Astro: the front matter and the expressions go, an expression\'s elements stay', () => {
    const text = '---\nimport Card from "./Card.astro";\nconst items: Array<string> = ["a"];\nconst less = 1 < 2;\n---\n<ul class={items.length > 1 ? "many" : "one"}>\n  {items.map((item) => (\n    <li data-n={item.length}>{item} {twice<number>(2)} {1 < 2 && 3 > 2}</li>\n  ))}\n</ul>\n{show && <Card title="x" {...rest} />}\n<>\n  <p>a</p>\n</>\n<script>\n  const a = 1 < 2;\n</script>\n<style>\n  p > b { color: red }\n</style>\n';
    const markup = astroText(text);
    assert.equal(markup.length, text.length);
    assert.equal(markup.split('\n').length, text.split('\n').length);
    for (const gone of ['import Card', 'Array<string>', 'items.map', 'twice', '&&', '<>', '</>']) assert.ok(!markup.includes(gone), gone);
    for (const kept of ['<ul class=', '<li data-n=', '</li>', '<Card title="x"', '<p>a</p>', 'const a = 1 < 2;']) assert.ok(markup.includes(kept), kept);
    assert.equal(markup.indexOf('<li'), text.indexOf('<li'));
    assert.deepEqual(said(astroProblems(text)), []);
    // an element of an expression that is not closed is told; so is one of the page
    assert.match(said(astroProblems('<ul>\n  {items.map((i) => <li><b>{i}</li>)}\n</ul>\n'))[0], /<b>/);
    assert.equal(astroProblems('---\nconst a = "<div>";\n---\n<section>\n  <p>x</p>\n').length, 1);
    assert.deepEqual(said(astroProblems('---\nconst a = "<div>";\n---\n<section>\n  <p>x</p>\n</section>\n')), []);
});

test('indentation: the blocks of each, and Astro\'s elements', () => {
    assert.deepEqual(told('<ul>\n  {{#each items}}\n    <li>{{this}}</li>\n  {{/each}}\n</ul>\n', 'handlebars'), []);
    assert.deepEqual(told('<ul>\n  {{#each items}}\n    <li>{{this}}</li>\n    {{/each}}\n  <li>last</li>\n</ul>\n', 'handlebars'), ['4: 4 -> 2']);
    assert.deepEqual(told('<div>\n  {{#if a}}\n    <p>a</p>\n    {{else}}\n    <p>b</p>\n  {{/if}}\n</div>\n', 'handlebars'), ['4: 4 -> 2']);
    assert.deepEqual(told('<ul>\n  <% items.forEach((i) => { %>\n    <li><%= i %></li>\n  <% }) %>\n</ul>\n', 'ejs'), []);
    assert.deepEqual(told('<div>\n  <% if (a) { %>\n    <p>a</p>\n      <% } else { %>\n    <p>b</p>\n  <% } %>\n</div>\n', 'ejs'), ['4: 6 -> 2']);
    const astro = '---\nconst items = [];\n   const odd = 1;\n---\n<main>\n  <ul>\n    {items.map((i) => (\n          <li>{i}</li>\n    ))}\n  </ul>\n      <p>text</p>\n</main>\n';
    assert.deepEqual(told(astro, 'astro'), ['11: 6 -> 2']);
    assert.equal(fixHtmlIndentation(astro, 'astro'), astro.replace('      <p>text</p>', '  <p>text</p>'));
    // a file that does not balance is the syntax check's to tell
    assert.deepEqual(told('<div>\n{{#if a}}\n      <p>\n{{/if}}\n', 'handlebars'), []);
});

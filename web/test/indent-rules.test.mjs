// The indentation rules of each kind of file (web/src/indent-rules-model.js): what indents the next line, what moves a line left.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { INDENT_RULES, INDENT_OUTDENT } from '../src/indent-rules-model.js';

const up = (lang, line) => INDENT_RULES[lang].increaseIndentPattern.test(line);
const down = (lang, line) => INDENT_RULES[lang].decreaseIndentPattern.test(line);

test('code: an open { ( [ indents, a closing one at the start moves left', () => {
    for (const l of ['if (a) {', "app.get('/', (req, res) => {", 'const a = [', 'f(', 'class A {  // x']) assert.ok(up('javascript', l), l);
    for (const l of ['if (a) { b(); }', '// if (a) {', 'const s = "{";', 'f(a);']) assert.ok(!up('javascript', l), l);
    for (const l of ['}', '  });', ']', ')']) assert.ok(down('typescript', l), l);
    assert.ok(up('javascript', '    <div className="a">') && !up('javascript', '<div>x</div>') && !down('javascript', '  </div>'));
    assert.ok(up('css', 'a, b {') && !up('css', 'a { color: red; }') && down('scss', '}'));
    assert.ok(up('json', '"a": [') && up('json', '{') && !up('json', '"a": [1, 2],') && down('json', '],'));
});

test('HTML and Svelte: an element left open indents; void elements and closed ones do not', async () => {
    for (const l of ['<div>', '<ul class="a">', '<template>', '{#if a}', '{:else}', '{#each xs as x}']) assert.ok(up('html', l), l);
    for (const l of ['<div>x</div>', '<img src="a">', '<br/>', '<input>', '<!DOCTYPE html>', '<html>']) assert.ok(!up('html', l), l);
    for (const l of ['</div>', '  </template>']) assert.ok(!down('html', l), l);   // moved once typed (closingTag)
    assert.deepEqual((await import('../src/indent-rules-model.js')).closingTag('    </div>'), { name: 6, end: 10 });
});

test("templates: each engine's block tags", () => {
    for (const l of ['{% if a %}', '{%- for x in xs -%}', '{% block content %}', '<li>{% if a %}', '{% else %}', '{% with a=b %}']) assert.ok(up('django-html', l), l);
    for (const l of ['{% if a %}x{% endif %}', '{% set x = 1 %}', '{% load static %}', '{% trans "x" %}', '{{ x }}']) assert.ok(!up('django-html', l), l);
    for (const l of ['{% endif %}', '  {%- endfor %}', '{% else %}', '{% elif b %}', '{% empty %}']) assert.ok(down('django-html', l), l);
    assert.ok(up('twig', '{% if a %}') && up('liquid', '{% case x %}') && down('liquid', '{% when 1 %}') && up('liquid', '{% when 1 %}'));
    for (const l of ['{{#if a}}', '{{#each xs}}', '{{^a}}', '{{else}}', '{{#a}}']) assert.ok(up('handlebars', l), l);
    for (const l of ['{{#if a}}x{{/if}}', '{{x}}', '{{> partial}}']) assert.ok(!up('handlebars', l), l);
    for (const l of ['{{/if}}', '{{else}}']) assert.ok(!down('handlebars', l), l);   // moved once typed (CLOSINGS)
    assert.ok(!down('handlebars', '{{^a}}'));
    for (const l of ['@if ($a)', '@foreach ($xs as $x)', "@section('content')", '@php', '@else', '@auth']) assert.ok(up('blade', l), l);
    for (const l of ["@section('title', 'Home')", '@php($x = 1)', '@if ($a) x @endif', '@csrf', '{{ $a }}']) assert.ok(!up('blade', l), l);
    for (const l of ['@endif', '@endforeach', '@else', '@endsection', '@show']) assert.ok(down('blade', l), l);
    for (const l of ['<% if (a) { %>', '<% xs.forEach(function (x) { %>', '<%_ } else { _%>']) assert.ok(up('ejs', l), l);
    for (const l of ['<% if (a) { %>x<% } %>', '<%= x %>']) assert.ok(!up('ejs', l), l);
    for (const l of ['<% } %>', '  <%_ }) _%>']) assert.ok(down('ejs', l), l);
    assert.ok(up('razor', '@if (a) {') && down('razor', '}'));
});

test('Enter between an opening and its closing', () => {
    const between = (lang, before, after) => INDENT_OUTDENT[lang].some((r) => r.beforeText.test(before) && r.afterText.test(after));
    assert.ok(between('django-html', '{% if a %}', '{% endif %}'));
    assert.ok(between('handlebars', '{{#if a}}', '{{/if}}'));
    assert.ok(between('blade', '@if ($a)', '@endif'));
    assert.ok(between('ejs', '<% if (a) { %>', '<% } %>'));
    assert.ok(between('html', '{#if a}', '{/if}'));
});

test("closings moved once typed: EJS, Handlebars, Svelte, nested and between branches", async () => {
    const { closingIndent } = await import('../src/indent-rules-model.js');
    const at = (lang, text, line) => closingIndent(lang, text.split('\n'), line);
    assert.deepEqual(at('ejs', '  <% if (a) { %>\n      <p>x</p>\n      <% } %>', 2), { end: 13, indent: '  ' });
    assert.equal(at('ejs', '<% if (a) { %>\n  <% if (b) { %>\n  <% } %>\n    <% } %>', 3).indent, '');
    assert.equal(at('ejs', '<% if (a) { %>\n    <% } else { %>', 1).indent, '');
    assert.equal(at('ejs', '<% if (a) { %>\n    <% }', 1), null);   // not complete yet
    assert.equal(at('handlebars', '{{#each xs}}\n  {{#if a}}\n  {{/if}}\n    {{/each}}', 3).indent, '');
    assert.equal(at('handlebars', '  {{#if a}}\n      {{else}}', 1).indent, '  ');
    assert.equal(at('handlebars', '{{x}}\n    {{/if}}', 1).indent, null);
    assert.equal(at('html', '<div>\n  {#if a}\n    x\n      {/if}', 3).indent, '  ');
    assert.equal(at('html', '<div>\n    </div>', 1), null);
});

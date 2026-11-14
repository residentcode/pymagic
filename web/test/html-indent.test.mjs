// Indentation marks of markup (web/src/html-indent-model.js): HTML and templates, Blade, Vue and Svelte.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { htmlIndentProblems, fixHtmlIndentation, markupKind } from '../src/html-indent-model.js';

const told = (text, kind = 'django') => htmlIndentProblems(text, kind).map((p) => `${p.line}: ${p.actual} -> ${p.expected}`);

test('which files', () => {
    assert.equal(markupKind('django-html', '/a/page.html'), 'django');
    assert.equal(markupKind('twig', '/a/page.njk'), 'django');
    assert.equal(markupKind('blade', '/a/x.blade.php'), 'blade');
    assert.equal(markupKind('html', '/a/Card.vue'), 'vue');
    assert.equal(markupKind('html', '/a/List.svelte'), 'svelte');
    assert.equal(markupKind('html', '/a/page.astro'), null);
    assert.equal(markupKind('javascript', '/a/a.js'), null);
});

test('a page as it should be: one step in, or level with the opening line; text, scripts and long tags left alone', () => {
    const page = '<!DOCTYPE html>\n<html lang="en">\n<head>\n    <meta charset="utf-8">\n    <title>Shop</title>\n    <style>\n  p { color: red }\n    </style>\n</head>\n<body>\n<main>\n    <ul>\n        <li>one</li>\n        <li>two\n        <li>three\n    </ul>\n'
        + '    <p>Some text that goes\n  on in its own way\n    </p>\n    <img src="a.png"\n            alt="a picture">\n    <!-- a note\n  over lines -->\n    <pre>\n as it is\n    </pre>\n    <table>\n        <tr><td>a</td>\n            <td>b</td></tr>\n    </table>\n</main>\n<script>\nif (a) {\n  b();\n}\n</script>\n</body>\n</html>\n';
    assert.deepEqual(told(page), []);
});

test('a closing tag not under its opening one, a line not with the others of its element', () => {
    assert.deepEqual(told('<div>\n    <p>a</p>\n  <p>b</p>\n    <p>c</p>\n</div>\n'), ['3: 2 -> 4']);
    assert.deepEqual(told('<ul>\n    <li>a</li>\n    <li>b</li>\n  </ul>\n'), ['4: 2 -> 0']);
    assert.deepEqual(told('<div>\n    <section>\n  <p>a</p>\n    </section>\n</div>\n'), ['3: 2 -> 8']);
    // the step is the file's own
    assert.deepEqual(told('<div>\n  <section>\n    <p>a</p>\n    <p>b</p>\n  </section>\n  <footer>\n <p>c</p>\n  </footer>\n</div>\n'), ['7: 1 -> 4']);
    // tabs
    assert.deepEqual(told('<div>\n\t<p>a</p>\n<p>b</p>\n\t<p>c</p>\n</div>\n'), ['3: 0 -> 1']);
    assert.match(htmlIndentProblems('<div>\n\t<p>a</p>\n<p>b</p>\n\t<p>c</p>\n</div>\n', 'django')[0].message, /^Indentation is 0 tabs, expected 1 tab \(Fix indentation: Alt\+Enter\)$/);
    // the file's own lines: all where the first is
    assert.deepEqual(told('<header>\n</header>\n  <main>\n  </main>\n<footer>\n</footer>\n'), ['3: 2 -> 0']);   // (its closing tag is under it: moved with it by the fix)
});

test('a template\'s blocks: {% if %} … {% else %} … {% endif %} as elements are', () => {
    const ok = '{% extends "base.html" %}\n{% load static %}\n\n{% block content %}\n<div class="list">\n    {% for item in items %}\n        <p>{{ item }}</p>\n    {% empty %}\n        <p>none</p>\n    {% endfor %}\n    {% if more %}\n        <a href="{% url \'more\' %}">more</a>\n    {% endif %}\n</div>\n{% endblock %}\n';
    assert.deepEqual(told(ok), []);
    assert.deepEqual(told(ok.replace('    {% empty %}', '  {% empty %}')), ['8: 2 -> 4']);
    assert.deepEqual(told(ok.replace('    {% endif %}', '        {% endif %}')), ['13: 8 -> 4']);
    assert.deepEqual(told(ok.replace('        <p>none</p>', '    <p>none</p>')), ['9: 4 -> 8']);   // the lines of every branch of a block are at one place
    assert.deepEqual(told(ok.replace('        <p>none</p>', '  <p>none</p>')), ['9: 2 -> 8']);
    // an element opened under one condition and closed under another: the file is left alone
    assert.deepEqual(told('{% if box %}<div>{% endif %}\n    <p>x</p>\n  <p>y</p>\n{% if box %}</div>{% endif %}\n'), []);
    // a file that does not balance is the syntax check's
    assert.deepEqual(told('<div>\n    <p>a</p>\n  <p>b</p>\n'), []);
});

test('a file with a layout of its own is not told line by line', () => {
    const own = '<table>\n<tr>\n        <td>a</td>\n   <td>b</td>\n     <td>c</td>\n <td>d</td>\n      <td>e</td>\n</tr>\n</table>\n';
    assert.deepEqual(told(own), []);
});

test('Blade, Vue and Svelte', () => {
    const blade = "@extends('layouts.app')\n\n@section('content')\n    @if ($items->count() > 0)\n        <ul>\n            @foreach ($items as $item)\n                <li>{{ $item->name }}</li>\n            @endforeach\n        </ul>\n    @else\n        <p>none</p>\n    @endif\n@endsection\n";
    assert.deepEqual(told(blade, 'blade'), []);
    assert.deepEqual(told(blade.replace('    @else', '@else'), 'blade'), ['10: 0 -> 4']);
    assert.deepEqual(told(blade.replace('            @endforeach', '        @endforeach'), 'blade'), ['8: 8 -> 12']);
    const vue = '<script setup>\nconst a = 1;\n  const b = 2;\n</script>\n\n<template>\n  <div class="card">\n    <p v-if="a < b">{{ a }}</p>\n    <MyItem />\n  </div>\n</template>\n';
    assert.deepEqual(told(vue, 'vue'), []);
    assert.deepEqual(told(vue.replace('    <MyItem />', '      <MyItem />'), 'vue'), ['9: 6 -> 4']);
    const svelte = '<script>\n  let items = [];\n</script>\n\n{#if items.length > 0}\n  <ul>\n    {#each items as item}\n      <li>{item}</li>\n    {/each}\n  </ul>\n{:else}\n  <p>none</p>\n{/if}\n';
    assert.deepEqual(told(svelte, 'svelte'), []);
    assert.deepEqual(told(svelte.replace('    {/each}', '  {/each}'), 'svelte'), ['9: 2 -> 4']);
    assert.deepEqual(told(svelte.replace('{:else}', '  {:else}'), 'svelte'), ['11: 2 -> 0']);
});

test('fixing moves a line with what it holds, and nothing else', () => {
    const bad = '<div>\n    <p>a</p>\n  <section>\n    <p>b\n       text</p>\n    <!-- note -->\n  </section>\n    <p>c</p>\n  </div>\n';
    assert.deepEqual(told(bad), ['3: 2 -> 4', '9: 2 -> 0']);
    const fixed = fixHtmlIndentation(bad, 'django');
    assert.equal(fixed, '<div>\n    <p>a</p>\n    <section>\n      <p>b\n         text</p>\n      <!-- note -->\n    </section>\n    <p>c</p>\n</div>\n');
    assert.deepEqual(told(fixed), []);
    assert.equal(fixHtmlIndentation(fixed, 'django'), fixed);
    // Windows line ends kept
    assert.equal(fixHtmlIndentation('<ul>\r\n    <li>a</li>\r\n  <li>b</li>\r\n</ul>\r\n', 'django'), '<ul>\r\n    <li>a</li>\r\n    <li>b</li>\r\n</ul>\r\n');
});

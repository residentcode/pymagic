// HTML's balance through a template's branches, and the markup of Vue and Svelte components and Blade views (web/src/htmlcheck-model.js).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { templateProblems, djangoBlocks, vueProblems, svelteProblems, bladeProblems } from '../src/htmlcheck-model.js';

const told = (find, text) => find(text).map((p) => `${text.slice(0, p.start).split('\n').length}: ${p.message}`);

test('a template with branches: each branch read with the elements open before it', () => {
    // every branch opens the same element: the rest of the file is balanced as ever
    const same = '{% if wide %}<div class="wide">{% else %}<div>{% endif %}\n<p>x</p>\n</div>\n<section>\n';
    assert.deepEqual(told(templateProblems, same), ['4: Element <section> is not closed: add </section>']);
    // a mistake inside one branch
    assert.deepEqual(told(templateProblems, '<ul>\n{% for i in items %}\n<li><b>{{ i }}</li>\n{% empty %}\n<li>none</li>\n{% endfor %}\n</ul>\n'),
        ['3: Element <b> is not closed: add </b> before </li>']);
    assert.deepEqual(told(templateProblems, '{% if a %}\n<p>x</span></p>\n{% elif b %}\n<p>y</p>\n{% else %}\n<p>z</p>\n{% endif %}\n'), ['2: Closing tag </span> matches nothing']);
    // a mistake before the branches, and one after branches that agree
    assert.deepEqual(told(templateProblems, '<div>\n<span>\n</div>\n{% if a %}<b>x</b>{% else %}<i>y</i>{% endif %}\n</em>\n'),
        ['2: Element <span> is not closed: add </span> before </div>', '5: Closing tag </em> matches nothing']);
    // an element opened under one condition and closed under another: no telling from there on, but what was sure before is told
    const split = '<p>a</b></p>\n{% if box %}<div class="box">{% endif %}\n<p>text</p>\n{% if box %}</div>{% endif %}\n<span>\n';
    assert.deepEqual(told(templateProblems, split), ['1: Closing tag </b> matches nothing']);
    assert.deepEqual(told(templateProblems, '{% for row in rows %}<tr>{% for c in row %}<td>{{ c }}</td>{% endfor %}</tr>{% endfor %}\n'), []);
    // blocks that are no branches, tags inside a tag, a comment block
    assert.deepEqual(told(templateProblems, '{% extends "base.html" %}\n{% load static %}\n{% block content %}\n{% with a=1 %}<div>{% endwith %}\n{% endblock %}\n'),
        ['4: Element <div> is not closed: add </div>']);
    assert.deepEqual(told(templateProblems, '<a {% if on %}class="on"{% else %}class="off"{% endif %} href="/">x</a>\n<div>\n'), ['2: Element <div> is not closed: add </div>']);
    assert.deepEqual(told(templateProblems, '{% comment %}\n<div>{% if x %}\n{% endcomment %}\n<p>x</p>\n'), []);
    assert.deepEqual(told(templateProblems, '{# {% if x %} #}<p>x</p>\n</div>\n'), ['2: Closing tag </div> matches nothing']);
    // Jinja: a loop's else, whitespace control
    assert.deepEqual(told(templateProblems, '<ul>\n{%- for i in items -%}\n<li>{{ i }}</li>\n{%- else -%}\n<li>none\n{%- endfor -%}\n</ul>\n</ol>\n'), ['8: Closing tag </ol> matches nothing']);
    assert.deepEqual(djangoBlocks('{% if a %}{% elif b %}{% else %}{% endif %}').map((b) => b.kind + (b.name ? ' ' + b.name : '') + (b.last ? ' last' : '')),
        ['open if', 'else', 'else last', 'close if']);
});

test('a Vue component: every element closed, {{ }} and the script left out', () => {
    const ok = '<script setup lang="ts">\nconst a = 1 < 2 ? "<div>" : "</p>";\n</script>\n\n<template>\n  <div class="card" :class="{ on: a > 1 }" @click="n => n > 0">\n    <MyItem v-for="i in items" :key="i" />\n'
        + '    <p v-if="a < b">{{ a < b ? "<" : ">" }}</p>\n    <img src="x.png">\n    <br>\n    <template #footer><slot /></template>\n  </div>\n</template>\n\n<style scoped>\n.card > p { color: red }\n</style>\n';
    assert.deepEqual(told(vueProblems, ok), []);
    assert.deepEqual(told(vueProblems, '<template>\n  <ul>\n    <li>one\n    <li>two</li>\n  </ul>\n</template>\n'), ['3: Element <li> is not closed: add </li> before </ul>']);
    assert.deepEqual(told(vueProblems, '<template>\n  <div>\n    <p>text\n  </div>\n</template>\n'), ['3: Element <p> is not closed: add </p> before </div>']);
    assert.deepEqual(told(vueProblems, '<template>\n  <MyCard>\n  <div\n  </MyCard>\n</template>\n'), ['3: Tag <div> is not ended: add >']);
    assert.deepEqual(told(vueProblems, '<template>\n  <span>a</b></span>\n</template>\n<script>\nexport default {}\n'),
        ['2: Closing tag </b> matches nothing', '4: Element <script> is not closed: add </script>']);
    assert.deepEqual(told(vueProblems, '<template lang="pug">\ndiv\n  p <b> text\n</template>\n'), []);
});

test('a Svelte component: its { } expressions left out, its blocks followed', () => {
    const ok = '<script>\n  let items = [];\n  const less = (a, b) => a < b;\n</script>\n\n<svelte:head><title>{title}</title></svelte:head>\n{#if items.length > 0}\n  <ul>\n    {#each items as item (item.id)}\n'
        + '      <li class:on={item.n > 1} on:click={() => pick(item, "<b>")}>{item.name}</li>\n    {:else}\n      <li>none</li>\n    {/each}\n  </ul>\n{:else if loading}\n  <p>…</p>\n{:else}\n  <p>{@html empty}</p>\n{/if}\n'
        + '{#await promise}\n  <p>wait</p>\n{:then value}\n  <p>{value}</p>\n{:catch error}\n  <p>{error.message}</p>\n{/await}\n<style>\n  li > b { color: red }\n</style>\n';
    assert.deepEqual(told(svelteProblems, ok), []);
    assert.deepEqual(told(svelteProblems, '{#if a}\n  <p>one\n{:else}\n  <p>two</p>\n{/if}\n'), ['2: Element <p> is not closed: add </p>']);
    assert.deepEqual(told(svelteProblems, '<div>\n  {#each rows as r}\n    <span>{r}</b></span>\n  {/each}\n</div>\n'), ['3: Closing tag </b> matches nothing']);
    assert.deepEqual(told(svelteProblems, '<ul>\n  <li>{a < b}\n</ul>\n'), ['2: Element <li> is not closed: add </li> before </ul>']);
    // a block holds whole elements (Svelte's own rule): one opened in a block and closed in another is told at both
    assert.deepEqual(told(svelteProblems, '{#if a}<div>{/if}\n<p>x</p>\n{#if a}</div>{/if}\n'), ['1: Element <div> is not closed: add </div>', '3: Closing tag </div> matches nothing']);
});

test('a Blade view: directives and echoes left out, the blocks of its directives followed', () => {
    const ok = "@extends('layouts.app')\n@section('title', 'Shop')\n\n@section('content')\n<div class=\"{{ $wide > 1 ? 'wide' : 'narrow' }}\" @class(['on' => $a > $b])>\n  @if ($items->count() > 0)\n    <ul>\n"
        + "      @foreach ($items as $item)\n        <li>{{ $item->name }} {!! $item->html !!}</li>\n      @endforeach\n    </ul>\n  @elseif ($loading)\n    <p>…</p>\n  @else\n    <p>none</p>\n  @endif\n"
        + "  @forelse ($rows as $row)\n    <p>{{ $row }}</p>\n  @empty\n    <p>empty</p>\n  @endforelse\n  {{-- <div> not open --}}\n  @php\n    $x = '<b>';\n  @endphp\n  <x-alert type=\"error\" :message=\"$message\" />\n"
        + "  <a href=\"mailto:me@example.com\">me@example.com</a>\n</div>\n@endsection\n";
    assert.deepEqual(told(bladeProblems, ok), []);
    assert.deepEqual(told(bladeProblems, "<div>\n@if ($a > 1)\n  <p>x</span></p>\n@endif\n</div>\n"), ['3: Closing tag </span> matches nothing']);
    assert.deepEqual(told(bladeProblems, "@foreach ($items as $item)\n  <li><b>{{ $item }}</li>\n@endforeach\n<section>\n"),
        ['2: Element <b> is not closed: add </b> before </li>', '4: Element <section> is not closed: add </section>']);
    assert.deepEqual(told(bladeProblems, "@if ($a)\n<div class=\"a\">\n@else\n<div>\n@endif\n<p>x</p>\n</div>\n</main>\n"), ['8: Closing tag </main> matches nothing']);
    // opened under one condition, closed under another: not told
    assert.deepEqual(told(bladeProblems, "@if ($box)<div>@endif\n<p>x</p>\n@if ($box)</div>@endif\n<span>\n"), []);
    assert.deepEqual(told(bladeProblems, "@switch($kind)\n  @case(1)\n    <b>one</b>\n    @break\n  @default\n    <i>other\n@endswitch\n"), []);   // (one branch leaves <i> open: which is meant is not known)
    assert.deepEqual(told(bladeProblems, '<p>hi\n<img src="a.png"\n<p>x</p>\n'), ['2: Tag <img> is not ended: add >']);
});

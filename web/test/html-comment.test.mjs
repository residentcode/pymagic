// Checks of web/src/html-comment-model.js: `node --test web/test/html-comment.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/html-comment-model.js', import.meta.url), 'utf8');
const { strayBlockComments, asTemplateComment } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const found = (text) => strayBlockComments(text).map(({ start, end }) => text.slice(start, end));

test('/* */ around tags of the HTML is found, as the user wrote it', () => {
    const page = [
        '<!-- Fonts -->',
        '/*<link rel="preconnect" href="https://fonts.bunny.net">',
        '<link href="https://fonts.bunny.net/css?family=figtree:400,600&display=swap" rel="stylesheet"/>*/',
        '<p>/* a note */ text</p>',
        '',
    ].join('\n');
    assert.deepEqual(found(page), [
        '/*<link rel="preconnect" href="https://fonts.bunny.net">\n<link href="https://fonts.bunny.net/css?family=figtree:400,600&display=swap" rel="stylesheet"/>*/',
        '/* a note */',
    ]);
});

test('/* */ where it is a comment or not HTML text is left alone', () => {
    const page = [
        '<script>/* js */ let a = 1; /* more */</script>',
        '<style>/* css */ p { color: red }</style>',
        '<a href="/files/*">x</a><img alt="*/">',
        '<!-- /* in an HTML comment */ -->',
        '{{-- /* in a Blade comment */ --}}',
        "{{ '/* in an echo */' }} {!! '/*' !!}",
        '@php /* PHP */ $a = 1; @endphp',
        '<?php /* PHP */ ?>',
        '/* not closed',
        '',
    ].join('\n');
    assert.deepEqual(found(page), []);
});

test('the fix: the template comment, with a space inside each end', () => {
    assert.equal(asTemplateComment('<link rel="x">', 'blade'), '{{-- <link rel="x"> --}}');
    assert.equal(asTemplateComment(' a note ', 'blade'), '{{-- a note --}}');
    assert.equal(asTemplateComment('<p>x</p>', 'html'), '<!-- <p>x</p> -->');
});

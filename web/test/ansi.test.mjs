// Checks of web/src/ansi.js: `node web/test/ansi.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// web/package.json does not say "type": "module", so Node would read src/*.js as CommonJS; load the file as a module instead
const source = readFileSync(new URL('../src/ansi.js', import.meta.url), 'utf8');
const { ansiReader, stripAnsi, cssOf, paletteColor } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const E = '\u001b';
const show = (pieces) => pieces.map((p) => `${p.text}:${cssOf(p.style).className}${cssOf(p.style).style ? '|' + cssOf(p.style).style : ''}`).join(' / ');

test('standard colours, bright colours, bold and the reset', () => {
    const r = ansiReader();
    assert.equal(show(r.feed(`${E}[31mred${E}[0m plain ${E}[1;92mok${E}[22m thin`)), 'red:ansi-fg1 /  plain : / ok:ansi-fg10 ansi-bold /  thin:ansi-fg10');
});

test('pytest-like output: green PASSED, red FAILED, the default colour back', () => {
    const r = ansiReader();
    assert.equal(show(r.feed(`a.py ${E}[32mPASSED${E}[0m\nb.py ${E}[31m${E}[1mFAILED${E}[39;22m done`)), 'a.py : / PASSED:ansi-fg2 / \nb.py : / FAILED:ansi-fg1 ansi-bold /  done:');
});

test('256 colours and 24-bit colours become inline colours', () => {
    const r = ansiReader();
    assert.equal(show(r.feed(`${E}[38;5;196mx${E}[48;2;10;20;30my`)), 'x:|color: rgb(255, 0, 0) / y:|color: rgb(255, 0, 0); background-color: rgb(10, 20, 30)');
    assert.equal(paletteColor(3), 3);
    assert.equal(paletteColor(244), 'rgb(128, 128, 128)');
});

test('a sequence cut between two pieces is put together, and the style carries over', () => {
    const r = ansiReader();
    assert.equal(show(r.feed(`one ${E}[3`)), 'one :');
    assert.equal(show(r.feed(`4mblue`)), 'blue:ansi-fg4');
    assert.equal(show(r.feed(` still`)), ' still:ansi-fg4');
});

test('cursor moves, erasing and window titles are dropped', () => {
    const r = ansiReader();
    assert.equal(show(r.feed(`${E}[2K${E}[1Gprogress${E}]0;title${'\u0007'} done`)), 'progress done:');
    assert.equal(stripAnsi(`${E}[1;31merror${E}[0m: ${E}]0;t${'\u0007'}x`), 'error: x');
});

test('inverse swaps the colours', () => {
    const r = ansiReader();
    assert.equal(show(r.feed(`${E}[7mrev`)), 'rev:ansi-fg-default-bg ansi-bg-default-fg');
});

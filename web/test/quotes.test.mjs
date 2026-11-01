// Checks of web/src/quotes-model.js: `node --test web/test/quotes.test.mjs`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/quotes-model.js', import.meta.url), 'utf8');
const { requote } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

test('a quote typed over a string changes its quotes instead of wrapping it', () => {
    assert.equal(requote('"/shop/about/"', "'"), "'/shop/about/'");
    assert.equal(requote("'pen'", '"'), '"pen"');
    assert.equal(requote('""', "'"), "''");
});

test('escapes follow the new quotes', () => {
    assert.equal(requote('"it\'s"', "'"), "'it\\'s'");
    assert.equal(requote("'say \\'hi\\''", '"'), '"say \'hi\'"');
    assert.equal(requote('"a\\nb \\" c"', "'"), "'a\\nb \" c'");
});

test('prefixes and triple quotes', () => {
    assert.equal(requote('f"{x}!"', "'"), "f'{x}!'");
    assert.equal(requote('rb"\\d+"', "'"), "rb'\\d+'");
    assert.equal(requote('"""doc\nmore"""', "'"), "'''doc\nmore'''");
    assert.equal(requote('f"{d[\'k\']}"', "'"), null);   // the new quote inside an f-string: left alone
    assert.equal(requote('r"it\'s"', "'"), null);
});

test('what is not one string, or already has that quote, is wrapped as before', () => {
    assert.equal(requote('"a" + "b"', "'"), null);
    assert.equal(requote("'pen'", "'"), null);
    assert.equal(requote('pen', "'"), null);
    assert.equal(requote('"open', "'"), null);
    assert.equal(requote('"a\\"', "'"), null);
    assert.equal(requote('"x"', '`'), null);
});

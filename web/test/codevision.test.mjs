// Checks of web/src/codevision-model.js: `node web/test/codevision.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/codevision-model.js', import.meta.url), 'utf8');
const { lensSymbols, usagesText, inheritorsText, mainAuthor } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

test('lensSymbols: classes, methods and functions, nested ones too, not variables', () => {
    const symbols = [
        { name: 'Shape', kind: 5, children: [{ name: 'area', kind: 6 }, { name: 'sides', kind: 13 }] },
        { name: 'move', kind: 12 }, { name: 'X', kind: 14 },
    ];
    assert.deepEqual(lensSymbols(symbols).map((s) => s.name), ['Shape', 'area', 'move']);
});

test('usagesText and inheritorsText', () => {
    assert.equal(usagesText(0), 'no usages');
    assert.equal(usagesText(1), '1 usage');
    assert.equal(usagesText(4), '4 usages');
    assert.equal(inheritorsText(1), '1 inheritor');
    assert.equal(inheritorsText(3), '3 inheritors');
});

test('mainAuthor: who wrote most lines of the range, with how many others; nothing for uncommitted lines', () => {
    const z = '0'.repeat(40);
    const blame = [{ hash: 'a1', author: 'Ann' }, { hash: 'b2', author: 'Bob' }, { hash: 'b2', author: 'Bob' }, { hash: z, author: 'Not Committed Yet' }, { hash: 'c3', author: 'Cy' }];
    assert.equal(mainAuthor(blame, 0, 3), 'Bob +1');
    assert.equal(mainAuthor(blame, 1, 2), 'Bob');
    assert.equal(mainAuthor(blame, 3, 3), null);
    assert.equal(mainAuthor([], 0, 5), null);
});

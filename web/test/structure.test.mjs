import test from 'node:test';
import assert from 'node:assert/strict';
import { pathAt } from '../src/structure.js';

const at = (line, character = 0) => ({ line, character });
const sym = (name, from, to, children) => ({ name, range: { start: at(from), end: at(to) }, selectionRange: { start: at(from) }, ...(children ? { children } : {}) });

const tree = [
    sym('Cart', 3, 20, [sym('__init__', 4, 7), sym('add', 9, 12), sym('total', 14, 20)]),
    sym('helper', 23, 30),
];

test('the symbols that contain a line, from the outermost to the innermost', () => {
    assert.deepEqual(pathAt(tree, 10).map((s) => s.name), ['Cart', 'add']);
    assert.deepEqual(pathAt(tree, 25).map((s) => s.name), ['helper']);
});

test('a line in a class but in none of its methods is the class alone', () => {
    assert.deepEqual(pathAt(tree, 8).map((s) => s.name), ['Cart']);
    assert.deepEqual(pathAt(tree, 3).map((s) => s.name), ['Cart']);
});

test('a line outside every symbol is in none', () => {
    assert.deepEqual(pathAt(tree, 1), []);
    assert.deepEqual(pathAt(tree, 22), []);
    assert.deepEqual(pathAt([], 5), []);
});

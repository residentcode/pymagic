import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const { remember, existing, MAX } = require(resolve(dirname(fileURLToPath(import.meta.url)), '..', 'recent-projects.js'));

test('the folder just opened comes first, once, whatever its case or closing slash', () => {
  assert.deepEqual(remember(['C:/a', 'C:/b'], 'C:/c'), ['C:/c', 'C:/a', 'C:/b']);
  assert.deepEqual(remember(['C:/a', 'C:/b'], 'c:/B/'), ['c:/B/', 'C:/a']);
  assert.deepEqual(remember(['C:\\a', 'C:\\b'], 'c:\\B\\'), ['c:\\B\\', 'C:\\a']);
  assert.deepEqual(remember(undefined, 'C:/a'), ['C:/a']);
  assert.deepEqual(remember([1, null, 'C:/a'], 'C:/b'), ['C:/b', 'C:/a']);
});

test('only the latest ones are kept', () => {
  let list = [];
  for (let i = 0; i < MAX + 5; i++) list = remember(list, `C:/p${i}`);
  assert.equal(list.length, MAX);
  assert.equal(list[0], `C:/p${MAX + 4}`);
});

test('the list shown has the folders that are still there, without the open one', () => {
  const there = new Set(['C:/a', 'C:/c']);
  assert.deepEqual(existing(['C:/a', 'C:/b', 'C:/c'], (p) => there.has(p), 'c:/C'), ['C:/a']);
  assert.deepEqual(existing(undefined, () => true), []);
});

test('a project removed from the list, and the list cleared but for the projects open in a window', () => {
  const { forget } = require(resolve(dirname(fileURLToPath(import.meta.url)), '..', 'recent-projects.js'));
  assert.deepEqual(forget(['C:/a', 'C:/b', 'C:/c'], 'c:/B/'), ['C:/a', 'C:/c']);
  assert.deepEqual(forget(['C:/a', 'C:/b', 'C:/c'], null, ['c:/c', undefined]), ['C:/c']);
  assert.deepEqual(forget(['C:/a', 7], null), []);
  assert.deepEqual(forget(undefined, 'C:/a'), []);
});

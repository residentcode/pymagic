import test from 'node:test';
import assert from 'node:assert/strict';
import { rememberSearch, multilinePattern, filterSymbols } from '../src/search-model.js';

test('a search goes first in the recent list, once, and the list stays short', () => {
    assert.deepEqual(rememberSearch(['b', 'a'], 'a'), ['a', 'b']);
    assert.deepEqual(rememberSearch(['b'], '  '), ['b']);
    assert.equal(rememberSearch(Array.from({ length: 20 }, (_, i) => String(i)), 'new').length, 15);
});

test('pasted lines become a pattern that finds them as written', () => {
    const p = multilinePattern('if x.y(1):\r\n    pass\n');
    assert.equal(p, 'if x\\.y\\(1\\):\\r?\\n    pass');
    const re = new RegExp(p);
    assert.ok(re.test('if x.y(1):\r\n    pass'));
    assert.ok(re.test('if x.y(1):\n    pass'));
    assert.ok(!re.test('if xay(1):\n    pass'));
});

test('symbols are filtered by kind', () => {
    const all = [{ name: 'C', kind: 5 }, { name: 'f', kind: 12 }, { name: 'm', kind: 6 }, { name: 'x', kind: 13 }];
    assert.deepEqual(filterSymbols(all, 'Classes').map((s) => s.name), ['C']);
    assert.deepEqual(filterSymbols(all, 'Methods').map((s) => s.name), ['m']);
    assert.equal(filterSymbols(all, 'All').length, 4);
});

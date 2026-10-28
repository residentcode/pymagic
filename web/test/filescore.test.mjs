import test from 'node:test';
import assert from 'node:assert/strict';
import { fileScore } from '../src/filescore.js';

test('a file named as typed comes first, then one that starts with it, then one that contains it', () => {
    assert.equal(fileScore('shop/cart.py', 'cart.py'), 0);
    assert.equal(fileScore('shop/cart_utils.py', 'cart'), 1);
    const inside = fileScore('tests/test_cart.py', 'cart');
    assert.ok(inside > 2 && inside < 3, 'contained in the name');
    assert.equal(fileScore('shop/models.py', 'cart'), -1);
});

test('the path counts after the name, and the letters in order are the last resort', () => {
    const inPath = fileScore('shop/models.py', 'shop');
    assert.ok(inPath >= 3 && inPath < 4, 'only the folder has it');
    assert.equal(fileScore('config_loader.py', 'cfgl'), 5);
    assert.equal(fileScore('config_loader.py', 'xyz'), -1);
});

test('case does not matter, and an empty query shows every file', () => {
    assert.equal(fileScore('Shop/Cart.py', 'CART.PY'), 0);
    assert.equal(fileScore('anything.py', ''), 10);
});

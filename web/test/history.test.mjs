import test from 'node:test';
import assert from 'node:assert/strict';
import { ago, clock, size } from '../src/history-model.js';

const at = (y, mo, d, h = 0, mi = 0, s = 0) => new Date(y, mo - 1, d, h, mi, s).getTime();

test('how long ago, in words', () => {
    const now = at(2026, 5, 10, 12, 0, 0);
    assert.equal(ago(now, now - 5_000), 'just now');
    assert.equal(ago(now, now - 60_000), 'a minute ago');
    assert.equal(ago(now, now - 7 * 60_000), '7 minutes ago');
    assert.equal(ago(now, now - 60 * 60_000), 'an hour ago');
    assert.equal(ago(now, now - 5 * 3600_000), '5 hours ago');
    assert.equal(ago(now, now - 26 * 3600_000), 'yesterday');
    assert.equal(ago(now, now - 5 * 86_400_000), '5 days ago');
    assert.equal(ago(now, now + 10_000), 'just now');   // a clock a little ahead is not the future
});

test('the time of day, with the day when it is not today', () => {
    const now = at(2026, 5, 10, 12, 0, 0);
    assert.equal(clock(now, at(2026, 5, 10, 9, 5, 7)), '09:05:07');
    assert.equal(clock(now, at(2026, 5, 9, 23, 59, 1)), 'yesterday 23:59:01');
    assert.equal(clock(now, at(2026, 3, 3, 18, 2, 44)), '3 Mar 18:02:44');
    assert.equal(clock(now, at(2025, 12, 31, 8, 0, 0)), '31 Dec 2025 08:00:00');
});

test('sizes', () => {
    assert.equal(size(812), '812 bytes');
    assert.equal(size(3500), '3.4 KB');
});

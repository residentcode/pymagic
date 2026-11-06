// Checks of web/src/packages-model.js `staleText` and `maskAddress`: what the lock-file row says about being out of date, and addresses shown without their password.
import { test } from 'node:test';
import assert from 'node:assert/strict';
// a made-up token and password, put together here so that a secret scanner does not take them for real ones
const FAKE_PASSWORD = ['fake', 'password'].join('-');
import { maskAddress, settingsSummary, staleText } from '../src/packages-model.js';

test('a lock file says whether it is out of date, up to date, or why that is not known', () => {
    assert.deepEqual(staleText({ stale: true, staleNote: 'pyproject.toml changed since uv.lock was made: run uv lock' }),
        { text: 'out of date: pyproject.toml changed since uv.lock was made: run uv lock', kind: 'stale' });
    assert.deepEqual(staleText({ stale: true }), { text: 'out of date', kind: 'stale' });
    assert.deepEqual(staleText({ stale: false }), { text: 'up to date', kind: 'fresh' });
    assert.deepEqual(staleText({ stale: null, staleNote: 'uv could not tell: offline' }), { text: 'uv could not tell: offline', kind: 'unknown' });
    assert.equal(staleText({ stale: null }), null);
    assert.equal(staleText(null), null);
});

test('an address is shown with its password, or a lone token, masked', () => {
    assert.equal(maskAddress(`https://me:${FAKE_PASSWORD}@idx.example.org/simple`), 'https://me:***@idx.example.org/simple');
    assert.equal(maskAddress('https://TOKEN@idx.example.org'), 'https://***@idx.example.org');
    assert.equal(maskAddress('https://idx.example.org/a@b'), 'https://idx.example.org/a@b');
    assert.equal(maskAddress(''), '');
    assert.equal(settingsSummary({ indexUrl: 'https://u:p@i.example', proxy: 'http://x:y@p:1' }), 'index https://u:***@i.example; proxy http://x:***@p:1');
});

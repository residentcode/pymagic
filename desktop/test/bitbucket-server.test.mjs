// Bitbucket Data Center (a company's own Bitbucket server) in Get from VCS (desktop/vcs-hosts-model.js): `node --test desktop/test/bitbucket-server.test.mjs`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { KINDS, serverOf, apiBaseOf, headersOf, signInProblem, loginOf, whoPathOf, reposPathOf, morePages, hostReposOf, accountKey } = require('../vcs-hosts-model.js');

test('the server, its API and the token', () => {
    assert.equal(KINDS.bbserver.label, 'Bitbucket Data Center');
    assert.equal(serverOf('bbserver', 'bitbucket.example.com/'), 'https://bitbucket.example.com');
    // a server under a path of its own keeps it
    assert.equal(serverOf('bbserver', 'https://git.example.com/bitbucket/'), 'https://git.example.com/bitbucket');
    assert.throws(() => serverOf('bbserver', ''), /address is needed/);
    assert.throws(() => serverOf('bbserver', 'http://bitbucket.example.com'), /https only/);
    assert.equal(apiBaseOf('bbserver', 'https://git.example.com/bitbucket'), 'https://git.example.com/bitbucket/rest/api/1.0');
    assert.equal(headersOf('bbserver', 'tok').Authorization, 'Bearer tok');
    assert.equal(signInProblem('bbserver', { token: 'NjE2MzQ5NTQ2MDQ3OjE2' }), null);
    assert.match(signInProblem('bbserver', { token: '' }), /token is needed/);
    assert.equal(accountKey('bbserver', 'https://bitbucket.example.com'), 'bbserver https://bitbucket.example.com');
});

test('whose token it is: the name in the answer\'s header', () => {
    assert.equal(whoPathOf('bbserver'), '/repos?limit=1');
    assert.equal(whoPathOf('gitlab'), '/user');
    assert.equal(loginOf('bbserver', { values: [], isLastPage: true }, 'jane.doe'), 'jane.doe');
    assert.equal(loginOf('bbserver', { values: [] }, 'jane%40example.com'), 'jane@example.com');
    assert.throws(() => loginOf('bbserver', { values: [] }, null), /did not say whose/);
    assert.throws(() => loginOf('bbserver', { errors: [] }, 'jane'), /did not say whose/);
    // the others as before
    assert.equal(loginOf('gitlab', { username: 'lab' }), 'lab');
});

test('the repositories: pages by their start, PROJECT/repo, the address without the account', () => {
    assert.equal(reposPathOf('bbserver', 1), '/repos?limit=100&start=0');
    assert.equal(reposPathOf('bbserver', 3), '/repos?limit=100&start=200');
    assert.equal(morePages('bbserver', { isLastPage: false, values: [] }), true);
    assert.equal(morePages('bbserver', { isLastPage: true, values: [] }), false);
    const page = (values) => ({ values, isLastPage: true });
    const repos = hostReposOf('bbserver', [page([
        { slug: 'shop-api', project: { key: 'SHOP' }, description: 'The API', public: false, links: { clone: [{ name: 'ssh', href: 'ssh://git@bitbucket.example.com:7999/shop/shop-api.git' }, { name: 'http', href: 'https://jane@bitbucket.example.com/scm/shop/shop-api.git' }] } },
        { slug: 'notes', project: { key: '~jane' }, public: true, origin: { slug: 'notes' }, archived: true, links: { clone: [{ name: 'http', href: 'https://bitbucket.example.com/scm/~jane/notes.git' }] } },
        { slug: 'no-http', project: { key: 'X' }, links: { clone: [{ name: 'ssh', href: 'ssh://x' }] } },
        { slug: 'shop-api', project: { key: 'SHOP' }, links: { clone: [{ name: 'http', href: 'https://bitbucket.example.com/scm/shop/again.git' }] } },
    ])]);
    assert.deepEqual(repos, [
        { name: 'SHOP/shop-api', url: 'https://bitbucket.example.com/scm/shop/shop-api.git', description: 'The API', private: true, fork: false, archived: false, pushed: '', owner: 'SHOP' },
        { name: '~jane/notes', url: 'https://bitbucket.example.com/scm/~jane/notes.git', description: '', private: false, fork: true, archived: true, pushed: '', owner: '~jane' },
    ]);
});

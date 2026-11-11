// Checks of the welcome screen's Customize (desktop/customize-model.js), GitHub sign-in (desktop/github-model.js) and other hosts (desktop/vcs-hosts-model.js), and of the keymaps they
// choose from (web/src/keymap-model.js): `node --test desktop/test/welcome-models.test.mjs`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { KEYMAPS, KEYMAP_KEYS, withKeymap, keymapChanges, effectiveKeys, normaliseKey } from '../../web/src/keymap-model.js';

const require = createRequire(import.meta.url);
const { engineSettingsFile, customizeOf, withCustomize, FIELDS } = require('../customize-model.js');
const { ghAccountOf, deviceCodeOf, ghWantsEnter, tokenOk, reposOf, filterRepos } = require('../github-model.js');
const { serverOf, apiBaseOf, headersOf, signInProblem, loginOf, reposPathOf, morePages, hostReposOf, accountKey, accountsShown } = require('../vcs-hosts-model.js');

test("Customize reads and writes the engine's settings file, only its own fields, each checked", () => {
    assert.equal(engineSettingsFile({ PYMAGIC_SETTINGS_FILE: 'C:\\t\\s.json' }), 'C:\\t\\s.json');
    assert.match(engineSettingsFile({ LOCALAPPDATA: 'C:\\Users\\a\\AppData\\Local' }), /AppData[\\/]Local[\\/]PyMagic[\\/]settings\.json$/);
    // what is not there, or not one of the choices, is the default
    assert.deepEqual(customizeOf({ theme: 'light', fontSize: 99, keymap: 'emacs' }), { theme: 'light', codeColors: 'vscode', fontFamily: FIELDS.fontFamily.default, fontSize: 14, keymap: 'pymagic' });
    // a change keeps the rest of the settings (the plugins, the keys) as they are
    const next = withCustomize({ plugins: { git: false }, keybindings: { a: 'F1' }, theme: 'dark' }, { theme: 'system', fontSize: '16', keymap: 'vscode' });
    assert.deepEqual(next, { plugins: { git: false }, keybindings: { a: 'F1' }, theme: 'system', fontSize: 16, keymap: 'vscode' });
    assert.throws(() => withCustomize({}, { theme: 'pink' }), /theme cannot be "pink"/);
    assert.throws(() => withCustomize({}, { fontSize: 100 }), /fontSize/);
    assert.throws(() => withCustomize({}, { fontFamily: 'x; } body { color: red' }), /fontFamily/);
    assert.throws(() => withCustomize({}, { plugins: {} }), /plugins cannot be/);
    // the keymaps Customize offers are the keymap model's
    assert.deepEqual(FIELDS.keymap.choices, KEYMAPS.map(([id]) => id));
});

test("a keymap gives the commands its keys, and the person's own changes go over it", () => {
    const commands = [
        { id: 'pymagic.run.stepOver', keys: ['F10'] }, { id: 'pymagic.edit.format', keys: ['Ctrl+Alt+L'] },
        { id: 'editor.action.copyLinesDownAction', keys: ['Shift+Alt+Down'] }, { id: 'pymagic.bookmarks.next', keys: ['Alt+F2'] },
    ];
    const pycharm = withKeymap(commands, 'pycharm');
    assert.deepEqual(effectiveKeys(pycharm, {}), { 'pymagic.run.stepOver': ['F8'], 'pymagic.edit.format': ['Ctrl+Alt+L'], 'editor.action.copyLinesDownAction': ['Ctrl+D'], 'pymagic.bookmarks.next': [] });
    assert.deepEqual(effectiveKeys(withKeymap(commands, 'vscode'), {})['pymagic.edit.format'], ['Shift+Alt+F']);
    assert.deepEqual(withKeymap(commands, 'pymagic'), commands);
    // the changes in force: the keymap's, then the person's
    assert.deepEqual(keymapChanges('pycharm', { 'pymagic.run.stepOver': 'F6' })['pymagic.run.stepOver'], 'F6');
    assert.equal(keymapChanges('pycharm', {})['editor.action.rename'], 'Shift+F6');
    assert.deepEqual(keymapChanges('nothing', { a: 'F1' }), { a: 'F1' });
    // every key of every keymap is one PyMagic can read, and no keymap gives one key to two commands
    for (const [name, keys] of Object.entries(KEYMAP_KEYS)) {
        const given = Object.values(keys).filter(Boolean);
        for (const k of given) assert.ok(normaliseKey(k), `${name}: ${k}`);
        assert.equal(new Set(given.map(normaliseKey)).size, given.length, `${name} gives a key twice`);
    }
});

test("the GitHub CLI's sign-in: its account, its one-time code, and the Enter it waits for", () => {
    assert.equal(ghAccountOf('github.com\n  ✓ Logged in to github.com account octo-cat (keyring)\n  - Active account: true'), 'octo-cat');
    assert.equal(ghAccountOf('✓ Logged in to github.com as old-style (oauth_token)'), 'old-style');
    assert.equal(ghAccountOf('You are not logged into any GitHub hosts. To log in, run: gh auth login'), null);
    const said = '! First copy your one-time code: 1A2B-3C4D\nOpen this URL to continue in your web browser: https://github.com/login/device\nPress Enter to open github.com in your browser... ';
    assert.deepEqual(deviceCodeOf(said), { code: '1A2B-3C4D', url: 'https://github.com/login/device' });
    assert.equal(deviceCodeOf('nothing yet'), null);
    assert.ok(ghWantsEnter(said));
});

test('a token is checked for its form, and the repositories are listed newest first, filtered', () => {
    assert.ok(tokenOk('ghp_' + 'a'.repeat(36)));
    assert.ok(tokenOk(' github_pat_' + 'A1_'.repeat(20) + ' '));
    assert.ok(!tokenOk('password123'));
    assert.ok(!tokenOk('ghp_short'));
    const repos = reposOf([
        [{ full_name: 'me/old', clone_url: 'https://github.com/me/old.git', description: null, private: false, pushed_at: '2024-01-01T00:00:00Z', owner: { login: 'me' } },
            { full_name: 'org/app', clone_url: 'https://github.com/org/app.git', description: 'The shop app', private: true, pushed_at: '2026-10-01T00:00:00Z', owner: { login: 'org' } }],
        [{ full_name: 'me/old', clone_url: 'https://github.com/me/old.git' }, { full_name: 'me/fork', clone_url: 'https://github.com/me/fork.git', fork: true, archived: true, pushed_at: '2025-05-05T00:00:00Z' }, { name: 'broken' }],
    ]);
    assert.deepEqual(repos.map((r) => `${r.name}${r.private ? ' private' : ''}${r.fork ? ' fork' : ''}${r.archived ? ' archived' : ''}`), ['org/app private', 'me/fork fork archived', 'me/old']);
    assert.deepEqual(filterRepos(repos, 'shop').map((r) => r.name), ['org/app']);
    assert.deepEqual(filterRepos(repos, 'ME o').map((r) => r.name), ['me/fork', 'me/old']);
    assert.equal(filterRepos(repos, '').length, 3);
});

test('GitHub Enterprise, GitLab and Bitbucket: the server, the token sent as each wants it, the account, the repositories of every page', () => {
    // the server: https, a GitLab server's own path kept, an Enterprise API path taken off; Bitbucket's is its own
    assert.equal(serverOf('ghe', 'github.example.com/'), 'https://github.example.com');
    assert.equal(serverOf('ghe', 'https://github.example.com/api/v3'), 'https://github.example.com');
    assert.equal(serverOf('gitlab', ''), 'https://gitlab.com');
    assert.equal(serverOf('gitlab', 'https://code.example.org/gitlab/'), 'https://code.example.org/gitlab');
    assert.equal(serverOf('gitlab', 'http://127.0.0.1:8123'), 'http://127.0.0.1:8123');
    assert.equal(serverOf('bitbucket', 'anything'), 'https://bitbucket.org');
    assert.throws(() => serverOf('ghe', ''), /address is needed/);
    assert.throws(() => serverOf('ghe', 'http://github.example.com'), /https only/);
    assert.throws(() => serverOf('ghe', 'https://github.com'), /choose GitHub/);
    assert.throws(() => serverOf('gitlab', 'https://me:pw@gitlab.example.com'), /no name, password/);
    assert.throws(() => serverOf('svn', 'x'), /No such host/);
    assert.equal(apiBaseOf('ghe', 'https://g.example.com', {}), 'https://g.example.com/api/v3');
    assert.equal(apiBaseOf('gitlab', 'https://gitlab.com', {}), 'https://gitlab.com/api/v4');
    assert.equal(apiBaseOf('bitbucket', 'https://bitbucket.org', {}), 'https://api.bitbucket.org/2.0');
    assert.equal(apiBaseOf('bitbucket', 'https://bitbucket.org', { PYMAGIC_BITBUCKET_API: 'http://127.0.0.1:9' }), 'http://127.0.0.1:9');
    assert.equal(headersOf('ghe', 'T0KEN').Authorization, 'Bearer T0KEN');
    assert.equal(headersOf('gitlab', 'glpat-x')['PRIVATE-TOKEN'], 'glpat-x');
    assert.equal(headersOf('gitlab', 'glpat-x').Authorization, undefined);
    assert.equal(headersOf('bitbucket', 'tok', 'me@example.com').Authorization, 'Basic ' + Buffer.from('me@example.com:tok').toString('base64'));
    assert.equal(signInProblem('gitlab', { token: '' }), 'The token is needed.');
    assert.match(signInProblem('gitlab', { token: 'has a space in it' }), /not a token/);
    assert.match(signInProblem('bitbucket', { token: 'ATATT3xFfGF0abc', user: '' }), /email/);
    assert.equal(signInProblem('bitbucket', { token: 'ATATT3xFfGF0abc', user: 'me@example.com' }), null);
    assert.equal(loginOf('ghe', { login: 'octo' }), 'octo');
    assert.equal(loginOf('gitlab', { username: 'lab' }), 'lab');
    assert.equal(loginOf('bitbucket', { username: 'bucket', display_name: 'B' }), 'bucket');
    assert.throws(() => loginOf('gitlab', {}), /whose token/);
    assert.match(reposPathOf('gitlab', 2), /^\/projects\?membership=true&.*page=2$/);
    assert.match(reposPathOf('bitbucket', 1), /^\/repositories\?role=member&.*page=1$/);
    assert.ok(morePages('gitlab', new Array(100).fill({})));
    assert.ok(!morePages('gitlab', [{}]));
    assert.ok(morePages('bitbucket', { values: [], next: 'https://api.bitbucket.org/2.0/repositories?page=2' }));
    assert.ok(!morePages('bitbucket', { values: [] }));
    const lab = hostReposOf('gitlab', [
        [{ path_with_namespace: 'grp/sub/app', http_url_to_repo: 'https://gitlab.com/grp/sub/app.git', description: 'The app', visibility: 'private', last_activity_at: '2026-09-01T00:00:00Z' },
            { path_with_namespace: 'me/lib', http_url_to_repo: 'https://gitlab.com/me/lib.git', description: null, visibility: 'public', forked_from_project: { id: 1 }, archived: true, last_activity_at: '2025-01-01T00:00:00Z' }],
        [{ path_with_namespace: 'grp/sub/app', http_url_to_repo: 'https://gitlab.com/grp/sub/app.git' }, { name: 'broken' }],
    ]);
    assert.deepEqual(lab.map((r) => `${r.name} ${r.url}${r.private ? ' private' : ''}${r.fork ? ' fork' : ''}${r.archived ? ' archived' : ''} ${r.owner}`),
        ['grp/sub/app https://gitlab.com/grp/sub/app.git private grp', 'me/lib https://gitlab.com/me/lib.git fork archived me']);
    const bucket = hostReposOf('bitbucket', [{ values: [
        { full_name: 'team/site', description: '', is_private: true, updated_on: '2026-02-02T00:00:00Z', links: { clone: [{ name: 'https', href: 'https://me@bitbucket.org/team/site.git' }, { name: 'ssh', href: 'git@bitbucket.org:team/site.git' }] } },
        { full_name: 'me/fork', is_private: false, parent: { full_name: 'x/y' }, updated_on: '2026-03-03T00:00:00Z', links: { clone: [{ name: 'https', href: 'https://bitbucket.org/me/fork.git' }] } },
        { full_name: 'me/noclone', links: {} },
    ] }]);
    assert.deepEqual(bucket.map((r) => `${r.name} ${r.url}${r.private ? ' private' : ''}${r.fork ? ' fork' : ''}`), ['me/fork https://bitbucket.org/me/fork.git fork', 'team/site https://bitbucket.org/team/site.git private']);
    const ghe = hostReposOf('ghe', [[{ full_name: 'corp/api', clone_url: 'https://g.example.com/corp/api.git', private: true, pushed_at: '2026-01-01T00:00:00Z' }]]);
    assert.deepEqual(ghe.map((r) => r.name + ' ' + r.url), ['corp/api https://g.example.com/corp/api.git']);
    // the page never sees a token
    const shown = accountsShown({ [accountKey('gitlab', 'https://gitlab.com')]: { kind: 'gitlab', server: 'https://gitlab.com', login: 'lab', token: 'glpat-secret', user: '' } });
    assert.deepEqual(shown, [{ key: 'gitlab https://gitlab.com', kind: 'gitlab', label: 'GitLab', server: 'https://gitlab.com', user: 'lab' }]);
    assert.ok(!JSON.stringify(shown).includes('secret'));
});

// Checks of the welcome screen's Customize (desktop/customize-model.js) and GitHub sign-in (desktop/github-model.js), and of the keymaps they
// choose from (web/src/keymap-model.js): `node --test desktop/test/welcome-models.test.mjs`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { KEYMAPS, KEYMAP_KEYS, withKeymap, keymapChanges, effectiveKeys, normaliseKey } from '../../web/src/keymap-model.js';

const require = createRequire(import.meta.url);
const { engineSettingsFile, customizeOf, withCustomize, FIELDS } = require('../customize-model.js');
const { ghAccountOf, deviceCodeOf, ghWantsEnter, tokenOk, reposOf, filterRepos } = require('../github-model.js');

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

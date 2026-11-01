// Checks of web/src/packages-model.js: `node web/test/packages.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { countNodes, listText, looksLikeSource, lockLabel, pinned, settingsFromForm, settingsSummary, toggled, treeLabel, treeRows, versionOptions } from '../src/packages-model.js';

test('lines and commas of a text box become a list without blanks or repeats', () => {
    assert.deepEqual(listText(' https://a/simple \n\nhttps://b/simple,https://a/simple\r\n'), ['https://a/simple', 'https://b/simple']);
    assert.deepEqual(listText(undefined), []);
});

test('the form becomes the settings object the host keeps', () => {
    assert.deepEqual(settingsFromForm({ indexUrl: ' https://i/simple ', extraIndexUrls: 'https://x/simple\nhttps://y/simple', trustedHosts: 'x\ny', proxy: '' }),
        { indexUrl: 'https://i/simple', extraIndexUrls: ['https://x/simple', 'https://y/simple'], trustedHosts: ['x', 'y'], proxy: '' });
    assert.equal(settingsSummary({ indexUrl: '', extraIndexUrls: [], trustedHosts: [], proxy: '' }), '');
    assert.equal(settingsSummary({ indexUrl: 'https://i/simple', extraIndexUrls: ['a', 'b'], trustedHosts: ['h'], proxy: 'http://p:1' }), 'index https://i/simple; 2 extra indexes; trusting h; proxy http://p:1');
});

test('what typed into the install box is a source, not a package name', () => {
    for (const s of ['git+https://github.com/x/y.git@v1', 'https://host/p-1.0.whl', 'C:\\work\\lib', 'c:/work/lib', './lib', '../lib', '/opt/lib', '~/lib', '"D:\\my libs\\x"', 'dist/pkg-1.0.tar.gz', '.', '\\\\server\\share\\x'])
        assert.equal(looksLikeSource(s), true, s);
    for (const s of ['requests', 'flask>=3', 'uvicorn[standard]', 'a.b-c', 'numpy==1.26.4 pandas', '', '  '])
        assert.equal(looksLikeSource(s), false, s);
});

test('the version picker: newest first, yanked ones never, pre-releases when asked, the installed one marked', () => {
    const versions = [{ version: '3.0b1', pre: true, yanked: false }, { version: '2.1', pre: false, yanked: true }, { version: '2.0', pre: false, yanked: false }, { version: '1.9', pre: false, yanked: false }];
    assert.deepEqual(versionOptions(versions).map((o) => o.value), ['2.0', '1.9']);
    assert.deepEqual(versionOptions(versions, { pre: true }).map((o) => o.label), ['3.0b1 (pre-release)', '2.0', '1.9']);
    assert.deepEqual(versionOptions(versions, { installed: '1.9' }).map((o) => o.label), ['2.0', '1.9 (installed)']);
    assert.deepEqual(versionOptions(versions, { installed: '3.0b1' }).map((o) => o.value), ['3.0b1', '2.0', '1.9']);   // an installed pre-release stays choosable
    assert.equal(pinned('Flask', '3.0.2'), 'Flask==3.0.2');
});

const tree = [
    { name: 'App', version: '1.0', specifier: '', children: [
        { name: 'lib-a', version: '1.2', specifier: '>=1', children: [{ name: 'lib-c', version: '3', specifier: '', children: [{ name: 'lib-a', version: '1.2', cycle: true, children: [] }] }, { name: 'ghost', missing: true, specifier: '<2', children: [] }] },
        { name: 'lib-b', version: '2.0', specifier: '', children: [] },
    ] },
    { name: 'alone', version: '0.1', children: [] },
];

test('a tree starts with its roots open and the rest closed; clicking a twisty flips one row', () => {
    let rows = treeRows(tree);
    assert.deepEqual(rows.map((r) => r.node.name), ['App', 'lib-a', 'lib-b', 'alone']);
    assert.deepEqual(rows.map((r) => [r.depth, r.hasChildren, r.open]), [[0, true, true], [1, true, false], [1, false, false], [0, false, false]]);
    let flipped = toggled(new Set(), rows[1]);
    rows = treeRows(tree, flipped);
    assert.deepEqual(rows.map((r) => r.node.name), ['App', 'lib-a', 'lib-c', 'ghost', 'lib-b', 'alone']);
    flipped = toggled(flipped, rows[0]);   // close the root
    assert.deepEqual(treeRows(tree, flipped).map((r) => r.node.name), ['App', 'alone']);
    flipped = toggled(flipped, rows[0]);
    assert.deepEqual(treeRows(tree, flipped).map((r) => r.node.name).length, 6);
    // the same package in two places has its own row key
    const keys = treeRows(tree, new Set(['/0:App/0:lib-a', '/0:App/0:lib-a/0:lib-c'])).map((r) => r.key);
    assert.equal(new Set(keys).size, keys.length);
});

test('tree labels say what is wrong with a row', () => {
    assert.equal(treeLabel({ name: 'lib-a', version: '1.2' }), 'lib-a 1.2');
    assert.equal(treeLabel({ name: 'ghost', missing: true }), 'ghost (not installed)');
    assert.equal(treeLabel({ name: 'lib-a', version: '1.2', cycle: true }), 'lib-a 1.2 (circular)');
    assert.equal(countNodes(tree), 6);   // App, lib-a, lib-c, ghost, lib-b, alone: lib-a counted once
    assert.equal(lockLabel({ file: 'uv.lock', tool: 'uv' }), 'uv.lock (uv)');
});

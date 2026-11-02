import { test } from 'node:test';
import assert from 'node:assert/strict';
import { PLUGINS, pluginsOff, pluginOn, switchPlugin, hiddenElements, findPlugins, toolOff } from '../src/plugin-model.js';

test('every plugin is on unless the setting says false', () => {
    assert.ok(PLUGINS.length >= 8);
    assert.equal(new Set(PLUGINS.map((p) => p.id)).size, PLUGINS.length);
    assert.ok(pluginOn({}, 'schemas') && pluginOn(undefined, 'database'));
    assert.ok(!pluginOn({ schemas: false }, 'schemas'));
    assert.ok(pluginOn({ schemas: true, database: 'no' }, 'database'), 'only false switches one off');
    assert.deepEqual([...pluginsOff({ profiler: false, nosuch: false })], ['profiler']);
});

test('switching keeps only the plugins that are off', () => {
    assert.deepEqual(switchPlugin({}, 'profiler', false), { profiler: false });
    assert.deepEqual(switchPlugin({ profiler: false, macros: false }, 'profiler', true), { macros: false });
    assert.deepEqual(switchPlugin({ gone: false, macros: true }, 'schemas', false), { schemas: false }, 'unknown ids and true values dropped');
});

test('a plugin that is off hides its panel tab and what it owns', () => {
    assert.deepEqual(hiddenElements({}), []);
    assert.deepEqual(hiddenElements({ profiler: false }), ['t-profiler', 'profiler-tab', 'b-profile']);
    assert.deepEqual(hiddenElements({ schemas: false }), [], 'no element of its own');
    assert.ok(toolOff({ database: false }, 'database') && !toolOff({}, 'database') && !toolOff({ database: false }, 'git'));
});

test('plugins are found by the words of their name and description', () => {
    assert.deepEqual(findPlugins('').length, PLUGINS.length);
    assert.deepEqual(findPlugins('http').map((p) => p.id), ['httpclient']);
    assert.deepEqual(findPlugins('KUBERNETES schema').map((p) => p.id), ['schemas']);
    assert.deepEqual(findPlugins('nothing like this'), []);
});

test('Pull Requests is off with Git, and the New Project kinds of plugins that are off are left out', async () => {
    const { requiredName, kindsOff } = await import('../src/plugin-model.js');
    assert.ok(!pluginOn({ git: false }, 'prs') && pluginOn({ git: false }, 'django'));
    assert.deepEqual([...pluginsOff({ git: false })].sort(), ['git', 'prs']);
    assert.equal(requiredName('prs'), 'Git');
    assert.equal(requiredName('git'), null);
    assert.deepEqual(kindsOff({}), []);
    assert.deepEqual(kindsOff({ django: false, notebooks: false }), ['django', 'jupyter']);
    assert.deepEqual(hiddenElements({ git: false }), ['t-git', 'git-tab', 'r-git', 'branch-chip', 'b-update', 'b-commit', 'b-push', 't-prs', 'prs-tab']);
    assert.deepEqual(hiddenElements({ django: false }), ['t-django', 'django-tab', 'st-django']);
});

test('Flask, FastAPI, WSL, SSH, coverage and diagrams: their kinds and parts; the remote interpreter button only while a remote kind is left', async () => {
    const { kindsOff, remoteKindsLeft } = await import('../src/plugin-model.js');
    assert.deepEqual(kindsOff({ django: false, notebooks: false, flask: false, fastapi: false }), ['django', 'flask', 'fastapi', 'jupyter']);
    assert.deepEqual(hiddenElements({ coverage: false }), ['t-coverage', 'coverage-tab', 'b-cov']);
    assert.deepEqual(hiddenElements({ diagrams: false, wsl: false, ssh: false }), []);
    assert.ok(remoteKindsLeft({ wsl: false, ssh: false }));
    assert.ok(!remoteKindsLeft({ wsl: false, ssh: false, docker: false }));
    assert.deepEqual(hiddenElements({ wsl: false, ssh: false, docker: false }), ['b-remote']);
});

test('JavaScript projects brings the seven other kinds of New Project', async () => {
    const { kindsOff } = await import('../src/plugin-model.js');
    assert.deepEqual(kindsOff({ javascript: false }), ['html5boilerplate', 'bootstrap', 'vite', 'reactnative', 'nextjs', 'nodejs', 'express']);
    assert.deepEqual(kindsOff({ javascript: false, django: false }), ['django', 'html5boilerplate', 'bootstrap', 'vite', 'reactnative', 'nextjs', 'nodejs', 'express']);
});

test('Emmet is a plugin that hides nothing of its own (its completion is simply not set up)', () => {
    assert.deepEqual(findPlugins('emmet').map((p) => p.id), ['emmet']);
    assert.deepEqual(hiddenElements({ emmet: false }), []);
});

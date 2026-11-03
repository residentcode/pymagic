import { test } from 'node:test';
import assert from 'node:assert/strict';
import { completesLanguage, engineLanguage } from '../src/fullline-model.js';
import { PLUGINS, pluginOn } from '../src/plugin-model.js';

test('lines are completed in code, templates and configuration, not in prose', () => {
    for (const language of ['python', 'javascript', 'typescript', 'django-html', 'css', 'sql', 'yaml', 'dockerfile'])
        assert.ok(completesLanguage(language), language);
    for (const language of ['plaintext', 'markdown', 'restructuredtext', 'http', undefined])
        assert.ok(!completesLanguage(language), String(language));
});

test('the engine is told the language whose comments it reads', () => {
    assert.equal(engineLanguage('python'), 'python');
    assert.equal(engineLanguage('powershell'), 'shell');
});

test('full line completion is a plugin, on unless switched off', () => {
    assert.ok(PLUGINS.some((p) => p.id === 'fullline' && /Qwen2\.5-Coder/.test(p.about)));
    assert.ok(pluginOn({}, 'fullline'));
    assert.ok(!pluginOn({ fullline: false }, 'fullline'));
});

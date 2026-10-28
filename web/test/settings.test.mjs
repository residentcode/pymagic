import test from 'node:test';
import assert from 'node:assert/strict';
import { DEFAULTS, merged, editorOptions } from '../src/settings.js';

test('what is stored is laid over the defaults, and a value of the wrong kind is dropped', () => {
    const s = merged({ theme: 'light', fontSize: '20', minimap: true, unknown: 1, wordWrap: 7 });
    assert.equal(s.theme, 'light');
    assert.equal(s.minimap, true);
    assert.equal(s.fontSize, DEFAULTS.fontSize);      // a string is not a size
    assert.equal(s.wordWrap, DEFAULTS.wordWrap);       // nor is a number a wrap mode
    assert.ok(!('unknown' in s));
});

test('sizes stay within what makes sense', () => {
    assert.equal(merged({ fontSize: 3 }).fontSize, 8);
    assert.equal(merged({ fontSize: 99 }).fontSize, 40);
    assert.equal(merged({ lineHeight: 500 }).lineHeight, 80);
    assert.equal(merged({ ruler: -4 }).ruler, 0);
    assert.equal(merged({ fontSize: 13.6 }).fontSize, 14);
});

test('nothing stored gives the defaults', () => {
    assert.deepEqual(merged(undefined), DEFAULTS);
    assert.deepEqual(merged({}), DEFAULTS);
});

test('the settings become the options of the editor', () => {
    const o = editorOptions({ ...DEFAULTS, minimap: true, ruler: 100, wordWrap: 'on', ligatures: true });
    assert.deepEqual(o.minimap, { enabled: true });
    assert.deepEqual(o.rulers, [100]);
    assert.equal(o.wordWrap, 'on');
    assert.equal(o.fontLigatures, true);
    assert.deepEqual(editorOptions(DEFAULTS).rulers, []);
});

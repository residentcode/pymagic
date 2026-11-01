// Checks of web/src/macro-model.js: `node web/test/macro.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { stepOf, compress, describe, comboOf, allowedCombo, tidy } from '../src/macro-model.js';

const key = (k, mods = '') => ({ key: k, ctrlKey: mods.includes('c'), shiftKey: mods.includes('s'), altKey: mods.includes('a'), metaKey: false });

test('a key press is a typed character or a command of the editor', () => {
    assert.deepEqual(stepOf(key('a')), { k: 'text', text: 'a' });
    assert.deepEqual(stepOf(key('A', 's')), { k: 'text', text: 'A' });
    assert.deepEqual(stepOf(key('(')), { k: 'text', text: '(' });
    assert.deepEqual(stepOf(key(' ')), { k: 'text', text: ' ' });
    assert.deepEqual(stepOf(key('Enter')), { k: 'text', text: '\n' });
    assert.deepEqual(stepOf(key('Backspace')), { k: 'cmd', id: 'deleteLeft' });
    assert.deepEqual(stepOf(key('ArrowRight', 'cs')), { k: 'cmd', id: 'cursorWordRightSelect' });
    assert.deepEqual(stepOf(key('Home', 'c')), { k: 'cmd', id: 'cursorTop' });
    assert.deepEqual(stepOf(key('k', 'cs')), { k: 'cmd', id: 'editor.action.deleteLines' });
    assert.deepEqual(stepOf(key('ArrowUp', 'a')), { k: 'cmd', id: 'editor.action.moveLinesUpAction' });
    // AltGr (Ctrl+Alt) makes a character on many keyboards
    assert.deepEqual(stepOf(key('@', 'ca')), { k: 'text', text: '@' });
    // what a macro cannot carry
    assert.equal(stepOf(key('c', 'c')), null);        // the clipboard
    assert.equal(stepOf(key('F5')), null);
    assert.equal(stepOf(key('Shift', 's')), null);
    assert.equal(stepOf(key('x', 'a')), null);
});

test('typed characters are joined, commands stay apart', () => {
    const steps = compress([{ k: 'text', text: 'd' }, { k: 'text', text: 'e' }, { k: 'text', text: 'f' }, { k: 'cmd', id: 'cursorLeft' }, { k: 'text', text: 'x' }, { k: 'text', text: '\n' }]);
    assert.deepEqual(steps, [{ k: 'text', text: 'def' }, { k: 'cmd', id: 'cursorLeft' }, { k: 'text', text: 'x\n' }]);
});

test('a macro in a line', () => {
    assert.equal(describe([{ k: 'text', text: 'def f' }, { k: 'text', text: '\n' }, { k: 'cmd', id: 'cursorWordLeft' }, { k: 'cmd', id: 'cursorLeftSelect' }, { k: 'text', text: 'a\nb' }]),
        '‘def f’ · Enter · Ctrl+← · Left+Shift · ‘a↵b’');
    assert.equal(describe([{ k: 'text', text: 'x'.repeat(200) }], 20).length, 20);
});

test('key combinations: written, and which a macro may be given', () => {
    assert.equal(comboOf(key('1', 'ca')), 'Ctrl+Alt+1');
    assert.equal(comboOf(key('F9', 'cs')), 'Ctrl+Shift+F9');
    assert.equal(comboOf(key('ArrowLeft', 'a')), 'Alt+Left');
    assert.equal(comboOf(key('Control', 'c')), null);
    assert.ok(allowedCombo('Ctrl+Alt+1') && allowedCombo('F9') && allowedCombo('Shift+F9') && allowedCombo('Alt+M'));
    assert.ok(!allowedCombo('A') && !allowedCombo('Shift+A') && !allowedCombo('') && !allowedCombo(null));
});

test('what the project keeps is tidied: names unique, keys allowed, empty macros gone', () => {
    const kept = tidy([
        { name: ' one ', key: 'Ctrl+Alt+1', steps: [{ k: 'text', text: 'a' }, { k: 'text', text: 'b' }] },
        { name: 'one', key: 'F9', steps: [{ k: 'text', text: 'x' }] },
        { name: 'two', key: 'A', steps: [{ k: 'cmd', id: 'undo' }, { k: 'bad' }] },
        { name: 'empty', steps: [] },
        { name: '', steps: [{ k: 'text', text: 'x' }] },
        null,
    ]);
    assert.deepEqual(kept, [{ name: 'one', key: 'Ctrl+Alt+1', steps: [{ k: 'text', text: 'ab' }] }, { name: 'two', key: '', steps: [{ k: 'cmd', id: 'undo' }] }]);
    assert.deepEqual(tidy('nonsense'), []);
});

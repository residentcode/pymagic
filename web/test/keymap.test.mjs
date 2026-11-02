// Keyboard shortcuts changed in Settings > Keymap (web/src/keymap-model.js).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { keyName, keyFromEvent, parseKey, normaliseKey, toKeybinding, effectiveKeys, clashes, withKey } from '../src/keymap-model.js';

test('a key press is a shortcut as text; modifiers alone are not', () => {
    assert.equal(keyFromEvent({ code: 'KeyD', ctrlKey: true }), 'Ctrl+D');
    assert.equal(keyFromEvent({ code: 'KeyD', ctrlKey: true, shiftKey: true, altKey: true }), 'Ctrl+Shift+Alt+D');
    assert.equal(keyFromEvent({ code: 'F5' }), 'F5');
    assert.equal(keyFromEvent({ code: 'ArrowUp', altKey: true }), 'Alt+Up');
    assert.equal(keyFromEvent({ code: 'Backquote', ctrlKey: true }), 'Ctrl+`');
    assert.equal(keyFromEvent({ code: 'Numpad5', ctrlKey: true }), 'Ctrl+5');
    assert.equal(keyFromEvent({ code: 'ControlLeft', ctrlKey: true }), null);
    assert.equal(keyFromEvent({ code: 'ShiftRight', shiftKey: true }), null);
    assert.equal(keyName('CapsLock'), null);
});

test('a shortcut written as text is read in any order and written the one way', () => {
    assert.deepEqual(parseKey('shift+ctrl+d'), { ctrl: true, shift: true, alt: false, win: false, key: 'KeyD' });
    assert.equal(normaliseKey('shift+ctrl+d'), 'Ctrl+Shift+D');
    assert.equal(normaliseKey('alt+up'), 'Alt+Up');
    assert.equal(normaliseKey('Ctrl+`'), 'Ctrl+`');
    assert.equal(normaliseKey('ctrl+f12'), 'Ctrl+F12');
    assert.equal(normaliseKey('Ctrl+Shift+Delete'), 'Ctrl+Shift+Delete');
    for (const bad of ['', 'Ctrl', 'Ctrl+', 'Hyper+D', 'Ctrl+Nope']) assert.equal(normaliseKey(bad), '', bad);
});

test('a shortcut becomes Monaco\'s number', () => {
    const KeyMod = { CtrlCmd: 2048, Shift: 1024, Alt: 512, WinCtrl: 256 };
    const KeyCode = { KeyD: 34, F5: 63, UpArrow: 16, Backquote: 91 };
    assert.equal(toKeybinding('Ctrl+D', KeyMod, KeyCode), 2048 | 34);
    assert.equal(toKeybinding('Ctrl+Shift+Alt+D', KeyMod, KeyCode), 2048 | 1024 | 512 | 34);
    assert.equal(toKeybinding('F5', KeyMod, KeyCode), 63);
    assert.equal(toKeybinding('Alt+Up', KeyMod, KeyCode), 512 | 16);
    assert.equal(toKeybinding('Ctrl+Q', KeyMod, KeyCode), null);     // not in this KeyCode
    assert.equal(toKeybinding('', KeyMod, KeyCode), null);
});

test('the person\'s changes over the keys each command starts with, the clashes they make, and back to the start', () => {
    const commands = [
        { id: 'select.next', keys: ['Ctrl+D'] },
        { id: 'line.copy', keys: ['Shift+Alt+Down'] },
        { id: 'run', keys: ['Ctrl+F5'] },
        { id: 'nothing', keys: [] },
    ];
    let changes = withKey({}, commands, 'line.copy', 'ctrl+d');
    assert.deepEqual(changes, { 'line.copy': 'Ctrl+D' });
    assert.deepEqual(clashes(commands, {}, 'line.copy', 'Ctrl+D'), ['select.next']);
    // the clash is settled by taking the key off the other command
    changes = withKey(changes, commands, 'select.next', '');
    assert.deepEqual(effectiveKeys(commands, changes), { 'select.next': [], 'line.copy': ['Ctrl+D'], run: ['Ctrl+F5'], nothing: [] });
    assert.deepEqual(clashes(commands, changes, 'line.copy', 'Ctrl+D'), []);
    // a key given to a command that had none, and a command back to its own key drops the change
    changes = withKey(changes, commands, 'nothing', 'Alt+N');
    changes = withKey(changes, commands, 'select.next', 'Ctrl+D');
    assert.deepEqual(changes, { 'line.copy': 'Ctrl+D', nothing: 'Alt+N' });
    assert.deepEqual(withKey({ nothing: 'Alt+N' }, commands, 'nothing', ''), {});
});

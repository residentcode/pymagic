// Checks of web/src/macro-model.js: `node web/test/macro.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { stepOf, compress, describe, comboOf, allowedCombo, tidy, recordedCommand, copiedText, pastedParts, isTableCommand } from '../src/macro-model.js';

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
    // the clipboard keys
    assert.deepEqual(stepOf(key('c', 'c')), { k: 'clip', op: 'copy' });
    assert.deepEqual(stepOf(key('x', 'c')), { k: 'clip', op: 'cut' });
    assert.deepEqual(stepOf(key('v', 'c')), { k: 'clip', op: 'paste' });
    assert.deepEqual(stepOf(key('Insert', 's')), { k: 'clip', op: 'paste' });
    assert.deepEqual(stepOf(key('Delete', 's')), { k: 'clip', op: 'cut' });
    // not in the table: a function key's command is recorded from the editor (macros.js), a lone modifier never
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


test('the commands the editor runs that a macro records: without the editor\'s prefix, not the typing, the clipboard, the pickers or its own', () => {
    assert.equal(recordedCommand('editor.action.revealDefinition'), 'editor.action.revealDefinition');
    assert.equal(recordedCommand('vs.editor.ICodeEditor:1:pymagic.refactor.rename'), 'pymagic.refactor.rename');
    for (const id of ['type', 'paste', 'replacePreviousChar', 'editor.action.quickCommand', 'vs.editor.ICodeEditor:1:pymagic.macro.toggle', '_internal', 'editor.action.clipboardCopyAction', '', null]) {
        assert.equal(recordedCommand(id), null, String(id));
    }
    assert.ok(isTableCommand('deleteLeft') && isTableCommand('editor.action.commentLine') && !isTableCommand('editor.action.rename'));
});

test('the macro\'s clipboard: what is copied and what each cursor gets', () => {
    assert.deepEqual(copiedText(['ab', '', 'cd'], ['x', 'y', 'z']), { text: 'ab\ncd', whole: false });
    // nothing selected: the lines whole, as the editor copies them
    assert.deepEqual(copiedText(['', ''], ['one', 'two']), { text: 'one\ntwo\n', whole: true });
    assert.deepEqual(pastedParts('a\nb', 2), ['a', 'b']);
    assert.deepEqual(pastedParts('a\nb\n', 2), ['a', 'b']);
    assert.deepEqual(pastedParts('a\nb', 3), ['a\nb', 'a\nb', 'a\nb']);
    assert.deepEqual(pastedParts('x', 1), ['x']);
});

test('clicks, the clipboard, menus and recorded commands are kept and described', () => {
    const steps = [
        { k: 'click', line: 3, column: 5, n: 1 }, { k: 'click', line: 3, column: 5, n: 2 }, { k: 'click', line: 4, column: 1, n: 1, mode: 'extend' },
        { k: 'click', line: 1, column: 1, n: 1, to: [2, 4] }, { k: 'clip', op: 'copy' }, { k: 'clip', op: 'paste' },
        { k: 'menu', path: 'Code > Reformat code' }, { k: 'cmd', id: 'editor.action.revealDefinition', label: 'Go to Definition' },
    ];
    assert.equal(describe(steps, 1000), 'Click 3:5 · Double-click 3:5 · Shift+Click 4:1 · Drag 1:1–2:4 · Copy · Paste · Menu Code > Reformat code · Go to Definition');
    const kept = tidy([{ name: 'm', steps: [...steps, { k: 'click', line: 0, column: 1 }, { k: 'clip', op: 'shred' }, { k: 'menu', path: 'nowhere' }, { k: 'click', line: 1, column: 1, to: ['a', 1] }] }]);
    assert.deepEqual(kept[0].steps, steps);
});

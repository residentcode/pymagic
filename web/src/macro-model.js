// Macros without the page: what a key press means to the editor (a character typed, a command of Monaco, a clipboard key), which commands
// the editor runs are recorded, what the macro's clipboard copies and pastes, how a recording is kept short, how a macro is described, and how
// a key combination is written and matched. No DOM here: web/test/macro.test.mjs checks it.

/** Keys that are commands of the editor: [key, ctrl, shift, alt] -> the handler or action a macro plays. */
const KEYS = [
    ['Backspace', 0, 0, 0, 'deleteLeft'], ['Delete', 0, 0, 0, 'deleteRight'], ['Backspace', 1, 0, 0, 'deleteWordLeft'], ['Delete', 1, 0, 0, 'deleteWordRight'],
    ['Tab', 0, 0, 0, 'tab'], ['Tab', 0, 1, 0, 'outdent'], ['Enter', 0, 0, 0, '\n'], ['Enter', 0, 1, 0, '\n'],
    ['ArrowLeft', 0, 0, 0, 'cursorLeft'], ['ArrowRight', 0, 0, 0, 'cursorRight'], ['ArrowUp', 0, 0, 0, 'cursorUp'], ['ArrowDown', 0, 0, 0, 'cursorDown'],
    ['ArrowLeft', 0, 1, 0, 'cursorLeftSelect'], ['ArrowRight', 0, 1, 0, 'cursorRightSelect'], ['ArrowUp', 0, 1, 0, 'cursorUpSelect'], ['ArrowDown', 0, 1, 0, 'cursorDownSelect'],
    ['ArrowLeft', 1, 0, 0, 'cursorWordLeft'], ['ArrowRight', 1, 0, 0, 'cursorWordRight'], ['ArrowLeft', 1, 1, 0, 'cursorWordLeftSelect'], ['ArrowRight', 1, 1, 0, 'cursorWordRightSelect'],
    ['Home', 0, 0, 0, 'cursorHome'], ['End', 0, 0, 0, 'cursorEnd'], ['Home', 0, 1, 0, 'cursorHomeSelect'], ['End', 0, 1, 0, 'cursorEndSelect'],
    ['Home', 1, 0, 0, 'cursorTop'], ['End', 1, 0, 0, 'cursorBottom'], ['Home', 1, 1, 0, 'cursorTopSelect'], ['End', 1, 1, 0, 'cursorBottomSelect'],
    ['PageUp', 0, 0, 0, 'cursorPageUp'], ['PageDown', 0, 0, 0, 'cursorPageDown'], ['PageUp', 0, 1, 0, 'cursorPageUpSelect'], ['PageDown', 0, 1, 0, 'cursorPageDownSelect'],
    ['a', 1, 0, 0, 'selectAll'], ['z', 1, 0, 0, 'undo'], ['y', 1, 0, 0, 'redo'], ['z', 1, 1, 0, 'redo'],
    ['ArrowUp', 0, 0, 1, 'editor.action.moveLinesUpAction'], ['ArrowDown', 0, 0, 1, 'editor.action.moveLinesDownAction'],
    ['ArrowUp', 0, 1, 1, 'editor.action.copyLinesUpAction'], ['ArrowDown', 0, 1, 1, 'editor.action.copyLinesDownAction'],
    ['k', 1, 1, 0, 'editor.action.deleteLines'], ['/', 1, 0, 0, 'editor.action.commentLine'], ['d', 1, 0, 0, 'editor.action.addSelectionToNextFindMatch'],
    ['Enter', 1, 0, 0, 'editor.action.insertLineAfter'], ['Enter', 1, 1, 0, 'editor.action.insertLineBefore'], ['l', 1, 0, 0, 'expandLineSelection'],
    [']', 1, 0, 0, 'editor.action.indentLines'], ['[', 1, 0, 0, 'editor.action.outdentLines'], ['Escape', 0, 0, 0, 'removeSecondaryCursors'],
];

const byKey = new Map(KEYS.map(([key, ctrl, shift, alt, id]) => [`${key.toLowerCase()}|${ctrl}|${shift}|${alt}`, id]));

/** Whether a command is one of the table's, which the editor plays itself (the others are played as Settings > Keymap runs them). */
const tableIds = new Set(KEYS.map((k) => k[4]));
export const isTableCommand = (id) => tableIds.has(id);

/** The clipboard keys: a macro plays them with its own clipboard, which starts as the system's (Ctrl+C, Ctrl+X, Ctrl+V and the Insert ones). */
const CLIP = new Map([['c|1|0|0', 'copy'], ['x|1|0|0', 'cut'], ['v|1|0|0', 'paste'], ['insert|1|0|0', 'copy'], ['insert|0|1|0', 'paste'], ['delete|0|1|0', 'cut']]);

/**
 * What the key press means: {k: 'text', text} | {k: 'cmd', id} | {k: 'clip', op}, or null when the table does not know it (a function key and
 * the other keys of commands: macros.js records the command the editor runs for them; a lone modifier).
 */
export function stepOf(e) {
    const key = e.key;
    if (!key || key === 'Control' || key === 'Shift' || key === 'Alt' || key === 'Meta' || key === 'Dead' || key === 'Process' || key === 'CapsLock') return null;
    const ctrl = e.ctrlKey || e.metaKey ? 1 : 0, shift = e.shiftKey ? 1 : 0, alt = e.altKey ? 1 : 0;
    const op = CLIP.get(`${key.toLowerCase()}|${ctrl}|${shift}|${alt}`);
    if (op) return { k: 'clip', op };
    const id = byKey.get(`${key.toLowerCase()}|${ctrl}|${shift}|${alt}`);
    if (id === '\n') return { k: 'text', text: '\n' };
    if (id) return { k: 'cmd', id };
    // a character: not with Ctrl or Alt alone (shortcuts), but with both together it is AltGr, which makes `@` or `{` on many keyboards
    if (key.length === 1 && ((!ctrl && !alt) || (ctrl && alt))) return { k: 'text', text: key };
    return null;
}

/** The steps with typed characters joined into one, so a long macro is short and plays quickly. */
export function compress(steps) {
    const out = [];
    for (const s of steps) {
        const last = out[out.length - 1];
        if (s.k === 'text' && last?.k === 'text') last.text += s.text;
        else out.push({ ...s });
    }
    return out;
}

const NAMES = {
    deleteLeft: 'Backspace', deleteRight: 'Delete', deleteWordLeft: 'Ctrl+Backspace', deleteWordRight: 'Ctrl+Delete', tab: 'Tab', outdent: 'Shift+Tab',
    cursorLeft: '←', cursorRight: '→', cursorUp: '↑', cursorDown: '↓', cursorHome: 'Home', cursorEnd: 'End', cursorTop: 'Ctrl+Home', cursorBottom: 'Ctrl+End',
    cursorWordLeft: 'Ctrl+←', cursorWordRight: 'Ctrl+→', cursorPageUp: 'PageUp', cursorPageDown: 'PageDown', selectAll: 'Ctrl+A', undo: 'Ctrl+Z', redo: 'Redo',
    'editor.action.deleteLines': 'Delete line', 'editor.action.commentLine': 'Comment line', 'editor.action.moveLinesUpAction': 'Move line up',
    'editor.action.moveLinesDownAction': 'Move line down', 'editor.action.copyLinesUpAction': 'Copy line up', 'editor.action.copyLinesDownAction': 'Copy line down',
    'editor.action.addSelectionToNextFindMatch': 'Add next match', 'editor.action.insertLineAfter': 'Line after', 'editor.action.insertLineBefore': 'Line before',
    expandLineSelection: 'Select line', 'editor.action.indentLines': 'Indent', 'editor.action.outdentLines': 'Outdent', removeSecondaryCursors: 'Esc',
};

const CLICKS = { 1: 'Click', 2: 'Double-click', 3: 'Triple-click' };

/** One step in words: a typed text, a key's name, a command's label, the clipboard, a click at line:column, an entry of the main menu. */
function stepName(s) {
    if (s.k === 'text') return s.text === '\n' ? 'Enter' : `‘${s.text.replace(/\n/g, '↵')}’`;
    if (s.k === 'clip') return s.op === 'copy' ? 'Copy' : s.op === 'cut' ? 'Cut' : 'Paste';
    if (s.k === 'click') {
        if (s.to) return `Drag ${s.line}:${s.column}–${s.to[0]}:${s.to[1]}`;
        return `${s.mode === 'extend' ? 'Shift+' : s.mode === 'add' ? 'Alt+' : ''}${CLICKS[s.n] ?? 'Click'} ${s.line}:${s.column}`;
    }
    if (s.k === 'menu') return `Menu ${s.path}`;
    return NAMES[s.id] ?? s.label ?? s.id.replace(/Select$/, '+Shift').replace(/^cursor/, '');
}

/** A macro in a line: "Typed ‘def f’ · Enter · Ctrl+→ …", for the list of macros. */
export function describe(steps, max = 80) {
    const text = steps.map(stepName).join(' · ');
    return text.length > max ? text.slice(0, max - 1) + '…' : text;
}

/** Commands the editor runs that a macro does not record: the typing and the clipboard (recorded as such), pickers whose choice is recorded instead, its own. */
const UNRECORDED = new Set(['type', 'paste', 'cut', 'copy', 'replacePreviousChar', 'compositionType', 'compositionStart', 'compositionEnd',
    'editor.action.quickCommand', 'pymagic.window.findAction', 'editor.action.clipboardCopyAction', 'editor.action.clipboardCutAction', 'editor.action.clipboardPasteAction',
    'setContext', 'hideSuggestWidget', 'closeParameterHints']);

/**
 * The command a macro keeps for one the editor ran (a function key's, a menu's of the editor, the command palette's): its id without the editor's
 * prefix ("vs.editor.ICodeEditor:1:pymagic.x" -> "pymagic.x", as Settings > Keymap keeps them), or null when it is not recorded.
 */
export function recordedCommand(commandId) {
    if (typeof commandId !== 'string' || !commandId || commandId.startsWith('_')) return null;
    const id = commandId.includes(':') ? commandId.slice(commandId.lastIndexOf(':') + 1) : commandId;
    if (!id || UNRECORDED.has(id) || id.startsWith('pymagic.macro.')) return null;
    return id;
}

/** What the clipboard gets from the selections: their texts one per line, or, when none has text, the cursors' lines whole (as the editor copies them). */
export function copiedText(selected, lines) {
    if (selected.some((t) => t)) return { text: selected.filter((t) => t).join('\n'), whole: false };
    return { text: lines.map((l) => l + '\n').join(''), whole: true };
}

/** What each of n cursors gets when the clipboard is pasted: a line each when it has as many lines as there are cursors, else all of it. */
export function pastedParts(text, n) {
    const lines = text.replace(/\n$/, '').split('\n');
    return n > 1 && lines.length === n ? lines : Array.from({ length: n }, () => text);
}

const posInt = (v) => Number.isInteger(v) && v >= 1;

/** Whether a kept step is one a macro can play. */
function goodStep(s) {
    if (!s) return false;
    if (s.k === 'text') return typeof s.text === 'string';
    if (s.k === 'cmd') return typeof s.id === 'string' && !!s.id;
    if (s.k === 'clip') return ['copy', 'cut', 'paste'].includes(s.op);
    if (s.k === 'click') return posInt(s.line) && posInt(s.column) && (s.to === undefined || (Array.isArray(s.to) && posInt(s.to[0]) && posInt(s.to[1])));
    if (s.k === 'menu') return typeof s.path === 'string' && s.path.includes(' > ');
    return false;
}

/** A key combination as it is kept and shown: "Ctrl+Alt+1", "Ctrl+Shift+F9". null for a lone modifier. */
export function comboOf(e) {
    const key = e.key;
    if (!key || ['Control', 'Shift', 'Alt', 'Meta'].includes(key)) return null;
    const name = key.length === 1 ? key.toUpperCase() : key === ' ' ? 'Space' : key.replace(/^Arrow/, '');
    return [e.ctrlKey || e.metaKey ? 'Ctrl' : '', e.altKey ? 'Alt' : '', e.shiftKey ? 'Shift' : '', name].filter(Boolean).join('+');
}

/** Whether a combination is one a macro may be given: it has Ctrl or Alt (or is a function key), so it never takes a typed character. */
export function allowedCombo(combo) {
    return !!combo && (/^(.*\+)?F\d{1,2}$/.test(combo) || /^(Ctrl|Alt)\+/.test(combo) || /\+(Ctrl|Alt)\+/.test(combo));
}

/** The macros as the project keeps them: [{name, key, steps}], tidied. */
export function tidy(list) {
    const seen = new Set();
    return (Array.isArray(list) ? list : []).filter((m) => m && typeof m.name === 'string' && m.name.trim() && Array.isArray(m.steps))
        .map((m) => ({ name: m.name.trim().slice(0, 60), key: typeof m.key === 'string' && allowedCombo(m.key) ? m.key : '', steps: compress(m.steps.filter(goodStep)) }))
        .filter((m) => m.steps.length && !seen.has(m.name) && seen.add(m.name));
}

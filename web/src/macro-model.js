// Macros without the page: what a key press means to the editor (a character typed, or a command of Monaco), how a recording is kept short,
// how a macro is described, and how a key combination is written and matched. No DOM here: web/test/macro.test.mjs checks it.

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

/** What the key press means: {k: 'text', text} | {k: 'cmd', id}, or null when a macro cannot carry it (a clipboard key, a function key, a lone modifier). */
export function stepOf(e) {
    const key = e.key;
    if (!key || key === 'Control' || key === 'Shift' || key === 'Alt' || key === 'Meta' || key === 'Dead' || key === 'Process' || key === 'CapsLock') return null;
    const ctrl = e.ctrlKey || e.metaKey ? 1 : 0, shift = e.shiftKey ? 1 : 0, alt = e.altKey ? 1 : 0;
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

/** A macro in a line: "Typed ‘def f’ · Enter · Ctrl+→ …", for the list of macros. */
export function describe(steps, max = 80) {
    const parts = steps.map((s) => (s.k === 'text' ? (s.text === '\n' ? 'Enter' : `‘${s.text.replace(/\n/g, '↵')}’`) : (NAMES[s.id] ?? s.id.replace(/Select$/, '+Shift').replace(/^cursor/, ''))));
    const text = parts.join(' · ');
    return text.length > max ? text.slice(0, max - 1) + '…' : text;
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
        .map((m) => ({ name: m.name.trim().slice(0, 60), key: typeof m.key === 'string' && allowedCombo(m.key) ? m.key : '', steps: compress(m.steps.filter((s) => (s.k === 'text' && typeof s.text === 'string') || (s.k === 'cmd' && typeof s.id === 'string'))) }))
        .filter((m) => m.steps.length && !seen.has(m.name) && seen.add(m.name));
}

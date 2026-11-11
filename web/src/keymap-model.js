// Keyboard shortcuts as the person changes them in Settings > Keymap, without the page: a key written as text ("Ctrl+Shift+D"), read from a key
// press, turned into Monaco's number, and the changes kept in the settings (`keybindings`: command id -> key, "" for none). keymap.js applies them.

/** KeyboardEvent.code -> the name of Monaco's KeyCode, for the keys that can be part of a shortcut. */
const CODE_NAMES = {
    ArrowUp: 'UpArrow', ArrowDown: 'DownArrow', ArrowLeft: 'LeftArrow', ArrowRight: 'RightArrow',
    Enter: 'Enter', NumpadEnter: 'Enter', Escape: 'Escape', Tab: 'Tab', Backspace: 'Backspace', Delete: 'Delete', Insert: 'Insert',
    Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown', Space: 'Space',
    Minus: 'Minus', Equal: 'Equal', BracketLeft: 'BracketLeft', BracketRight: 'BracketRight', Backslash: 'Backslash', Semicolon: 'Semicolon',
    Quote: 'Quote', Backquote: 'Backquote', Comma: 'Comma', Period: 'Period', Slash: 'Slash', IntlBackslash: 'IntlBackslash',
};
/** The name of a key as a person reads it (and writes it in the settings). */
const KEY_TEXT = {
    UpArrow: 'Up', DownArrow: 'Down', LeftArrow: 'Left', RightArrow: 'Right', Escape: 'Escape', Enter: 'Enter', Tab: 'Tab', Backspace: 'Backspace',
    Delete: 'Delete', Insert: 'Insert', Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown', Space: 'Space',
    Minus: '-', Equal: '=', BracketLeft: '[', BracketRight: ']', Backslash: '\\', Semicolon: ';', Quote: "'", Backquote: '`', Comma: ',', Period: '.', Slash: '/', IntlBackslash: '<',
};
const TEXT_KEY = Object.fromEntries(Object.entries(KEY_TEXT).map(([k, v]) => [v.toLowerCase(), k]));

/** Monaco's KeyCode name of the key of a KeyboardEvent.code ("KeyD" -> "KeyD", "Digit5" -> "Digit5", "F5" -> "F5", "ArrowUp" -> "UpArrow"); null for a modifier or a key not used in shortcuts. */
export function keyName(code) {
    if (/^Key[A-Z]$/.test(code) || /^Digit[0-9]$/.test(code) || /^F([1-9]|1[0-9])$/.test(code)) return code;
    if (/^Numpad[0-9]$/.test(code)) return 'Digit' + code.slice(6);
    return CODE_NAMES[code] ?? null;
}

/** A shortcut as text, from a key press: "Ctrl+Shift+D"; null while only modifiers are held, or for a key that cannot be one. */
export function keyFromEvent(e) {
    const name = keyName(e.code ?? '');
    if (!name) return null;
    const parts = [];
    if (e.ctrlKey) parts.push('Ctrl');
    if (e.shiftKey) parts.push('Shift');
    if (e.altKey) parts.push('Alt');
    if (e.metaKey) parts.push('Win');
    parts.push(textOf(name));
    return parts.join('+');
}

function textOf(name) {
    if (/^Key[A-Z]$/.test(name)) return name.slice(3);
    if (/^Digit[0-9]$/.test(name)) return name.slice(5);
    return KEY_TEXT[name] ?? name;
}

/** "Ctrl+Shift+D" -> { ctrl, shift, alt, win, key: 'KeyD' }; null when it is not a shortcut. Modifiers in any order; "+" itself is written "Plus". */
export function parseKey(text) {
    const parts = String(text ?? '').split('+').map((p) => p.trim()).filter(Boolean);
    if (!parts.length) return null;
    const out = { ctrl: false, shift: false, alt: false, win: false, key: null };
    for (const [i, p] of parts.entries()) {
        const low = p.toLowerCase();
        if (i < parts.length - 1) {
            if (low === 'ctrl' || low === 'control') out.ctrl = true;
            else if (low === 'shift') out.shift = true;
            else if (low === 'alt') out.alt = true;
            else if (low === 'win' || low === 'meta' || low === 'cmd') out.win = true;
            else return null;
            continue;
        }
        if (/^[a-z]$/i.test(p)) out.key = 'Key' + p.toUpperCase();
        else if (/^[0-9]$/.test(p)) out.key = 'Digit' + p;
        else if (/^f([1-9]|1[0-9])$/i.test(p)) out.key = 'F' + p.slice(1);
        else out.key = TEXT_KEY[low] ?? (Object.values(CODE_NAMES).includes(p) ? p : null);
    }
    return out.key ? out : null;
}

/** The text of a shortcut written the one way ("shift+ctrl+d" -> "Ctrl+Shift+D"); "" for none or for what is not a shortcut. */
export function normaliseKey(text) {
    const k = parseKey(text);
    if (!k) return '';
    return [k.ctrl && 'Ctrl', k.shift && 'Shift', k.alt && 'Alt', k.win && 'Win', textOf(k.key)].filter(Boolean).join('+');
}

/** Monaco's number for a shortcut written as text (KeyMod and KeyCode are Monaco's); null when it is not one. */
export function toKeybinding(text, KeyMod, KeyCode) {
    const k = parseKey(text);
    if (!k || KeyCode[k.key] === undefined) return null;
    return (k.ctrl ? KeyMod.CtrlCmd : 0) | (k.shift ? KeyMod.Shift : 0) | (k.alt ? KeyMod.Alt : 0) | (k.win ? KeyMod.WinCtrl : 0) | KeyCode[k.key];
}

/**
 * The shortcuts as they are with the person's changes: `commands` is [{ id, keys: [text] }] with the keys each starts with; `changes` the settings'
 * id -> key ("" for none). Returns id -> [keys].
 */
export function effectiveKeys(commands, changes = {}) {
    const out = {};
    for (const c of commands) out[c.id] = Object.hasOwn(changes, c.id) ? (changes[c.id] ? [normaliseKey(changes[c.id])] : []) : c.keys.map(normaliseKey).filter(Boolean);
    return out;
}

/** The other commands that answer to a key, with the person's changes: [id]. */
export function clashes(commands, changes, id, key) {
    const want = normaliseKey(key);
    if (!want) return [];
    const now = effectiveKeys(commands, changes);
    return Object.entries(now).filter(([other, keys]) => other !== id && keys.includes(want)).map(([other]) => other);
}

/** The settings' changes after a command gets a key (or "" for none); a key back to what the command starts with drops the change. */
export function withKey(changes, commands, id, key) {
    const next = { ...changes };
    const start = (commands.find((c) => c.id === id)?.keys ?? []).map(normaliseKey).filter(Boolean);
    const want = normaliseKey(key);
    if ((want === '' && start.length === 0) || (start.length === 1 && start[0] === want)) delete next[id];
    else next[id] = want;
    return next;
}

// ------------------------------------------------------------ keymaps: the keys a command starts with, as PyCharm or VS Code give them

/** The keymaps to choose from (Settings > Keymap, the welcome screen's Customize): [id, name]. */
export const KEYMAPS = [['pymagic', "PyMagic's own"], ['pycharm', 'PyCharm (Windows)'], ['vscode', 'VS Code']];

/**
 * Each keymap's keys where it differs from PyMagic's own: command id -> key ("" for none). PyMagic's commands and Monaco's own actions alike;
 * a key it takes from another command gives that one its own key in the keymap too (PyCharm's F2 is the next problem, so rename is Shift+F6).
 */
export const KEYMAP_KEYS = {
    pymagic: {},
    pycharm: {
        'pymagic.run.run': 'Shift+F10', 'pymagic.run.debug': 'Shift+F9', 'pymagic.run.stop': 'Ctrl+F2',
        'pymagic.run.stepOver': 'F8', 'pymagic.run.stepInto': 'F7', 'pymagic.run.stepOut': 'Shift+F8',
        'pymagic.run.toggleBreakpoint': 'Ctrl+F8', 'pymagic.run.editBreakpoint': 'Ctrl+Shift+F8',
        'pymagic.bookmarks.toggle': 'F11', 'pymagic.bookmarks.next': '', 'pymagic.bookmarks.previous': '',
        'pymagic.search.symbol': 'Ctrl+Alt+Shift+N', 'pymagic.edit.organizeImports': 'Ctrl+Alt+O',
        'pymagic.window.searchEverywhere': 'Ctrl+Shift+N', 'pymagic.window.settings': 'Ctrl+Alt+S', 'pymagic.window.terminal': 'Alt+F12',
        'pymagic.tabs.close': 'Ctrl+F4', 'pymagic.tabs.next': 'Alt+Right', 'pymagic.tabs.previous': 'Alt+Left',
        'editor.action.copyLinesDownAction': 'Ctrl+D', 'editor.action.addSelectionToNextFindMatch': 'Alt+J', 'editor.action.selectHighlights': 'Ctrl+Alt+Shift+J',
        'editor.action.deleteLines': 'Ctrl+Y', 'redo': 'Ctrl+Shift+Z',
        'editor.action.smartSelect.expand': 'Ctrl+W', 'editor.action.smartSelect.shrink': 'Ctrl+Shift+W',
        'editor.action.revealDefinition': 'Ctrl+B', 'editor.action.goToImplementation': 'Ctrl+Alt+B', 'editor.action.goToReferences': 'Alt+F7',
        'editor.action.rename': 'Shift+F6', 'editor.action.marker.nextInFiles': 'F2', 'editor.action.marker.prevInFiles': 'Shift+F2',
        'editor.action.moveLinesUpAction': 'Ctrl+Shift+Up', 'editor.action.moveLinesDownAction': 'Ctrl+Shift+Down',
        'editor.action.startFindReplaceAction': 'Ctrl+R', 'editor.action.showHover': 'Ctrl+Q', 'editor.action.joinLines': 'Ctrl+Shift+J',
        'editor.action.triggerParameterHints': 'Ctrl+P', 'editor.action.quickFix': 'Alt+Enter',
    },
    vscode: {
        'pymagic.edit.format': 'Shift+Alt+F', 'editor.action.formatDocument': '',
        'pymagic.window.findAction': 'Ctrl+Shift+P', 'pymagic.search.symbol': 'Ctrl+T',
        'pymagic.tabs.next': 'Ctrl+PageDown', 'pymagic.tabs.previous': 'Ctrl+PageUp',
        'pymagic.nav.back': 'Alt+Left', 'pymagic.nav.forward': 'Alt+Right',
    },
};

/** The commands with the keys a keymap starts them with (Settings > Keymap's list, its Reset); the others keep their own. */
export function withKeymap(commands, keymap) {
    const keys = KEYMAP_KEYS[keymap] ?? {};
    return commands.map((c) => (Object.hasOwn(keys, c.id) ? { ...c, keys: keys[c.id] ? [normaliseKey(keys[c.id])] : [] } : c));
}

/** The changes in force: the keymap's keys, then the person's own over them. */
export function keymapChanges(keymap, changes = {}) {
    return { ...(KEYMAP_KEYS[keymap] ?? {}), ...changes };
}

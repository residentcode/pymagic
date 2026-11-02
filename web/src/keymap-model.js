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

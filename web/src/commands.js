// PyMagic's commands with keys: each has an id, a name and a group (Settings > Keymap lists them), and acts on the editor that has the focus.
// The keys are Monaco's rules, so the person's changes (the `keybindings` setting: id -> key, "" for none) are rules too: the key a command starts
// with is taken away ("-id") and the new one added, for PyMagic's commands and for Monaco's own (Ctrl+D, Alt+Up...) alike. A command marked
// `everywhere` also answers when the focus is outside the editors (a panel, the tree).
import * as monaco from 'monaco-editor';
import { toKeybinding, normaliseKey, keyFromEvent, effectiveKeys } from './keymap-model.js';

const registry = new Map();       // id -> { id, label, group, keys, run, when, everywhere }
let main = null;                  // the editor a command acts on when none has the focus
let lastFocused = null;

monaco.editor.onDidCreateEditor((ed) => ed.onDidFocusEditorText?.(() => { lastFocused = ed; }));

/** The editor a command acts on: the one with the focus, else the one that had it last, else the main one. */
export function focusedEditor() {
    return monaco.editor.getEditors().find((e) => e.hasTextFocus()) ?? (lastFocused && !lastFocused.getModel()?.isDisposed?.() ? lastFocused : null) ?? main;
}

/**
 * A command of PyMagic with its keys (as text: "Ctrl+F5"); `run(editor)` gets the editor that has the focus. Defined once: a second definition of
 * the same id (the same keys for another editor) is ignored.
 */
export function defineCommand({ id, label, group, keys = [], run, when, everywhere = false, terminal = false, noRepeat = false }) {
    if (registry.has(id)) return;
    // terminal: the key also works when the focus is in a terminal (the terminal leaves it alone); noRepeat: a held key runs it once
    const entry = { id, label, group, keys: keys.map(normaliseKey).filter(Boolean), run, when, everywhere: everywhere || terminal, terminal, noRepeat, rules: [] };
    registry.set(id, entry);
    // in an editor Monaco runs a command for each repeat of a held key: one held a moment counts once
    let last = 0;
    monaco.editor.registerCommand(id, (_accessor, ...args) => {
        if (noRepeat && Date.now() - last < 400) { last = Date.now(); return; }
        last = Date.now();
        return run(focusedEditor(), ...args);
    });
    ownKeys(entry, true);
}

/** Puts a command's own keys in force (on) or takes them away (off): PyMagic added these rules, so it can drop them (Monaco's "-id" does not reach them). */
function ownKeys(entry, on) {
    if (!on) { for (const d of entry.rules) d.dispose(); entry.rules = []; return; }
    if (entry.rules.length) return;
    for (const k of entry.keys) {
        const kb = toKeybinding(k, monaco.KeyMod, monaco.KeyCode);
        if (kb !== null) entry.rules.push(monaco.editor.addKeybindingRule({ keybinding: kb, command: entry.id, when: entry.when }));
    }
}

/** PyMagic's commands with keys, as defined (the keys they start with). */
export function pymagicCommands() {
    return [...registry.values()].map(({ id, label, group, keys, everywhere, terminal, noRepeat }) => ({ id, label, group, keys, everywhere, terminal, noRepeat }));
}

/** The keys of a command now, with the person's changes. */
export function keysOf(id) {
    return effectiveKeys(pymagicCommands().filter((c) => c.id === id), changesNow)[id] ?? [];
}

/**
 * The key a menu shows for a command it names by the key it starts with ("Ctrl+D"): the person's key when that command was given another,
 * "" when it was left without one, the same key otherwise (also when no command of the keymap starts with it).
 */
export function currentKeyLabel(key) {
    const want = normaliseKey(key);
    if (!want || !Object.keys(changesNow).length) return key;
    // the changed commands by the keys they start with, made again when the changes change
    if (!byStartKey) {
        byStartKey = new Map();
        for (const c of keymapCommands()) if (Object.hasOwn(changesNow, c.id)) for (const k of c.keys) if (!byStartKey.has(k)) byStartKey.set(k, c.id);
    }
    const owner = byStartKey.get(want);
    if (!owner) return key;
    return changesNow[owner] ? normaliseKey(changesNow[owner]) : '';
}
let byStartKey = null;

/** Whether a key press in a terminal is one of the window's (Ctrl+` and the like): the terminal leaves those to the window. */
export function isTerminalKey(e) {
    const key = keyFromEvent(e);
    if (!key) return false;
    const now = effectiveKeys(pymagicCommands().filter((c) => c.terminal), changesNow);
    return Object.values(now).some((keys) => keys.includes(key));
}

// ---------------------------------------------------------------- the person's changes

let applied = [];                  // the rules of the changes now in force
let changesNow = {};
let baseline = null;               // command -> [{ key, when }]: Monaco's own keys, as they were before any change

/** The keys of every command as Monaco has them before any change of the person's (an action of an editor is listed under its own id). */
function readBaseline() {
    const svc = main?._standaloneKeybindingService;
    const out = new Map();
    for (const k of svc?.getKeybindings?.() ?? []) {
        const command = k.command ?? '';
        if (!command || command.startsWith('-') || command.startsWith('DYNAMIC_')) continue;
        const label = k.resolvedKeybinding?.getLabel?.() ?? '';
        if (!label || label.includes(' ')) continue;   // a chord (Ctrl+K Ctrl+C) is left as it is
        const key = normaliseKey(label);
        if (!key) continue;
        if (!out.has(command)) out.set(command, []);
        // the condition as text, which is what a new rule takes (Monaco keeps it as an expression)
        out.get(command).push({ key, when: k.when?.serialize?.() || undefined });
    }
    return out;
}

/** The commands of the Keymap page: PyMagic's and Monaco's actions of the editor, each with its name, group and the keys it starts with. */
export function keymapCommands() {
    baseline ??= readBaseline();
    const list = new Map();
    for (const c of pymagicCommands()) list.set(c.id, { id: c.id, label: c.label, group: c.group, keys: c.keys });
    // the editor's actions: Monaco's (Ctrl+D, comments, lines...) and those PyMagic added to the editor (their keys are under "<editor>:<id>")
    for (const a of main?.getSupportedActions?.() ?? []) {
        // an action PyMagic added to the editor is "<editor>:<id>": it is kept under its own id, the same for every editor
        const id = a.id.includes(':') ? a.id.slice(a.id.lastIndexOf(':') + 1) : a.id;
        if (list.has(id) || !a.label) continue;
        const own = baseline.get(a.id) ?? baseline.get(id) ?? [...baseline.entries()].find(([cmd]) => cmd.endsWith(':' + id))?.[1] ?? [];
        list.set(id, { id, label: a.label, group: id.startsWith('pymagic.') ? 'PyMagic' : 'Editor', keys: [...new Set(own.map((o) => o.key))] });
    }
    return [...list.values()];
}

/** Puts the person's changes in force: the rules of the last ones go, then for each changed command its own keys are taken away and the new one added. */
export function applyKeybindings(changes) {
    baseline ??= readBaseline();
    for (const d of applied) d.dispose();
    applied = [];
    stale = new Set();
    byStartKey = null;
    changesNow = { ...(changes ?? {}) };
    const { KeyMod, KeyCode } = monaco;
    const rule = (key, command, when) => {
        const kb = toKeybinding(key, KeyMod, KeyCode);
        if (kb !== null) applied.push(monaco.editor.addKeybindingRule({ keybinding: kb, command, when }));
    };
    // PyMagic's commands: their own keys when unchanged, else none of them and the new one
    for (const entry of registry.values()) {
        const changed = Object.hasOwn(changesNow, entry.id);
        ownKeys(entry, !changed);
        if (changed && changesNow[entry.id]) rule(changesNow[entry.id], entry.id, entry.when);
    }
    for (const [id, key] of Object.entries(changesNow)) {
        if (registry.has(id)) continue;
        // Monaco's commands and the editor's actions ("<editor>:<id>"): their keys are taken away with "-id", and the new one added
        const targets = [...(baseline?.entries() ?? [])].filter(([cmd]) => cmd === id || cmd.endsWith(':' + id));
        for (const [cmd, keys] of targets) {
            for (const k of keys) {
                rule(k.key, '-' + cmd, k.when);
                // an action an editor was given ("<editor>:<id>") keeps its key whatever "-id" says: its old key is caught before it (setupCommands)
                if (cmd.includes(':')) stale.add(k.key);
            }
            if (key) rule(key, cmd, keys[0]?.when);
        }
    }
    // a stale key that another command got now runs that one
    given = new Map();
    for (const [id, key] of Object.entries(changesNow)) if (key && stale.has(key)) given.set(key, id);
}

let stale = new Set();             // old keys of editors' actions whose key changed
let given = new Map();             // a stale key given to another command -> that command

/** Runs a command of the keymap by its id: PyMagic's, else an action of the editor (Monaco's or one PyMagic added). */
function runById(id, ed) {
    const own = registry.get(id);
    if (own) return void own.run(ed);
    const action = ed?.getAction?.(id) ?? ed?.getSupportedActions?.().find((a) => a.id.endsWith(':' + id));
    if (action) void action.run();
    else ed?.trigger('keymap', id, null);
}

/** Starts the keymap: the main editor (the one a command acts on when none has the focus, and whose actions are listed). */
export function setupCommands(editor) {
    main = editor;
    // in an editor, before Monaco: the old key of an editor's action that got another key does nothing, or what it was given to now
    document.addEventListener('keydown', (e) => {
        if (!stale.size || !(e.target instanceof Element) || !e.target.closest('.monaco-editor')) return;
        const key = keyFromEvent(e);
        if (!key || !stale.has(key)) return;
        e.preventDefault();
        e.stopPropagation();
        const to = given.get(key);
        if (to) runById(to, focusedEditor());
    }, true);
    // outside the editors (a panel, the file tree, a field), the commands marked "everywhere" answer to their keys as they are now
    document.addEventListener('keydown', (e) => {
        // the editors have the rules; a terminal's keys are the program's in it, but for the window's own (Ctrl+`)
        if (e.defaultPrevented || (e.target instanceof Element && e.target.closest('.monaco-editor'))) return;
        const inTerminal = e.target instanceof Element && !!e.target.closest('.xterm');
        const key = keyFromEvent(e);
        if (!key) return;
        const now = effectiveKeys(pymagicCommands().filter((c) => c.everywhere && (!inTerminal || c.terminal)), changesNow);
        const hit = Object.entries(now).find(([, keys]) => keys.includes(key));
        if (!hit) return;
        e.preventDefault();
        const entry = registry.get(hit[0]);
        if (e.repeat && entry.noRepeat) return;
        void entry.run(focusedEditor());
    });
}

// The settings of the person using PyMagic: what they are, where they are kept (the engine keeps one file for the person, /api/settings), and how
// they change the page and every editor. The dialog that edits them is settings-ui.js.

export const MONO = '"Cascadia Code", "Cascadia Mono", Consolas, "Courier New", monospace';

export const DEFAULTS = {
    theme: 'dark',            // dark | light | system
    fontFamily: MONO,
    fontSize: 14,
    lineHeight: 22,
    ligatures: false,
    wordWrap: 'off',          // off | on
    minimap: false,
    lineNumbers: 'on',        // on | relative | off
    whitespace: 'selection',  // none | selection | boundary | all
    smoothScrolling: true,
    cursorStyle: 'line',      // line | block | underline
    cursorBlinking: 'blink',  // blink | smooth | phase | expand | solid
    bracketColors: false,
    stickyScroll: false,
    ruler: 0,                 // the column of a vertical guide, 0 for none
    autoSave: 'off',          // off | blur (when the window is left) | delay (a second after the last change)
    confirmGit: true,         // the bar's update and push ask first
};

let current = { ...DEFAULTS };
const listeners = new Set();

export const settings = () => current;
export const onSettings = (fn) => { listeners.add(fn); return () => listeners.delete(fn); };

/** What is in the file of the person, over the defaults; a value of the wrong kind is dropped. */
export function merged(stored) {
    const out = { ...DEFAULTS };
    for (const [key, fallback] of Object.entries(DEFAULTS)) {
        const v = stored?.[key];
        if (v !== undefined && typeof v === typeof fallback) out[key] = v;
    }
    out.fontSize = Math.min(40, Math.max(8, Math.round(out.fontSize)));
    out.lineHeight = Math.min(80, Math.max(12, Math.round(out.lineHeight)));
    out.ruler = Math.min(400, Math.max(0, Math.round(out.ruler)));
    return out;
}

export async function loadSettings() {
    try {
        const res = await fetch('/api/settings');
        current = merged(res.ok ? await res.json() : {});
    }
    catch { current = { ...DEFAULTS }; }
    return current;
}

let saving = 0;
/** Changes some settings: the page and the editors follow at once, the file of the person is written a moment later. */
export function change(patch) {
    current = merged({ ...current, ...patch });
    for (const fn of listeners) fn(current);
    window.clearTimeout(saving);
    saving = window.setTimeout(() => {
        fetch('/api/settings', { method: 'PUT', body: JSON.stringify(current) }).catch(() => { /* the choice holds until the page is closed */ });
    }, 300);
}

export function resetAll() { change({ ...DEFAULTS }); }

/** The theme in force: the setting, or what the system says when it is "system". */
export function themeNow(s = current) {
    if (s.theme === 'system') return window.matchMedia?.('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
    return s.theme;
}

/** Monaco's options for these settings. */
export function editorOptions(s = current) {
    return {
        fontFamily: s.fontFamily,
        fontSize: s.fontSize,
        lineHeight: s.lineHeight,
        fontLigatures: s.ligatures,
        wordWrap: s.wordWrap,
        minimap: { enabled: s.minimap },
        lineNumbers: s.lineNumbers,
        renderWhitespace: s.whitespace,
        smoothScrolling: s.smoothScrolling,
        cursorStyle: s.cursorStyle,
        cursorBlinking: s.cursorBlinking,
        bracketPairColorization: { enabled: s.bracketColors },
        stickyScroll: { enabled: s.stickyScroll },
        rulers: s.ruler > 0 ? [s.ruler] : [],
    };
}

/** Makes the page and the editors follow the settings, now and whenever they change; `themes` maps 'dark' and 'light' to Monaco's theme names. */
export function applySettings(monaco, themes, main) {
    const apply = (s) => {
        const theme = themeNow(s);
        document.documentElement.dataset.theme = theme;
        monaco.editor.setTheme(themes[theme]);
        const opts = editorOptions(s);
        // the cells of a notebook share the type, not the rest of the editor's choices
        const type = { fontFamily: opts.fontFamily, fontSize: opts.fontSize, fontLigatures: opts.fontLigatures };
        for (const e of monaco.editor.getEditors()) e.updateOptions(e === main ? opts : type);
    };
    onSettings(apply);
    window.matchMedia?.('(prefers-color-scheme: light)').addEventListener?.('change', () => { if (current.theme === 'system') apply(current); });
    apply(current);
}

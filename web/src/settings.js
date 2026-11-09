// The settings of the person using PyMagic: what they are, where they are kept (the engine keeps one file for the person, /api/settings), and how
// they change the page and every editor. The dialog that edits them is settings-ui.js.

export const MONO = '"Cascadia Code", "Cascadia Mono", Consolas, "Courier New", monospace';

export const DEFAULTS = {
    theme: 'dark',            // dark | light | system
    codeColors: 'vscode',     // vscode (VS Code's Dark Modern / Light Modern) | pymagic (PyMagic's own palette)
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
    bracketColors: true,      // as VS Code has it
    stickyScroll: false,
    ruler: 0,                 // the column of a vertical guide, 0 for none
    autoSave: 'off',          // off | blur (when the window is left) | delay (a second after the last change)
    phpPath: '',              // Settings > PHP: php.exe (or its folder); empty: the one found (PATH, C:\php, XAMPP, Laragon, WAMP, Herd, Scoop)
    composerPath: '',         // Composer: composer.bat / .exe / .phar; empty: the one found
    xdebugPath: '',           // Xdebug's DLL for Debug; empty: php.ini's own, or php_xdebug*.dll in PHP's ext folder
    tailwindIn: 'project',    // project (one that uses Tailwind) | all: where the Tailwind CSS plugin completes its classes
    openProjectIn: 'ask',     // ask | this (replaces the project of the window) | new (a window of its own): the desktop app's Open Project
    confirmGit: true,         // the bar's update and push ask first
    terminalShell: 'auto',    // the id of the shell new terminals start (/api/shells), auto for PowerShell 7 or Windows PowerShell
    tabLimit: 0,              // the most editor tabs kept open, the ones used longest ago closing first; 0 for no limit
    tabClose: true,           // each tab has its ✕
    breadcrumbs: true,        // the file and the class and function of the cursor, above the editor
    methodSeparators: false,  // a line above each function and method
    lensMode: true,           // hovering the scroll bar shows that part of the file
    previewTab: false,        // a file opened with one click replaces the last one opened so, until it is edited
    plugins: {},              // Settings > Plugins: plugin id -> false for one switched off; every plugin is on unless it is here
    keybindings: {},          // Settings > Keymap: command id -> key ("Ctrl+Shift+D"), "" for none; what is not here keeps its own key
    codeVision: true,         // usages and inheritors above functions and classes
    codeVisionAuthor: true,   // and who wrote most of each (Git blame)
    hintParams: true,         // the names of parameters before arguments
    hintTypes: true,          // inferred return and variable types, the types along a method chain
    callParens: true,         // completing a function or method writes its parentheses too, `area()` or `area(|)`
    grammar: false,           // sentences of comments, docstrings, Markdown and commit messages checked by LanguageTool (downloaded once, with a yes)
    grammarLanguage: 'en-US', // the language LanguageTool checks in
    spelling: true,           // typos in comments, strings, names and Markdown, marked as hints (Windows' own spell checker)
    strictUnions: true,       // a union argument fits only when every member does (PyCharm 2026.1's default); off: when one member does
    quotes: 'single',         // single | double | preserve: the quotes the formatter gives strings when the project's pyproject.toml, ruff.toml or .editorconfig does not say
};

/** A completion's text as the settings want it: without the call's parentheses when those are not to be written. */
export function withoutCallParens(insertText, s = current) {
    if (s.callParens || typeof insertText !== 'string') return insertText;
    const m = /^([A-Za-z_][\w]*)\((\$0)?\)$/.exec(insertText);
    return m ? m[1] : insertText;
}

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
    out.tabLimit = Math.min(100, Math.max(0, Math.round(out.tabLimit)));
    // only text keys of command ids (a broken file changes no key)
    out.plugins = out.plugins && !Array.isArray(out.plugins) ? Object.fromEntries(Object.entries(out.plugins).filter(([k, v]) => k && v === false)) : {};
    out.keybindings = out.keybindings && !Array.isArray(out.keybindings) ? Object.fromEntries(Object.entries(out.keybindings).filter(([k, v]) => k && typeof v === 'string')) : {};
    return out;
}

let startPlugins = {};
/** The plugins as they were when the window opened: those are the ones set up (a change takes effect at the next reload). */
export const pluginsAtStart = () => startPlugins;

export async function loadSettings() {
    try {
        const res = await fetch('/api/settings');
        current = merged(res.ok ? await res.json() : {});
    }
    catch { current = { ...DEFAULTS }; }
    startPlugins = current.plugins;
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

export function resetAll() { change({ ...DEFAULTS, plugins: current.plugins }); }   // the plugins are not the editor's look: Settings > Plugins switches them

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
        mouseWheelZoom: true,
        codeLens: s.codeVision || s.codeVisionAuthor,
        inlayHints: { enabled: s.hintParams || s.hintTypes ? 'on' : 'off' },
        linkedEditing: true,   // a tag's two names renamed together (the HTML tag pairs plugin gives the pairs; without it there are none)
        inlineSuggest: { enabled: true },   // the rest of a line in grey (the full line completion plugin writes it; without it there is none)
    };
}

// the editors that take all of the editor's choices: the main one and the right side of a split
const fullEditors = new Set();

/** An editor that follows every setting, as the main one does (the right side of a split). */
export function followSettings(editor) {
    fullEditors.add(editor);
    editor.updateOptions(editorOptions(current));
    editor.onDidDispose(() => fullEditors.delete(editor));
}

/** Makes the page and the editors follow the settings, now and whenever they change; `themeOf(codeColors, 'dark' | 'light')` names Monaco's theme. */
export function applySettings(monaco, themeOf, main) {
    fullEditors.add(main);
    const apply = (s) => {
        const theme = themeNow(s);
        document.documentElement.dataset.theme = theme;
        document.documentElement.classList.toggle('no-tab-close', !s.tabClose);
        monaco.editor.setTheme(themeOf(s.codeColors, theme));
        const opts = editorOptions(s);
        // the cells of a notebook share the type, not the rest of the editor's choices
        const type = { fontFamily: opts.fontFamily, fontSize: opts.fontSize, fontLigatures: opts.fontLigatures };
        for (const e of monaco.editor.getEditors()) e.updateOptions(fullEditors.has(e) ? opts : type);
    };
    onSettings(apply);
    window.matchMedia?.('(prefers-color-scheme: light)').addEventListener?.('change', () => { if (current.theme === 'system') apply(current); });
    apply(current);
}

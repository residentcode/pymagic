// The Terminal panel: real consoles for the project. Each tab holds one terminal or several side by side (Split); each terminal is a shell in a
// Windows pseudo console on the host (/console), drawn by xterm.js: the interactive python prompt with its line editing, progress bars,
// full-screen programs, colours, Ctrl+C, copy and paste, all as in Windows Terminal. The project's interpreter is first on the PATH and its
// virtual environment active. The shell is the one of the settings (Terminal page) or one picked with ▾; a shell that ended starts again with
// Enter. The tabs, their names, their shells and what their screens showed are kept, and open again (with new shells) when PyMagic starts again.
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import { showTab } from './panel';
import { cleanName, nextName, afterClose, keptLines, restoredTabs, MAX_PANES, RESTORED } from './termtabs';
import { ansiReader, cssOf } from './ansi';
import { settings } from './settings';
import { openMenu } from './menu';

const $ = (id) => document.getElementById(id);
const KEEP_KEY = 'pymagic.terminals';
// a window opened with Open Tab in New Window (?open=) neither keeps its terminals nor takes those of the first window
const SECOND_WINDOW = new URLSearchParams(location.search).has('open');

/** The pieces of coloured output as spans (`cls` marks what PyMagic itself prints): the Run output uses it. */
export function colouredSpans(reader, text, cls = '') {
    return reader.feed(text).map((piece) => {
        const span = document.createElement('span');
        const css = cssOf(piece.style);
        span.className = [cls, css.className].filter(Boolean).join(' ');
        if (css.style) span.style.cssText = css.style;
        span.textContent = piece.text;
        return span;
    });
}

/** xterm's colours from the page's theme: the background and text of the editor, the 16 colours of the Run output. */
function themeOf() {
    const css = getComputedStyle(document.documentElement);
    const v = (name, fallback) => css.getPropertyValue(name).trim() || fallback;
    const names = ['black', 'red', 'green', 'yellow', 'blue', 'magenta', 'cyan', 'white'];
    const theme = { background: v('--bg', '#0f111a'), foreground: v('--fg', '#c8ccdc'), cursor: v('--accent', '#82aaff'), selectionBackground: v('--sel', '#3b3f5180') };
    names.forEach((n, i) => {
        theme[n] = v(`--ansi-${i}`, undefined);
        theme['bright' + n[0].toUpperCase() + n.slice(1)] = v(`--ansi-${i + 8}`, undefined);
    });
    return theme;
}

/** The lines a terminal's screen and scrollback show, a wrapped line put back together (the normal screen, not a full-screen program's). */
function screenLines(xterm) {
    const buf = xterm.buffer.normal;
    const lines = [];
    for (let i = 0; i < buf.length; i++) {
        const line = buf.getLine(i);
        if (!line) continue;
        const text = line.translateToString(true);
        if (line.isWrapped && lines.length) lines[lines.length - 1] += text;
        else lines.push(text);
    }
    return keptLines(lines);
}

export function setupTerminal() {
    const tabsEl = $('term-tabs');
    const views = $('term-views');
    const status = $('term-status');
    // the tabs in their order: { id, name, el, panes, focused, tab }; a pane: { id, group, view, xterm, fit, ws, alive, closed, shellId, shellName }
    const groups = [];
    let active = null;
    let lastId = 0;
    let renaming = false;     // the tabs are not redrawn while a name is being typed
    let root = null;          // the project folder, so another project's terminals are not opened here
    let shells = null;        // what /api/shells found: { auto, shells: [{ id, name }] }

    const focusedPane = () => active?.focused ?? active?.panes[0] ?? null;
    // after the layout, not on an animation frame: those wait while the window is hidden or minimised
    const soon = (fn) => window.setTimeout(fn, 0);

    function paint() {
        const p = focusedPane();
        if (!p) return;
        status.textContent = p.alive ? p.shellName ?? '' : p.ws ? 'Ended. Press Enter to start a new shell.' : 'Starting…';
        $('term-split').disabled = active.panes.length >= MAX_PANES;
    }

    /** Connects the pane to a new shell of the size its view has; one not laid out yet waits for its size (a shell started 2 columns wide stays garbled). */
    function connect(p) {
        if (p.ws && (p.ws.readyState === WebSocket.OPEN || p.ws.readyState === WebSocket.CONNECTING)) return;
        fitNow(p);
        if (!p.view.offsetParent || p.xterm.cols < 20 || p.xterm.rows < 4) { p.waiting = true; return; }
        p.waiting = false;
        // the pseudo console clears the screen when it starts: what was restored goes up into the scrollback first, so it stays
        if (p.restored) { p.xterm.write('\r\n'.repeat(p.xterm.rows)); p.restored = false; }
        const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
        const ws = new WebSocket(`${scheme}://${location.host}/console?cols=${p.xterm.cols}&rows=${p.xterm.rows}&shell=${encodeURIComponent(p.shellId)}${p.cwd ? `&cwd=${encodeURIComponent(p.cwd)}` : ''}`);
        p.ws = ws;
        p.alive = true;
        p.shellName = 'Terminal';
        ws.onmessage = (ev) => {
            const m = JSON.parse(ev.data);
            if (m.type === 'output') { p.xterm.write(m.data); keepSoon(); }
            else if (m.type === 'shell') { p.shellName = m.name; renderTabs(); paint(); }
            else if (m.type === 'exit') {
                p.alive = false;
                p.xterm.write(`\r\n\x1b[2m[the shell ended${m.code ? ` with code ${m.code}` : ''}: press Enter to start a new one]\x1b[0m\r\n`);
                renderTabs(); paint();
            } else if (m.type === 'error') {
                p.alive = false;
                p.xterm.write(`\x1b[31m${m.text}\x1b[0m\r\n`);
                renderTabs(); paint();
            }
        };
        ws.onclose = () => {
            if (p.closed || p.ws !== ws) return;
            if (p.alive) p.xterm.write('\r\n\x1b[31m[the connection to PyMagic was lost: press Enter to start a new shell]\x1b[0m\r\n');
            p.alive = false;
            renderTabs(); paint();
        };
        renderTabs(); paint();
    }

    function send(p, message) { if (p.ws?.readyState === WebSocket.OPEN) p.ws.send(JSON.stringify(message)); }

    function fitNow(p) {
        if (!p.view.offsetParent) return;   // hidden: measured when shown
        try { p.fit.fit(); } catch { /* not laid out yet */ }
    }

    /** A terminal in `group`; `lines` is what its screen showed before PyMagic was closed, written dimmed above the new prompt. */
    function addPane(group, shellId, lines = [], cwd = '') {
        const view = document.createElement('div');
        view.className = 'term-pane';
        const x = document.createElement('span');
        x.className = 'term-pane-close';
        x.textContent = '✕';
        x.title = 'Close this terminal of the split (its shell and what runs in it end)';
        view.append(x);
        group.el.append(view);
        const xterm = new Terminal({
            fontFamily: 'Consolas, "Cascadia Mono", monospace', fontSize: 13, lineHeight: 1.15, cursorBlink: true, scrollback: 5000,
            allowProposedApi: false, theme: themeOf(),
        });
        const fit = new FitAddon();
        xterm.loadAddon(fit);
        const p = { id: ++lastId, group, view, xterm, fit, ws: null, alive: false, closed: false, shellId: shellId || settings().terminalShell || 'auto', shellName: null, cwd };
        group.panes.push(p);
        group.focused = p;
        xterm.open(view);
        if (lines.length) { xterm.write(`\x1b[2m${lines.join('\r\n')}\r\n${RESTORED}\x1b[0m`); p.restored = true; }
        x.onclick = (e) => { e.stopPropagation(); closePane(p); };
        xterm.textarea?.addEventListener('focus', () => { group.focused = p; markFocus(group); paint(); });
        // keys go to the shell; Ctrl+C copies when text is selected (it interrupts otherwise), Ctrl+V pastes
        xterm.attachCustomKeyEventHandler((e) => {
            if (e.type !== 'keydown') return true;
            if (e.ctrlKey && !e.shiftKey && !e.altKey && e.key === 'c' && xterm.hasSelection()) { void navigator.clipboard?.writeText(xterm.getSelection()); xterm.clearSelection(); return false; }
            if (e.ctrlKey && e.shiftKey && (e.key === 'C' || e.key === 'c')) { void navigator.clipboard?.writeText(xterm.getSelection()); return false; }
            // Ctrl+` and Ctrl+Shift+` belong to the panel, Ctrl+Shift+5 splits
            if (e.ctrlKey && (e.key === '`' || e.key === '~' || e.code === 'Backquote')) return false;
            if (e.ctrlKey && e.shiftKey && e.code === 'Digit5') return false;
            return true;
        });
        xterm.onData((data) => {
            if (p.alive) send(p, { type: 'input', data });
            else if (data === '\r') { xterm.reset(); connect(p); }
        });
        xterm.onResize(({ cols, rows }) => send(p, { type: 'resize', cols, rows }));
        new ResizeObserver(() => { fitNow(p); if (p.waiting) connect(p); }).observe(view);
        markFocus(group);
        return p;
    }

    function markFocus(group) {
        group.el.classList.toggle('split', group.panes.length > 1);
        for (const p of group.panes) p.view.classList.toggle('focused', p === group.focused);
    }

    /** A new tab; its terminals start when it is first shown. */
    function addGroup(name, panes) {
        const el = document.createElement('div');
        el.className = 'term-group';
        views.append(el);
        const group = { id: ++lastId, name: name ?? nextName(groups.map((g) => g.name)), el, panes: [], focused: null };
        groups.push(group);
        for (const p of panes) addPane(group, p.shell, p.lines, p.cwd);
        group.focused = group.panes[0];
        markFocus(group);
        return group;
    }

    function create(shellId, cwd = '') {
        const group = addGroup(cwd ? cwd.slice(cwd.lastIndexOf('/') + 1) : null, [{ shell: shellId, cwd }]);
        select(group);
        keep();
        return group;
    }

    function split(shellId) {
        if (!active || active.panes.length >= MAX_PANES) return;
        const from = focusedPane();
        const p = addPane(active, shellId ?? from?.shellId, [], from?.cwd);
        renderTabs();
        soon(() => { for (const x of active.panes) fitNow(x); connect(p); p.xterm.focus(); });
        keep();
    }

    function select(group) {
        active = group;
        for (const g of groups) g.el.classList.toggle('active', g === group);
        renderTabs();
        paint();
        soon(() => {
            for (const p of group.panes) { fitNow(p); if (!p.ws) connect(p); }
            focusedPane()?.xterm.focus();
        });
    }

    function disposePane(p) {
        p.closed = true;
        // closing the connection makes the host end the shell and what it started
        try { p.ws?.close(); } catch { /* already closed */ }
        p.xterm.dispose();
        p.view.remove();
    }

    function closePane(p) {
        const group = p.group;
        if (group.panes.length === 1) { close(group); return; }
        const at = group.panes.indexOf(p);
        disposePane(p);
        group.panes.splice(at, 1);
        if (group.focused === p) group.focused = group.panes[Math.min(at, group.panes.length - 1)];
        markFocus(group);
        soon(() => { for (const x of group.panes) fitNow(x); group.focused.xterm.focus(); });
        renderTabs(); paint(); keep();
    }

    function close(group) {
        if (!groups.includes(group)) return;
        for (const p of group.panes) disposePane(p);
        group.el.remove();
        const next = afterClose(groups.map((g) => g.id), group.id, active?.id);
        groups.splice(groups.indexOf(group), 1);
        if (groups.length === 0) create();   // the panel always has a terminal
        else select(groups.find((g) => g.id === next));
        keep();
    }

    function rename(group, nameEl) {
        renaming = true;
        const field = document.createElement('input');
        field.className = 'ttab-rename';
        field.value = group.name;
        field.size = Math.max(8, group.name.length);
        const finish = (unchanged) => {
            if (!renaming) return;
            renaming = false;
            const name = unchanged ? null : cleanName(field.value);
            if (name) group.name = name;
            field.replaceWith(nameEl);
            renderTabs();
            focusedPane()?.xterm.focus();
            keep();
        };
        field.onkeydown = (e) => {
            e.stopPropagation();
            if (e.key === 'Enter') { e.preventDefault(); finish(false); }
            else if (e.key === 'Escape') { e.preventDefault(); finish(true); }
        };
        field.onblur = () => finish(false);
        field.onclick = (e) => e.stopPropagation();
        nameEl.replaceWith(field);
        field.focus();
        field.select();
    }

    /** A tab, made once: a double-click needs both clicks to land on the same element. */
    function makeTab(group) {
        const tab = document.createElement('div');
        tab.className = 'ttab';
        const name = document.createElement('span');
        name.className = 'ttab-name';
        name.ondblclick = (e) => { e.stopPropagation(); rename(group, name); };
        const x = document.createElement('span');
        x.className = 'ttab-close';
        x.textContent = '✕';
        x.title = 'Close this terminal (its shells and what runs in them end)';
        x.onclick = (e) => { e.stopPropagation(); close(group); };
        tab.append(name, x);
        tab.onclick = () => { if (group !== active) select(group); else focusedPane()?.xterm.focus(); };
        tab.onmousedown = (e) => { if (e.button === 1) e.preventDefault(); };
        tab.onmouseup = (e) => { if (e.button === 1) { e.preventDefault(); close(group); } };
        return tab;
    }

    function renderTabs() {
        if (renaming) return;
        for (const g of groups) {
            g.tab ??= makeTab(g);
            const alive = g.panes.some((p) => p.alive || !p.ws);
            g.tab.classList.toggle('active', g === active);
            g.tab.classList.toggle('ended', !alive);
            const kinds = [...new Set(g.panes.map((p) => p.shellName).filter(Boolean))].join(', ');
            g.tab.title = `${g.name}${kinds ? ` (${kinds})` : ''}${alive ? '' : ' (ended)'}: double-click to rename`;
            g.tab.firstChild.textContent = g.panes.length > 1 ? `${g.name} ⫴${g.panes.length}` : g.name;
        }
        const tabs = groups.map((g) => g.tab);
        if (tabs.length !== tabsEl.children.length || tabs.some((tab, i) => tabsEl.children[i] !== tab)) tabsEl.replaceChildren(...tabs);
    }

    /** Shows the panel with a terminal, a new one when asked or when there is none yet. */
    function open(fresh, shellId) {
        showTab('terminal');
        if (fresh || groups.length === 0) create(shellId);
        else select(active);
    }

    // ---- kept for the next start
    let keeping = 0;
    function keep() {
        if (root === null || SECOND_WINDOW) return;
        window.clearTimeout(keeping);
        keeping = 0;
        const tabs = groups.map((g) => ({ name: g.name, panes: g.panes.map((p) => ({ shell: p.shellId, cwd: p.cwd, lines: screenLines(p.xterm) })) }));
        try { localStorage.setItem(KEEP_KEY, JSON.stringify({ root, tabs })); } catch { /* storage full or unavailable: not kept */ }
    }
    /** What came out is kept a few seconds later, so a crash loses little and typing does not write the storage each key. */
    function keepSoon() { if (!keeping) keeping = window.setTimeout(keep, 5000); }
    window.addEventListener('beforeunload', keep);

    async function pickShell(anchor, run) {
        // the interpreter may have been changed since the page loaded: ask again, so "in the interpreter" is there when it is one of another machine
        try { shells = await (await fetch('/api/shells')).json(); } catch { /* the last answer stands */ }
        const list = shells?.shells ?? [];
        const items = list.map((s) => ({ label: s.name, run: () => run(s.id) }));
        // a shell inside the project's WSL distribution, Docker image or container, Compose service or SSH host
        if (shells?.interpreter) items.unshift({ label: `In the interpreter: ${shells.interpreter}`, run: () => run('interpreter') });
        openMenu(anchor, items.length ? items : [{ label: 'No other shell was found', disabled: true }]);
    }

    $('term-stop').onclick = () => { const p = focusedPane(); if (p?.alive) send(p, { type: 'input', data: '\u0003' }); p?.xterm.focus(); };
    $('term-clear').onclick = () => { const p = focusedPane(); p?.xterm.clear(); p?.xterm.focus(); keep(); };
    $('term-new').onclick = () => open(true);
    $('term-pick').onclick = (e) => pickShell(e.currentTarget, (id) => open(true, id));
    $('term-split').onclick = () => split();
    $('term-split').oncontextmenu = (e) => { e.preventDefault(); pickShell(e.currentTarget, (id) => split(id)); };
    $('t-terminal').onclick = () => open(false);
    // Ctrl+` opens the panel and Ctrl+Shift+` a new terminal, as in other editors (Shift+` is "~" on many keyboards); Ctrl+Shift+5 splits
    window.addEventListener('keydown', (e) => {
        if (!e.ctrlKey || e.altKey || e.repeat) return;   // a held key must not open a terminal per repeat
        if (e.shiftKey && (e.code === 'Backquote' || e.key === '~' || e.key === '`')) { e.preventDefault(); open(true); }
        else if (!e.shiftKey && e.key === '`') { e.preventDefault(); open(false); }
        else if (e.shiftKey && e.code === 'Digit5' && e.target?.closest?.('#terminal-tab')) { e.preventDefault(); split(); }
    });
    // the colours follow the theme
    new MutationObserver(() => { for (const g of groups) for (const p of g.panes) p.xterm.options.theme = themeOf(); }).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

    fetch('/api/shells').then((r) => r.json()).then((s) => { shells = s; }).catch(() => { /* ▾ says none was found */ });
    // the tabs of the last session open again, their shells starting when the panel shows them
    fetch('/api/info').then((r) => r.json()).then((info) => {
        root = info.root ?? '';
        let raw = null;
        try { raw = SECOND_WINDOW ? null : JSON.parse(localStorage.getItem(KEEP_KEY) ?? 'null'); } catch { /* nothing kept */ }
        const tabs = restoredTabs(raw, root);
        if (!tabs.length || groups.length) return;
        for (const t of tabs) addGroup(t.name, t.panes);
        active = groups[0];
        for (const g of groups) g.el.classList.toggle('active', g === active);
        renderTabs();
        if ($('terminal-tab')?.classList.contains('active') && $('panel')?.classList.contains('open')) select(active);
    }).catch(() => { root = ''; });

    // a new terminal that starts in a folder of the project (Open In > Terminal of the tree and the tabs)
    return {
        openAt: (folder) => { showTab('terminal'); create(undefined, folder); },
        /** The terminals with a shell in them, as services of the Services window. */
        services: () => groups.flatMap((g) => g.panes.filter((p) => p.alive).map((p) => ({
            id: `terminal-${p.id ?? g.id}`, group: 'Terminals', name: `${g.name}${g.panes.length > 1 ? ` (${g.panes.indexOf(p) + 1})` : ''} · ${p.shellName ?? 'shell'}`, state: 'running',
            show: () => { showTab('terminal'); select(g); p.xterm.focus(); },
            stop: () => closePane(p),
        }))),
    };
}

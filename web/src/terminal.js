// The Terminal panel: real consoles for the project, one per tab. Each tab is a shell in a Windows pseudo console on the host (/console), drawn by
// xterm.js: the interactive python prompt with its line editing, progress bars, full-screen programs, colours, Ctrl+C, copy and paste, all as in
// Windows Terminal. The project's interpreter is first on the PATH and its virtual environment active. A shell that ended starts again with Enter.
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import { showTab } from './panel';
import { cleanName, nextName, afterClose } from './termtabs';
import { ansiReader, cssOf } from './ansi';

const $ = (id) => document.getElementById(id);

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

export function setupTerminal() {
    const tabsEl = $('term-tabs');
    const views = $('term-views');
    const status = $('term-status');
    const terms = [];         // in the order of the tabs: { id, name, view, xterm, fit, ws, alive, closed, tab }
    let active = null;
    let lastId = 0;
    let renaming = false;     // the tabs are not redrawn while a name is being typed

    function paint() {
        const t = active;
        if (!t) return;
        status.textContent = t.alive ? t.shell ?? '' : t.ws ? 'Ended. Press Enter to start a new shell.' : 'Starting…';
    }

    /** Connects the tab to a new shell of the size its view has. */
    function connect(t) {
        if (t.ws && (t.ws.readyState === WebSocket.OPEN || t.ws.readyState === WebSocket.CONNECTING)) return;
        const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
        const ws = new WebSocket(`${scheme}://${location.host}/console?cols=${t.xterm.cols}&rows=${t.xterm.rows}`);
        t.ws = ws;
        t.alive = true;
        t.shell = 'Terminal';
        ws.onmessage = (ev) => {
            const m = JSON.parse(ev.data);
            if (m.type === 'output') t.xterm.write(m.data);
            else if (m.type === 'exit') {
                t.alive = false;
                t.xterm.write(`\r\n\x1b[2m[the shell ended${m.code ? ` with code ${m.code}` : ''}: press Enter to start a new one]\x1b[0m\r\n`);
                renderTabs(); paint();
            } else if (m.type === 'error') {
                t.alive = false;
                t.xterm.write(`\x1b[31m${m.text}\x1b[0m\r\n`);
                renderTabs(); paint();
            }
        };
        ws.onclose = () => {
            if (t.closed || t.ws !== ws) return;
            if (t.alive) t.xterm.write('\r\n\x1b[31m[the connection to PyMagic was lost: press Enter to start a new shell]\x1b[0m\r\n');
            t.alive = false;
            renderTabs(); paint();
        };
        renderTabs(); paint();
    }

    function send(t, message) { if (t.ws?.readyState === WebSocket.OPEN) t.ws.send(JSON.stringify(message)); }

    function fitNow(t) {
        if (!t.view.offsetParent) return;   // hidden: measured when shown
        try { t.fit.fit(); } catch { /* not laid out yet */ }
    }

    function create() {
        const view = document.createElement('div');
        view.className = 'term-view';
        views.append(view);
        const xterm = new Terminal({
            fontFamily: 'Consolas, "Cascadia Mono", monospace', fontSize: 13, lineHeight: 1.15, cursorBlink: true, scrollback: 5000,
            allowProposedApi: false, theme: themeOf(),
        });
        const fit = new FitAddon();
        xterm.loadAddon(fit);
        const t = { id: ++lastId, name: nextName(terms.map((x) => x.name)), view, xterm, fit, ws: null, alive: false, closed: false };
        terms.push(t);
        xterm.open(view);
        // keys go to the shell; Ctrl+C copies when text is selected (it interrupts otherwise), Ctrl+V pastes
        xterm.attachCustomKeyEventHandler((e) => {
            if (e.type !== 'keydown') return true;
            if (e.ctrlKey && !e.shiftKey && !e.altKey && e.key === 'c' && xterm.hasSelection()) { void navigator.clipboard?.writeText(xterm.getSelection()); xterm.clearSelection(); return false; }
            if (e.ctrlKey && e.shiftKey && (e.key === 'C' || e.key === 'c')) { void navigator.clipboard?.writeText(xterm.getSelection()); return false; }
            // Ctrl+` and Ctrl+Shift+` belong to the panel
            if (e.ctrlKey && (e.key === '`' || e.key === '~' || e.code === 'Backquote')) return false;
            return true;
        });
        xterm.onData((data) => {
            if (t.alive) send(t, { type: 'input', data });
            else if (data === '\r') { xterm.reset(); connect(t); }
        });
        xterm.onResize(({ cols, rows }) => send(t, { type: 'resize', cols, rows }));
        new ResizeObserver(() => fitNow(t)).observe(view);
        select(t);
        fitNow(t);
        connect(t);
        return t;
    }

    function select(t) {
        active = t;
        for (const x of terms) x.view.classList.toggle('active', x === t);
        renderTabs();
        paint();
        requestAnimationFrame(() => { fitNow(t); t.xterm.focus(); });
    }

    function close(t) {
        if (!terms.includes(t)) return;
        t.closed = true;
        // closing the connection makes the host end the shell and what it started
        try { t.ws?.close(); } catch { /* already closed */ }
        t.xterm.dispose();
        t.view.remove();
        const next = afterClose(terms.map((x) => x.id), t.id, active?.id);
        terms.splice(terms.indexOf(t), 1);
        if (terms.length === 0) create();   // the panel always has a terminal
        else select(terms.find((x) => x.id === next));
    }

    function rename(t, nameEl) {
        renaming = true;
        const field = document.createElement('input');
        field.className = 'ttab-rename';
        field.value = t.name;
        field.size = Math.max(8, t.name.length);
        const finish = (keep) => {
            if (!renaming) return;
            renaming = false;
            const name = keep ? null : cleanName(field.value);
            if (name) t.name = name;
            field.replaceWith(nameEl);
            renderTabs();
            t.xterm.focus();
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

    /** A terminal's tab, made once: a double-click needs both clicks to land on the same element. */
    function makeTab(t) {
        const tab = document.createElement('div');
        tab.className = 'ttab';
        const name = document.createElement('span');
        name.className = 'ttab-name';
        name.ondblclick = (e) => { e.stopPropagation(); rename(t, name); };
        const x = document.createElement('span');
        x.className = 'ttab-close';
        x.textContent = '✕';
        x.title = 'Close this terminal (its shell and what runs in it end)';
        x.onclick = (e) => { e.stopPropagation(); close(t); };
        tab.append(name, x);
        tab.onclick = () => { if (t !== active) select(t); else t.xterm.focus(); };
        tab.onmousedown = (e) => { if (e.button === 1) e.preventDefault(); };
        tab.onmouseup = (e) => { if (e.button === 1) { e.preventDefault(); close(t); } };
        return tab;
    }

    function renderTabs() {
        if (renaming) return;
        for (const t of terms) {
            t.tab ??= makeTab(t);
            t.tab.classList.toggle('active', t === active);
            t.tab.classList.toggle('ended', !t.alive);
            t.tab.title = `${t.name}${t.alive ? '' : ' (ended)'} (double-click to rename)`;
            t.tab.firstChild.textContent = t.name;
        }
        const tabs = terms.map((t) => t.tab);
        if (tabs.length !== tabsEl.children.length || tabs.some((tab, i) => tabsEl.children[i] !== tab)) tabsEl.replaceChildren(...tabs);
    }

    /** Shows the panel with a terminal, a new one when asked or when there is none yet. */
    function open(fresh) {
        showTab('terminal');
        if (fresh || terms.length === 0) create();
        else { select(active); if (!active.alive && !active.ws) connect(active); }
    }

    $('term-stop').onclick = () => { if (active?.alive) send(active, { type: 'input', data: '\u0003' }); active?.xterm.focus(); };
    $('term-clear').onclick = () => { active?.xterm.clear(); active?.xterm.focus(); };
    $('term-new').onclick = () => open(true);
    $('t-terminal').onclick = () => open(false);
    // Ctrl+` opens the panel and Ctrl+Shift+` a new terminal, as in other editors (Shift+` is "~" on many keyboards)
    window.addEventListener('keydown', (e) => {
        if (!e.ctrlKey || e.altKey || e.repeat) return;   // a held key must not open a terminal per repeat
        if (e.shiftKey && (e.code === 'Backquote' || e.key === '~' || e.key === '`')) { e.preventDefault(); open(true); }
        else if (!e.shiftKey && e.key === '`') { e.preventDefault(); open(false); }
    });
    // the colours follow the theme
    new MutationObserver(() => { for (const t of terms) t.xterm.options.theme = themeOf(); }).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
}

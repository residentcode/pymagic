// The Terminal panel: command lines for the project, one per tab (one command at a time in each, in the project's
// environment). Each terminal has its own /term connection, so the host gives it its own session and folder.
import { showTab } from './panel';
import { afterClose, cleanName, nextName } from './termtabs';
import { ansiReader, cssOf } from './ansi';

const $ = (id) => document.getElementById(id);

/** The pieces of coloured output as spans (`cls` marks what PyMagic itself prints: the command, errors). */
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

/** Asks before closing a terminal whose command is still running. Resolves to true to stop it and close. */
function askClose(name) {
    return new Promise((resolve) => {
        const shade = document.createElement('div');
        shade.className = 'ask-shade';
        const box = document.createElement('div');
        box.className = 'ask-box';
        const title = document.createElement('div');
        title.className = 'ask-title';
        title.textContent = `Close ${name}?`;
        const text = document.createElement('div');
        text.className = 'ask-hint';
        text.textContent = 'A command is still running in it. Closing the terminal stops the command.';
        const row = document.createElement('div');
        row.className = 'ask-buttons';
        const done = (value) => { shade.remove(); resolve(value); };
        for (const [label, value, cls] of [['Stop and close', true, 'danger'], ['Cancel', false, '']]) {
            const b = document.createElement('button');
            b.textContent = label;
            if (cls) b.className = cls;
            b.onclick = () => done(value);
            row.append(b);
        }
        box.append(title, text, row);
        shade.append(box);
        shade.onmousedown = (e) => { if (e.target === shade) done(false); };
        shade.onkeydown = (e) => { if (e.key === 'Escape') done(false); };
        document.body.append(shade);
        row.lastChild.focus();
    });
}

export function setupTerminal() {
    const tabsEl = $('term-tabs');
    const main = $('terminal-main');
    const line = $('term-line');
    const input = $('term-input');
    const promptEl = $('term-prompt');
    const status = $('term-status');
    const terms = [];         // in the order of the tabs: { id, name, output, ws, cwd, shell, running, history, historyAt, draft }
    let active = null;
    let lastId = 0;
    let renaming = false;     // the tabs are not redrawn while a name is being typed

    function shortCwd(t) {
        return t.cwd.length > 48 ? '…' + t.cwd.slice(-47) : t.cwd;
    }

    function print(t, text, cls) {
        // the colours programs ask for are shown; a carriage return alone (a progress bar redrawing its line) is dropped
        t.ansi ??= ansiReader();
        t.output.append(...colouredSpans(t.ansi, text.replace(/\r(?!\n)/g, ''), cls));
        // keep the page light: the oldest output goes after a lot of it
        while (t.output.childNodes.length > 4000) t.output.removeChild(t.output.firstChild);
        t.output.scrollTop = t.output.scrollHeight;
    }

    /** The prompt, the status line and Stop show the open terminal. */
    function paint() {
        const t = active;
        if (!t) return;
        $('term-stop').disabled = !t.running;
        promptEl.classList.toggle('busy', t.running);
        promptEl.textContent = t.running ? '▶' : `${shortCwd(t)} >`;
        if (t.running) status.textContent = 'Running…  Enter sends a line to the program, Ctrl+C stops it';
        else if (t.ws && t.ws.readyState === WebSocket.OPEN) status.textContent = t.shell;
        else status.textContent = t.ws ? 'Disconnected. Press Enter to connect again.' : '';
    }

    function setRunning(t, value) {
        t.running = value;
        renderTabs();
        if (t === active) paint();
    }

    function connect(t) {
        if (t.ws && (t.ws.readyState === WebSocket.OPEN || t.ws.readyState === WebSocket.CONNECTING)) return;
        const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
        const ws = new WebSocket(`${scheme}://${location.host}/term`);
        t.ws = ws;
        ws.onmessage = (ev) => {
            const m = JSON.parse(ev.data);
            switch (m.type) {
                case 'ready':
                    t.cwd = m.cwd;
                    t.shell = m.shell;
                    setRunning(t, false);
                    break;
                case 'out': print(t, m.text); break;
                case 'err': print(t, m.text, 'err'); break;
                case 'exit':
                    t.cwd = m.cwd;
                    if (m.code !== 0) print(t, `(exit code ${m.code})\n`, 'info');
                    setRunning(t, false);
                    if (t === active && !document.querySelector('.ask-shade')) input.focus();
                    break;
            }
        };
        ws.onclose = () => {
            if (t.closed || t.ws !== ws) return;
            if (t.running) print(t, '(the connection to the host was lost)\n', 'err');
            setRunning(t, false);
        };
    }

    function create() {
        const output = document.createElement('pre');
        output.className = 'term-output body';
        output.onclick = () => { if (!window.getSelection()?.toString()) input.focus(); };
        main.insertBefore(output, line);
        const t = { id: ++lastId, name: nextName(terms.map((x) => x.name)), output, ws: null, cwd: '', shell: '', running: false, history: [], historyAt: 0, draft: '' };
        terms.push(t);
        select(t);
        connect(t);
        return t;
    }

    function select(t) {
        // the half-typed line moves only when the open terminal changes (closing another one must not touch it)
        if (t !== active) {
            if (active) active.draft = input.value;
            input.value = t.draft;
            active = t;
            for (const x of terms) x.output.classList.toggle('active', x === t);
            t.output.scrollTop = t.output.scrollHeight;
        }
        renderTabs();
        paint();
    }

    async function close(t) {
        if (t.running && !(await askClose(t.name))) { input.focus(); return; }
        if (!terms.includes(t)) return;
        t.closed = true;
        // closing the connection makes the host kill the command and end the session
        try { t.ws?.close(); } catch { /* already closed */ }
        t.output.remove();
        const next = afterClose(terms.map((x) => x.id), t.id, active?.id);
        terms.splice(terms.indexOf(t), 1);
        if (terms.length === 0) create();   // the panel always has a terminal
        else select(terms.find((x) => x.id === next));
        input.focus();
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
            input.focus();
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
        x.title = 'Close this terminal';
        x.onclick = (e) => { e.stopPropagation(); void close(t); };
        tab.append(name, x);
        tab.onclick = () => { if (t !== active) select(t); input.focus(); };
        tab.onmousedown = (e) => { if (e.button === 1) e.preventDefault(); };
        tab.onmouseup = (e) => { if (e.button === 1) { e.preventDefault(); void close(t); } };
        return tab;
    }

    function renderTabs() {
        if (renaming) return;
        for (const t of terms) {
            t.tab ??= makeTab(t);
            t.tab.classList.toggle('active', t === active);
            t.tab.classList.toggle('busy', t.running);
            t.tab.title = t.running ? `${t.name}: a command is running` : `${t.name} (double-click to rename)`;
            t.tab.firstChild.textContent = t.name;
        }
        const tabs = terms.map((t) => t.tab);
        if (tabs.length !== tabsEl.children.length || tabs.some((tab, i) => tabsEl.children[i] !== tab)) tabsEl.replaceChildren(...tabs);
    }

    function submit() {
        const t = active;
        const text = input.value;
        if (!t.ws || t.ws.readyState !== WebSocket.OPEN) {
            connect(t);
            return;
        }
        if (t.running) {
            print(t, text + '\n', 'info');
            t.ws.send(JSON.stringify({ type: 'stdin', text }));
        } else {
            print(t, `${shortCwd(t)} > ${text}\n`, 'cmd');
            if (text.trim() && t.history[t.history.length - 1] !== text) t.history.push(text);
            t.historyAt = t.history.length;
            if (text.trim().toLowerCase() === 'clear' || text.trim().toLowerCase() === 'cls') t.output.replaceChildren();
            else {
                setRunning(t, true);
                t.ws.send(JSON.stringify({ type: 'run', command: text }));
            }
        }
        input.value = '';
    }

    function stop() {
        if (active?.ws && active.ws.readyState === WebSocket.OPEN) active.ws.send(JSON.stringify({ type: 'kill' }));
    }

    /** Shows the panel with a terminal, a new one when asked or when there is none yet. */
    function open(fresh) {
        showTab('terminal');
        if (fresh || terms.length === 0) create();
        else connect(active);
        input.focus();
    }

    input.onkeydown = (e) => {
        const t = active;
        if (e.key === 'Enter') { e.preventDefault(); submit(); }
        else if (e.key === 'c' && e.ctrlKey && input.selectionEnd === input.selectionStart) { e.preventDefault(); stop(); }
        else if (e.key === 'ArrowUp' && !t.running) {
            e.preventDefault();
            if (t.historyAt > 0) input.value = t.history[--t.historyAt];
        } else if (e.key === 'ArrowDown' && !t.running) {
            e.preventDefault();
            t.historyAt = Math.min(t.history.length, t.historyAt + 1);
            input.value = t.history[t.historyAt] ?? '';
        }
    };
    $('term-stop').onclick = stop;
    $('term-clear').onclick = () => active?.output.replaceChildren();
    $('term-new').onclick = () => open(true);
    $('t-terminal').onclick = () => open(false);
    // Ctrl+` opens the panel and Ctrl+Shift+` a new terminal, as in other editors (Shift+` is "~" on many keyboards)
    window.addEventListener('keydown', (e) => {
        if (!e.ctrlKey || e.altKey || e.repeat) return;   // a held key must not open a terminal per repeat
        if (e.shiftKey && (e.code === 'Backquote' || e.key === '~' || e.key === '`')) { e.preventDefault(); open(true); }
        else if (!e.shiftKey && e.key === '`') { e.preventDefault(); open(false); }
    });
}

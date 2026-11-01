// The Python console (a tab of the dock): interpreters of the project's Python that keep their names between commands, beside the
// editor. There can be several consoles, each with output, names, history and queue of its own (the tabs at the top, +). Execute
// Selection / Line in Python Console (Alt+Shift+E) and Run File in Python Console send code to the one shown; its names are listed
// beside it after each command; Up / Down walk what was typed; Tab completes; Restart starts it again (with the starting script of the
// settings, .pymagic/console-startup.py when there is one). Commands given while one runs wait in a queue that shows under the output
// (a waiting command can be taken out with its x). With IPython ticked, the console is IPython's shell when the project's Python has it.
// The engine runs pm_console.py (RunSession.StartConsole).
import * as monaco from 'monaco-editor';
import { RunClient } from './runclient';
import { showTab } from './panel';
import { CommandQueue, consoleName, queueLabel } from './pyconsole-model.js';

const $ = (id) => document.getElementById(id);
const STARTUP = '.pymagic/console-startup.py';
const IPYTHON_KEY = 'pymagic.consoleIPython';
const PLACEHOLDER = '>>> Python code (Enter runs it, Shift+Enter for another line)';

export function setupPythonConsole(deps) {
    const { editor } = deps;
    const input = $('pyc-input');
    const varsBox = $('pyc-vars');
    const views = $('pyc-views');
    const tabs = $('pyc-tabs');
    const queueBox = $('pyc-queue');
    const ipythonBox = $('pyc-ipython');
    let ipythonWanted = false;
    try { ipythonWanted = localStorage.getItem(IPYTHON_KEY) === '1'; } catch { /* storage unavailable: the default stands */ }
    ipythonBox.checked = ipythonWanted;

    // { id, name, out, client, ready, pending, django, ipython, history, at, vars, queue, tab }
    const consoles = [];
    let active = null;
    let lastId = 0;

    function make(name, django = false) {
        const c = { id: ++lastId, name, django, ipython: ipythonWanted, client: null, ready: null, pending: '', history: [], at: 0, vars: [], out: document.createElement('pre'), tab: null, ipythonSeen: null };
        c.out.className = 'body pyc-out';
        c.out.hidden = true;
        c.queue = new CommandQueue((item) => runOne(c, item), () => { if (c === active) paintQueue(); renderTabs(); });
        views.append(c.out);
        consoles.push(c);
        return c;
    }

    function append(c, text, cls = '') {
        const span = document.createElement('span');
        if (cls)
            span.className = cls;
        span.textContent = text;
        c.out.append(span);
        c.out.scrollTop = c.out.scrollHeight;
    }

    function select(c) {
        active = c;
        for (const o of consoles)
            o.out.hidden = o !== c;
        input.placeholder = c.pending ? '... (an empty line runs the block)' : PLACEHOLDER;
        ipythonBox.checked = c.ipython;
        paintVars(c);
        paintQueue();
        renderTabs();
        c.out.scrollTop = c.out.scrollHeight;
    }

    function renderTabs() {
        tabs.replaceChildren(...consoles.map((c) => {
            const tab = document.createElement('div');
            tab.className = 'ttab' + (c === active ? ' active' : '') + (c.queue.busy ? ' busy' : '');
            const name = document.createElement('span');
            name.className = 'ttab-name';
            name.textContent = c.name;
            tab.append(name);
            if (consoles.length > 1) {
                const close = document.createElement('span');
                close.className = 'ttab-close';
                close.textContent = '✕';
                close.title = 'Close this console';
                close.onclick = (e) => { e.stopPropagation(); closeConsole(c); };
                tab.append(close);
            }
            tab.title = c.ipython && c.ipythonSeen ? `${c.name} (IPython ${c.ipythonSeen})` : c.name;
            tab.onclick = () => { select(c); input.focus(); };
            c.tab = tab;
            return tab;
        }));
    }

    function closeConsole(c) {
        if (consoles.length < 2)
            return;
        end(c);
        c.out.remove();
        consoles.splice(consoles.indexOf(c), 1);
        if (active === c)
            select(consoles[Math.max(0, consoles.length - 1)]);
        else
            renderTabs();
    }

    /** Stops the process of a console and forgets what it was going to run. */
    function end(c) {
        c.queue.clear();
        c.client?.send({ type: 'stop' });
        c.client?.close();
        c.client = null;
        c.ready = null;
        c.pending = '';
    }

    /** The console, started when it is first needed; resolves once it is ready for code. */
    function start(c) {
        if (c.ready)
            return c.ready;
        c.out.textContent = '';
        c.ready = new Promise((resolve) => {
            fetch('/api/file?path=' + encodeURIComponent(STARTUP)).then((r) => r.ok).catch(() => false).then((hasStartup) => {
                const me = new RunClient({ type: 'console', startup: hasStartup ? STARTUP : undefined, django: c.django, ipython: c.ipython });
                c.client = me;
                // a console that was restarted may still send its last messages: only this one's count
                me.onMessage = (m) => {
                    if (c.client !== me)
                        return;
                    if (m.type === 'output')
                        append(c, m.text, m.stream === 'stderr' ? 'err' : '');
                    else if (m.type === 'consoleReady') {
                        c.ipythonSeen = m.ipython || null;
                        resolve(true);
                        void refreshVars(c);
                        renderTabs();
                    }
                    else if (m.type === 'exited') {
                        append(c, `\nThe console ended (exit code ${m.code}). Restart starts it again.\n`, 'info');
                        c.queue.clear();
                        c.client = null;
                        c.ready = null;
                        resolve(false);
                    }
                    else if (m.type === 'error') {
                        append(c, m.message + '\n', 'err');
                        resolve(false);
                    }
                };
                me.onClose = () => { if (c.client === me) { c.client = null; c.ready = null; } };
            });
        });
        return c.ready;
    }

    function paintVars(c) {
        varsBox.replaceChildren();
        for (const v of c.vars) {
            const row = document.createElement('div');
            row.className = 'pyc-var';
            const name = document.createElement('span');
            name.className = 'pyc-name';
            name.textContent = v.name;
            const type = document.createElement('span');
            type.className = 'pyc-type';
            type.textContent = v.type;
            const value = document.createElement('span');
            value.className = 'pyc-value';
            value.textContent = v.value;
            row.title = `${v.name}: ${v.type} = ${v.value}`;
            row.append(name, value, type);
            varsBox.append(row);
        }
        if (!varsBox.children.length) {
            const none = document.createElement('div');
            none.className = 'side-empty';
            none.textContent = 'No names yet.';
            varsBox.append(none);
        }
    }

    async function refreshVars(c) {
        if (!c.client)
            return;
        const answer = await c.client.ask({ type: 'consoleVars' });
        c.vars = answer.vars ?? [];
        if (c === active)
            paintVars(c);
    }

    /** What waits behind the command that runs: each waiting command with an x to take it out. */
    function paintQueue() {
        const c = active;
        const waiting = c?.queue.waiting ?? [];
        if (!c || waiting.length === 0) {
            queueBox.replaceChildren();
            queueBox.hidden = true;
            return;
        }
        const head = document.createElement('span');
        head.className = 'pyc-run';
        head.textContent = `Running ${queueLabel(c.queue.current?.label ?? '')} · waiting:`;
        head.title = 'The console runs one command at a time; these wait their turn';
        queueBox.replaceChildren(head, ...waiting.map((w, i) => {
            const chip = document.createElement('span');
            chip.className = 'pyc-wait';
            chip.textContent = queueLabel(w.label);
            chip.title = w.label;
            const x = document.createElement('button');
            x.textContent = '✕';
            x.title = 'Take this command out of the queue';
            x.onclick = () => c.queue.remove(i);
            chip.append(x);
            return chip;
        }));
        queueBox.hidden = false;
    }

    /** One command of a console, when its turn comes. */
    async function runOne(c, item) {
        if (!(await start(c)) || !c.client)
            return null;
        if (item.echo)
            append(c, item.label.split('\n').map((l, i) => (i === 0 ? '>>> ' : '... ') + l).join('\n') + '\n', 'pyc-echo');
        const answer = await c.client.ask(item.message);
        await refreshVars(c);
        return answer;
    }

    /** Runs code in a console (the one shown unless one is given), as typed; commands wait their turn. */
    function execute(code, echo = true, c = active) {
        showTab('pyconsole');
        return c.queue.add({ label: code, echo, message: { type: 'consoleExec', code } });
    }

    input.onkeydown = async (e) => {
        const c = active;
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            const code = c.pending + input.value;
            input.value = '';
            if (code.trim())
                c.history = [...c.history.filter((h) => h !== code), code].slice(-200);
            c.at = c.history.length;
            const answer = await execute(code, true, c);
            // a block that goes on (after "def f():") waits for more lines; an empty line ends it
            c.pending = answer?.more ? code + '\n' : '';
            if (c === active)
                input.placeholder = c.pending ? '... (an empty line runs the block)' : PLACEHOLDER;
        }
        else if (e.key === 'ArrowUp' && !input.value.slice(0, input.selectionStart).includes('\n') && c.history.length) {
            e.preventDefault();
            c.at = Math.max(0, c.at - 1);
            input.value = c.history[c.at];
        }
        else if (e.key === 'ArrowDown' && !input.value.slice(input.selectionEnd).includes('\n') && c.history.length) {
            e.preventDefault();
            c.at = Math.min(c.history.length, c.at + 1);
            input.value = c.history[c.at] ?? '';
        }
        else if (e.key === 'Tab' && c.client) {
            e.preventDefault();
            const before = input.value.slice(0, input.selectionStart);
            const answer = await c.client.ask({ type: 'consoleComplete', text: before });
            const items = [...new Set(answer.items ?? [])];
            if (items.length === 1)
                input.setRangeText(items[0].slice(answer.word.length), input.selectionStart, input.selectionStart, 'end');
            else if (items.length > 1)
                append(c, items.join('  ') + '\n', 'info');
        }
    };

    function restart(c) {
        end(c);
        void start(c);
    }

    // the Django console: another console, with Django set up
    window.addEventListener('pymagic:djangoConsole', () => {
        const c = make(consoleName(consoles.map((o) => o.name), 'Django console'), true);
        select(c);
        showTab('pyconsole');
        void start(c);
        input.focus();
    });
    $('pyc-new').onclick = () => {
        const c = make(consoleName(consoles.map((o) => o.name)));
        select(c);
        showTab('pyconsole');
        void start(c);
        input.focus();
    };
    ipythonBox.onchange = () => {
        ipythonWanted = ipythonBox.checked;
        try { localStorage.setItem(IPYTHON_KEY, ipythonWanted ? '1' : '0'); } catch { /* not kept */ }
        active.ipython = ipythonWanted;
        restart(active);
    };
    $('pyc-restart').onclick = () => restart(active);
    $('pyc-stop').onclick = () => { active.queue.clear(); active.client?.send({ type: 'stop' }); };
    $('pyc-clear').onclick = () => { active.out.textContent = ''; };
    $('t-pyconsole').onclick = () => { showTab('pyconsole'); void start(active); input.focus(); };

    /** The selection, else the line of the cursor (Execute Line moves the cursor to the next line, as PyCharm does). */
    function executeFromEditor(ed = editor) {
        const model = ed.getModel(), sel = ed.getSelection();
        if (!model || !sel)
            return;
        let code;
        if (sel.isEmpty()) {
            code = model.getLineContent(sel.startLineNumber);
            if (sel.startLineNumber < model.getLineCount())
                ed.setPosition({ lineNumber: sel.startLineNumber + 1, column: 1 });
        }
        else
            code = model.getValueInRange(sel);
        // the common indentation of the selection goes, so a block from inside a function runs
        const lines = code.replace(/\r\n/g, '\n').split('\n');
        const used = lines.filter((l) => l.trim());
        const cut = used.length ? Math.min(...used.map((l) => l.length - l.trimStart().length)) : 0;
        code = lines.map((l) => l.slice(Math.min(cut, l.length - l.trimStart().length))).join('\n').replace(/\n+$/, '');
        if (code.trim())
            void execute(code + (code.includes('\n') ? '\n' : ''));
    }

    async function runFile(rel) {
        await deps.saveAll();
        showTab('pyconsole');
        const c = active;
        await c.queue.add({ label: `runfile('${rel}')`, echo: true, message: { type: 'consoleRunFile', path: rel } });   // the console runs in the project folder
    }

    function attach(ed) {
        ed.addAction({ id: 'pymagic.consoleExecute', label: 'Execute Selection in Python Console', contextMenuGroupId: 'z_run', contextMenuOrder: 3,
            keybindings: [monaco.KeyMod.Alt | monaco.KeyMod.Shift | monaco.KeyCode.KeyE], precondition: 'editorLangId == python', run: () => executeFromEditor(ed) });
        ed.addAction({ id: 'pymagic.consoleRunFile', label: 'Run File in Python Console', contextMenuGroupId: 'z_run', contextMenuOrder: 4,
            precondition: 'editorLangId == python', run: () => { const rel = deps.relOf(ed.getModel()); if (rel) void runFile(rel); } });
    }
    attach(editor);
    select(make(consoleName([])));
    return { attach, execute, runFile,
        /** The consoles that are started, as services of the Services window. */
        services: () => consoles.filter((c) => c.client !== null).map((c) => ({
            id: `pyconsole-${c.id}`, group: 'Consoles', name: c.name, state: 'running', show: () => { select(c); showTab('pyconsole'); input.focus(); },
            stop: () => end(c),
            restart: () => restart(c),
        })),
        show: () => { showTab('pyconsole'); void start(active); input.focus(); } };
}

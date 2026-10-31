// The Python console (a tab of the dock): an interpreter of the project's Python that keeps its names between commands, beside the
// editor. Execute Selection / Line in Python Console (Alt+Shift+E) and Run File in Python Console send code to it; its names are listed
// beside it after each command; Up / Down walk what was typed; Tab completes; Restart starts it again (with the starting script of the
// settings, .pymagic/console-startup.py when there is one). The engine runs pm_console.py (RunSession.StartConsole).
import * as monaco from 'monaco-editor';
import { RunClient } from './runclient';
import { showTab } from './panel';

const $ = (id) => document.getElementById(id);
const STARTUP = '.pymagic/console-startup.py';

export function setupPythonConsole(deps) {
    const { editor } = deps;
    const out = $('pyc-output');
    const input = $('pyc-input');
    const varsBox = $('pyc-vars');
    let client = null, ready = null, pending = '', django = false;
    let history = [], at = 0;

    function append(text, cls = '') {
        const span = document.createElement('span');
        if (cls)
            span.className = cls;
        span.textContent = text;
        out.append(span);
        out.scrollTop = out.scrollHeight;
    }

    /** The console, started when it is first needed; resolves once it is ready for code. */
    function start() {
        if (ready)
            return ready;
        out.textContent = '';
        ready = new Promise((resolve) => {
            fetch('/api/file?path=' + encodeURIComponent(STARTUP)).then((r) => r.ok).catch(() => false).then((hasStartup) => {
                const me = new RunClient({ type: 'console', startup: hasStartup ? STARTUP : undefined, django });
                client = me;
                // a console that was restarted may still send its last messages: only this one's count
                me.onMessage = (m) => {
                    if (client !== me)
                        return;
                    if (m.type === 'output')
                        append(m.text, m.stream === 'stderr' ? 'err' : '');
                    else if (m.type === 'consoleReady') {
                        resolve(true);
                        void refreshVars();
                    }
                    else if (m.type === 'exited') {
                        append(`\nThe console ended (exit code ${m.code}). Restart starts it again.\n`, 'info');
                        client = null;
                        ready = null;
                        resolve(false);
                    }
                    else if (m.type === 'error') {
                        append(m.message + '\n', 'err');
                        resolve(false);
                    }
                };
                me.onClose = () => { if (client === me) { client = null; ready = null; } };
            });
        });
        return ready;
    }

    async function refreshVars() {
        if (!client)
            return;
        const answer = await client.ask({ type: 'consoleVars' });
        varsBox.replaceChildren();
        for (const v of answer.vars ?? []) {
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

    /** Runs code in the console, as typed (with the >>> and ... of the prompt shown before it). */
    async function execute(code, echo = true) {
        showTab('pyconsole');
        if (!(await start()))
            return;
        if (echo)
            append(code.split('\n').map((l, i) => (i === 0 ? '>>> ' : '... ') + l).join('\n') + '\n', 'pyc-echo');
        const answer = await client.ask({ type: 'consoleExec', code });
        await refreshVars();
        return answer;
    }

    input.onkeydown = async (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            const code = pending + input.value;
            input.value = '';
            if (code.trim())
                history = [...history.filter((h) => h !== code), code].slice(-200);
            at = history.length;
            const answer = await execute(code);
            // a block that goes on (after "def f():") waits for more lines; an empty line ends it
            if (answer?.more) {
                pending = code + '\n';
                input.placeholder = '... (an empty line runs the block)';
            }
            else {
                pending = '';
                input.placeholder = '>>> Python code (Enter runs it, Shift+Enter for another line)';
            }
        }
        else if (e.key === 'ArrowUp' && !input.value.slice(0, input.selectionStart).includes('\n') && history.length) {
            e.preventDefault();
            at = Math.max(0, at - 1);
            input.value = history[at];
        }
        else if (e.key === 'ArrowDown' && !input.value.slice(input.selectionEnd).includes('\n') && history.length) {
            e.preventDefault();
            at = Math.min(history.length, at + 1);
            input.value = history[at] ?? '';
        }
        else if (e.key === 'Tab' && client) {
            e.preventDefault();
            const before = input.value.slice(0, input.selectionStart);
            const answer = await client.ask({ type: 'consoleComplete', text: before });
            const items = [...new Set(answer.items ?? [])];
            if (items.length === 1)
                input.setRangeText(items[0].slice(answer.word.length), input.selectionStart, input.selectionStart, 'end');
            else if (items.length > 1)
                append(items.join('  ') + '\n', 'info');
        }
    };

    // the Django console: the same console, started again with Django set up
    window.addEventListener('pymagic:djangoConsole', () => {
        client?.send({ type: 'stop' });
        client?.close();
        client = null;
        ready = null;
        pending = '';
        django = true;
        showTab('pyconsole');
        void start();
        input.focus();
    });
    $('pyc-restart').onclick = () => { client?.send({ type: 'stop' }); client?.close(); client = null; ready = null; pending = ''; void start(); };
    $('pyc-stop').onclick = () => { client?.send({ type: 'stop' }); };
    $('pyc-clear').onclick = () => { out.textContent = ''; };
    $('t-pyconsole').onclick = () => { showTab('pyconsole'); void start(); input.focus(); };

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
        if (!(await start()))
            return;
        append(`>>> runfile('${rel}')\n`, 'pyc-echo');
        await client.ask({ type: 'consoleRunFile', path: rel });   // the console runs in the project folder
        await refreshVars();
    }

    function attach(ed) {
        ed.addAction({ id: 'pymagic.consoleExecute', label: 'Execute Selection in Python Console', contextMenuGroupId: 'z_run', contextMenuOrder: 3,
            keybindings: [monaco.KeyMod.Alt | monaco.KeyMod.Shift | monaco.KeyCode.KeyE], precondition: 'editorLangId == python', run: () => executeFromEditor(ed) });
        ed.addAction({ id: 'pymagic.consoleRunFile', label: 'Run File in Python Console', contextMenuGroupId: 'z_run', contextMenuOrder: 4,
            precondition: 'editorLangId == python', run: () => { const rel = deps.relOf(ed.getModel()); if (rel) void runFile(rel); } });
    }
    attach(editor);
    return { attach, execute, runFile, show: () => { showTab('pyconsole'); void start(); input.focus(); } };
}

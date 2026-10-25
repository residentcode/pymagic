// The Terminal panel: a command line for the project (one command at a time, in the project's environment).
import { showTab } from './panel';

const $ = (id) => document.getElementById(id);

// ANSI colour and cursor sequences some tools print even when they are not on a terminal
const ANSI = /\u001b\[[0-9;?]*[A-Za-z]/g;

export function setupTerminal() {
    const output = $('term-output');
    const input = $('term-input');
    const promptEl = $('term-prompt');
    const status = $('term-status');
    let ws = null;
    let cwd = '';
    let shell = '';
    let running = false;
    const history = [];
    let historyAt = 0;

    function print(text, cls) {
        const span = document.createElement('span');
        if (cls) span.className = cls;
        span.textContent = text.replace(ANSI, '').replace(/\r(?!\n)/g, '');
        output.appendChild(span);
        // keep the page light: the oldest output goes after a lot of it
        while (output.childNodes.length > 4000) output.removeChild(output.firstChild);
        output.scrollTop = output.scrollHeight;
    }

    function setRunning(value) {
        running = value;
        $('term-stop').disabled = !value;
        promptEl.classList.toggle('busy', value);
        status.textContent = value ? 'Running…  Enter sends a line to the program, Ctrl+C stops it' : `${shell}`;
    }

    function shortCwd() {
        return cwd.length > 48 ? '…' + cwd.slice(-47) : cwd;
    }

    function paintPrompt() {
        promptEl.textContent = running ? '▶' : `${shortCwd()} >`;
    }

    function connect() {
        if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
        const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
        ws = new WebSocket(`${scheme}://${location.host}/term`);
        ws.onmessage = (ev) => {
            const m = JSON.parse(ev.data);
            switch (m.type) {
                case 'ready':
                    cwd = m.cwd;
                    shell = m.shell;
                    setRunning(false);
                    paintPrompt();
                    break;
                case 'out': print(m.text); break;
                case 'err': print(m.text, 'err'); break;
                case 'exit':
                    cwd = m.cwd;
                    if (m.code !== 0) print(`(exit code ${m.code})\n`, 'info');
                    setRunning(false);
                    paintPrompt();
                    input.focus();
                    break;
            }
        };
        ws.onclose = () => {
            if (running) print('(the connection to the host was lost)\n', 'err');
            setRunning(false);
            status.textContent = 'Disconnected. Press Enter to connect again.';
        };
    }

    function submit() {
        const text = input.value;
        if (!ws || ws.readyState !== WebSocket.OPEN) {
            connect();
            return;
        }
        if (running) {
            print(text + '\n', 'info');
            ws.send(JSON.stringify({ type: 'stdin', text }));
        } else {
            print(`${shortCwd()} > ${text}\n`, 'cmd');
            if (text.trim() && history[history.length - 1] !== text) history.push(text);
            historyAt = history.length;
            if (text.trim().toLowerCase() === 'clear' || text.trim().toLowerCase() === 'cls') output.replaceChildren();
            else {
                setRunning(true);
                paintPrompt();
                ws.send(JSON.stringify({ type: 'run', command: text }));
            }
        }
        input.value = '';
    }

    function stop() {
        if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'kill' }));
    }

    input.onkeydown = (e) => {
        if (e.key === 'Enter') { e.preventDefault(); submit(); }
        else if (e.key === 'c' && e.ctrlKey && input.selectionEnd === input.selectionStart) { e.preventDefault(); stop(); }
        else if (e.key === 'ArrowUp' && !running) {
            e.preventDefault();
            if (historyAt > 0) input.value = history[--historyAt];
        } else if (e.key === 'ArrowDown' && !running) {
            e.preventDefault();
            historyAt = Math.min(history.length, historyAt + 1);
            input.value = history[historyAt] ?? '';
        }
    };
    $('term-stop').onclick = stop;
    $('term-clear').onclick = () => output.replaceChildren();
    $('t-terminal').onclick = () => { showTab('terminal'); connect(); input.focus(); };
    output.onclick = () => { if (!window.getSelection()?.toString()) input.focus(); };
    // Ctrl+` opens it, as in other editors
    window.addEventListener('keydown', (e) => {
        if (e.ctrlKey && e.key === '`') { e.preventDefault(); $('t-terminal').click(); }
    });
}

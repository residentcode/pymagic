// Test explorer: discovered tests as a tree, run all / selected / failed / at the cursor, results with tracebacks.
import { putLabel } from './labels';
import * as monaco from 'monaco-editor';
import { RunClient } from './runclient';
import { showTab } from './panel';
import { openMenu } from './menu';
import { toast } from './toast';
import { summarizeRun, statistics, junitXml, htmlReport, addRun, missingRunner } from './tests-model.js';
import { defineCommand } from './commands.js';

const HISTORY = '.pymagic/test-history.json';
const $ = (id) => document.getElementById(id);
const ICONS = {
    idle: ['○', 'var(--dim)'], queued: ['◔', 'var(--dim)'], running: ['◐', 'var(--amber)'],
    passed: ['✔', 'var(--green)'], failed: ['✖', 'var(--red)'], error: ['⚠', 'var(--red)'],
    skipped: ['⊘', 'var(--dim)'], xfailed: ['◇', 'var(--dim)'], xpassed: ['◆', 'var(--amber)'],
};
export function setupTests(deps) {
    const { editor } = deps;
    const byId = new Map();
    let roots = [];
    let selected = null;
    let client = null;
    let started = 0;
    let markerFiles = new Set();
    const tree = $('tests-tree');
    const detail = $('test-detail');
    const consoleOut = $('tests-console');
    // ------------------------------------------------------------ tree model
    function build(raw, parent) {
        const old = byId.get(raw.id);
        const node = {
            id: raw.id, name: raw.name, kind: raw.kind, file: raw.file, line: raw.line, endLine: raw.endLine,
            // a method inherited from a base class of another file: where it is written
            writtenIn: raw.writtenIn ?? null, writtenAt: raw.writtenAt ?? 0,
            children: [], parent, status: old?.status ?? 'idle', result: old?.result, open: old?.open ?? raw.kind === 'file',
        };
        byId.set(node.id, node);
        node.children = (raw.children ?? []).map((c) => build(c, node));
        return node;
    }
    async function load() {
        const raw = await (await fetch('/api/tests')).json();
        const known = new Set();
        const mark = (r) => { known.add(r.id); (r.children ?? []).forEach(mark); };
        raw.forEach(mark);
        for (const id of [...byId.keys()])
            if (!known.has(id) && !id.includes('['))
                byId.delete(id); // keep parametrized cases of kept parents
        roots = raw.map((r) => build(r));
        render();
        summarize();
    }
    function leaves(n) {
        return n.children.length === 0 ? [n] : n.children.flatMap(leaves);
    }
    function aggregate(n) {
        if (n.children.length === 0)
            return n.status;
        const kids = n.children.map(aggregate);
        for (const s of ['running', 'failed', 'error'])
            if (kids.includes(s))
                return s;
        if (kids.includes('queued'))
            return 'queued';
        if (kids.every((s) => s === 'idle'))
            return 'idle';
        if (kids.some((s) => s === 'idle'))
            return 'idle';
        return kids.includes('passed') ? 'passed' : kids[0];
    }
    function paint(n) {
        for (let x = n; x; x = x.parent) {
            if (!x.icon)
                continue;
            const [glyph, color] = ICONS[aggregate(x)];
            x.icon.textContent = glyph;
            x.icon.style.color = color;
        }
    }
    // ------------------------------------------------------------ rendering
    function render() {
        paintRunMarks();
        if (showingFixtures)
            return;   // the Fixtures view is shown instead of the tree
        tree.textContent = '';
        if (roots.length === 0) {
            tree.innerHTML = '<div class="empty">No tests found. Test files are named test*.py or *_test.py.</div>';
            return;
        }
        const ul = document.createElement('ul');
        for (const r of roots)
            ul.appendChild(renderNode(r));
        tree.appendChild(ul);
    }
    function renderNode(n) {
        const li = document.createElement('li');
        const row = document.createElement('div');
        row.className = 'trow' + (selected === n ? ' sel' : '');
        const caret = document.createElement('span');
        caret.className = 'caret';
        caret.textContent = n.children.length ? (n.open ? '▾' : '▸') : '';
        const icon = document.createElement('span');
        icon.className = 'ticon';
        const label = document.createElement('span');
        label.className = 'tname';
        label.textContent = n.name;
        const play = document.createElement('span');
        play.className = 'tplay';
        play.textContent = '▶';
        play.title = 'Run';
        row.append(caret, icon, label, play);
        li.appendChild(row);
        n.row = row;
        n.icon = icon;
        const [glyph, color] = ICONS[aggregate(n)];
        icon.textContent = glyph;
        icon.style.color = color;
        caret.onclick = (e) => { e.stopPropagation(); n.open = !n.open; render(); };
        row.onclick = () => select(n);
        row.ondblclick = () => void goTo(n);
        play.onclick = (e) => { e.stopPropagation(); void run([n.id]); };
        row.oncontextmenu = (e) => {
            e.preventDefault();
            select(n);
            openMenu({ x: e.clientX, y: e.clientY }, [
                { label: 'Run', run: () => void run([n.id]) },
                { label: 'Debug', run: () => debugTests([n.id]) },
                { label: 'Profile', run: () => profileTests([n.id]) },
                { label: 'Run with coverage', run: () => void run([n.id], true) },
                '-',
                { label: 'Go to the code', run: () => void goTo(n) },
            ]);
        };
        if (n.children.length && n.open) {
            const ul = document.createElement('ul');
            for (const c of n.children)
                ul.appendChild(renderNode(c));
            li.appendChild(ul);
            n.list = ul;
        }
        return li;
    }
    function select(n) {
        selected?.row?.classList.remove('sel');
        selected = n;
        n.row?.classList.add('sel');
        showDetail(n);
    }
    async function goTo(n, line) {
        // an inherited test goes to the method in its base class's file
        if (line === undefined && n.writtenIn) {
            await deps.openFile(n.writtenIn, new monaco.Range(n.writtenAt, 1, n.writtenAt, 1));
            return;
        }
        line ??= n.line;
        await deps.openFile(n.file, new monaco.Range(line, 1, line, 1));
    }
    function showDetail(n) {
        detail.textContent = '';
        const title = $('detail-title');
        title.textContent = n.id;
        const add = (cls, text, tag = 'div') => {
            const el = document.createElement(tag);
            el.className = cls;
            el.textContent = text;
            detail.appendChild(el);
            return el;
        };
        const r = n.result;
        if (!r) {
            const kids = leaves(n);
            if (n.children.length) {
                const count = (s) => kids.filter((k) => k.status === s).length;
                add('dim', `${kids.length} tests: ${count('passed')} passed, ${count('failed') + count('error')} failed, ${count('skipped')} skipped, ${count('idle') + count('queued')} not run`);
            }
            else
                add('dim', n.status === 'queued' ? 'Waiting to run…' : n.status === 'running' ? 'Running…' : 'Not run yet.');
            return;
        }
        add(`outcome ${r.outcome}`, `${r.outcome.toUpperCase()}   ${(r.duration * 1000).toFixed(1)} ms`);
        if (r.message)
            add('msg', r.message);
        if (r.file && r.line) {
            const link = add('link', `Go to ${r.file}:${r.line}`);
            link.onclick = () => void deps.openFile(r.file, new monaco.Range(r.line, 1, r.line, 1));
        }
        if (r.traceback)
            add('tb', r.traceback, 'pre');
        if (r.output) {
            add('dim', 'Captured output');
            add('tb', r.output, 'pre');
        }
    }
    // ------------------------------------------------------------ results
    function ensureNode(id, file, line) {
        let node = byId.get(id);
        if (node)
            return node;
        // a case the static scan could not know about (parametrized tests): hang it under its function
        const base = id.includes('[') ? id.slice(0, id.indexOf('[')) : id;
        let parent = byId.get(base);
        if (!parent || base === id) {
            const parts = id.split('::');
            parent = byId.get(parts[0]);
            if (!parent) {
                parent = { id: parts[0], name: parts[0], kind: 'file', file: parts[0], line: 1, endLine: 1, children: [], status: 'idle', open: true };
                byId.set(parent.id, parent);
                roots.push(parent);
            }
            for (let i = 1; i < parts.length - 1; i++) {
                const cid = parts.slice(0, i + 1).join('::');
                let c = byId.get(cid);
                if (!c) {
                    c = { id: cid, name: parts[i], kind: 'class', file: parts[0], line: 1, endLine: 1, children: [], parent, status: 'idle', open: true };
                    byId.set(cid, c);
                    parent.children.push(c);
                }
                parent = c;
            }
        }
        node = {
            id, name: id.includes('[') ? id.slice(id.indexOf('[')) : id.split('::').pop(), kind: 'case',
            file: file ?? parent.file, line: line || parent.line, endLine: parent.endLine, children: [], parent, status: 'idle', open: false,
        };
        byId.set(id, node);
        if (parent.children.length === 0 && parent.kind !== 'file') { /* the function becomes a group of cases */ }
        parent.children.push(node);
        parent.open = true;
        render();
        return node;
    }
    function setStatus(n, s) {
        n.status = s;
        paint(n);
        schedulePaintMarks();
    }
    let marksTimer = 0;
    function schedulePaintMarks() {
        clearTimeout(marksTimer);
        marksTimer = window.setTimeout(() => paintRunMarks(), 150);
    }
    async function updateMarkers() {
        const perFile = new Map();
        for (const n of byId.values()) {
            const r = n.result;
            if (!r || (r.outcome !== 'failed' && r.outcome !== 'error') || !r.file || !r.line)
                continue;
            const list = perFile.get(r.file) ?? [];
            list.push({
                severity: monaco.MarkerSeverity.Error, startLineNumber: r.line, startColumn: 1, endLineNumber: r.line, endColumn: 1000,
                message: `${n.name}: ${r.message}`, source: 'tests',
            });
            perFile.set(r.file, list);
        }
        for (const file of new Set([...markerFiles, ...perFile.keys()])) {
            const model = await deps.modelFor(file);
            if (model)
                monaco.editor.setModelMarkers(model, 'pymagic-tests', perFile.get(file) ?? []);
        }
        markerFiles = new Set(perFile.keys());
    }
    function summarize() {
        const all = [...byId.values()].filter((n) => n.children.length === 0);
        const count = (...s) => all.filter((n) => s.includes(n.status)).length;
        const parts = [];
        const passed = count('passed', 'xfailed'), failed = count('failed', 'error', 'xpassed'), skipped = count('skipped');
        if (passed)
            parts.push(`${passed} passed`);
        if (failed)
            parts.push(`${failed} failed`);
        if (skipped)
            parts.push(`${skipped} skipped`);
        const running = count('running', 'queued');
        if (running)
            parts.push(`${running} running`);
        const total = all.reduce((t, n) => t + (n.result?.duration ?? 0), 0);
        $('tests-summary').textContent = parts.length ? parts.join(' · ') + (total ? ` · ${total.toFixed(2)}s` : '') : `${all.length} tests`;
        $('tests-summary').className = failed ? 'bad' : passed ? 'good' : '';
        $('tests-rerun').disabled = client !== null || failed === 0;
    }
    function appendConsole(text, cls = '') {
        const span = document.createElement('span');
        if (cls)
            span.className = cls;
        span.textContent = text.split('\r\n').join('\n');
        consoleOut.appendChild(span);
        consoleOut.scrollTop = consoleOut.scrollHeight;
    }
    function handle(m) {
        switch (m.type) {
            case 'output':
                appendConsole(m.text, m.stream === 'stderr' ? 'err' : '');
                break;
            case 'error':
                appendConsole(m.message + '\n', 'err');
                finish();
                break;
            case 'collected':
                for (const t of m.tests)
                    setStatus(ensureNode(t.id, t.file, t.line), 'queued');
                summarize();
                break;
            case 'testStarted': {
                const n = ensureNode(m.id);
                n.result = undefined;
                setStatus(n, 'running');
                summarize();
                break;
            }
            case 'testResult': {
                const n = ensureNode(m.id);
                n.result = { outcome: m.outcome, duration: m.duration, message: m.message, traceback: m.traceback, output: m.output, file: m.file, line: m.line };
                setStatus(n, m.outcome);
                if (selected === n)
                    showDetail(n);
                void updateMarkers();
                summarize();
                break;
            }
            case 'collectError': {
                const n = ensureNode(String(m.file).split('::')[0]);
                n.result = { outcome: 'error', duration: 0, message: 'Collection failed', traceback: m.message, output: '', file: null, line: null };
                setStatus(n, 'error');
                appendConsole(`Collection error in ${m.file}\n`, 'err');
                showDetail(n);
                break;
            }
            case 'exited':
                appendConsole(`\nTest process finished with exit code ${m.code}\n`, 'info');
                finish();
                break;
        }
    }
    // ------------------------------------------------------------ the runs: history, statistics, export
    let lastIds = null;        // what the last run ran ([] for all), for Auto
    let shownRun = null;       // the run whose results the tree shows (the last one, or one picked from History)
    let history = [];
    fetch('/api/file?path=' + encodeURIComponent(HISTORY)).then((r) => (r.ok ? r.json() : [])).then((h) => { history = Array.isArray(h) ? h : []; }).catch(() => { /* none yet */ });
    function recordRun() {
        const results = [...byId.values()].filter((n) => n.children.length === 0 && n.result)
            .map((n) => ({ id: n.id, outcome: n.result.outcome, duration: n.result.duration ?? 0, message: n.result.message ?? '', traceback: n.result.traceback ?? '' }));
        if (results.length === 0)
            return;
        shownRun = { when: new Date().toISOString(), framework: $('tests-fw').value, results };
        // the history keeps the outcome of each test; the tracebacks stay with the run in this window only
        history = addRun(history, { ...shownRun, results: results.map(({ traceback, ...r }) => r) });
        fetch('/api/file?path=' + encodeURIComponent(HISTORY), { method: 'PUT', body: JSON.stringify(history) }).catch(() => { /* kept for this session */ });
    }
    const label = (run) => {
        const s = summarizeRun(run.results);
        const when = new Date(run.when);
        return `${when.toLocaleDateString()} ${when.toLocaleTimeString()} · ${s.passed} passed${s.failed + s.errors ? `, ${s.failed + s.errors} failed` : ''}${s.skipped ? `, ${s.skipped} skipped` : ''} · ${s.duration.toFixed(2)}s`;
    };
    function showRun(run) {
        shownRun = run;
        for (const n of byId.values())
            if (n.children.length === 0) {
                n.result = undefined;
                setStatus(n, 'idle');
            }
        for (const r of run.results) {
            const n = ensureNode(r.id);
            n.result = { outcome: r.outcome, duration: r.duration, message: r.message, traceback: r.traceback ?? '', output: '', file: null, line: null };
            setStatus(n, r.outcome);
        }
        summarize();
        $('tests-summary').textContent += ` · the run of ${new Date(run.when).toLocaleString()}`;
    }
    $('tests-history').onclick = (e) => openMenu(e.currentTarget, history.length
        ? history.map((run, i) => ({ label: label(run), run: () => showRun(run), checked: i === 0 && shownRun?.when === run.when }))
        : [{ label: 'No runs yet', disabled: true }]);
    $('tests-stats').onclick = () => {
        if (!shownRun) {
            toast('Run the tests first.', true);
            return;
        }
        const s = statistics(shownRun.results), sum = summarizeRun(shownRun.results);
        $('detail-title').textContent = 'Statistics';
        const table = (head, rows) => {
            const t = document.createElement('table');
            t.className = 'test-stats';
            const tr = document.createElement('tr');
            for (const h of head) {
                const th = document.createElement('th');
                th.textContent = h;
                tr.append(th);
            }
            t.append(tr);
            for (const row of rows) {
                const r = document.createElement('tr');
                for (const cell of row) {
                    const td = document.createElement('td');
                    td.textContent = cell;
                    r.append(td);
                }
                t.append(r);
            }
            return t;
        };
        const intro = document.createElement('p');
        intro.textContent = `${sum.total} tests in ${s.files.length} files, ${sum.duration.toFixed(2)} s: ${sum.passed} passed, ${sum.failed} failed, ${sum.errors} errors, ${sum.skipped} skipped.`;
        detail.replaceChildren(intro,
            table(['File', 'Tests', 'Failed', 'Time'], s.files.map((f) => [f.file, String(f.tests), String(f.failed), `${f.duration.toFixed(3)} s`])),
            table(['Test', 'Result', 'Time'], s.tests.slice(0, 200).map((t) => [t.id, t.outcome, `${(t.duration ?? 0).toFixed(3)} s`])));
    };
    function download(name, type, text) {
        const a = document.createElement('a');
        a.href = URL.createObjectURL(new Blob([text], { type }));
        a.download = name;
        document.body.append(a);
        a.click();
        a.remove();
        window.setTimeout(() => URL.revokeObjectURL(a.href), 5000);
    }
    $('tests-export').onclick = (e) => {
        if (!shownRun) {
            toast('Run the tests first.', true);
            return;
        }
        const stamp = shownRun.when.slice(0, 19).replace(/[:T]/g, '-');
        openMenu(e.currentTarget, [
            { label: 'As an HTML page…', run: () => download(`test-results-${stamp}.html`, 'text/html', htmlReport(shownRun)) },
            { label: 'As JUnit XML…', run: () => download(`test-results-${stamp}.xml`, 'application/xml', junitXml(shownRun)) },
        ]);
    };
    // Auto: the last run again a moment after a file is saved, when nothing is running
    let autoTimer = 0, ownSave = false, autoRun = false;
    window.addEventListener('pymagic:saved', () => {
        if (!$('tests-auto').checked || lastIds === null || ownSave || client)
            return;
        clearTimeout(autoTimer);
        autoTimer = window.setTimeout(() => { if (!client) { autoRun = true; void run(lastIds); } }, 800);
    });
    /** A run that could not start for want of its package: a button in the console installs it. */
    function offerInstall() {
        const pkg = missingRunner(consoleOut.textContent);
        if (!pkg)
            return;
        const b = document.createElement('button');
        b.className = 'tests-install';
        b.textContent = `Install ${pkg}`;
        b.onclick = () => window.dispatchEvent(new CustomEvent('pymagic:install', { detail: pkg }));
        consoleOut.append(b, document.createTextNode('\n'));
    }
    function finish() {
        const wasRunning = client !== null || debugRun;
        client?.close();
        client = null;
        debugRun = false;
        if (wasRunning) {
            recordRun();
            offerInstall();
        }
        if (coverageRun) {
            coverageRun = false;
            window.dispatchEvent(new CustomEvent('pymagic:coverage'));
        }
        for (const n of byId.values())
            if (n.status === 'queued' || n.status === 'running')
                setStatus(n, 'idle');
        setButtons();
        summarize();
        if (selected)
            showDetail(selected);
    }
    function setButtons() {
        const busy = client !== null || debugRun;
        for (const id of ['tests-all', 'tests-cov', 'tests-sel', 'tests-here', 'tests-debug'])
            $(id).disabled = busy;
        $('tests-rerun').disabled = busy;
        $('tests-stop').disabled = !busy;
    }
    // Stop: the test process is ended; the tests that did not run go back to idle
    $('tests-stop').onclick = () => {
        if (client)
            client.send({ type: 'stop' });
        else if (debugRun)
            window.dispatchEvent(new CustomEvent('pymagic:stopDebugTests'));
        appendConsole('\nStopped.\n', 'info');
    };
    // ------------------------------------------------------------ running
    let coverageRun = false;
    async function run(ids, coverage = false, extra = {}) {
        if (client)
            return;
        coverageRun = coverage;
        showTab('tests');
        // the save a run starts with is not a change for Auto to run again on
        ownSave = true;
        try {
            await deps.saveAll();
        }
        finally {
            ownSave = false;
        }
        // the project is kept as it is before a run you asked for (not before each run of Auto)
        const byAuto = autoRun;
        autoRun = false;
        if (!byAuto)
            await putLabel(ids.length === 1 ? `Before running ${byId.get(ids[0])?.name ?? 'a test'}` : 'Before running the tests');
        await load();
        consoleOut.textContent = '';
        const wanted = ids.length ? ids : roots.map((r) => r.id);
        for (const id of wanted) {
            const n = byId.get(id);
            if (!n)
                continue;
            for (const leaf of leaves(n)) {
                leaf.result = undefined;
                setStatus(leaf, 'queued');
            }
        }
        started = performance.now();
        lastIds = ids;
        const framework = $('tests-fw').value;
        client = new RunClient({ type: 'startTests', ids, framework, coverage, ...extra });
        client.onMessage = handle;
        client.onClose = () => { if (client) {
            appendConsole('\nConnection to the host lost.\n', 'err');
            finish();
        } };
        setButtons();
        summarize();
    }
    /** The test (or class, or file) around the cursor. */
    function idAtCursor() {
        const model = editor.getModel(), pos = editor.getPosition();
        const rel = model && deps.relOf(model);
        if (!rel || !pos)
            return null;
        let best = null;
        for (const n of byId.values()) {
            if (n.file !== rel || n.kind === 'case' || pos.lineNumber < n.line || pos.lineNumber > n.endLine)
                continue;
            if (!best || (n.endLine - n.line) <= (best.endLine - best.line))
                best = n;
        }
        return best?.id ?? null;
    }
    function runAtCursor() {
        const id = idAtCursor();
        if (id)
            void run([id]);
        else {
            showTab('tests');
            appendConsole('The cursor is not inside a test.\n', 'info');
        }
    }
    $('tests-all').onclick = () => void run([]);
    $('tests-cov').onclick = () => void run([], true);
    $('tests-sel').onclick = () => { if (selected)
        void run([selected.id]); };
    $('tests-here').onclick = runAtCursor;
    // a run configuration of tests (Django tests): its framework and settings, its results in the tree
    window.addEventListener('pymagic:runTests', (e) => {
        const { ids, framework, settings, failfast } = e.detail;
        $('tests-fw').value = framework;
        void run(ids, false, { settings, failfast });
    });
    /** Debugging tests: the test runner itself started under the debugger (Run panel), so the tests' breakpoints stop;
     * its results come back here (pymagic:testMessage) and fill the tree as a run's do. */
    let debugRun = false;
    async function debugTests(ids) {
        if (client || debugRun)
            return;
        await load();
        consoleOut.textContent = '';
        for (const id of ids.length ? ids : roots.map((r) => r.id)) {
            const n = byId.get(id);
            for (const leaf of n ? leaves(n) : []) {
                leaf.result = undefined;
                setStatus(leaf, 'queued');
            }
        }
        started = performance.now();
        lastIds = ids;
        debugRun = true;
        setButtons();
        summarize();
        window.dispatchEvent(new CustomEvent('pymagic:debugTests', { detail: { ids, framework: $('tests-fw').value } }));
    }
    /** Profiling tests: pytest (or unittest, as the panel's framework says) under the profiler; the Profiler tab shows where the time went. */
    function profileTests(ids) {
        const fw = $('tests-fw').value;
        window.dispatchEvent(new CustomEvent('pymagic:profileTests', { detail: { ids, framework: fw === 'pytest' || fw === 'unittest' ? fw : 'auto' } }));
    }
    window.addEventListener('pymagic:testMessage', (e) => {
        if (debugRun)
            handle(e.detail);
    });
    $('tests-debug').onclick = () => {
        const id = selected?.id ?? idAtCursor();
        debugTests(id ? [id] : []);
    };
    defineCommand({ id: 'pymagic.tests.debugAtCursor', label: 'Debug the test at the cursor', group: 'Run', keys: ['Ctrl+Shift+F9'], run: () => {
        const id = idAtCursor();
        if (id)
            debugTests([id]);
        else {
            showTab('tests');
            appendConsole('The cursor is not inside a test.\n', 'info');
        }
    } });
    $('tests-rerun').onclick = () => {
        const failed = [...byId.values()].filter((n) => n.children.length === 0 && (n.status === 'failed' || n.status === 'error')).map((n) => n.id);
        if (failed.length)
            void run(failed);
    };
    $('tests-refresh').onclick = () => void load();
    // ------------------------------------------------------------ run marks in the gutter: each test and test class, with its last result
    const runMarks = editor.createDecorationsCollection();
    function markedNodes(rel) {
        // an inherited test has no mark of its own: its class's mark runs it
        return [...byId.values()].filter((n) => n.file === rel && n.line && n.kind !== 'file' && n.kind !== 'case' && !n.writtenIn);
    }
    function paintRunMarks() {
        const model = editor.getModel();
        const rel = model && deps.relOf(model);
        if (!rel || model.getLanguageId() !== 'python') {
            runMarks.clear();
            return;
        }
        runMarks.set(markedNodes(rel).map((n) => ({
            range: new monaco.Range(n.line, 1, n.line, 1),
            options: {
                glyphMarginClassName: `test-run test-run-${n.status}`,
                glyphMarginHoverMessage: { value: `${n.kind === 'class' ? 'Tests' : 'Test'} **${n.name}**: ${n.status === 'idle' ? 'not run yet' : n.status}. Click to run or debug it.` },
                glyphMargin: { position: monaco.editor.GlyphMarginLane.Left },
                stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
            },
        })));
    }
    editor.onDidChangeModel(() => paintRunMarks());
    editor.onMouseDown((e) => {
        if (e.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN || !e.target.position || e.target.detail?.glyphMarginLane !== monaco.editor.GlyphMarginLane.Left)
            return;
        const model = editor.getModel();
        const line = e.target.position.lineNumber;
        if (!model || !model.getLineDecorations(line).some((d) => d.options.glyphMarginClassName?.startsWith('test-run')))
            return;
        // the test whose mark it is: decorations follow the edits, so the line of the node may be old
        const at = runMarks.getRanges().findIndex((r) => r.startLineNumber === line);
        const nodes = markedNodes(deps.relOf(model));
        const n = at >= 0 && nodes[at] ? nodes[at] : nodes.find((x) => x.line === line);
        if (!n)
            return;
        const where = { x: e.event.browserEvent.clientX, y: e.event.browserEvent.clientY };
        openMenu(where, [
            { label: `Run '${n.name}'`, run: () => void run([n.id]) },
            { label: `Debug '${n.name}'`, run: () => debugTests([n.id]) },
            { label: `Profile '${n.name}'`, run: () => profileTests([n.id]) },
            { label: `Run '${n.name}' with coverage`, run: () => void run([n.id], true) },
        ]);
    });
    // ------------------------------------------------------------ fixtures: every one the tests can use, where it is, its scope and its users
    const fixturesButton = document.createElement('button');
    fixturesButton.id = 'tests-fixtures';
    fixturesButton.title = 'The fixtures the tests can use: where each is defined, its scope, what it gives and how many ask for it';
    fixturesButton.textContent = 'Fixtures';
    $('tests-refresh').after(fixturesButton);
    let showingFixtures = false;
    async function showFixtures() {
        const list = (await deps.lsp.request('pymagic/fixtures', {}).catch(() => null)) ?? [];
        tree.replaceChildren();
        const filter = document.createElement('input');
        filter.className = 'fx-filter';
        filter.placeholder = 'Filter fixtures';
        tree.append(filter);
        const body = document.createElement('div');
        tree.append(body);
        const paintList = () => {
            body.replaceChildren();
            const words = filter.value.trim().toLowerCase();
            const groups = new Map();
            for (const f of list.filter((x) => !words || x.name.toLowerCase().includes(words) || (x.origin ?? '').toLowerCase().includes(words))) {
                const key = f.where === 'pytest' ? 'pytest' : `${f.where} · ${f.origin}`;
                if (!groups.has(key))
                    groups.set(key, []);
                groups.get(key).push(f);
            }
            for (const [key, items] of groups) {
                const head = document.createElement('div');
                head.className = 'fx-group';
                head.textContent = key;
                body.append(head);
                for (const f of items) {
                    const row = document.createElement('div');
                    row.className = 'fx-row' + (f.uses === 0 && f.where !== 'pytest' && !f.autouse ? ' fx-unused' : '');
                    const name = document.createElement('span');
                    name.className = 'fx-name';
                    name.textContent = f.name;
                    const meta = document.createElement('span');
                    meta.className = 'fx-meta';
                    meta.textContent = [f.scope, f.autouse ? 'autouse' : null, f.type, `${f.uses} use${f.uses === 1 ? '' : 's'}`].filter(Boolean).join(' · ');
                    row.append(name, meta);
                    row.title = f.doc ?? '';
                    if (f.uri) {
                        row.onclick = () => {
                            const rel = deps.relOfUri(f.uri);
                            if (rel)
                                void deps.openFile(rel, new monaco.Range(f.line + 1, 1, f.line + 1, 1));
                        };
                    }
                    body.append(row);
                }
            }
            if (!body.children.length)
                body.append(Object.assign(document.createElement('div'), { className: 'dim', textContent: 'No fixture.' }));
        };
        filter.oninput = paintList;
        paintList();
        $('detail-title').textContent = 'Fixtures';
        detail.textContent = `${list.filter((f) => f.where !== 'pytest').length} fixtures of the project and its plugins, ${list.filter((f) => f.where === 'pytest').length} of pytest. A dimmed one is asked for by nothing.`;
    }
    fixturesButton.onclick = () => {
        showingFixtures = !showingFixtures;
        fixturesButton.classList.toggle('active', showingFixtures);
        if (showingFixtures)
            void showFixtures();
        else
            render();
    };
    $('t-tests').onclick = () => { showTab('tests'); if (roots.length === 0)
        void load(); };
    $('b-tests').onclick = () => { showTab('tests'); void load(); };
    defineCommand({ id: 'pymagic.tests.runAtCursor', label: 'Run the test at the cursor', group: 'Run', keys: ['Ctrl+Shift+F10'], run: () => runAtCursor() });
    void load().catch(() => { });
    setButtons();
    /** Runs the tests of a file, or of every test file under a folder (the menus of the project tree and the tabs). */
    async function runPath(path, debug = false) {
        await load();
        const ids = roots.filter((r) => !path || r.file === path || r.file.startsWith(path + '/')).map((r) => r.id);
        if (debug && ids.length) {
            debugTests(path ? ids : []);
            return;
        }
        if (ids.length === 0) {
            showTab('tests');
            appendConsole(`No tests were found in ${path || 'the project'}.
`, 'info');
            return;
        }
        await run(ids);
    }
    return { runPath };
}

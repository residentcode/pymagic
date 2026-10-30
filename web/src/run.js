// Run and debug: toolbar, output panel, breakpoints in the gutter, stepping, frames, variables and evaluate.
import * as monaco from 'monaco-editor';
import { startOf } from './runconfig-model.js';
import { RunClient } from './runclient';
import { showTab } from './panel';
import { editBreakpoint, isPlain } from './bpdialog';
import { ansiReader } from './ansi';
import { colouredSpans } from './terminal';
import { inlineLines, inlineText, callsOn, expressionAt, stackText } from './debug-model.js';
import { openMenu } from './menu';
import { toast } from './toast';
const $ = (id) => document.getElementById(id);
const STORAGE_KEY = 'pymagic.breakpoints';
const norm = (p) => p.replace(/\\/g, '/').toLowerCase();
export function setupRun(deps) {
    const { editor } = deps;
    const rootNorm = norm(deps.root).replace(/\/$/, '');
    const metaOf = new Map(); // model uri + decoration id -> settings of a breakpoint
    const keyOf = (model, id) => model.uri.toString() + '#' + id;
    const isBp = (d) => !!d.options.glyphMarginClassName?.startsWith('bp-glyph');
    function optionsFor(meta) {
        const kind = meta?.logMessage ? ' bp-log' : meta?.condition || meta?.hitCondition ? ' bp-cond' : '';
        const text = [meta?.condition && `when ${meta.condition}`, meta?.hitCondition && `hit count ${meta.hitCondition}`, meta?.logMessage && `logs "${meta.logMessage}"`]
            .filter(Boolean).join(', ');
        return {
            glyphMarginClassName: 'bp-glyph' + kind,
            glyphMarginHoverMessage: { value: text ? `Breakpoint: ${text}` : 'Breakpoint' },
            stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
        };
    }
    const stored = () => {
        try {
            return JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}');
        }
        catch {
            return {};
        }
    };
    const breakpointsOf = (model) => {
        const byLine = new Map();
        for (const d of model.getAllDecorations().filter(isBp)) {
            const line = d.range.startLineNumber;
            if (!byLine.has(line))
                byLine.set(line, { line, ...(metaOf.get(keyOf(model, d.id)) ?? {}) });
        }
        return [...byLine.values()].sort((a, b) => a.line - b.line);
    };
    /** What the debugger is sent: a plain number, or an object when the breakpoint has settings. */
    const wire = (bps) => bps.map((b) => (isPlain(b) ? b.line : b));
    function persist() {
        const all = {};
        for (const m of monaco.editor.getModels()) {
            const rel = deps.relOf(m);
            const bps = rel ? breakpointsOf(m) : [];
            if (rel && bps.length)
                all[rel] = wire(bps);
        }
        try {
            localStorage.setItem(STORAGE_KEY, JSON.stringify(all));
        }
        catch { /* storage unavailable */ }
    }
    function sendBreakpoints(model) {
        const rel = deps.relOf(model);
        if (rel && run)
            run.send({ type: 'setBreakpoints', file: rel, lines: wire(breakpointsOf(model)) });
    }
    function placeBreakpoint(model, line, meta) {
        const [id] = model.deltaDecorations([], [{ range: new monaco.Range(line, 1, line, 1), options: optionsFor(meta) }]);
        if (meta && !isPlain(meta))
            metaOf.set(keyOf(model, id), meta);
    }
    function removeBreakpoint(model, line) {
        const existing = model.getLineDecorations(line).filter(isBp);
        for (const d of existing)
            metaOf.delete(keyOf(model, d.id));
        model.deltaDecorations(existing.map((d) => d.id), []);
    }
    function toggleBreakpoint(model, line) {
        if (model.getLineDecorations(line).some(isBp))
            removeBreakpoint(model, line);
        else
            placeBreakpoint(model, line);
        persist();
        sendBreakpoints(model);
    }
    async function editBreakpointAt(model, line) {
        const current = breakpointsOf(model).find((b) => b.line === line);
        const result = await editBreakpoint(line, current, !!current);
        if (result === null) {
            editor.focus();
            return;
        }
        removeBreakpoint(model, line);
        if (result !== 'remove')
            placeBreakpoint(model, line, result);
        persist();
        sendBreakpoints(model);
        editor.focus();
    }
    editor.onMouseDown((e) => {
        if (e.target.type === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN && e.target.position) {
            // the right lane holds the override marks (hierarchy.ts)
            if (e.target.detail?.glyphMarginLane === monaco.editor.GlyphMarginLane.Right)
                return;
            const model = editor.getModel();
            if (!model)
                return;
            if (e.event.rightButton)
                void editBreakpointAt(model, e.target.position.lineNumber);
            else
                toggleBreakpoint(model, e.target.position.lineNumber);
        }
    });
    editor.addCommand(monaco.KeyCode.F9, () => {
        const model = editor.getModel(), pos = editor.getPosition();
        if (model && pos)
            toggleBreakpoint(model, pos.lineNumber);
    });
    editor.addCommand(monaco.KeyMod.Shift | monaco.KeyCode.F9, () => {
        const model = editor.getModel(), pos = editor.getPosition();
        if (model && pos)
            void editBreakpointAt(model, pos.lineNumber);
    });
    /** Called for every model the app creates, so saved breakpoints come back. */
    function onModelCreated(rel, model) {
        for (const saved of stored()[rel] ?? []) {
            const bp = typeof saved === 'number' ? { line: saved } : saved;
            if (bp.line <= model.getLineCount())
                placeBreakpoint(model, bp.line, bp);
        }
        model.onDidChangeDecorations(() => { });
        model.onDidChangeContent(() => { persist(); });
    }
    let run = null;
    let debugging = false;
    let paused = false;
    let frames = [];
    let selectedFrame = 0;
    let currentLine = null;
    // ------------------------------------------------------------ UI state
    const panel = $('panel');
    const output = $('output');
    $('t-output').onclick = () => showTab('output');
    $('t-debug').onclick = () => showTab('debug');
    $('t-close').onclick = () => panel.classList.remove('open');
    let colours = ansiReader();
    function append(text, cls = '') {
        const stick = output.scrollTop + output.clientHeight >= output.scrollHeight - 20;
        // a program that prints colours (rich, colorama, pytest --color=yes) shows them
        output.append(...colouredSpans(colours, text.split('\r\n').join('\n'), cls));
        if (stick)
            output.scrollTop = output.scrollHeight;
    }
    function setButtons() {
        const active = run !== null;
        $('b-run').disabled = active;
        $('b-profile').disabled = active;
        $('b-debug').disabled = active && !paused;
        $('b-debug').querySelector('.lbl').textContent = paused ? 'Resume' : 'Debug';
        $('b-debug').querySelector('use').setAttribute('href', paused ? '#i-run' : '#i-debug');
        $('b-stop').disabled = !active;
        for (const id of ['b-over', 'b-into', 'b-out'])
            $(id).disabled = !paused;
        $('runstate').textContent = !active ? '' : paused ? 'paused' : debugging ? 'debugging…' : profiling ? 'profiling…' : 'running…';
    }
    function relOfAbsolute(file) {
        const n = norm(file);
        return n.startsWith(rootNorm + '/') ? file.replace(/\\/g, '/').slice(rootNorm.length + 1) : null;
    }
    function clearCurrentLine() {
        if (currentLine) {
            currentLine.model.deltaDecorations(currentLine.ids, []);
            currentLine = null;
        }
        clearInline();
    }
    // ------------------------------------------------------------ values at the end of the lines
    let inline = null;   // { collections }: one per editor that shows the frame's file
    const inlineStyle = document.head.appendChild(document.createElement('style'));
    const cssString = (text) => '"' + text.replace(/\\/g, '\\\\').replace(/"/g, '\\"').replace(/[\r\n]+/g, ' ') + '"';
    function clearInline() {
        if (inline) {
            for (const c of inline.collections)
                c.clear();
            inline = null;
        }
        inlineStyle.textContent = '';
    }
    /** Shows, at the end of each line of the frame's function up to the stopped one, the values of the names on it. */
    async function showInline(f, model) {
        clearInline();
        const lines = inlineLines((l) => (l <= model.getLineCount() ? model.getLineContent(l) : null), Math.max(f.first ?? f.line, f.line - 200), f.line);
        const names = [...new Set(lines.flatMap((l) => l.names))];
        if (!names.length || !run || !paused)
            return;
        const asked = run;
        const values = (await run.ask({ type: 'values', frameId: selectedFrame, names })).values ?? {};
        if (run !== asked || !paused)
            return;
        // this Monaco does not draw injected text (`after`), so each value is the ::after of an empty decoration at the line's end
        const decorations = [], rules = [];
        for (const { line, names: onLine } of lines) {
            const text = inlineText(onLine, values);
            if (!text)
                continue;
            const cls = `debug-inline-${decorations.length}`;
            rules.push(`.${cls}::after { content: ${cssString('   ' + text)}; }`);
            const end = model.getLineMaxColumn(line);
            decorations.push({ range: new monaco.Range(line, end, line, end), options: { afterContentClassName: `debug-inline ${cls}`, showIfCollapsed: true } });
        }
        clearInline();
        inlineStyle.textContent = rules.join('\n');
        const shows = [editor, focused].filter((e, i, all) => all.indexOf(e) === i && e.getModel() === model);
        inline = { collections: shows.map((e) => e.createDecorationsCollection(decorations)) };
    }
    async function showFrame(index) {
        selectedFrame = index;
        const f = frames[index];
        if (!f)
            return;
        document.querySelectorAll('#frames div').forEach((d, i) => d.classList.toggle('sel', i === index));
        clearCurrentLine();
        const rel = relOfAbsolute(f.file);
        if (rel) {
            await deps.openFile(rel, new monaco.Range(f.line, 1, f.line, 1));
            const model = await deps.modelFor(rel);
            if (model) {
                const ids = model.deltaDecorations([], [{
                        range: new monaco.Range(f.line, 1, f.line, 1),
                        options: { isWholeLine: true, className: 'debug-line', glyphMarginClassName: 'debug-arrow' },
                    }]);
                currentLine = { model, ids };
                editor.revealLineInCenterIfOutsideViewport(f.line);
                void showInline(f, model);
            }
        }
        await loadVariables();
        void refreshWatches();
    }
    // ------------------------------------------------------------ variables
    async function loadVariables() {
        const holder = $('vars');
        holder.textContent = '';
        if (!run || !paused)
            return;
        const scopes = (await run.ask({ type: 'scopes', frameId: selectedFrame })).scopes;
        const list = document.createElement('ul');
        holder.appendChild(list);
        for (const [i, s] of scopes.entries()) {
            const li = document.createElement('li');
            const head = document.createElement('div');
            head.innerHTML = `<span class="caret"></span><span class="scope"></span>`;
            head.querySelector('.scope').textContent = s.name;
            li.appendChild(head);
            list.appendChild(li);
            attachChildren(head, li, s.ref, i === 0 || s.name === 'Locals', s.ref);
        }
    }
    /** Makes a node expandable: children are fetched from the debugger the first time it opens. */
    function attachChildren(head, li, ref, openNow, scopeRef) {
        const caret = head.querySelector('.caret');
        let child = null;
        let open = false;
        const toggle = async () => {
            if (!run)
                return;
            open = !open;
            caret.textContent = open ? '▾' : '▸';
            if (!open) {
                child?.remove();
                child = null;
                return;
            }
            const items = (await run.ask({ type: 'variables', ref })).items;
            if (!open)
                return;
            child = document.createElement('ul');
            for (const v of items)
                child.appendChild(variableNode(v, scopeRef));
            if (!items.length)
                child.innerHTML = '<li><div class="type">(empty)</div></li>';
            li.appendChild(child);
        };
        caret.textContent = '▸';
        caret.onclick = toggle;
        if (openNow)
            void toggle();
    }
    function variableNode(v, scopeRef) {
        const li = document.createElement('li');
        const head = document.createElement('div');
        head.innerHTML = '<span class="caret"></span><span class="name"></span> = <span class="val"></span> <span class="type"></span>';
        head.querySelector('.name').textContent = v.name;
        head.querySelector('.val').textContent = v.value;
        head.querySelector('.type').textContent = v.type;
        li.appendChild(head);
        if (v.ref > 0)
            attachChildren(head, li, v.ref, false);
        if (scopeRef !== undefined) {
            const val = head.querySelector('.val');
            val.title = 'Double-click to change the value';
            val.ondblclick = () => editVariable(val, scopeRef, v);
        }
        return li;
    }
    /** Replaces the shown value by an input; Enter assigns the expression typed there to the variable. */
    function editVariable(valueEl, scopeRef, v) {
        const input = document.createElement('input');
        input.className = 'edit';
        input.value = v.value;
        input.spellcheck = false;
        valueEl.replaceWith(input);
        input.focus();
        input.select();
        let done = false;
        const finish = async (apply) => {
            if (done)
                return;
            done = true;
            if (apply && run && paused) {
                const r = await run.ask({ type: 'setVariable', ref: scopeRef, name: v.name, expression: input.value });
                if (r.error)
                    logLine(`${v.name} = ${input.value}`, r.error);
            }
            await loadVariables();
            void refreshWatches();
        };
        input.onkeydown = (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                void finish(true);
            }
            else if (e.key === 'Escape') {
                e.preventDefault();
                void finish(false);
            }
        };
        input.onblur = () => void finish(false);
    }
    function logLine(question, answer, isError = true) {
        const q = document.createElement('div');
        q.className = 'q';
        q.textContent = '> ' + question;
        const a = document.createElement('div');
        if (isError)
            a.className = 'e';
        a.textContent = answer;
        $('watch-log').append(q, a);
        $('watch-log').scrollTop = $('watch-log').scrollHeight;
    }
    // ------------------------------------------------------------ watches
    const WATCH_KEY = 'pymagic.watches';
    let watches = [];
    try {
        watches = JSON.parse(localStorage.getItem(WATCH_KEY) ?? '[]');
    }
    catch { /* storage unavailable */ }
    const saveWatches = () => { try {
        localStorage.setItem(WATCH_KEY, JSON.stringify(watches));
    }
    catch { /* storage unavailable */ } };
    /** Evaluates every watch in the selected frame (expressions only: a watch never assigns). */
    async function refreshWatches() {
        const holder = $('watches');
        holder.textContent = '';
        for (const [index, expression] of watches.entries()) {
            const row = document.createElement('div');
            row.className = 'w';
            const name = document.createElement('span');
            name.className = 'wx';
            name.textContent = expression;
            const value = document.createElement('span');
            value.className = 'wv';
            const del = document.createElement('span');
            del.className = 'wd';
            del.textContent = '✕';
            del.title = 'Remove the watch';
            del.onclick = () => { watches.splice(index, 1); saveWatches(); void refreshWatches(); };
            row.append(name, value, del);
            holder.appendChild(row);
            if (!run || !paused) {
                value.className = 'wv wn';
                value.textContent = 'not paused';
                continue;
            }
            const r = await run.ask({ type: 'evaluate', frameId: selectedFrame, expression, evalOnly: true });
            if (r.error) {
                value.className = 'wv we';
                value.textContent = r.error;
            }
            else {
                value.textContent = '= ' + r.result;
                value.title = r.valueType;
            }
        }
    }
    const watchAdd = $('watch-add');
    watchAdd.onkeydown = (e) => {
        if (e.key !== 'Enter' || !watchAdd.value.trim())
            return;
        watches.push(watchAdd.value.trim());
        watchAdd.value = '';
        saveWatches();
        void refreshWatches();
    };
    void refreshWatches();
    // ------------------------------------------------------------ frames and messages
    function renderFrames() {
        const holder = $('frames');
        holder.textContent = '';
        frames.forEach((f, i) => {
            const row = document.createElement('div');
            const rel = relOfAbsolute(f.file) ?? f.file.split(/[\\/]/).pop() ?? f.file;
            row.innerHTML = '<span class="fn"></span> <span class="loc"></span>';
            row.querySelector('.fn').textContent = f.name;
            row.querySelector('.loc').textContent = `${rel}:${f.line}`;
            row.onclick = () => void showFrame(i);
            holder.appendChild(row);
        });
    }
    function handle(m) {
        switch (m.type) {
            case 'started':
                append(`${debugging ? 'Debugging' : 'Running'} (pid ${m.pid})\n`, 'info');
                break;
            case 'output':
                append(m.text, m.stream === 'stderr' ? 'err' : '');
                break;
            case 'error':
                append(m.message + '\n', 'err');
                finish();
                break;
            case 'stopped':
                paused = true;
                frames = m.frames;
                if (m.reason === 'exception')
                    append(`Paused on exception: ${m.message}\n`, 'err');
                if (m.thread && m.thread !== 'MainThread')
                    append(`Paused in thread ${m.thread}\n`, 'info');
                renderFrames();
                showTab('debug');
                setButtons();
                void showFrame(0);
                break;
            case 'running':
                paused = false;
                clearCurrentLine();
                $('vars').textContent = '';
                setButtons();
                void refreshWatches();
                break;
            case 'exited':
                append(`\nProcess finished with exit code ${m.code}\n`, 'info');
                if (profiling) {
                    profiling = false;
                    window.dispatchEvent(new CustomEvent('pymagic:profile'));
                }
                finish();
                break;
        }
    }
    function finish() {
        run?.close();
        run = null;
        paused = false;
        frames = [];
        clearCurrentLine();
        $('frames').textContent = '';
        $('vars').textContent = '';
        setButtons();
    }
    // ------------------------------------------------------------ commands
    let profiling = false;
    async function start(debug, profile = false) {
        if (run)
            return;
        const chosen = deps.runConfig?.();
        if (chosen) {
            const s = startOf(chosen);
            await launch(s.file, debug, profile, s.args, { env: s.env, cwd: s.cwd });
            return;
        }
        const model = editor.getModel();
        const rel = model && deps.relOf(model);
        if (!rel)
            return;
        await launch(rel, debug, profile, []);
    }
    // runs a file of the project with arguments (the Django tab runs manage.py this way); false when a run is going
    async function launch(rel, debug, profile, args, extra = {}) {
        if (run)
            return false;
        await deps.saveAll();
        const breakpoints = {};
        for (const m of monaco.editor.getModels()) {
            const r = deps.relOf(m);
            const bps = r ? breakpointsOf(m) : [];
            if (r && bps.length)
                breakpoints[r] = wire(bps);
        }
        output.textContent = '';
        colours = ansiReader();
        debugging = debug;
        profiling = profile;
        paused = false;
        showTab('output');
        run = new RunClient({ type: 'start', file: rel, debug, profile, args, env: extra.env, cwd: extra.cwd, breakpoints, exceptionBreak: raisedBox.checked ? 'raised' : 'uncaught',
            exceptionClasses: exceptionClasses(), muted: muteBox.checked });
        run.onMessage = handle;
        run.onClose = () => { if (run) {
            append('\nConnection to the host lost.\n', 'err');
            finish();
        } };
        setButtons();
        return true;
    }
    const raisedBox = $('exc-raised');
    try {
        raisedBox.checked = localStorage.getItem('pymagic.breakOnRaised') === '1';
    }
    catch { /* storage unavailable */ }
    const classesBox = $('exc-classes');
    const muteBox = $('bp-mute');
    const exceptionClasses = () => classesBox.value.split(/[\s,]+/).filter(Boolean);
    try {
        classesBox.value = localStorage.getItem('pymagic.breakOnRaisedClasses') ?? '';
        muteBox.checked = localStorage.getItem('pymagic.mutedBreakpoints') === '1';
    }
    catch { /* storage unavailable */ }
    const sendExceptionBreak = () => run?.send({ type: 'setExceptionBreak', mode: raisedBox.checked ? 'raised' : 'uncaught', classes: exceptionClasses() });
    raisedBox.onchange = () => {
        try {
            localStorage.setItem('pymagic.breakOnRaised', raisedBox.checked ? '1' : '0');
        }
        catch { /* storage unavailable */ }
        sendExceptionBreak();
    };
    classesBox.onchange = () => {
        try {
            localStorage.setItem('pymagic.breakOnRaisedClasses', classesBox.value);
        }
        catch { /* storage unavailable */ }
        sendExceptionBreak();
    };
    function setMuted(on) {
        muteBox.checked = on;
        document.documentElement.classList.toggle('bp-muted', on);
        try {
            localStorage.setItem('pymagic.mutedBreakpoints', on ? '1' : '0');
        }
        catch { /* storage unavailable */ }
        run?.send({ type: 'mute', on });
    }
    muteBox.onchange = () => setMuted(muteBox.checked);
    document.documentElement.classList.toggle('bp-muted', muteBox.checked);
    $('frames-copy').onclick = () => {
        if (!frames.length)
            return;
        void navigator.clipboard.writeText(stackText(frames)).then(() => toast('Call stack copied'), () => toast('Could not copy'));
    };
    // ------------------------------------------------------------ stepping to a place
    /** The editor the cursor is in: the main one, or a side of a split. */
    let focused = editor;
    const cursorOf = (ed = focused) => {
        const model = ed.getModel(), pos = ed.getPosition();
        const rel = model && deps.relOf(model);
        return rel && pos ? { ed, model, rel, line: pos.lineNumber } : null;
    };
    /** Run to Cursor (Alt+F9), and with `force` Force Run to Cursor (Ctrl+Alt+F9), which passes the breakpoints on the way. */
    function runToCursor(force = false) {
        const at = cursorOf();
        if (run && paused && at)
            run.send({ type: 'runTo', file: at.rel, line: at.line, force });
    }
    /** Jump to Cursor: goes on from the cursor's line of the stopped function, without running the lines in between. */
    async function jumpToCursor() {
        const at = cursorOf();
        if (!run || !paused || !at)
            return;
        const r = await run.ask({ type: 'jump', line: at.line });
        if (r.error) {
            toast(r.error);
            return;
        }
        frames = r.frames;
        renderFrames();
        await showFrame(0);
    }
    /** Smart Step Into (Shift+F7): enters the call of the stopped line chosen from a list, when it has more than one. */
    async function smartStepInto() {
        if (!run || !paused || !frames.length)
            return;
        const rel = relOfAbsolute(frames[0].file);
        const model = rel ? await deps.modelFor(rel) : null;
        const calls = model ? callsOn(model.getLineContent(frames[0].line)) : [];
        if (calls.length < 2) {
            if (calls.length === 1)
                run.send({ type: 'stepInto', target: calls[0] });
            else
                resume('stepIn');
            return;
        }
        const ed = focused.getModel() === model ? focused : editor;
        const pos = ed.getScrolledVisiblePosition({ lineNumber: frames[0].line, column: 1 });
        const box = ed.getContainerDomNode().getBoundingClientRect();
        openMenu({ x: box.left + (pos?.left ?? 40) + 40, y: box.top + (pos?.top ?? 0) + (pos?.height ?? 18) },
            calls.map((name) => ({ label: name + '()', run: () => run?.send({ type: 'stepInto', target: name }) })));
    }
    /** Show Execution Point (Alt+F10): back to the stopped line of the innermost frame. */
    function showExecutionPoint() {
        if (paused && frames.length)
            void showFrame(0).then(() => editor.focus());
    }
    // ------------------------------------------------------------ values under the mouse
    monaco.languages.registerHoverProvider('python', {
        async provideHover(model, position) {
            if (!run || !paused || !deps.relOf(model))
                return null;
            const ed = [focused, editor].find((e) => e.getModel() === model);
            const sel = ed?.getSelection();
            let expression = null, range = null;
            if (sel && !sel.isEmpty() && sel.startLineNumber === sel.endLineNumber && sel.containsPosition(position)) {
                expression = model.getValueInRange(sel).trim();
                range = sel;
            }
            else {
                expression = expressionAt(model.getLineContent(position.lineNumber), position.column);
                const word = model.getWordAtPosition(position);
                if (word)
                    range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
            }
            if (!expression)
                return null;
            const r = await run.ask({ type: 'evaluate', frameId: selectedFrame, expression, evalOnly: true });
            if (r.error)
                return sel && !sel.isEmpty() ? { range, contents: [{ value: '`' + r.error + '`' }] } : null;
            return { range, contents: [{ value: '```python\n' + expression + ' = ' + r.result + '\n```' }, { value: r.valueType }] };
        },
    });
    /** Quick Evaluate (Ctrl+Alt+F8): the value of the selection, or of the expression at the cursor, in a popup. */
    function quickEvaluate(ed = focused) {
        if (run && paused)
            ed.trigger('debug', 'editor.action.showHover', { focus: false });
    }
    const resume = (type) => { if (run && paused)
        run.send({ type }); };
    $('b-run').onclick = () => void start(false);
    $('b-profile').onclick = () => void start(false, true);
    $('b-debug').onclick = () => (paused ? resume('continue') : void start(true));
    $('b-stop').onclick = () => run?.send({ type: 'stop' });
    $('b-over').onclick = () => resume('next');
    $('b-into').onclick = () => resume('stepIn');
    $('b-out').onclick = () => resume('stepOut');
    const K = monaco.KeyCode, M = monaco.KeyMod;
    editor.addCommand(K.F5, () => (paused ? resume('continue') : void start(true)));
    editor.addCommand(M.CtrlCmd | K.F5, () => void start(false));
    editor.addCommand(M.Shift | K.F5, () => run?.send({ type: 'stop' }));
    editor.addCommand(K.F10, () => resume('next'));
    editor.addCommand(K.F11, () => resume('stepIn'));
    editor.addCommand(M.Shift | K.F11, () => resume('stepOut'));
    /** The keys of stepping to a place and of looking at values, in the main editor and the sides of a split. */
    function placeKeys(ed) {
        ed.onDidFocusEditorText(() => { focused = ed; });
        ed.addCommand(M.Alt | K.F9, () => { focused = ed; runToCursor(false); });
        ed.addCommand(M.CtrlCmd | M.Alt | K.F9, () => { focused = ed; runToCursor(true); });
        ed.addCommand(M.Shift | K.F7, () => void smartStepInto());
        ed.addCommand(M.Alt | K.F10, showExecutionPoint);
        ed.addCommand(M.CtrlCmd | M.Alt | K.F8, () => { focused = ed; quickEvaluate(ed); });
    }
    placeKeys(editor);
    // program input
    const stdin = $('stdin');
    stdin.onkeydown = (e) => {
        if (e.key !== 'Enter' || !run)
            return;
        append(stdin.value + '\n', 'info');
        run.send({ type: 'stdin', text: stdin.value + '\n' });
        stdin.value = '';
    };
    // evaluate while paused
    const watchIn = $('watch-in');
    const log = $('watch-log');
    watchIn.onkeydown = async (e) => {
        if (e.key !== 'Enter' || !watchIn.value.trim())
            return;
        const expression = watchIn.value;
        watchIn.value = '';
        const q = document.createElement('div');
        q.className = 'q';
        q.textContent = '> ' + expression;
        log.appendChild(q);
        const a = document.createElement('div');
        if (!run || !paused) {
            a.className = 'e';
            a.textContent = 'The program is not paused.';
        }
        else {
            const r = await run.ask({ type: 'evaluate', frameId: selectedFrame, expression });
            if (r.error) {
                a.className = 'e';
                a.textContent = r.error;
            }
            else
                a.textContent = r.result + (r.valueType ? `   (${r.valueType})` : '');
            if (!r.error)
                void loadVariables(); // an assignment may have changed what is shown
        }
        log.appendChild(a);
        log.scrollTop = log.scrollHeight;
    };
    // which interpreter will be used
    setButtons();
    /** Another editor (the right side of a split) takes breakpoints with a click in its margin, F9 and Shift+F9, and the run and debug keys. */
    function attach(other) {
        other.onMouseDown((e) => {
            if (e.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN || !e.target.position)
                return;
            if (e.target.detail?.glyphMarginLane === monaco.editor.GlyphMarginLane.Right)
                return;
            const model = other.getModel();
            if (!model)
                return;
            if (e.event.rightButton)
                void editBreakpointAt(model, e.target.position.lineNumber);
            else
                toggleBreakpoint(model, e.target.position.lineNumber);
        });
        const at = (fn) => () => { const model = other.getModel(), pos = other.getPosition(); if (model && pos)
            fn(model, pos.lineNumber); };
        other.addCommand(monaco.KeyCode.F9, at(toggleBreakpoint));
        other.addCommand(monaco.KeyMod.Shift | monaco.KeyCode.F9, at((m, l) => void editBreakpointAt(m, l)));
        other.addCommand(K.F5, () => (paused ? resume('continue') : void start(true)));
        other.addCommand(M.CtrlCmd | K.F5, () => void start(false));
        other.addCommand(M.Shift | K.F5, () => run?.send({ type: 'stop' }));
        other.addCommand(K.F10, () => resume('next'));
        other.addCommand(K.F11, () => resume('stepIn'));
        other.addCommand(M.Shift | K.F11, () => resume('stepOut'));
        placeKeys(other);
    }
    return {
        onModelCreated, runScript: (rel, args, debug = false) => launch(rel, debug, false, args), isRunning: () => run !== null,
        attach,
        /** The breakpoint commands of the editor (F9, Shift+F9), for the menus. */
        breakpoints: {
            toggle() { const model = editor.getModel(), pos = editor.getPosition(); if (model && pos) toggleBreakpoint(model, pos.lineNumber); },
            edit() { const model = editor.getModel(), pos = editor.getPosition(); if (model && pos) void editBreakpointAt(model, pos.lineNumber); },
            muted: () => muteBox.checked,
            mute: () => setMuted(!muteBox.checked),
        },
        /** The debugger commands of the Run menu; `paused()` tells whether they can be used now. */
        debug: {
            paused: () => run !== null && paused,
            runToCursor, jumpToCursor: () => void jumpToCursor(), smartStepInto: () => void smartStepInto(), showExecutionPoint,
            quickEvaluate: () => quickEvaluate(),
        },
    };
}

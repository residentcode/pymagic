// Run and debug: toolbar, output panel, breakpoints in the gutter, stepping, frames, variables and evaluate.
import * as monaco from 'monaco-editor';
import { beforeSteps, compoundMembers, parseMappings, portOf, startOf } from './runconfig-model.js';
import { RunClient } from './runclient';
import { withOutput } from './services-model.js';
import { showTab } from './panel';
import { editBreakpoint, isPlain } from './bpdialog';
import { ansiReader } from './ansi';
import { colouredSpans } from './terminal';
import { inlineLines, inlineText, callsOn, expressionAt, phpExpressionAt, stackText, functionBreakpoint, childExpression, readWatches, moveItem, declarationLine, historyStep, framesOfStop, threadHeadings, serverProgramNote } from './debug-model.js';
import { openMenu } from './menu';
import { toast } from './toast';
import { linkPieces } from './tracelink-model.js';
import { defineCommand } from './commands.js';
import { scriptEntries, isPackageJson, folderOf as npmFolderOf } from './npm-model.js';
const $ = (id) => document.getElementById(id);
const STORAGE_KEY = 'pymagic.breakpoints';
const norm = (p) => p.replace(/\\/g, '/').toLowerCase();
export function setupRun(deps) {
    const { editor } = deps;
    const rootNorm = norm(deps.root).replace(/\/$/, '');
    const scratchNorm = deps.scratches ? norm(deps.scratches).replace(/\/$/, '') : '';
    const metaOf = new Map(); // model uri + decoration id -> settings of a breakpoint
    const keyOf = (model, id) => model.uri.toString() + '#' + id;
    const isBp = (d) => !!d.options.glyphMarginClassName?.startsWith('bp-glyph');
    function optionsFor(meta) {
        const kind = meta?.logMessage ? ' bp-log' : meta?.condition || meta?.hitCondition ? ' bp-cond' : '';
        const text = [meta?.condition && `when ${meta.condition}`, meta?.hitCondition && `hit count ${meta.hitCondition}`, meta?.logMessage && `logs "${meta.logMessage}"`,
            meta?.after && `disabled until ${meta.after.file}:${meta.after.line} is hit`]
            .filter(Boolean).join(', ');
        const head = meta?.description ? `**${meta.description}**\n\n` : '';
        return {
            glyphMarginClassName: 'bp-glyph' + kind + (meta?.after ? ' bp-after' : ''),
            glyphMarginHoverMessage: { value: head + (text ? `Breakpoint: ${text}` : 'Breakpoint') + '\n\nDrag it to another line to move it; on a `def` line it stops when the function is called.' },
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
    const lineTextOf = (model) => (l) => (l >= 1 && l <= model.getLineCount() ? model.getLineContent(l) : null);
    /** What the debugger is sent: a plain number, or an object when the breakpoint has settings or is on a `def` line
     * (a function breakpoint, stopping when the function is called). */
    const wire = (model, bps) => {
        const rel = deps.relOf(model);
        return bps.map((b) => {
            const fn = functionBreakpoint(lineTextOf(model), b.line);
            const out = { ...b, function: fn?.function, codeLine: fn?.codeLine };
            // waiting for a function breakpoint of the same file: the helper knows it by the line its code starts on
            const target = b.after && b.after.file === rel ? functionBreakpoint(lineTextOf(model), b.after.line) : null;
            if (target)
                out.after = { ...b.after, line: target.codeLine };
            return isPlain(b) && !fn ? b.line : out;
        });
    };
    function persist() {
        const all = {};
        for (const m of monaco.editor.getModels()) {
            const rel = deps.relOf(m);
            const bps = rel ? breakpointsOf(m) : [];
            if (rel && bps.length)
                all[rel] = bps.map((b) => (isPlain(b) ? b.line : b));
        }
        try {
            localStorage.setItem(STORAGE_KEY, JSON.stringify(all));
        }
        catch { /* storage unavailable */ }
    }
    /** The file a model's breakpoints belong to: a project file, or the file that stands for a notebook cell while it is debugged. */
    const fileOf = (model) => deps.relOf(model) ?? deps.cellFileOf?.(model) ?? null;
    function sendBreakpoints(model) {
        const rel = fileOf(model);
        if (rel && run) {
            sentLines.set(model, breakpointsOf(model).map((b) => b.line).join(','));
            run.send({ type: 'setBreakpoints', file: rel, lines: wire(model, breakpointsOf(model)) });
        }
    }
    const sentLines = new WeakMap();
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
        const others = [];
        for (const m of monaco.editor.getModels()) {
            const r = deps.relOf(m);
            if (r)
                others.push(...breakpointsOf(m).filter((b) => !(m === model && b.line === line)).map((b) => ({ file: r, line: b.line })));
        }
        const result = await editBreakpoint(line, current, !!current, others, functionBreakpoint(lineTextOf(model), line)?.function ?? null);
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
    /** A breakpoint dragged to another line of the margin moves there with its settings. */
    function moveBreakpoint(model, from, to) {
        const meta = breakpointsOf(model).find((b) => b.line === from);
        if (!meta || from === to || model.getLineDecorations(to).some(isBp))
            return;
        removeBreakpoint(model, from);
        const { line: _, ...settings } = meta;
        placeBreakpoint(model, to, settings);
        persist();
        sendBreakpoints(model);
    }
    /** Clicks in the margin of an editor: a click toggles, a right click edits, a drag moves a breakpoint. */
    function glyphMouse(ed) {
        const testMark = (e) => e.target.detail?.glyphMarginLane === monaco.editor.GlyphMarginLane.Left
            && ed.getModel()?.getLineDecorations(e.target.position.lineNumber).some((d) => d.options.glyphMarginClassName?.startsWith('test-run'));
        const glyphLine = (e) => (e.target.type === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN && e.target.position
            // the right lane holds the override marks (hierarchy.js), the left one a test's run mark (tests.js)
            && e.target.detail?.glyphMarginLane !== monaco.editor.GlyphMarginLane.Right && !testMark(e) ? e.target.position.lineNumber : null);
        ed.onMouseDown((e) => {
            const line = glyphLine(e), model = ed.getModel();
            if (line === null || !model)
                return;
            if (e.event.rightButton) {
                void editBreakpointAt(model, line);
                return;
            }
            if (!model.getLineDecorations(line).some(isBp)) {
                toggleBreakpoint(model, line);
                return;
            }
            // on a breakpoint: where the button comes up decides (Monaco sends no mouse-up after a press in the margin)
            window.addEventListener('mouseup', (up) => {
                const to = ed.getTargetAtClientPoint(up.clientX, up.clientY)?.position?.lineNumber ?? line;
                if (ed.getModel() !== model)
                    return;
                if (to === line)
                    toggleBreakpoint(model, line);
                else
                    moveBreakpoint(model, line, to);
            }, { once: true, capture: true });
        });
    }
    glyphMouse(editor);
    // the line of the editor that has the focus (the main one, a side of a split, a cell of a notebook)
    const atLine = (fn) => (ed) => { const model = ed?.getModel(), pos = ed?.getPosition(); if (model && pos) fn(model, pos.lineNumber); };
    defineCommand({ id: 'pymagic.run.toggleBreakpoint', label: 'Toggle a breakpoint', group: 'Run', keys: ['F9'], run: atLine(toggleBreakpoint) });
    defineCommand({ id: 'pymagic.run.editBreakpoint', label: 'Breakpoint with a condition', group: 'Run', keys: ['Shift+F9'], run: atLine((m, l) => void editBreakpointAt(m, l)) });
    /** Called for every model the app creates, so saved breakpoints come back. */
    function onModelCreated(rel, model) {
        for (const saved of stored()[rel] ?? []) {
            const bp = typeof saved === 'number' ? { line: saved } : saved;
            if (bp.line <= model.getLineCount())
                placeBreakpoint(model, bp.line, bp);
        }
        model.onDidChangeDecorations(() => { });
        model.onDidChangeContent(() => { persist(); sendMoved(model); });
    }
    // lines added or removed above a breakpoint while a run goes on: the run is told its new line (the file saved and reloaded, as a dev
    // server's hot reload does, stops there and not on the line that was there before)
    const movedTimers = new WeakMap();
    function sendMoved(model) {
        if (!run || !fileOf(model))
            return;
        clearTimeout(movedTimers.get(model));
        movedTimers.set(model, setTimeout(() => {
            const lines = breakpointsOf(model).map((b) => b.line).join(',');
            if (lines !== sentLines.get(model))
                sendBreakpoints(model);
        }, 250));
    }
    let run = null;
    // the members of compounds that are running or have run (name -> entry), each with a run of its own beside the Run panel's one
    const sides = new Map();
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
    // where an interpreter of another machine or a container has the project (/workspace); its tracebacks name files by that
    let remoteRoot = null;
    /** The places of a traceback in a piece of output become links that open the file of the project at that line. */
    function linked(span) {
        const text = span.textContent;
        if (!text.includes('.py')) return [span];
        const pieces = linkPieces(text, deps.root, remoteRoot);
        if (!pieces.some((p) => p.rel)) return [span];
        return pieces.map((p) => {
            const part = span.cloneNode(false);
            part.textContent = p.text;
            if (p.rel) {
                part.classList.add('tb-link');
                part.dataset.rel = p.rel;
                part.dataset.line = String(p.line);
                part.title = `Open ${p.rel}, line ${p.line}`;
            }
            return part;
        });
    }
    output.addEventListener('click', (e) => {
        const link = e.target instanceof Element ? e.target.closest('.tb-link') : null;
        if (!link || getSelection()?.toString()) return;
        const line = Number(link.dataset.line);
        void deps.openFile(link.dataset.rel, new monaco.Range(line, 1, line, 1));
    });
    function append(text, cls = '') {
        const stick = output.scrollTop + output.clientHeight >= output.scrollHeight - 20;
        // a program that prints colours (rich, colorama, pytest --color=yes) shows them
        output.append(...colouredSpans(colours, text.split('\r\n').join('\n'), cls).flatMap(linked));
        if (stick)
            output.scrollTop = output.scrollHeight;
    }
    let serving = false, connected = false;   // a debug server is listening / a program is connected to it
    let cellHook = null, cellMode = false;    // Debug Cell: who waits for the address of the debug server / the session debugs a notebook cell ('cell') or the console ('console')
    let hookFor = 'cell';                     // what the debug server that is starting is for: a notebook 'cell' or the Python 'console' (Attach Debugger)
    function setButtons() {
        const active = run !== null;
        $('b-run').disabled = active;
        $('b-profile').disabled = active;
        $('b-debug').disabled = active && !paused;
        $('b-debug').querySelector('.lbl').textContent = paused ? 'Resume' : 'Debug';
        $('b-debug').querySelector('use').setAttribute('href', paused ? '#i-run' : '#i-debug');
        $('b-stop').disabled = !active && ![...sides.values()].some((e) => e.live);
        $('b-pause').disabled = !active || !debugging || paused || (serving && !connected);
        for (const id of ['b-over', 'b-into', 'b-out'])
            $(id).disabled = !paused;
        $('runstate').textContent = !active ? '' : serving && !connected ? 'waiting for a program…' : paused ? 'paused' : debugging ? 'debugging…' : profiling ? 'profiling…' : 'running…';
    }
    function relOfAbsolute(file) {
        const n = norm(file);
        // a scratch file is kept outside the project and opened as .scratches/name
        if (scratchNorm && n.startsWith(scratchNorm + '/') && !n.slice(scratchNorm.length + 1).includes('/')) return '.scratches/' + file.replace(/\\/g, '/').slice(scratchNorm.length + 1);
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
        const shows = [editor, focused, deps.editorOfModel?.(model)].filter((e, i, all) => e && all.indexOf(e) === i && e.getModel() === model);
        const byLine = new Map(lines.map(({ line, names: onLine }) => [line, onLine.filter((n) => Object.prototype.hasOwnProperty.call(values, n))]));
        inline = { model, byLine, collections: shows.map((e) => e.createDecorationsCollection(decorations)) };
    }
    async function showFrame(index) {
        selectedFrame = index;
        const f = frames[index];
        if (!f)
            return;
        document.querySelectorAll('#frames div.frame').forEach((d, i) => d.classList.toggle('sel', i === index));
        clearCurrentLine();
        const rel = relOfAbsolute(f.file);
        // a stop in the file that stands for a notebook cell is shown in the cell
        const cell = rel ? deps.cellAtFile?.(rel, f.line) : null;
        if (cell) {
            const ids = cell.model.deltaDecorations([], [{
                    range: new monaco.Range(f.line, 1, f.line, 1),
                    options: { isWholeLine: true, className: 'debug-line', glyphMarginClassName: 'debug-arrow' },
                }]);
            currentLine = { model: cell.model, ids };
            void showInline(f, cell.model);
        }
        else if (rel) {
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
            attachChildren(head, li, s.ref, i === 0 || s.name === 'Locals', s.name === 'Return values' ? undefined : s.ref, { expr: null, type: s.name });
        }
    }
    /** Makes a node expandable: children are fetched from the debugger the first time it opens. */
    function attachChildren(head, li, ref, openNow, scopeRef, parent) {
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
            for (const [index, v] of items.entries())
                child.appendChild(variableNode(v, scopeRef, parent, { ref, index }));
            if (!items.length)
                child.innerHTML = '<li><div class="type">(empty)</div></li>';
            li.appendChild(child);
        };
        caret.textContent = '▸';
        caret.onclick = toggle;
        if (openNow)
            void toggle();
    }
    function variableNode(v, scopeRef, parent, source) {
        const li = document.createElement('li');
        const head = document.createElement('div');
        head.innerHTML = '<span class="caret"></span><span class="name"></span> = <span class="val"></span> <span class="type"></span>';
        head.querySelector('.name').textContent = v.name;
        head.querySelector('.val').textContent = v.value;
        head.querySelector('.type').textContent = v.type;
        head.tabIndex = 0;
        li.appendChild(head);
        // the expression that gives this value: a name of the frame, or a member of the value above it
        const expr = parent?.type === 'Return values' ? null : parent?.expr === null ? v.name : childExpression(parent?.expr, parent?.type, v.name);
        if (v.ref > 0)
            attachChildren(head, li, v.ref, false, undefined, { expr, type: v.type });
        const val = head.querySelector('.val');
        // a value longer than its row shows: View opens the whole of it (as PyCharm's "View")
        if (!v.lazy && (v.value ?? '').length > 80) {
            const view = document.createElement('a');
            view.className = 'view-link';
            view.textContent = 'View';
            view.title = 'The whole value';
            view.onclick = (e) => { e.stopPropagation(); void viewValue(expr, v.value); };
            head.appendChild(view);
        }
        if (v.lazy) {
            // loaded on demand: the value is asked for when clicked
            val.textContent = '… show';
            val.classList.add('lazy');
            val.title = 'Click to load the value';
            val.onclick = async () => {
                const r = run && paused ? await run.ask({ type: 'reprOf', ref: source.ref, index: source.index }) : { error: 'not paused' };
                val.textContent = r.error ?? r.value;
                val.classList.remove('lazy');
                val.onclick = null;
                v.value = r.value ?? v.value;
            };
        }
        // a variable of the frame, or a member of a value (an attribute, a dict's item, a list's item): tuples and sets cannot be changed
        const editRef = scopeRef !== undefined ? scopeRef
            : source && parent?.type !== 'Return values' && !['tuple', 'set', 'frozenset', 'str', 'bytes'].includes(parent?.type) ? source.ref : undefined;
        const member = scopeRef === undefined;
        if (editRef !== undefined) {
            val.title = 'Double-click or F2 to change the value; right-click for more';
            val.ondblclick = () => editVariable(val, editRef, v, member);
        }
        const isName = parent?.expr === null && expr !== null;
        head.onkeydown = (e) => {
            if (e.key === 'F2' && editRef !== undefined) {
                e.preventDefault();
                editVariable(val, editRef, v, member);
            }
            else if (e.key === 'F4' && !e.shiftKey && isName) {
                e.preventDefault();
                void jumpToDeclaration(v.name);
            }
            else if (e.key === 'F4' && e.shiftKey && expr) {
                e.preventDefault();
                void jumpToTypeSource(expr);
            }
        };
        head.oncontextmenu = (e) => {
            e.preventDefault();
            openMenu({ x: e.clientX, y: e.clientY }, [
                ...valueItems(expr, v.value, editRef !== undefined ? () => editVariable(val, editRef, v, member) : null),
                '-',
                { label: 'Jump to source', key: 'F4', run: () => void jumpToDeclaration(v.name), disabled: !isName },
                { label: 'Jump to type source', key: 'Shift+F4', run: () => void jumpToTypeSource(expr), disabled: !expr },
            ]);
        };
        return li;
    }
    /** Jump to Source (F4): the line of the selected frame where a variable is first bound. */
    async function jumpToDeclaration(name) {
        const f = frames[selectedFrame], rel = f && relOfAbsolute(f.file);
        const model = rel ? await deps.modelFor(rel) : null;
        const line = model ? declarationLine(lineTextOf(model), f.first ?? 1, f.line, name) : null;
        if (!line) {
            toast(`Where ${name} is set is not in this frame (a global, or a builtin).`);
            return;
        }
        await deps.openFile(rel, new monaco.Range(line, 1, line, 1));
    }
    /** Jump to Type Source (Shift+F4): the class of a value, when it is written in the project. */
    async function jumpToTypeSource(expr) {
        if (!run || !paused || !expr)
            return;
        const r = await run.ask({ type: 'typeSource', frameId: selectedFrame, expression: expr });
        if (r.error) {
            toast(r.error);
            return;
        }
        const rel = relOfAbsolute(r.file);
        if (rel)
            await deps.openFile(rel, new monaco.Range(r.line, 1, r.line, 1));
        else
            toast(`${r.name} is defined in ${r.file}, line ${r.line} (outside the project)`);
    }
    /** Set Value from the values at the end of a line: assigns an expression to a name of the frame. */
    async function setName(name) {
        if (!run || !paused)
            return;
        const current = await fullValue(name, '');
        const text = await deps.askText(`Set ${name}`, current.length <= 200 ? current : '', 'A Python expression; its value is given to the name in this frame');
        if (text === null || text === undefined || !text.trim())
            return;
        const r = await run.ask({ type: 'setName', frameId: selectedFrame, name, expression: text });
        if (r.error)
            toast(r.error);
        await showFrame(selectedFrame);
    }
    /** What can be done with a value shown by the debugger (a variable, a member, a value at the end of a line). */
    function valueItems(expr, shown, setValue = null) {
        return [
            { label: 'View value…', run: () => void viewValue(expr, shown) },
            { label: 'View as table…', run: () => void viewTable(expr), disabled: !expr },
            '-',
            { label: 'Copy value', run: () => void fullValue(expr, shown).then((t) => navigator.clipboard.writeText(t)).then(() => toast('Value copied'), () => toast('Could not copy')) },
            { label: 'Compare with clipboard', run: () => void compareValue(expr, shown) },
            { label: 'Add to watches', run: () => addWatch(expr), disabled: !expr },
            { label: 'Break when value changes', run: () => void addDataBreak(expr), disabled: !expr || !paused },
            ...(setValue ? ['-', { label: 'Set value…', key: 'F2', run: setValue }] : []),
        ];
    }
    /** The whole value of an expression (the panes cut it at 200 characters), or what is shown when it has no expression. */
    async function fullValue(expr, shown) {
        return (await valueDetail(expr, shown)).result;
    }
    /** The whole value, with the text of a string and the JSON of a dict or list when the helper can give them. */
    async function valueDetail(expr, shown) {
        if (!expr || !run || !paused)
            return { result: shown };
        const r = await run.ask({ type: 'evaluate', frameId: selectedFrame, expression: expr, evalOnly: true, limit: 200000, detail: true });
        return r.error ? { result: shown } : r;
    }
    /** JSON as a tree of <details>, objects and arrays folded below the first two levels. */
    function jsonTree(value, key = null, depth = 0) {
        const label = (k) => (k === null ? '' : `${k}: `);
        if (value === null || typeof value !== 'object') {
            const leaf = document.createElement('div');
            leaf.className = 'jt-leaf';
            leaf.textContent = label(key) + JSON.stringify(value);
            return leaf;
        }
        const entries = Array.isArray(value) ? value.map((v, i) => [String(i), v]) : Object.entries(value);
        const node = document.createElement('details');
        node.open = depth < 2;
        const summary = document.createElement('summary');
        summary.textContent = label(key) + (Array.isArray(value) ? `[${entries.length}]` : `{${entries.length}}`);
        node.appendChild(summary);
        for (const [k, v] of entries.slice(0, 2000))
            node.appendChild(jsonTree(v, k, depth + 1));
        return node;
    }
    async function compareValue(expr, shown) {
        let clip = '';
        try {
            clip = await navigator.clipboard.readText();
        }
        catch {
            toast('The clipboard cannot be read here.');
            return;
        }
        deps.showDiff(`${expr ?? 'The value'} (right) and the clipboard (left)`, clip, await fullValue(expr, shown), null);
    }
    /** A dialog over the page with a title, some content and a Close button; Escape or a click outside closes it. */
    function dialog(title, content, extraButtons = []) {
        const overlay = document.createElement('div');
        overlay.id = 'sigdialog';
        const box = document.createElement('div');
        box.className = 'box value-box';
        const h = document.createElement('h2');
        h.textContent = title;
        const buttons = document.createElement('div');
        buttons.className = 'buttons';
        const spacer = document.createElement('span');
        spacer.style.flex = '1';
        const close = document.createElement('button');
        close.textContent = 'Close';
        close.className = 'primary';
        buttons.append(...extraButtons, spacer, close);
        box.append(h, content, buttons);
        overlay.appendChild(box);
        const done = () => { overlay.remove(); editor.focus(); };
        close.onclick = done;
        overlay.onmousedown = (e) => { if (e.target === overlay)
            done(); };
        overlay.onkeydown = (e) => { if (e.key === 'Escape') {
            e.preventDefault();
            done();
        } };
        document.body.appendChild(overlay);
        close.focus();
    }
    /** View Value: the whole value in a dialog, with a Copy button. */
    async function viewValue(expr, shown) {
        const detail = await valueDetail(expr, shown);
        const text = detail.text ?? detail.result;
        const pre = document.createElement('pre');
        pre.className = 'value-text';
        pre.textContent = text;
        const copy = document.createElement('button');
        copy.textContent = 'Copy';
        copy.onclick = () => void navigator.clipboard.writeText(text).then(() => toast('Value copied'), () => toast('Could not copy'));
        // a dict, a list or a string that holds JSON can also be read as a tree
        let parsed;
        try {
            parsed = JSON.parse(detail.json ?? detail.text ?? '');
        }
        catch { /* not JSON */ }
        if (parsed === undefined || parsed === null || typeof parsed !== 'object') {
            dialog(expr ?? 'Value', pre, [copy]);
            return;
        }
        const holder = document.createElement('div');
        const tree = document.createElement('div');
        tree.className = 'value-text json-tree';
        tree.appendChild(jsonTree(parsed));
        holder.append(tree, pre);
        pre.hidden = true;
        const toggle = document.createElement('button');
        toggle.textContent = 'Text';
        toggle.onclick = () => { pre.hidden = !pre.hidden; tree.hidden = !tree.hidden; toggle.textContent = pre.hidden ? 'Text' : 'Tree'; };
        dialog(expr ?? 'Value', holder, [copy, toggle]);
    }
    /** Data View: a list, dict, array, DataFrame or Series as a table (the first 1000 rows). */
    async function viewTable(expr) {
        if (!expr || !run || !paused)
            return;
        const t = await run.ask({ type: 'table', frameId: selectedFrame, expression: expr });
        if (t.error) {
            toast(t.error);
            return;
        }
        const wrap = document.createElement('div');
        wrap.className = 'data-view';
        const table = document.createElement('table');
        const headRow = table.createTHead().insertRow();
        for (const c of ['', ...t.columns]) {
            const th = document.createElement('th');
            th.textContent = c;
            headRow.appendChild(th);
        }
        const body = table.createTBody();
        t.rows.forEach((row, i) => {
            const tr = body.insertRow();
            const th = document.createElement('th');
            th.textContent = t.index[i] ?? String(i);
            tr.appendChild(th);
            for (const cell of row)
                tr.insertCell().textContent = cell;
        });
        wrap.appendChild(table);
        const note = document.createElement('div');
        note.className = 'dim';
        note.textContent = `${t.kind}: ${t.total} rows${t.total > t.rows.length ? `, the first ${t.rows.length} shown` : ''}, ${t.columns.length} columns`;
        wrap.appendChild(note);
        dialog(`${expr} — Data View`, wrap);
    }
    /** Replaces the shown value by an input; Enter assigns the expression typed there to the variable, or to the member
     * of the value above it (`member`), which is then shown in place without folding the tree up again. */
    function editVariable(valueEl, ref, v, member = false) {
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
            let r = null;
            if (apply && run && paused) {
                r = await run.ask({ type: 'setVariable', ref, frameId: selectedFrame, name: v.name, expression: input.value });
                if (r.error)
                    logLine(`${v.name} = ${input.value}`, r.error);
            }
            if (member) {
                if (r && !r.error) {
                    v.value = r.value;
                    valueEl.textContent = r.value;
                }
                input.replaceWith(valueEl);
                if (r && !r.error && inline)
                    void showInline(frames[selectedFrame], inline.model);
                return;
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
    // kept per project, like PyCharm's (the first version kept one list for every project)
    const WATCH_KEY = 'pymagic.watches:' + rootNorm;
    let watches = [];
    try {
        watches = readWatches(JSON.parse(localStorage.getItem(WATCH_KEY) ?? localStorage.getItem('pymagic.watches') ?? '[]'));
    }
    catch { /* storage unavailable */ }
    const saveWatches = () => { try {
        localStorage.setItem(WATCH_KEY, JSON.stringify(watches));
    }
    catch { /* storage unavailable */ } };
    const changeWatches = (next) => { watches = next; saveWatches(); void refreshWatches(); };
    function addWatch(expression) {
        if (expression?.trim())
            changeWatches([...watches, { expression: expression.trim(), paused: false }]);
    }
    async function editWatch(index) {
        const text = await deps.askText('Watch', watches[index].expression, 'The expression to watch');
        if (text?.trim())
            changeWatches(watches.map((w, i) => (i === index ? { ...w, expression: text.trim() } : w)));
    }
    // ------------------------------------------------------------ data breakpoints (Break when value changes)
    let dataBreaks = [];   // [{ key, expression }] of this run: the helper forgets them when the program ends
    async function addDataBreak(expression) {
        if (!run || !paused || !expression)
            return;
        const r = await run.ask({ type: 'setDataBreak', frameId: selectedFrame, expression });
        if (r.error) {
            toast(r.error);
            return;
        }
        dataBreaks = [...dataBreaks, { key: r.key, expression }];
        toast(`The program stops when ${expression} changes (now ${r.value})`);
        void refreshWatches();
    }
    function removeDataBreak(key) {
        run?.send({ type: 'clearDataBreak', key });
        dataBreaks = dataBreaks.filter((d) => d.key !== key);
        void refreshWatches();
    }
    function dataBreakRows(holder) {
        for (const d of dataBreaks) {
            const row = document.createElement('div');
            row.className = 'w databreak';
            row.title = 'A data breakpoint: the program stops at the line after the one that changes this value';
            const name = document.createElement('span');
            name.className = 'wx';
            name.textContent = '◆ ' + d.expression;
            const note = document.createElement('span');
            note.className = 'wv wn';
            note.textContent = 'stops when it changes';
            const del = document.createElement('span');
            del.className = 'wd';
            del.textContent = '✕';
            del.title = 'Remove the data breakpoint';
            del.onclick = () => removeDataBreak(d.key);
            row.append(name, note, del);
            holder.appendChild(row);
        }
    }
    const openWatches = new Set();   // the expressions of the watches opened into their members, kept open across stops
    /** Evaluates every watch in the selected frame (expressions only: a watch never assigns); paused ones are not evaluated. */
    async function refreshWatches() {
        const holder = $('watches');
        holder.textContent = '';
        dataBreakRows(holder);
        for (const [index, watch] of watches.entries()) {
            const { expression } = watch;
            const row = document.createElement('div');
            row.className = 'w' + (watch.paused ? ' paused' : '');
            const name = document.createElement('span');
            name.className = 'wx';
            name.textContent = expression;
            const value = document.createElement('span');
            value.className = 'wv';
            const del = document.createElement('span');
            del.className = 'wd';
            del.textContent = '✕';
            del.title = 'Remove the watch';
            del.onclick = () => changeWatches(watches.filter((_, i) => i !== index));
            row.append(name, value, del);
            row.title = 'Right-click to edit, duplicate, move, pause or remove';
            row.ondblclick = () => void editWatch(index);
            row.oncontextmenu = (e) => {
                e.preventDefault();
                openMenu({ x: e.clientX, y: e.clientY }, [
                    { label: 'Edit…', run: () => void editWatch(index) },
                    { label: 'Duplicate', run: () => changeWatches([...watches.slice(0, index + 1), { ...watch }, ...watches.slice(index + 1)]) },
                    { label: 'Move up', run: () => changeWatches(moveItem(watches, index, -1)), disabled: index === 0 },
                    { label: 'Move down', run: () => changeWatches(moveItem(watches, index, 1)), disabled: index === watches.length - 1 },
                    { label: watch.paused ? 'Resume' : 'Pause', run: () => changeWatches(watches.map((w, i) => (i === index ? { ...w, paused: !w.paused } : w))) },
                    '-',
                    { label: 'View value…', run: () => void viewValue(expression, value.textContent), disabled: !paused },
                    { label: 'View as table…', run: () => void viewTable(expression), disabled: !paused },
                    '-',
                    { label: 'Remove', run: () => changeWatches(watches.filter((_, i) => i !== index)) },
                ]);
            };
            holder.appendChild(row);
            if (watch.paused) {
                value.className = 'wv wn';
                value.textContent = 'paused';
                continue;
            }
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
                if (r.ref > 0)
                    expandableWatch(row, expression, r);
            }
        }
    }
    /** A watch whose value has members opens into them, as a variable does; it stays open at the next stops. */
    function expandableWatch(row, expression, r) {
        const caret = document.createElement('span');
        caret.className = 'caret';
        row.prepend(caret);
        const li = document.createElement('div');
        li.className = 'wtree';
        row.after(li);
        attachChildren(row, li, r.ref, openWatches.has(expression), undefined, { expr: expression, type: r.valueType });
        caret.addEventListener('click', () => {
            if (openWatches.has(expression))
                openWatches.delete(expression);
            else
                openWatches.add(expression);
        });
    }
    const watchAdd = $('watch-add');
    watchAdd.onkeydown = (e) => {
        if (e.key !== 'Enter' || !watchAdd.value.trim())
            return;
        addWatch(watchAdd.value);
        watchAdd.value = '';
    };
    // text dragged from the editor (or anywhere) onto the watches becomes a watch
    const watchesCol = $('watches-col');
    watchesCol.ondragover = (e) => { if (e.dataTransfer?.types.includes('text/plain')) {
        e.preventDefault();
        watchesCol.classList.add('drop');
    } };
    watchesCol.ondragleave = () => watchesCol.classList.remove('drop');
    watchesCol.ondrop = (e) => {
        e.preventDefault();
        watchesCol.classList.remove('drop');
        addWatch(e.dataTransfer?.getData('text/plain').split('\n')[0] ?? '');
    };
    void refreshWatches();
    // ------------------------------------------------------------ frames and messages
    let stoppedThread = null;
    function renderFrames() {
        const holder = $('frames');
        holder.textContent = '';
        // the other threads' stacks follow the stopped thread's, each under its name
        const headings = threadHeadings(frames, stoppedThread);
        frames.forEach((f, i) => {
            if (headings.has(i)) {
                const head = document.createElement('div');
                head.className = 'thread';
                head.textContent = headings.get(i);
                holder.appendChild(head);
            }
            const row = document.createElement('div');
            row.className = 'frame';
            const rel = relOfAbsolute(f.file) ?? f.file.split(/[\\/]/).pop() ?? f.file;
            row.innerHTML = '<span class="fn"></span> <span class="loc"></span>';
            row.querySelector('.fn').textContent = f.name;
            row.querySelector('.loc').textContent = `${rel}:${f.line}`;
            row.onclick = () => void showFrame(i);
            if (f.thread)
                row.title = `Thread ${f.thread}: its frame while the program is paused (stepping goes on in the stopped thread)`;
            holder.appendChild(row);
        });
    }
    let testsRun = false;   // this run is Debug of tests: its results belong to the Tests panel too
    const TEST_MESSAGES = ['collected', 'testStarted', 'testResult', 'collectError'];
    function handle(m) {
        if (testsRun && (TEST_MESSAGES.includes(m.type) || m.type === 'exited' || m.type === 'error'))
            window.dispatchEvent(new CustomEvent('pymagic:testMessage', { detail: m }));
        switch (m.type) {
            case 'listening': {
                if (cellHook) {
                    // Debug Cell: the notebook is told where to connect; there is no snippet to show
                    const hook = cellHook, mine = run;
                    cellHook = null;
                    cellMode = hookFor;
                    append(hookFor === 'console' ? 'The debugger is attached to the Python console: the breakpoints of the project stop the code it runs. Detach Debugger (or Stop) ends it; the console goes on.\n'
                        : hookFor === 'compound' ? `Debugging ${compoundDebugged}: every member runs under the debugger, each with its own output in the Services window. The breakpoints stop each of them; one member's stop is shown at a time, by its name, and the next comes up when it goes on. Stop ends them all.\n`
                        : 'Debugging a notebook cell: the cell runs in the notebook\'s kernel, under the debugger.\n', 'info');
                    hook({ host: m.host, port: m.port, token: m.token, end: () => { if (run && run === mine) run.send({ type: 'stop' }); } });
                    break;
                }
                const mapped = m.mapped ? `${m.mapped} path mapping${m.mapped === 1 ? '' : 's'} between this computer and the program's machine` : 'no path mappings: the files are taken to be the same on both sides';
                append(`Debug server listening on ${m.host}:${m.port} (${mapped}).\nPut this in the program, started on the other machine:\n\n${m.snippet}\n\nThe program stops at the breakpoints set here, once it has run that line. Stop ends the listening; a program that is connected goes on running.\n`, 'info');
                showServerSnippet(m);
                break;
            }
            case 'connected':
                connected = true;
                append(serverProgramNote(m).text, 'info');
                setButtons();
                break;
            case 'disconnected':
                // others still connected: the engine says what their stops are (a program that goes away is "running")
                if (serverProgramNote(m).still) { append(serverProgramNote(m).text, 'info'); break; }
                connected = false;
                paused = false;
                frames = [];
                clearCurrentLine();
                $('frames').textContent = '';
                $('vars').textContent = '';
                append('The program disconnected; the debug server is waiting for the next one.\n', 'info');
                setButtons();
                break;
            case 'started':
                remoteRoot = m.remoteRoot ?? null;
                if (m.listening)
                    break;
                append(m.attached ? `Attached to process ${m.pid}; the debugger starts with the program's next line of Python (its output is not shown here)\n`
                    : `${debugging ? 'Debugging' : 'Running'} (pid ${m.pid})\n`, 'info');
                break;
            case 'output':
                append(m.text, m.stream === 'stderr' ? 'err' : '');
                if (openBrowser) {
                    // Django, uvicorn and Flask say where they listen
                    const at = /(?:development server at|running on|Uvicorn running on)\s+(https?:\/\/[^\s/]+\/?)/i.exec(m.text);
                    if (at) {
                        openBrowser = false;
                        window.open(at[1].replace('0.0.0.0', '127.0.0.1'), '_blank');
                    }
                }
                break;
            case 'error':
                append(m.message + '\n', 'err');
                finish();
                break;
            case 'stopped':
                paused = true;
                clearTimeout(pauseTimer);
                frames = framesOfStop(m);
                stoppedThread = m.thread ?? null;
                if (m.reason === 'pause')
                    append('Paused\n', 'info');
                if (m.reason === 'data')
                    append(`Stopped: ${m.message}\n`, 'info');
                if (m.reason === 'exception')
                    append(`Paused on exception: ${m.message}\n`, 'err');
                if (m.program)
                    append(`In program ${m.program}\n`, 'info');
                if (m.thread && m.thread !== 'MainThread')
                    append(`Paused in thread ${m.thread}${m.suspended === 'thread' ? ' (the other threads go on)' : ''}\n`, 'info');
                renderFrames();
                showTab('debug');
                setButtons();
                void showFrame(0);
                break;
            case 'dataBreakEnded':
                dataBreaks = dataBreaks.filter((d) => d.key !== m.key);
                append(`The data breakpoint on ${m.expression} ended with its function's call\n`, 'info');
                void refreshWatches();
                break;
            case 'running':
                paused = false;
                clearCurrentLine();
                $('vars').textContent = '';
                setButtons();
                void refreshWatches();
                break;
            case 'exited':
                append(cellMode === 'console' ? '\nThe debugger is detached from the Python console.\n' : cellMode === 'compound' ? `\nThe debugger of ${compoundDebugged} is stopped.\n` : cellMode ? '\nThe cell is done; the debugger is detached.\n' : m.detached ? (serving ? '\nDebug server stopped; a program that was connected goes on running.\n' : '\nDetached; the program goes on running.\n') : `\nProcess finished with exit code ${m.code}\n`, 'info');
                clearInterval(logTimer);
                openBrowser = false;
                if (lastLaunch?.extra?.member) lastLaunch.extra.member.code = m.code ?? null;
                if (afterExit) {
                    const next = afterExit;
                    afterExit = null;
                    window.setTimeout(() => next(m.detached ? 0 : m.code ?? null), 0);
                }
                if (profiling) {
                    profiling = false;
                    window.dispatchEvent(new CustomEvent('pymagic:profile'));
                }
                finish();
                break;
        }
    }
    function finish() {
        // a Before launch task that ended without exiting (an error, the connection lost) starts nothing after it
        if (afterExit) { const next = afterExit; afterExit = null; window.setTimeout(() => next(null), 0); }
        run?.close();
        run = null;
        serving = connected = false;
        // the debug server of a compound is gone: its members that still run do so without the debugger
        if (cellMode === 'compound') for (const e of sides.values()) e.debugged = false;
        cellMode = false;
        testsRun = false;
        dataBreaks = [];
        paused = false;
        frames = [];
        clearCurrentLine();
        $('frames').textContent = '';
        $('vars').textContent = '';
        setButtons();
    }
    // ------------------------------------------------------------ commands
    let profiling = false;
    // afterExit: called with the exit code when the program of a Before launch task ends (null when it did not end by itself)
    let afterExit = null, stoppedByUser = false;
    // what a configuration adds while it runs: the browser opened at the server's address, log files followed in the console
    let openBrowser = false, logTimer = 0;
    const logSizes = new Map();
    function followLogs(files) {
        clearInterval(logTimer);
        logSizes.clear();
        if (!files.length) return;
        const poll = async () => {
            for (const f of files) {
                const text = await fetch('/api/file?path=' + encodeURIComponent(f)).then((r) => (r.ok ? r.text() : null)).catch(() => null);
                if (text === null) continue;
                const seen = logSizes.get(f);
                if (seen !== undefined && text.length > seen) append(text.slice(seen).replace(/^/gm, `[${f}] `).replace(/\[[^\]]*\] $/, ''), 'info');
                logSizes.set(f, text.length);
            }
        };
        void poll();
        logTimer = window.setInterval(() => void poll(), 1000);
    }
    async function start(debug, profile = false) {
        if (run)
            return;
        const chosen = deps.runConfig?.();
        if (chosen?.kind === 'compound') {
            await startCompound(chosen, debug, profile);
            return;
        }
        if (chosen) {
            const s = startOf(chosen);
            // an HTTP request of an .http file: sent by the HTTP client, its response in the HTTP tab
            if (s.http) {
                window.dispatchEvent(new CustomEvent('pymagic:runHttp', { detail: s.http }));
                return;
            }
            if (s.debugServer) {
                await startDebugServer(s.debugServer, chosen.openPanel);
                return;
            }
            // JavaScript Debug: Debug opens the page in Edge or Chrome under the debugger; Run only opens it in the browser
            if (s.browser && !debug) {
                window.open(s.browser.url, '_blank');
                return;
            }
            // Django tests (and the like) go to the Tests panel and its tree
            if (s.tests) {
                window.dispatchEvent(new CustomEvent('pymagic:runTests', { detail: { ...s.tests, debug } }));
                return;
            }
            // Before launch: other configurations, manage.py commands and the browser first, in order; this one starts when they have finished
            // well (a task that fails or is stopped starts nothing), the output of all of them kept in the Output one after the other
            const { steps, skipped } = beforeSteps(chosen, (name) => deps.runConfigByName?.(name) ?? null);
            let kept = false;
            if (steps.length || skipped.length) {
                output.textContent = '';
                kept = true;
                if (chosen.openPanel !== false) showTab('output');
            }
            for (const k of skipped) append(`Before launch: ${k.name} is left out, ${k.why}.\n`, 'err');
            for (const step of steps) {
                if (step.browser) {
                    append(`Before launch: the browser at ${step.browser}\n`, 'info');
                    window.open(step.browser, '_blank');
                    continue;
                }
                append(`Before launch: ${step.label}\n`, 'info');
                const task = step.config ? startOf(step.config) : { ...s, module: undefined, file: step.file, args: step.manage, allCode: undefined };
                const finished = new Promise((resolve) => { afterExit = resolve; });
                if (!(await launch(task.file ?? null, false, false, task.args, { ...task, keepOutput: true, openPanel: chosen.openPanel, label: `${step.label} (before ${chosen.name})` }))) {
                    afterExit = null;
                    return;
                }
                const code = await finished;
                if (stoppedByUser) { stoppedByUser = false; append(`Before launch was stopped; ${chosen.name} is not started.\n`, 'info'); return; }
                if (code !== 0) {
                    append(`Before launch: ${step.label} ${code === null ? 'did not finish' : `ended with exit code ${code}`}; ${chosen.name} is not started.\n`, 'err');
                    return;
                }
            }
            await launch(s.file ?? null, debug, profile, s.args, { ...s, keepOutput: kept, openPanel: chosen.openPanel, openBrowser: chosen.kind === 'django' && chosen.openBrowser, logFiles: chosen.logFiles });
            return;
        }
        const model = editor.getModel();
        const rel = model && deps.relOf(model);
        if (!rel)
            return;
        await launch(rel, debug, profile, []);
    }
    // runs a file of the project with arguments (the Django tab runs manage.py this way); false when a run is going
    /** Attach to Process: the Python programs running on this computer, and the debugger put into the one chosen
     * (Python 3.14's sys.remote_exec: the program and the selected interpreter must both be 3.14 or later). */
    async function attachToProcess() {
        if (run)
            return;
        let list = [];
        try {
            list = await (await fetch('/api/processes')).json();
        }
        catch { /* the engine did not answer */ }
        const holder = document.createElement('div');
        holder.className = 'attach-list';
        const note = document.createElement('div');
        note.className = 'dim';
        note.textContent = list.length ? 'A Python program and the selected interpreter must be Python 3.14 or later; a Node.js program has its inspector opened (or the one it opened with --inspect used). Stop detaches and leaves the program running.'
            : 'No Python or Node.js program is running on this computer.';
        holder.appendChild(note);
        let close = () => { };
        for (const p of list) {
            const row = document.createElement('button');
            row.className = 'attach-row';
            row.textContent = `${p.pid}  ${p.name}${p.title ? ' — ' + p.title : ''}${p.started ? '  (started ' + p.started + ')' : ''}`;
            row.title = p.path ?? '';
            // a Node.js program: its inspector, through the helper (Attach to Node.js)
            row.onclick = () => { close(); void (p.kind === 'node' ? launch(null, true, false, [], { nodeAttach: { host: '127.0.0.1', port: p.inspectPort ?? 9229, pid: p.pid } }) : startAttach(p.pid)); };
            holder.appendChild(row);
        }
        dialog('Attach to Process', holder);
        const overlay = holder.closest('#sigdialog');
        close = () => overlay?.remove();
    }
    /** The snippet to put in the program, in a dialog with a Copy button (the same text is in the Output). */
    function showServerSnippet(m) {
        const holder = document.createElement('div');
        holder.className = 'debugserver-info';
        const note = document.createElement('div');
        note.className = 'dim';
        note.textContent = `Listening on ${m.host}:${m.port}. Put this in the program that runs elsewhere; copy pm_debug.py from ${m.helper} to that machine first (next to the program or on its PYTHONPATH).`;
        const pre = document.createElement('pre');
        pre.className = 'value-text';
        pre.id = 'debugserver-snippet';
        pre.textContent = m.snippet;
        holder.append(note, pre);
        const copy = document.createElement('button');
        copy.textContent = 'Copy snippet';
        copy.onclick = () => void navigator.clipboard.writeText(m.snippet).then(() => toast('Snippet copied'), () => toast('Could not copy'));
        dialog('Debug server', holder, [copy]);
    }
    /** Python Debug Server: this program listens, and a program started elsewhere connects back to be debugged (breakpoints, stepping, variables as in any session).
     * `settings`: { host, port, connectHost, mappings: [{local, remote}] }. */
    async function startDebugServer(settings, openPanel = true) {
        if (run)
            return false;
        await deps.saveAll();
        const breakpoints = {};
        for (const m of monaco.editor.getModels()) {
            const r = fileOf(m);
            const bps = r ? breakpointsOf(m) : [];
            if (r && bps.length)
                breakpoints[r] = wire(m, bps);
        }
        output.textContent = '';
        colours = ansiReader();
        debugging = true;
        profiling = false;
        paused = false;
        serving = true;
        connected = false;
        if (openPanel !== false)
            showTab('output');
        lastLaunch = { rel: null, debug: true, profile: false, args: [], extra: {}, started: Date.now(), label: `Debug server ${settings.host}:${settings.port}` };
        run = new RunClient({ type: 'listen', ...settings, breakpoints, exceptionBreak: raisedBox.checked ? 'raised' : 'uncaught',
            exceptionClasses: exceptionClasses(), muted: muteBox.checked, loadPolicy: policyBox.value, functionBreaks: functionNames() });
        run.onMessage = handle;
        const mine = run;
        run.onClose = () => { if (run && run === mine) {
            append('\nConnection to the host lost.\n', 'err');
            finish();
        } };
        setButtons();
        return true;
    }
    /** Debug server… of the Run menu: the settings in a dialog, then listen. */
    function debugServerDialog() {
        if (run)
            return;
        const holder = document.createElement('div');
        holder.className = 'debugserver-form';
        const row = (label, input) => {
            const l = document.createElement('label');
            l.style.display = 'block';
            l.style.margin = '6px 0';
            l.append(label + ' ', input);
            return l;
        };
        const input = (value, placeholder) => Object.assign(document.createElement('input'), { value, placeholder, spellcheck: false });
        const host = input('127.0.0.1', '127.0.0.1');
        host.id = 'ds-host';
        const port = input('5678', '5678');
        port.id = 'ds-port';
        const reach = input('', "empty: this computer's name");
        reach.id = 'ds-reach';
        const maps = document.createElement('textarea');
        maps.id = 'ds-maps';
        maps.rows = 4;
        maps.cols = 50;
        maps.spellcheck = false;
        maps.placeholder = 'C:\\proj\\src=/app/src (local=remote, one per line)';
        const note = document.createElement('div');
        note.className = 'dim';
        note.textContent = 'This IDE listens; a program started elsewhere calls pm_debug.connect(...) to be debugged here. Use * to listen on every network interface. The same settings can be kept in a Python Debug Server run configuration.';
        holder.append(note, row('Listen on', host), row('Port', port), row('The program reaches this computer as', reach), row('Path mappings', maps));
        const go = document.createElement('button');
        go.textContent = 'Listen';
        go.id = 'ds-listen';
        go.className = 'primary';
        go.onclick = () => {
            holder.closest('#sigdialog')?.remove();
            void startDebugServer({ host: host.value.trim() || '127.0.0.1', port: portOf(port.value), connectHost: reach.value.trim(), mappings: parseMappings(maps.value) });
        };
        dialog('Debug server', holder, [go]);
        host.focus();
    }
    async function startAttach(pid) {
        await deps.saveAll();
        const breakpoints = {};
        for (const m of monaco.editor.getModels()) {
            const r = deps.relOf(m);
            const bps = r ? breakpointsOf(m) : [];
            if (r && bps.length)
                breakpoints[r] = wire(m, bps);
        }
        output.textContent = '';
        colours = ansiReader();
        debugging = true;
        profiling = false;
        paused = false;
        showTab('output');
        run = new RunClient({ type: 'attach', pid, breakpoints });
        run.onMessage = handle;
        // only this run's connection counts: an earlier one closing late (Before launch) must not end the next run
        const mine = run;
        run.onClose = () => { if (run && run === mine) {
            append('\nConnection to the host lost.\n', 'err');
            finish();
        } };
        setButtons();
    }
    let lastLaunch = null;          // what the program that runs (or ran last) was started with, for the Services window
    async function launch(rel, debug, profile, args, extra = {}) {
        if (run)
            return false;
        // an attach is the debugger's alone: Run of it debugs too
        if (extra.nodeAttach)
            debug = true;
        await deps.saveAll();
        const breakpoints = {};
        for (const m of monaco.editor.getModels()) {
            const r = deps.relOf(m);
            const bps = r ? breakpointsOf(m) : [];
            if (r && bps.length)
                breakpoints[r] = wire(m, bps);
        }
        // the program after its Before launch tasks keeps their output above its own
        if (!extra.keepOutput) {
            output.textContent = '';
            colours = ansiReader();
        }
        debugging = debug;
        profiling = profile;
        paused = false;
        // a configuration may leave the panel where it is (Open the Run panel when started: off)
        if (extra.openPanel !== false)
            showTab('output');
        openBrowser = !!extra.openBrowser;
        followLogs((extra.logFiles ?? '').split(',').map((s) => s.trim()).filter(Boolean));
        testsRun = !!extra.tests;
        lastLaunch = { rel, debug, profile, args, extra, started: Date.now(), label: extra.label ?? (extra.module ? `python -m ${extra.module}` : extra.npmScript ? `npm run ${extra.npmScript}` : extra.browser ? `JavaScript Debug ${extra.browser.url}` : extra.nodeAttach ? `Attach to Node.js ${extra.nodeAttach.pid ? 'process ' + extra.nodeAttach.pid : (extra.nodeAttach.host ?? '127.0.0.1') + ':' + (extra.nodeAttach.port ?? 9229)}` : extra.artisan ? `php artisan ${extra.artisan.join(' ')}` : extra.composer ? `composer ${extra.composer.join(' ')}` : extra.tests ? 'tests' : (rel ?? 'a program')) };
        run = new RunClient({ type: 'start', file: rel ?? undefined, module: extra.module, tests: extra.tests, profileTests: extra.profileTests, allCode: extra.allCode, debug, profile, args, env: extra.env, cwd: extra.cwd, breakpoints, exceptionBreak: raisedBox.checked ? 'raised' : 'uncaught',
            exceptionClasses: exceptionClasses(), muted: muteBox.checked, loadPolicy: policyBox.value, functionBreaks: functionNames(),
            envFiles: extra.envFiles, interpreter: extra.interpreter, uv: extra.uv, uvPath: extra.uvPath, contentRoots: extra.contentRoots, sourceRoots: extra.sourceRoots,
            // JavaScript: an npm script of a package.json (its folder), and the Node program a configuration names
            npmScript: extra.npmScript, packageDir: extra.packageDir, node: extra.node, browser: extra.browser, nodeAttach: extra.nodeAttach,
            // PHP: an Artisan command of a Laravel project (php artisan serve, migrate, …)
            artisan: extra.artisan,
            // Composer: a command of the project's composer.json (install, require …, a script)
            composer: extra.composer });
        run.onMessage = handle;
        // only this run's connection counts: an earlier one closing late (Before launch) must not end the next run
        const mine = run;
        run.onClose = () => { if (run && run === mine) {
            append('\nConnection to the host lost.\n', 'err');
            finish();
        } };
        setButtons();
        return true;
    }
    // ------------------------------------------------------------ compound configurations
    // A compound starts several configurations together. Each member runs on a connection of its own (without the debugger), its lines in the Output
    // marked with its name, and each is a row of the Services window to stop or restart, with its own output beside the list. One member at a time
    // can run in the Run panel instead, under the debugger or the profiler (Debug, Profile of its row; the first one when the compound is debugged).
    const MEMBER_TEXT = 200000;   // the characters of a member's own output that are kept
    function sayFor(entry, text, cls = '') {
        entry.text = withOutput(entry.text, text, MEMBER_TEXT);
        let out = '';
        for (const part of text.split(/(?<=\n)/)) {
            if (entry.lineStart) out += `[${entry.name}] `;
            out += part;
            entry.lineStart = part.endsWith('\n');
        }
        append(out, cls);
    }
    function endSide(entry, code) {
        entry.live = false;
        entry.code = code;
        entry.client?.close();
        entry.client = null;
        // the last member debugged under the Run panel's debug server has ended: the server goes too
        if (entry.debugged) {
            entry.debugged = false;
            if (cellMode === 'compound' && run !== null && !groupOf(entry.group).some((o) => o.debugged && o.live)) run.send({ type: 'stop' });
        }
        setButtons();
    }
    /** Starts a member on a connection of its own; with `server` (the Run panel's debug server) under the debugger, as one of its programs. */
    function launchSide(entry, server = null) {
        if (inPanel(entry)) return;
        const s = startOf(entry.config);
        const client = new RunClient({ type: 'start', file: s.file, module: s.module, args: s.args, env: s.env, cwd: s.cwd, envFiles: s.envFiles, interpreter: s.interpreter,
            uv: s.uv, uvPath: s.uvPath, contentRoots: s.contentRoots, sourceRoots: s.sourceRoots, allCode: s.allCode, debug: false, profile: false, breakpoints: {}, exceptionBreak: 'uncaught',
            ...(server ? { debugServer: { port: server.port, token: server.token, name: entry.name } } : {}) });
        entry.debugged = !!server;
        entry.client = client;
        entry.live = true;
        entry.code = null;
        entry.started = Date.now();
        entry.lineStart = true;
        entry.text = '';
        client.onMessage = (m) => {
            if (entry.client !== client) return;
            if (m.type === 'started') sayFor(entry, `Running (pid ${m.pid})\n`, 'info');
            else if (m.type === 'output') sayFor(entry, m.text, m.stream === 'stderr' ? 'err' : '');
            else if (m.type === 'error') { sayFor(entry, m.message + '\n', 'err'); endSide(entry, null); }
            else if (m.type === 'exited') { sayFor(entry, `\nProcess finished with exit code ${m.code}\n`, 'info'); endSide(entry, m.code); }
        };
        client.onClose = () => { if (entry.client === client) { sayFor(entry, '\nConnection to the host lost.\n', 'err'); endSide(entry, null); } };
        setButtons();
    }
    // the member that runs in the Run panel (under the debugger or the profiler) rather than on a connection of its own
    const inPanel = (entry) => run !== null && lastLaunch?.extra?.member === entry;
    const stopSide = (entry) => {
        if (inPanel(entry)) run.send({ type: 'stop' });
        else if (entry.live) entry.client?.send({ type: 'stop' });
    };
    /** Starts a member in the Run panel under the debugger (or the profiler): its breakpoints stop, the Debug tab shows its frames. It stops first
     * when it runs on its own; false when another program has the Run panel. */
    async function debugSide(entry, profile = false) {
        if (run !== null) {
            append(`${entry.name}: the Run panel has a program running; stop it first to ${profile ? 'profile' : 'debug'} this one.\n`, 'err');
            showTab('output');
            return false;
        }
        stopSide(entry);
        for (let i = 0; i < 50 && entry.live; i++) await new Promise((ok) => setTimeout(ok, 100));
        if (entry.live || run !== null) return false;
        const s = startOf(entry.config);
        entry.code = null;
        entry.started = Date.now();
        return launch(s.file ?? null, !profile, profile, s.args, { ...s, label: entry.name, member: entry, openPanel: true });
    }
    async function restartSide(entry) {
        if (inPanel(entry)) {
            const again = lastLaunch;
            run.send({ type: 'stop' });
            for (let i = 0; i < 50 && run !== null; i++) await new Promise((ok) => setTimeout(ok, 100));
            if (run === null) await launch(again.rel, again.debug, again.profile, again.args, { ...again.extra, openPanel: false });
            return;
        }
        stopSide(entry);
        for (let i = 0; i < 50 && entry.live; i++) await new Promise((ok) => setTimeout(ok, 100));
        if (!entry.live) launchSide(entry);
    }
    const groupOf = (group) => [...sides.values()].filter((e) => e.group === group);
    /** Starts the members of a compound that are not running; the running ones are left. */
    async function startCompound(compound, debug = false, profile = false) {
        const { members, skipped } = compoundMembers(compound, (name) => deps.runConfigByName?.(name) ?? null);
        if (compound.openPanel !== false) showTab('output');
        for (const k of skipped) append(`${compound.name}: ${k.name} is left out, ${k.why}.\n`, 'err');
        if (!members.length) { append(`${compound.name}: there is no configuration to start.\n`, 'err'); return; }
        await deps.saveAll();
        for (const [name, e] of sides) if (e.group === compound.name && !e.live && !members.some((m) => m.name === name)) sides.delete(name);
        // debugged: every member runs under the debugger, as a program of one debug server in the Run panel (its breakpoints stop each of them;
        // one member's stop is shown at a time, by its name). Without the Run panel free, the first member goes there, as before.
        const server = debug && !profile && run === null ? await debugServerFor('compound', compound.name) : null;
        if (server) debug = false;
        for (const c of members) {
            if (c.kind === 'http') { window.dispatchEvent(new CustomEvent('pymagic:runHttp', { detail: startOf(c).http })); continue; }
            let entry = sides.get(c.name);
            if (entry?.live) continue;
            if (!entry) { entry = { name: c.name, live: false }; sides.set(c.name, entry); }
            entry.config = c;
            entry.group = compound.name;
            // debugged or profiled: the first member goes to the Run panel under the debugger (or the profiler), the others run as they do
            if ((debug || profile) && run === null) {
                append(`${compound.name}: ${c.name} runs under the ${profile ? 'profiler' : 'debugger'} in the Run panel, the others without it (Debug or Profile of a row in the Services window moves another one there).\n`, 'info');
                if (await debugSide(entry, profile)) { debug = profile = false; continue; }
            }
            launchSide(entry, server);
        }
    }
    /** The members of compounds, as rows of the Services window: each stopped or restarted by itself, the group started or stopped as a whole. */
    function sideServices() {
        return [...sides.values()].map((e) => {
            const panel = inPanel(e);
            const ranInPanel = lastLaunch?.extra?.member === e;
            const live = e.live || panel;
            return {
                id: 'side:' + e.name, group: 'Compound ' + e.group, name: e.name,
                state: panel ? (paused ? 'paused' : profiling ? 'profiling' : 'debugging') : e.live ? (e.debugged ? 'debugging' : 'running') : 'finished', started: live ? e.started : undefined,
                detail: panel ? 'in the Run panel' : e.live ? (e.debugged ? 'under the debugger of the Run panel' : undefined) : e.code === null || e.code === undefined ? 'did not run' : `exit code ${e.code}`, done: !live,
                // its own output beside the list; the one that ran in the Run panel has the Run panel's
                output: () => (ranInPanel ? output.textContent : e.text ?? ''),
                show: () => { if (panel) showTab(debugging ? 'debug' : 'output'); else window.dispatchEvent(new CustomEvent('pymagic:showService', { detail: 'side:' + e.name })); },
                stop: live ? () => stopSide(e) : undefined,
                restart: () => restartSide(e),
                actions: panel ? [] : [
                    { label: 'Debug', title: 'Run it again in the Run panel under the debugger: its breakpoints stop', run: () => debugSide(e) },
                    { label: 'Profile', title: 'Run it again in the Run panel under the profiler', run: () => debugSide(e, true) },
                ],
                groupActions: [
                    { label: 'Start all', title: 'Start the members that are not running', run: () => { for (const o of groupOf(e.group)) if (!o.live && !inPanel(o)) launchSide(o); } },
                    { label: 'Stop all', title: 'Stop every member of this compound', run: () => { for (const o of groupOf(e.group)) stopSide(o); } },
                    { label: 'Close', title: 'Take the finished members out of the list', run: () => { for (const o of groupOf(e.group)) if (!o.live && !inPanel(o)) sides.delete(o.name); } },
                ],
            };
        });
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
    // function breakpoints by name, kept with the project
    const fnBox = $('fn-breaks');
    const FN_KEY = 'pymagic.functionBreakpoints:' + rootNorm;
    const functionNames = () => fnBox.value.split(/[\s,]+/).filter(Boolean);
    try {
        fnBox.value = localStorage.getItem(FN_KEY) ?? '';
    }
    catch { /* storage unavailable */ }
    fnBox.onchange = () => {
        try {
            localStorage.setItem(FN_KEY, fnBox.value);
        }
        catch { /* storage unavailable */ }
        run?.send({ type: 'setFunctionBreaks', names: functionNames() });
    };
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
    // Load values: "all" computes every value shown; "on demand" only those clicked (for objects whose repr is slow)
    const policyBox = $('load-policy');
    try {
        policyBox.value = localStorage.getItem('pymagic.loadPolicy') ?? 'all';
    }
    catch { /* storage unavailable */ }
    policyBox.onchange = () => {
        try {
            localStorage.setItem('pymagic.loadPolicy', policyBox.value);
        }
        catch { /* storage unavailable */ }
        run?.send({ type: 'setLoadPolicy', policy: policyBox.value });
        if (paused)
            void showFrame(selectedFrame);
    };
    document.documentElement.classList.toggle('bp-muted', muteBox.checked);
    $('frames-copy').onclick = () => {
        if (!frames.length)
            return;
        void navigator.clipboard.writeText(stackText(frames.filter((f) => !f.thread))).then(() => toast('Call stack copied'), () => toast('Could not copy'));
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
        frames = [...framesOfStop(r), ...frames.filter((f) => f.thread)];
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
    // ▶ Run and Debug above each script of a package.json (the project's own, not node_modules'), as PyCharm's run icons beside them:
    // npm run <script> in that package.json's folder, in the Run panel (and under the debugger, with the processes it starts)
    monaco.languages.registerCodeLensProvider('json', {
        provideCodeLenses(model) {
            const rel = deps.relOf(model);
            if (!rel || !isPackageJson(rel))
                return { lenses: [], dispose() { } };
            const lenses = scriptEntries(model.getValue()).flatMap((s) => {
                const range = new monaco.Range(s.line, 1, s.line, 1);
                return [
                    { range, command: { id: 'pymagic.npm.run', title: '▶ Run', tooltip: `npm run ${s.name}`, arguments: [rel, s.name, false] } },
                    { range, command: { id: 'pymagic.npm.run', title: 'Debug', tooltip: `npm run ${s.name} under the debugger`, arguments: [rel, s.name, true] } },
                ];
            });
            return { lenses, dispose() { } };
        },
    });
    monaco.editor.registerCommand('pymagic.npm.run', (_, rel, name, debug) => {
        const dir = npmFolderOf(rel);
        void launch(null, !!debug, false, [], { npmScript: name, ...(dir ? { packageDir: dir } : {}), label: `npm run ${name}${dir ? ` (${dir})` : ''}` });
    });
    /** The expression under a place of the editor, as its language writes one (PHP's $variables and ->properties). */
    function exprAt(model, line, column) {
        const text = model.getLineContent(line);
        return model.getLanguageId() === 'php' ? phpExpressionAt(text, column) : expressionAt(text, column);
    }
    // Python's values, JavaScript's and TypeScript's (run by Node, debugged through pm_nodedebug.mjs) and PHP's (Xdebug)
    monaco.languages.registerHoverProvider(['python', 'javascript', 'typescript', 'php'], {
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
                expression = exprAt(model, position.lineNumber, position.column);
                const word = model.getWordAtPosition(position);
                if (word)
                    range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
            }
            if (!expression)
                return null;
            // the same value shown already: the popup stays as it is (opened nodes, the selection)
            if (valuePopup && valuePopup.expression === expression && valuePopup.model === model && valuePopup.line === position.lineNumber)
                return null;
            const r = await run.ask({ type: 'evaluate', frameId: selectedFrame, expression, evalOnly: true });
            if (r.error)
                return sel && !sel.isEmpty() ? { range, contents: [{ value: '`' + r.error + '`' }] } : null;
            showValuePopup(ed ?? editor, model, range, expression, r);
            return null;
        },
    });
    /** Whether the language's own hover should stand aside: the program is stopped and the mouse is on a value the debugger shows. */
    function debugValueAt(model, position) {
        return !!(run && paused && deps.relOf(model) && exprAt(model, position.lineNumber, position.column));
    }

    // ------------------------------------------------------------ the value popup, as PyCharm's: the value under the mouse as a tree to open
    let valuePopup = null;   // { el, expression, model, line, watch }
    function closeValuePopup() {
        if (!valuePopup)
            return;
        clearInterval(valuePopup.watch);
        clearTimeout(valuePopup.leave?.());
        valuePopup.moved?.dispose();
        valuePopup.el.remove();
        document.removeEventListener('mousedown', valuePopup.outside, true);
        document.removeEventListener('keydown', valuePopup.escape, true);
        valuePopup = null;
    }
    /**
     * The value of an expression under the mouse (or Quick Evaluate's) in a popup below it: the value and its type, and when it has members its
     * tree, opened one level at a time as in the Variables panel (F2 sets a member's value, a right-click gives View value, Copy value, Add to
     * watches and the rest, View opens a long value whole). It stays while the mouse is on the value or in the popup, and closes with Escape,
     * a click elsewhere, or when the program goes on.
     */
    function showValuePopup(ed, model, range, expression, r) {
        closeValuePopup();
        const el = document.createElement('div');
        el.className = 'value-popup';
        const tree = document.createElement('ul');
        tree.className = 'vars value-popup-tree';
        // the expression itself is the tree's first row, open; a name of the frame can be set with F2 too
        const root = variableNode({ name: expression, value: r.result, type: r.valueType ?? '', ref: r.ref ?? 0 }, undefined, { expr: null, type: '' });
        tree.appendChild(root);
        root.querySelector('.caret')?.click();
        const rootHead = root.querySelector('div');
        if (/^[A-Za-z_]\w*$/.test(expression)) {
            rootHead.addEventListener('keydown', (e) => { if (e.key === 'F2') { e.preventDefault(); closeValuePopup(); void setName(expression); } });
            rootHead.title = 'F2 sets the value of this name in the frame';
        }
        const foot = document.createElement('div');
        foot.className = 'value-popup-foot';
        foot.textContent = 'F2 sets the selected value · right-click for more · Esc closes';
        el.append(tree, foot);
        document.body.appendChild(el);
        // below the value, kept inside the window
        const box = ed.getContainerDomNode().getBoundingClientRect();
        const at = ed.getScrolledVisiblePosition({ lineNumber: range?.startLineNumber ?? 1, column: range?.startColumn ?? 1 });
        const x = box.left + (at?.left ?? 40), y = box.top + (at?.top ?? 0) + (at?.height ?? 18) + 2;
        el.style.left = `${Math.max(8, Math.min(x, window.innerWidth - Math.min(720, window.innerWidth - 16) - 8))}px`;
        el.style.top = `${Math.min(y, window.innerHeight - 120)}px`;
        el.style.maxHeight = `${Math.max(120, window.innerHeight - Math.min(y, window.innerHeight - 120) - 16)}px`;
        rootHead.focus({ preventScroll: true });
        // it goes when the mouse leaves both the value and the popup for a moment, on Escape, on a click elsewhere, and when the program goes on
        let leaveTimer = 0;
        // a value being set (its input open) keeps the popup
        const leaveSoon = () => {
            clearTimeout(leaveTimer);
            leaveTimer = window.setTimeout(() => { if (valuePopup?.el === el && !el.matches(':hover') && !el.querySelector('input.edit')) closeValuePopup(); }, 600);
        };
        el.addEventListener('mouseenter', () => clearTimeout(leaveTimer));
        el.addEventListener('mouseleave', leaveSoon);
        const moved = ed.onMouseMove((e) => {
            const p = e.target.position;
            if (p && range && range.containsPosition(p)) clearTimeout(leaveTimer);
            else leaveSoon();
        });
        const outside = (e) => { if (!el.contains(e.target) && !e.target.closest?.('.ctx-menu') && !e.target.closest?.('#sigdialog')) closeValuePopup(); };
        const escape = (e) => { if (e.key === 'Escape' && valuePopup?.el === el && !el.querySelector('input.edit')) { e.preventDefault(); e.stopPropagation(); closeValuePopup(); ed.focus(); } };
        document.addEventListener('mousedown', outside, true);
        document.addEventListener('keydown', escape, true);
        const watch = window.setInterval(() => { if (!run || !paused || !el.isConnected) closeValuePopup(); }, 250);
        valuePopup = { el, expression, model, line: range?.startLineNumber, watch, outside, escape, moved, leave: () => leaveTimer };
    }
    /** Evaluate Expression (Alt+F8): a window that stays open beside the editor, for an expression or several lines of
     * code run in the selected frame (Ctrl+Enter). Each Alt+F8 opens another one. */
    function evaluateWindow(initial = '') {
        const win = document.createElement('div');
        win.className = 'eval-win';
        win.innerHTML = '<div class="eval-title"><span>Evaluate</span><button class="eval-close" title="Close">✕</button></div>'
            + '<textarea spellcheck="false" placeholder="An expression, or lines of code; Ctrl+Enter runs them in the selected frame"></textarea>'
            + '<div class="eval-row"><button class="primary eval-go">Evaluate</button><span class="eval-type dim"></span></div><pre class="eval-out"></pre>';
        const area = win.querySelector('textarea'), out = win.querySelector('.eval-out'), type = win.querySelector('.eval-type');
        area.value = initial;
        const n = document.querySelectorAll('.eval-win').length;
        win.style.right = `${24 + n * 24}px`;
        win.style.top = `${90 + n * 24}px`;
        const go = async () => {
            if (!run || !paused) {
                out.className = 'eval-out e';
                out.textContent = 'The program is not paused.';
                return;
            }
            const r = await run.ask({ type: 'evaluate', frameId: selectedFrame, expression: area.value, limit: 20000 });
            out.className = 'eval-out' + (r.error ? ' e' : '');
            out.textContent = (r.output ?? '') + (r.error ?? (r.result === '' ? (r.output ? '' : '(done)') : r.result));
            type.textContent = r.valueType ?? '';
            if (!r.error) {
                void loadVariables();
                void refreshWatches();
            }
        };
        win.querySelector('.eval-go').onclick = () => void go();
        win.querySelector('.eval-close').onclick = () => win.remove();
        area.onkeydown = (e) => {
            if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                e.preventDefault();
                void go();
            }
            else if (e.key === 'Escape')
                win.remove();
        };
        // the title bar moves the window
        const title = win.querySelector('.eval-title');
        title.onmousedown = (e) => {
            if (e.target.closest('button'))
                return;
            const r = win.getBoundingClientRect(), dx = e.clientX - r.left, dy = e.clientY - r.top;
            const move = (m) => { win.style.left = `${m.clientX - dx}px`; win.style.top = `${m.clientY - dy}px`; win.style.right = 'auto'; };
            const up = () => { document.removeEventListener('mousemove', move); document.removeEventListener('mouseup', up); };
            document.addEventListener('mousemove', move);
            document.addEventListener('mouseup', up);
        };
        document.body.appendChild(win);
        area.focus();
        if (initial)
            void go();
    }
    /** The selection of an editor, or the expression at its cursor. */
    function expressionOf(ed) {
        const model = ed.getModel(), sel = ed.getSelection();
        if (!model || !sel)
            return '';
        if (!sel.isEmpty())
            return model.getValueInRange(sel);
        const pos = ed.getPosition();
        return pos ? exprAt(model, pos.lineNumber, pos.column) ?? '' : '';
    }
    // the links of the hover
    monaco.editor.registerCommand('pymagic.debug.view', (_, expr) => void viewValue(expr, ''));
    monaco.editor.registerCommand('pymagic.debug.table', (_, expr) => void viewTable(expr));
    monaco.editor.registerCommand('pymagic.debug.watch', (_, expr) => addWatch(expr));
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
    $('b-stop').onclick = () => { if (afterExit) stoppedByUser = true; run?.send({ type: 'stop' }); for (const e of sides.values()) stopSide(e); };
    /** Pause: the program stops at its next line of Python; one waiting outside Python (input(), a socket) stops when it comes back. */
    let pauseTimer = 0;
    function pause() {
        if (!run || !debugging || paused)
            return;
        run.send({ type: 'pause' });
        clearTimeout(pauseTimer);
        pauseTimer = window.setTimeout(() => {
            if (run && !paused)
                toast('The program is waiting outside Python (for input, a socket or a lock); it pauses when it runs Python again.');
        }, 3000);
    }
    $('b-pause').onclick = pause;
    $('b-over').onclick = () => resume('next');
    $('b-into').onclick = () => resume('stepIn');
    $('b-out').onclick = () => resume('stepOut');
    const K = monaco.KeyCode, M = monaco.KeyMod;
    defineCommand({ id: 'pymagic.run.debug', label: 'Debug (or resume)', group: 'Run', keys: ['F5'], everywhere: true, run: () => (paused ? resume('continue') : void start(true)) });
    defineCommand({ id: 'pymagic.run.run', label: 'Run the file', group: 'Run', keys: ['Ctrl+F5'], everywhere: true, run: () => void start(false) });
    defineCommand({ id: 'pymagic.run.stop', label: 'Stop', group: 'Run', keys: ['Shift+F5'], everywhere: true, run: () => run?.send({ type: 'stop' }) });
    defineCommand({ id: 'pymagic.run.stepOver', label: 'Step over', group: 'Run', keys: ['F10'], everywhere: true, run: () => resume('next') });
    defineCommand({ id: 'pymagic.run.stepInto', label: 'Step into', group: 'Run', keys: ['F11'], everywhere: true, run: () => resume('stepIn') });
    defineCommand({ id: 'pymagic.run.stepOut', label: 'Step out', group: 'Run', keys: ['Shift+F11'], everywhere: true, run: () => resume('stepOut') });
    defineCommand({ id: 'pymagic.run.toCursor', label: 'Run to cursor', group: 'Run', keys: ['Alt+F9'], run: (ed) => { focused = ed; runToCursor(false); } });
    defineCommand({ id: 'pymagic.run.forceToCursor', label: 'Force run to cursor', group: 'Run', keys: ['Ctrl+Alt+F9'], run: (ed) => { focused = ed; runToCursor(true); } });
    defineCommand({ id: 'pymagic.run.smartStepInto', label: 'Smart step into', group: 'Run', keys: ['Shift+F7'], run: () => void smartStepInto() });
    defineCommand({ id: 'pymagic.run.showExecutionPoint', label: 'Show execution point', group: 'Run', keys: ['Alt+F10'], run: () => showExecutionPoint() });
    defineCommand({ id: 'pymagic.run.quickEvaluate', label: 'Quick evaluate', group: 'Run', keys: ['Ctrl+Alt+F8'], run: (ed) => { focused = ed; quickEvaluate(ed); } });
    defineCommand({ id: 'pymagic.run.evaluate', label: 'Evaluate expression (a window)', group: 'Run', keys: ['Alt+F8'], run: (ed) => { focused = ed; evaluateWindow(expressionOf(ed)); } });
    defineCommand({ id: 'pymagic.run.forceStepInto', label: 'Force step into', group: 'Run', keys: ['Alt+Shift+F7'], run: () => forceStep('stepIn') });
    defineCommand({ id: 'pymagic.run.forceStepOver', label: 'Force step over', group: 'Run', keys: ['Alt+Shift+F8'], run: () => forceStep('next') });
    /** The editors whose place counts for stepping to the cursor and looking at values: the main one and the sides of a split. */
    function placeKeys(ed) {
        ed.onDidFocusEditorText(() => { focused = ed; });
        ed.addAction({
            id: 'pymagic.addWatch', label: 'Add to Watches', contextMenuGroupId: 'z_debug', contextMenuOrder: 1,
            precondition: undefined, run: () => addWatch(expressionOf(ed)),
        });
        // a click on the values at the end of a line: what can be done with each of them
        ed.onMouseDown((e) => {
            const pos = e.target.position, model = ed.getModel();
            if (!inline || !pos || !model || inline.model !== model || e.event.rightButton)
                return;
            const T = monaco.editor.MouseTargetType;
            const afterEnd = e.target.type === T.CONTENT_EMPTY || (e.target.type === T.CONTENT_TEXT && pos.column === model.getLineMaxColumn(pos.lineNumber));
            const names = inline.byLine.get(pos.lineNumber);
            if (!afterEnd || !names?.length)
                return;
            openMenu({ x: e.event.posx, y: e.event.posy }, names.map((name) => ({ label: name, submenu: valueItems(name, '', () => void setName(name)) })));
        });
    }
    /** Force Step Into (Alt+Shift+F7) may stop in library code; Force Step Over (Alt+Shift+F8) passes the breakpoints of the calls. */
    function forceStep(type) {
        if (run && paused)
            run.send({ type, force: true });
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
    const history = [];
    let historyAt = 0;
    watchIn.onkeydown = async (e) => {
        // Up and Down walk the history when the cursor is on the first or the last line; Shift+Enter starts a new line
        const firstLine = !watchIn.value.slice(0, watchIn.selectionStart).includes('\n');
        const lastLine = !watchIn.value.slice(watchIn.selectionEnd).includes('\n');
        if ((e.key === 'ArrowUp' && firstLine) || (e.key === 'ArrowDown' && lastLine)) {
            e.preventDefault();
            const step = historyStep(history, historyAt, e.key === 'ArrowUp' ? -1 : 1);
            historyAt = step.index;
            watchIn.value = step.text;
            fitConsole();
            return;
        }
        if (e.key !== 'Enter' || e.shiftKey || !watchIn.value.trim()) {
            requestAnimationFrame(fitConsole);
            return;
        }
        e.preventDefault();
        const expression = watchIn.value;
        if (history[history.length - 1] !== expression)
            history.push(expression);
        historyAt = history.length;
        watchIn.value = '';
        fitConsole();
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
            const r = await run.ask({ type: 'evaluate', frameId: selectedFrame, expression, limit: 20000 });
            if (r.output) {
                const printed = document.createElement('div');
                printed.className = 'o';
                printed.textContent = r.output.replace(/\n$/, '');
                log.appendChild(printed);
            }
            if (r.error) {
                a.className = 'e';
                a.textContent = r.error;
            }
            else
                a.textContent = r.result === '' ? '' : r.result + (r.valueType ? `   (${r.valueType})` : '');
            if (!r.error)
                void loadVariables(); // an assignment may have changed what is shown
        }
        log.appendChild(a);
        log.scrollTop = log.scrollHeight;
    };
    /** The console's box grows with its lines, up to six. */
    function fitConsole() {
        watchIn.rows = Math.min(6, Math.max(1, watchIn.value.split('\n').length));
    }
    // which interpreter will be used
    setButtons();
    /** Another editor (the right side of a split) takes breakpoints with a click in its margin, F9 and Shift+F9, and the run and debug keys. */
    function attach(other) {
        glyphMouse(other);
        // F9, Shift+F9 and the run and debug keys are commands of every editor (commands.js): they act on this one when it has the focus
        placeKeys(other);
    }
    // Debug tests (the Tests panel, the menus of the tree): pytest, or unittest for the unittest framework, run as a module under the debugger
    window.addEventListener('pymagic:debugTests', (e) => {
        const { ids, framework } = e.detail;
        if (run) {
            append('A program is running; stop it first.\n', 'err');
            showTab('output');
            window.dispatchEvent(new CustomEvent('pymagic:testMessage', { detail: { type: 'error', message: 'A program is running in the Run panel; stop it first.' } }));
            return;
        }
        // PyMagic's own test runner, under the debugger: the results land in the Tests tree as a run's do
        void launch(null, true, false, [], { tests: { ids, framework }, openPanel: false });   // the Tests tab stays in front
    });
    // Profile of tests (the Tests panel's menus): pytest or unittest under the profiler, the result in the Profiler tab
    window.addEventListener('pymagic:profileTests', (e) => {
        if (run) {
            append('A program is running; stop it first.\n', 'err');
            showTab('output');
            return;
        }
        void launch(null, false, true, [], { profileTests: e.detail });
    });
    window.addEventListener('pymagic:stopDebugTests', () => { if (run && testsRun) run.send({ type: 'stop' }); });
    /** A debug server on a free port for a notebook cell ('cell', Debug Cell), the Python console ('console', Attach Debugger) or the members of a
     * compound ('compound', its name): resolves with { host, port, token, end } once it listens, null when another run is going. */
    let compoundDebugged = '';
    async function debugServerFor(kind, name = '') {
        if (run)
            return null;
        hookFor = kind;
        compoundDebugged = name;
        const got = new Promise((resolve) => { cellHook = resolve; });
        if (!(await startDebugServer({ host: '127.0.0.1', port: 0, connectHost: '', mappings: [] }, true))) {
            cellHook = null;
            return null;
        }
        return got;
    }
    return {
        /** Stopped on a value the debugger shows in its popup: the language's own hover stands aside. */
        debugValueAt,
        onModelCreated, runScript: (rel, args, debug = false) => launch(rel, debug, false, args), isRunning: () => run !== null,
        /** A file run under the profiler (the tree's More Run/Debug > Profile). */
        profileScript: (rel) => launch(rel, false, true, ''),
        /** `python -m module args`; false when a run is going. */
        runModule: (module, args = [], debug = false) => launch(null, debug, false, args, { module }),
        /** `php artisan command args` of a Laravel project; false when a run is going. */
        runArtisan: (args, debug = false) => launch(null, debug, false, [], { artisan: args }),
        /** `composer command args` of the project; false when a run is going. */
        runComposer: (args) => launch(null, false, false, [], { composer: args }),
        /** The program that is running, as a service of the Services window: stopped, restarted (started again as it was) or shown. */
        services: () => [...(run === null || !lastLaunch || lastLaunch.extra?.member ? [] : [{
            id: 'run', group: 'Run', name: lastLaunch.label, started: lastLaunch.started,
            state: paused ? 'paused' : debugging ? 'debugging' : profiling ? 'profiling' : 'running',
            show: () => showTab('output'),
            stop: () => { if (afterExit) stoppedByUser = true; run?.send({ type: 'stop' }); },
            restart: async () => {
                const again = lastLaunch;
                run?.send({ type: 'stop' });
                for (let i = 0; i < 50 && run !== null; i++) await new Promise((ok) => setTimeout(ok, 100));
                // the window that asked stays where it is: the panel does not jump to Output
                if (run === null) await launch(again.rel, again.debug, again.profile, again.args, { ...again.extra, openPanel: false });
            },
        }]), ...sideServices()],
        attach,
        /** Attach Debugger of the Python console: a debug server the console connects to, as Debug Cell's: { host, port, token, end, live } (null when another run
         * is going); `live()` tells whether the session still runs (Stop in the Run panel ends it too). */
        consoleSession: async () => {
            const s = await debugServerFor('console');
            return s && { ...s, live: () => run !== null && cellMode === 'console' };
        },
        /**
         * What the notebook view needs to debug a cell: `attachEditor` gives a cell's editor the breakpoint margin and F9; `cellSession` starts a debug
         * server for one cell and resolves with { host, port, token, end } (null when another run is going); `breakpointsOf` / `restoreBreakpoints`
         * keep the breakpoints of a cell whose editor is rebuilt.
         */
        notebook: {
            attachEditor(ed) {
                glyphMouse(ed);   // F9 is the command of every editor: it toggles in the cell when the cell has the focus
            },
            cellSession: () => debugServerFor('cell'),
            breakpointsOf: (model) => breakpointsOf(model),
            restoreBreakpoints(model, list) {
                for (const bp of list)
                    if (bp.line <= model.getLineCount())
                        placeBreakpoint(model, bp.line, bp);
            },
        },
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
            evaluate: () => evaluateWindow(expressionOf(focused)),
            forceStepInto: () => forceStep('stepIn'), forceStepOver: () => forceStep('next'),
            attach: () => void attachToProcess(), running: () => run !== null,
            debugServer: () => debugServerDialog(), startDebugServer,
            pause, canPause: () => run !== null && debugging && !paused,
        },
    };
}

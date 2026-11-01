// The notebook view: an .ipynb file shown as cells instead of JSON. The file stays an ordinary editor model (so saving,
// the dirty mark, git reloads and tabs work as for every file); this view reads the model's text and writes it back
// when a cell changes. Code cells run in a Python kernel on the host (the /nb WebSocket, one kernel per notebook).
import {
    ansiSegments, applyKernelMessage, changeType, clearOutputs, collapseCarriageReturns, deleteCell, describeOutput, insertCell,
    cellOfLine, lastExecutionCount, mergeCells, moveCell, notebookSymbols, offsetOfPosition, parseNotebook, parseTags, sectionOf, serializeNotebook,
    setSource, setTags, sourceOf, splitCell, tagsOf, virtualDocument, virtualLine,
} from './notebook-model';
import { blocksOf, inlineOf } from './markdown-lite';
import { toast } from './toast';

const el = (tag, cls, text) => {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
};

const MAX_OUTPUT_CHARS = 200000;

// ------------------------------------------------------------ markdown to elements (from the structure of markdown-lite)

function inlineNodes(parts) {
    return parts.map((p) => {
        switch (p.t) {
            case 'bold': return el('strong', '', p.text);
            case 'italic': return el('em', '', p.text);
            case 'code': return el('code', '', p.text);
            case 'link': { const a = el('a', '', p.text); a.href = p.href; a.target = '_blank'; a.rel = 'noopener noreferrer'; return a; }
            case 'image': { const i = el('img'); i.alt = p.text; i.src = p.href; i.className = 'nb-md-img'; return i; }
            default: return document.createTextNode(p.text);
        }
    });
}

function blockNodes(blocks) {
    const nodes = [];
    for (const b of blocks) {
        switch (b.type) {
            case 'heading': { const h = el(`h${b.level}`); h.append(...inlineNodes(b.inline)); nodes.push(h); break; }
            case 'paragraph': { const p = el('p'); p.append(...inlineNodes(b.inline)); nodes.push(p); break; }
            case 'code': { const pre = el('pre', 'nb-md-code'); pre.append(el('code', '', b.text)); nodes.push(pre); break; }
            case 'rule': nodes.push(el('hr')); break;
            case 'list': {
                const list = el(b.ordered ? 'ol' : 'ul');
                for (const item of b.items) { const li = el('li'); li.append(...inlineNodes(item)); list.append(li); }
                nodes.push(list);
                break;
            }
            case 'quote': { const q = el('blockquote'); q.append(...blockNodes(b.blocks)); nodes.push(q); break; }
        }
    }
    return nodes;
}

export function renderMarkdown(into, text) {
    into.replaceChildren(...blockNodes(blocksOf(text)));
}

// ------------------------------------------------------------ outputs

/** The style of an HTML output (it is shown in a frame of its own): the colours of the page around it, light or dark. */
function htmlStyle() {
    const css = getComputedStyle(document.documentElement);
    const fg = css.getPropertyValue('--fg').trim() || '#d4d4d4';
    const line = css.getPropertyValue('--line-strong').trim() || '#555';
    const head = css.getPropertyValue('--hover3').trim() || '#333';
    const stripe = css.getPropertyValue('--hover').trim() || '#2a2a2a';
    return `<style>body{margin:0;font:13px/1.4 system-ui,sans-serif;color:${fg};background:transparent}`
        + `table{border-collapse:collapse}td,th{border:1px solid ${line};padding:2px 8px;text-align:right}th{background:${head}}tr:nth-child(even){background:${stripe}}`
        + 'img,svg{max-width:100%}</style>';
}

function textOutput(text, cls) {
    let t = collapseCarriageReturns(text);
    if (t.length > MAX_OUTPUT_CHARS) t = t.slice(0, MAX_OUTPUT_CHARS) + `\n… (${t.length - MAX_OUTPUT_CHARS} more characters not shown)`;
    const pre = el('pre', cls);
    for (const seg of ansiSegments(t)) {
        if (seg.cls) pre.append(el('span', seg.cls, seg.text)); else pre.append(document.createTextNode(seg.text));
    }
    return pre;
}

export function renderOutput(o) {
    const d = describeOutput(o);
    switch (d.kind) {
        case 'text': return textOutput(d.text, `nb-out nb-${d.stream}`);
        case 'error': return textOutput(d.text, 'nb-out nb-error');
        case 'image': { const img = el('img', 'nb-out nb-image'); img.src = `data:${d.mime};base64,${d.data}`; return img; }
        case 'html': {
            // scripts are off in a sandboxed frame; allow-same-origin only lets the page measure it
            const frame = el('iframe', 'nb-out nb-html');
            frame.setAttribute('sandbox', 'allow-same-origin');
            frame.srcdoc = htmlStyle() + d.html;
            frame.onload = () => { try { frame.style.height = `${Math.min(frame.contentDocument.documentElement.scrollHeight + 4, 600)}px`; } catch { /* not measurable */ } };
            return frame;
        }
        case 'markdown': { const box = el('div', 'nb-out nb-md'); renderMarkdown(box, d.text); return box; }
        case 'json': return textOutput(d.text, 'nb-out nb-result');
        default: return textOutput(d.text ?? '', 'nb-out nb-result');
    }
}

// ------------------------------------------------------------ the view

/** Findings that only get in the way in a notebook: layout rules between cells, and the value a cell ends with (it is shown). */
const NOISE = new Set(['pep8-blank-lines', 'pep8-imports']);
const NOISE_MESSAGE = /^PEP 8: (W29\d|W391|E30\d)/;   // no newline at the end, blank lines between cells

export function setupNotebook({ monaco, area, editorEl, save, relOf, lsp, rootUri, formatEngine = () => 'builtin' }) {
    const root = el('div', 'nb-root');
    root.id = 'notebook';
    area.append(root);
    const states = new Map();      // rel -> state of one open notebook
    const cellModels = new WeakMap();   // the Monaco model of a cell's editor -> { state, view }
    let shown = null;              // the state on screen

    // ---- the notebook as one Python file for the language server

    /** A file name inside .pymagic that no project file can have; it is never written to disk. */
    function virtualUri(rel) {
        const name = rel.replace(/[\/]/g, '__').replace(/\./g, '_');
        return `${rootUri().replace(/\/$/, '')}/.pymagic/notebooks/${encodeURIComponent(name)}.py`;
    }

    function syncVirtual(state) {
        clearTimeout(state.vtimer);
        state.vtimer = 0;
        if (!state.nb) return;
        state.vdoc = virtualDocument(state.nb);
        state.vversion++;
        if (!state.vopen) {
            state.vopen = true;
            lsp.notify('textDocument/didOpen', { textDocument: { uri: state.vuri, languageId: 'python', version: state.vversion, text: state.vdoc.text } });
        } else {
            lsp.notify('textDocument/didChange', { textDocument: { uri: state.vuri, version: state.vversion }, contentChanges: [{ text: state.vdoc.text }] });
        }
    }

    const virtualSoon = (state) => { clearTimeout(state.vtimer); state.vtimer = setTimeout(() => syncVirtual(state), 150); };

    function showRoot(on) {
        root.classList.toggle('active', on);
        editorEl.classList.toggle('nb-hidden', on);
    }

    // ---- one notebook

    function open(rel, model) {
        const state = {
            rel, model, nb: null, views: [], selected: 0, pushing: false, timer: 0, ws: null, status: 'off', busy: false, queue: [], ids: new Map(), nextId: 1,
            dom: el('div', 'nb-doc'), listEl: el('div', 'nb-cells'), barEl: el('div', 'nb-bar'), errorEl: el('div', 'nb-error-box'), observer: null,
            vuri: virtualUri(rel), vdoc: { text: '', cells: [] }, vversion: 0, vopen: false, vtimer: 0, diags: new Map(),
        };
        states.set(rel, state);
        state.observer = new IntersectionObserver((entries) => {
            for (const e of entries) if (e.isIntersecting) e.target.__create?.();
        }, { root, rootMargin: '300px' });
        state.varsEl = el('div', 'nb-vars');
        state.vars = [];
        state.varsOpen = true;
        state.dom.append(state.barEl, state.varsEl, state.errorEl, state.listEl);
        root.append(state.dom);
        buildBar(state);
        paintVars(state);
        model.onDidChangeContent(() => { if (!state.pushing) load(state); });
        load(state);
        return state;
    }

    function load(state) {
        const { nb, error } = parseNotebook(state.model.getValue());
        state.errorEl.textContent = error ?? '';
        state.errorEl.style.display = error ? 'block' : 'none';
        state.listEl.style.display = error ? 'none' : 'block';
        if (error) { state.nb = null; return; }
        state.nb = nb;
        render(state);
    }

    /** Writes the cells back to the model (after a short pause: typing goes on while the text is serialised). */
    function pushSoon(state) {
        clearTimeout(state.timer);
        state.timer = setTimeout(() => flush(state), 300);
    }

    function flush(state) {
        clearTimeout(state.timer);
        state.timer = 0;
        if (!state.nb) return;
        const text = serializeNotebook(state.nb);
        if (text === state.model.getValue()) return;
        state.pushing = true;
        try { state.model.setValue(text); } finally { state.pushing = false; }
    }

    function dispose(state) {
        for (const v of state.views) v.editor?.dispose();
        state.observer?.disconnect();
        state.ws?.close();
        state.dom.remove();
    }

    // ---- the toolbar

    function buildBar(state) {
        const btn = (label, title, on) => { const b = el('button', '', label); b.title = title; b.onclick = on; return b; };
        state.statusEl = el('span', 'nb-status', 'kernel: not started');
        state.barEl.append(
            btn('▶ Run', 'Run the selected cell (Ctrl+Enter)', () => runSelected(state, false)),
            btn('▶▶ Run all', 'Run every cell from the top', () => runAll(state)),
            btn('■ Interrupt', 'Stop the running cell', () => interrupt(state)),
            btn('↻ Restart', 'Start a fresh kernel (variables are lost, outputs stay)', () => restart(state)),
            el('span', 'sep'),
            btn('＋ Code', 'Add a code cell below (Alt+Enter)', () => addCell(state, 'code')),
            btn('＋ Text', 'Add a markdown cell below', () => addCell(state, 'markdown')),
            btn('Format', 'Lay out the code of every code cell (the formatter of the toolbar); one cell: Shift+Alt+F', () => void formatAll(state)),
            btn('Clear outputs', 'Remove the outputs of every cell', () => { clearOutputs(state.nb); state.views.forEach(paintOutputs); pushSoon(state); }),
            btn('Variables', 'Show or hide the Variables window (the names the kernel holds after each run)', () => { state.varsOpen = !state.varsOpen; paintVars(state); }),
            btn('Save', 'Save the notebook (Ctrl+S)', () => { flush(state); void save(); }),
            state.statusEl,
        );
    }

    function setStatus(state, status, text) {
        state.status = status;
        state.statusEl.textContent = `kernel: ${text ?? status}`;
        state.statusEl.className = `nb-status ${status}`;
    }

    // ---- the Variables window: the names the kernel holds, sent after each run

    function paintVars(state) {
        const box = state.varsEl;
        box.style.display = state.varsOpen ? 'block' : 'none';
        if (!state.varsOpen) return;
        const head = el('div', 'nb-vars-head', `Variables (${state.vars.length})`);
        if (!state.vars.length) { box.replaceChildren(head, el('div', 'nb-empty', 'Nothing yet: run a cell and the names it leaves are listed here')); return; }
        const table = el('table', 'nb-vars-table');
        const row = (cells, tag = 'td') => { const tr = el('tr'); for (const c of cells) tr.append(el(tag, '', c)); return tr; };
        table.append(row(['Name', 'Type', 'Size', 'Value'], 'th'));
        for (const v of state.vars) table.append(row([v.name, v.type, v.size ?? '', v.value]));
        const scroll = el('div', 'nb-vars-scroll');
        scroll.append(table);
        box.replaceChildren(head, scroll);
    }

    // ---- cells

    function render(state) {
        for (const v of state.views) v.editor?.dispose();
        state.observer.disconnect();
        // the cells being run or waiting to run keep their place across a rebuild (the kernel is still writing into those cells)
        const before = new Map(state.views.map((v) => [v.cell, v]));
        state.views = state.nb.cells.map((cell) => ({ cell, el: null, editor: null, running: before.get(cell)?.running, queued: before.get(cell)?.queued }));
        const now = new Map(state.views.map((v) => [v.cell, v]));
        state.queue = state.queue.map((v) => now.get(v.cell) ?? v);
        for (const [id, v] of state.ids) state.ids.set(id, now.get(v.cell) ?? v);
        state.listEl.replaceChildren(...state.views.map((v, i) => buildCell(state, v, i)));
        select(state, Math.min(state.selected, state.views.length - 1), false);
        virtualSoon(state);
    }

    function select(state, index, scroll = true) {
        state.selected = Math.max(0, index);
        state.views.forEach((v, i) => v.el?.classList.toggle('selected', i === state.selected));
        if (scroll) state.views[state.selected]?.el?.scrollIntoView({ block: 'nearest' });
        window.dispatchEvent(new CustomEvent('pymagic:nbselect', { detail: { rel: state.rel, cell: state.selected } }));
    }

    const indexOf = (state, view) => state.views.indexOf(view);

    function buildCell(state, view, index) {
        const { cell } = view;
        const box = el('div', `nb-cell ${cell.cell_type}`);
        view.el = box;
        const gutter = el('div', 'nb-gutter');
        view.countEl = el('span', 'nb-count');
        gutter.append(view.countEl);
        const main = el('div', 'nb-main');
        view.inputEl = el('div', 'nb-input');
        view.outEl = el('div', 'nb-outputs');
        view.tagsEl = el('div', 'nb-tags');
        main.append(view.inputEl, view.tagsEl, view.outEl);

        const tools = el('div', 'nb-tools');
        const tool = (label, title, on, cls) => { const b = el('button', cls ?? '', label); b.title = title; b.onclick = (e) => { e.stopPropagation(); on(); }; return b; };
        tools.append(
            tool('▶', 'Run this cell', () => { select(state, indexOf(state, view)); runSelected(state, false); }),
            ...(cell.cell_type === 'markdown' ? [tool('▶ Section', 'Run Section: this heading cell and the cells up to the next heading of the same or a higher level', () => runSection(state, view), 'nb-run-section')] : []),
            tool('Split', 'Split the cell at the cursor (Ctrl+Shift+-)', () => split(state, view), 'nb-split'),
            tool('Merge ↓', 'Merge the cell with the one below it', () => merge(state, view), 'nb-merge'),
            tool('Tags', 'Edit the tags of the cell (its metadata)', () => { view.tagEdit = !view.tagEdit; paintTags(state, view); }, 'nb-tag-btn'),
            tool('▲', 'Move up', () => move(state, view, -1)),
            tool('▼', 'Move down', () => move(state, view, 1)),
            tool(cell.cell_type === 'code' ? 'To text' : 'To code', 'Change the kind of cell', () => {
                changeType(state.nb, cell, cell.cell_type === 'code' ? 'markdown' : 'code');
                flush(state); render(state);
            }),
            tool('✕', 'Delete this cell', () => remove(state, view)),
        );
        box.append(gutter, main, tools);
        box.onmousedown = () => select(state, indexOf(state, view), false);

        paintCount(view);
        paintOutputs(view);
        paintTags(state, view);
        if (cell.cell_type === 'markdown') paintMarkdown(state, view);
        else placeholder(state, view);
        // the editor of a cell is made when the cell comes into view: a notebook of hundreds of cells opens at once
        box.__create = () => { if (!view.editor && !view.rendered) createEditor(state, view); state.observer.unobserve(box); };
        state.observer.observe(box);
        return box;
    }

    function placeholder(state, view) {
        const pre = el('pre', 'nb-placeholder', sourceOf(view.cell));
        view.inputEl.replaceChildren(pre);
        pre.onclick = () => createEditor(state, view, true);
    }

    function createEditor(state, view, focus = false) {
        // a callback of a cell that has since been rebuilt (the notebook was re-rendered) must not make an editor nobody owns
        if (view.editor || !state.views.includes(view)) return;
        const { cell } = view;
        const language = cell.cell_type === 'code' ? 'python' : 'markdown';
        view.inputEl.replaceChildren();
        const host = el('div', 'nb-monaco');
        view.inputEl.append(host);
        const editor = monaco.editor.create(host, {
            value: sourceOf(cell), language, theme: 'vs-dark', minimap: { enabled: false }, lineNumbers: 'off', folding: false, glyphMargin: false,
            scrollBeyondLastLine: false, scrollbar: { vertical: 'hidden', horizontal: 'auto', alwaysConsumeMouseWheel: false, handleMouseWheel: false },
            overviewRulerLanes: 0, renderLineHighlight: 'none', lineDecorationsWidth: 6, padding: { top: 4, bottom: 4 }, fontSize: 14,
            automaticLayout: true, wordWrap: 'off', contextmenu: true, quickSuggestions: { other: true, comments: false, strings: false },
        });
        view.editor = editor;
        const fit = () => { host.style.height = `${Math.max(34, editor.getContentHeight())}px`; editor.layout(); };
        editor.onDidContentSizeChange(fit);
        fit();
        editor.onDidChangeModelContent(() => { setSource(cell, editor.getValue()); pushSoon(state); virtualSoon(state); });
        cellModels.set(editor.getModel(), { state, view });
        applyMarkers(state, view);
        editor.onDidFocusEditorText(() => select(state, indexOf(state, view), false));
        const K = monaco.KeyCode, M = monaco.KeyMod;
        editor.addCommand(M.Shift | K.Enter, () => runSelected(state, true));
        editor.addCommand(M.CtrlCmd | K.Enter, () => runSelected(state, false));
        editor.addCommand(M.Alt | K.Enter, () => { runSelected(state, false); addCell(state, 'code'); });
        editor.addCommand(M.CtrlCmd | K.KeyS, () => { flush(state); void save(); });
        editor.addCommand(M.CtrlCmd | M.Shift | K.Minus, () => split(state, view));
        // Shift+Alt+F with a selection lays out only the lines it touches, as in the code editor
        if (cell.cell_type === 'code')
            editor.addCommand(M.Shift | M.Alt | K.KeyF, () => {
                const sel = editor.getSelection();
                void editor.getAction(sel && !sel.isEmpty() ? 'editor.action.formatSelection' : 'editor.action.formatDocument')?.run();
            });
        if (cell.cell_type === 'markdown') editor.addCommand(K.Escape, () => { view.rendered = true; paintMarkdown(state, view); });
        if (focus) editor.focus();
    }

    function paintMarkdown(state, view) {
        view.editor?.dispose();
        view.editor = null;
        view.rendered = true;
        const box = el('div', 'nb-md');
        const text = sourceOf(view.cell);
        if (text.trim() === '') box.append(el('span', 'nb-empty', 'Empty text cell: double-click to edit'));
        else renderMarkdown(box, text);
        box.ondblclick = () => { view.rendered = false; createEditor(state, view, true); };
        view.inputEl.replaceChildren(box);
    }

    function paintCount(view) {
        if (view.cell.cell_type !== 'code') { view.countEl.textContent = ''; return; }
        view.countEl.textContent = view.running ? '[*]' : view.queued ? '[…]' : `[${view.cell.execution_count ?? ' '}]`;
    }

    function paintOutputs(view) {
        if (view.cell.cell_type !== 'code') { view.outEl.replaceChildren(); return; }
        view.outEl.replaceChildren(...(view.cell.outputs ?? []).map(renderOutput));
        view.el?.classList.toggle('has-error', (view.cell.outputs ?? []).some((o) => o.output_type === 'error'));
    }

    /** The tags of a cell as chips (a click on x removes one) and, while editing, a field that takes more (Enter; commas or spaces separate). */
    function paintTags(state, view) {
        const tags = tagsOf(view.cell);
        view.tagsEl.style.display = tags.length || view.tagEdit ? 'flex' : 'none';
        view.tagsEl.replaceChildren();
        for (const tag of tags) {
            const chip = el('span', 'nb-tag');
            chip.append(el('span', 'nb-tag-name', tag));
            const x = el('button', 'nb-tag-x', '×');
            x.title = `Remove the tag ${tag}`;
            x.onclick = (e) => { e.stopPropagation(); setTags(view.cell, tags.filter((t) => t !== tag)); paintTags(state, view); flush(state); };
            chip.append(x);
            view.tagsEl.append(chip);
        }
        if (view.tagEdit) {
            const input = el('input', 'nb-tag-input');
            input.placeholder = 'tag, another tag';
            input.onkeydown = (e) => {
                e.stopPropagation();
                if (e.key === 'Enter') {
                    const more = parseTags(input.value);
                    view.tagEdit = false;
                    if (more.length) setTags(view.cell, [...new Set([...tags, ...more])]);
                    paintTags(state, view);
                    flush(state);
                } else if (e.key === 'Escape') { view.tagEdit = false; paintTags(state, view); }
            };
            view.tagsEl.append(input);
            input.focus();
        }
    }

    /** Merges a cell with the one below it. */
    function merge(state, view) {
        const i = indexOf(state, view);
        if (i + 1 >= state.views.length) { toast('There is no cell below to merge with'); return; }
        if (state.views[i].running || state.views[i + 1].running) { toast('A cell that is running cannot be merged'); return; }
        mergeCells(state.nb, i);
        state.selected = i;
        flush(state); render(state);
    }

    /** Splits a cell where the cursor is in its editor. */
    function split(state, view) {
        const i = indexOf(state, view);
        const pos = view.editor?.getPosition();
        if (!pos) { toast('Click in the cell where it should be cut (a text cell: double-click it first)'); return; }
        const offset = offsetOfPosition(sourceOf(view.cell), pos.lineNumber, pos.column);
        if (view.running) { toast('A cell that is running cannot be split'); return; }
        splitCell(state.nb, i, offset);
        state.selected = i + 1;
        flush(state); render(state);
        select(state, i + 1);
        const next = state.views[i + 1];
        if (next) { next.rendered = false; createEditor(state, next, true); next.editor?.setPosition({ lineNumber: 1, column: 1 }); }
    }

    /** Run Section: a heading cell and every cell after it up to the next heading of the same or a higher level. */
    function runSection(state, view) {
        const range = sectionOf(state.nb, indexOf(state, view));
        if (!range) { toast('This text cell does not start with a heading'); return; }
        enqueue(state, state.views.slice(range.start, range.end));
    }

    function addCell(state, type) {
        const at = state.selected + 1;
        insertCell(state.nb, at, type);
        flush(state);
        render(state);
        select(state, at);
        const v = state.views[at];
        if (v) { v.rendered = false; createEditor(state, v, true); }
    }

    function move(state, view, by) {
        const i = indexOf(state, view);
        const to = moveCell(state.nb, i, by);
        if (to === i) return;
        state.selected = to;
        flush(state); render(state);
        select(state, to);
    }

    function remove(state, view) {
        const i = indexOf(state, view);
        deleteCell(state.nb, i);
        if (state.nb.cells.length === 0) insertCell(state.nb, 0, 'code');
        state.selected = Math.min(i, state.nb.cells.length - 1);
        flush(state); render(state);
    }

    // ---- the kernel

    function connect(state) {
        if (state.ws && state.ws.readyState <= 1) return;
        const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
        const ws = new WebSocket(`${scheme}://${location.host}/nb`);
        state.ws = ws;
        setStatus(state, 'starting', 'starting…');
        ws.onmessage = (ev) => onKernel(state, JSON.parse(ev.data));
        ws.onclose = () => { if (state.ws === ws) { setStatus(state, 'dead', 'disconnected'); failQueue(state); } };
    }

    const send = (state, msg) => { if (state.ws?.readyState === WebSocket.OPEN) state.ws.send(JSON.stringify(msg)); };

    function whenOpen(state, then) {
        connect(state);
        if (state.ws.readyState === WebSocket.OPEN) then();
        else state.ws.addEventListener('open', then, { once: true });
    }

    function onKernel(state, m) {
        switch (m.type) {
            case 'ready': setStatus(state, 'idle', `Python ${m.python} idle`); state.python = m.python; pump(state); return;
            case 'kernelError': setStatus(state, 'dead', 'not started'); toast(m.message, true); failQueue(state); return;
            case 'kernelExited': setStatus(state, 'dead', 'stopped'); failQueue(state); return;
            case 'kernelLog': return;
        }
        if (m.type === 'variables') { state.vars = m.vars ?? []; paintVars(state); return; }
        const view = state.ids.get(m.id);
        if (!view) return;
        if (m.type === 'done') {
            view.cell.execution_count = m.execution_count ?? view.cell.execution_count;
            view.running = false;
            state.busy = false;
            paintCount(view);
            setStatus(state, 'idle', `Python ${state.python ?? ''} idle`);
            state.ids.delete(m.id);
            pushSoon(state);
            pump(state);
            return;
        }
        applyKernelMessage(view.cell, m);
        paintOutputs(view);
        pushSoon(state);
    }

    function failQueue(state) {
        for (const v of state.views) { v.running = false; v.queued = false; paintCount(v); }
        state.queue = [];
        state.busy = false;
        state.ids.clear();
    }

    function enqueue(state, views) {
        const codes = views.filter((v) => v.cell.cell_type === 'code');
        for (const v of views) if (v.cell.cell_type === 'markdown') paintMarkdown(state, v);
        if (!codes.length) return;
        for (const v of codes) { if (!state.queue.includes(v)) { v.queued = true; paintCount(v); state.queue.push(v); } }
        whenOpen(state, () => pump(state));
    }

    /** Sends the next queued cell when the kernel is up and free. */
    function pump(state) {
        if (state.busy || state.status === 'starting' || !state.queue.length) return;
        if (state.ws?.readyState !== WebSocket.OPEN) return;
        const view = state.queue.shift();
        view.queued = false;
        view.running = true;
        view.cell.outputs = [];
        view.cell.execution_count = null;
        paintOutputs(view);
        paintCount(view);
        const id = `c${state.nextId++}`;
        state.ids.set(id, view);
        state.busy = true;
        setStatus(state, 'busy', 'busy');
        flush(state);
        send(state, { type: 'execute', id, code: sourceOf(view.cell) });
    }

    function runSelected(state, advance) {
        const view = state.views[state.selected];
        if (!view) return;
        if (view.cell.cell_type === 'markdown') paintMarkdown(state, view);
        else enqueue(state, [view]);
        if (advance) {
            if (state.selected + 1 >= state.views.length) { addCell(state, 'code'); return; }
            select(state, state.selected + 1);
            const next = state.views[state.selected];
            if (next && next.cell.cell_type === 'code') createEditor(state, next, true);
        }
    }

    function runAll(state) { enqueue(state, [...state.views]); }

    function interrupt(state) {
        for (const v of state.queue) { v.queued = false; paintCount(v); }
        state.queue = [];
        send(state, { type: 'interrupt' });
    }

    function restart(state) {
        failQueue(state);
        state.vars = [];
        paintVars(state);
        if (state.ws?.readyState === WebSocket.OPEN) { setStatus(state, 'starting', 'restarting…'); send(state, { type: 'restart' }); }
        else connect(state);
    }

    // ---- what the language server says about a cell

    /** Shows the findings the server made for a cell on the cell's editor (they wait here until the editor exists). */
    function applyMarkers(state, view) {
        const model = view.editor?.getModel();
        if (model) monaco.editor.setModelMarkers(model, 'pymagic', state.diags.get(indexOf(state, view)) ?? []);
    }

    /**
     * Where a position of a cell editor is in the notebook's virtual file: { uri, position, start, fromServer } or null for
     * an editor that is not a notebook cell. The file is brought up to date first, so an answer never lags behind typing.
     */
    function targetOf(model, position) {
        const entry = cellModels.get(model);
        if (!entry) return null;
        const { state, view } = entry;
        if (state.vtimer) syncVirtual(state);
        const cellIndex = indexOf(state, view);
        const line = virtualLine(state.vdoc, cellIndex, position.lineNumber - 1);
        if (line === null) return null;
        const start = line - (position.lineNumber - 1);
        return {
            uri: state.vuri,
            position: { line, character: position.column - 1 },
            start,
            /** A range the server sent about this notebook's file, as a range inside the cell. */
            fromServer: (r) => ({ start: { line: r.start.line - start, character: r.start.character }, end: { line: r.end.line - start, character: r.end.character } }),
            /** The cell's own text the server has for it (the cell's editor value as the virtual file holds it). */
            sync: () => syncVirtual(state),
        };
    }

    /** A location in a notebook's virtual file as a place in a cell: { model, range } or null. */
    function locate(uri, range) {
        for (const state of states.values()) {
            if (state.vuri !== uri) continue;
            const at = cellOfLine(state.vdoc, range.start.line);
            if (!at) return null;
            const view = state.views[at.index];
            if (!view) return null;
            if (!view.editor) createEditor(state, view);
            const delta = range.start.line - at.line;
            return { model: view.editor.getModel(), range: { start: { line: range.start.line - delta, character: range.start.character }, end: { line: range.end.line - delta, character: range.end.character } } };
        }
        return null;
    }

    /**
     * Findings of the language server for a notebook's virtual file become markers on the cells. `toMarker(d, start)` makes a
     * Monaco marker from a finding whose lines are `start` lines too far down. Returns false when the file is not a notebook's.
     */
    function diagnostics(p, toMarker) {
        const state = [...states.values()].find((s) => s.vuri === p.uri);
        if (!state) return false;
        const byCell = new Map();
        for (const d of p.diagnostics) {
            const at = cellOfLine(state.vdoc, d.range.start.line);
            if (!at) continue;
            const view = state.views[at.index];
            if (!view) continue;
            if (NOISE.has(d.code) || NOISE_MESSAGE.test(d.message)) continue;
            // the last expression of a cell is its shown value, not a statement without effect
            if (d.code === 'statement-effect' && d.range.end.line - (d.range.start.line - at.line) >= lastCodeLine(view.cell)) continue;
            const start = d.range.start.line - at.line;
            if (!byCell.has(at.index)) byCell.set(at.index, []);
            byCell.get(at.index).push(toMarker(d, start));
        }
        state.diags = byCell;
        state.views.forEach((v) => applyMarkers(state, v));
        return true;
    }

    /** The index of the last line of a cell that is not blank. */
    function lastCodeLine(cell) {
        const lines = sourceOf(cell).split('\n');
        let i = lines.length - 1;
        while (i > 0 && lines[i].trim() === '') i--;
        return i;
    }

    /**
     * The text of a code cell laid out by the formatter (its `%magic` and `!command` lines are set aside and put back by the engine); with a
     * range (0-based lines and characters), only the lines it touches. Throws with a reason when the cell cannot be formatted (`%%bash`, errors).
     */
    async function formattedText(state, text, range) {
        if (text.trim() === '') return text;
        const r = await lsp.request('pymagic/formatText', { textDocument: { uri: state.vuri }, text, range, options: { pymagicEngine: formatEngine() } });
        return r.text;
    }

    /** Formats every code cell that can be; the ones that cannot (syntax errors, `%%bash` cells) are counted, not touched. */
    async function formatAll(state, quiet = false) {
        let changed = 0, skipped = 0;
        for (const view of state.views) {
            if (view.cell.cell_type !== 'code') continue;
            const text = view.editor ? view.editor.getValue() : sourceOf(view.cell);
            try {
                const out = await formattedText(state, text);
                if (out === text) continue;
                if (!view.editor) createEditor(state, view);
                const ed = view.editor;
                ed.pushUndoStop();
                ed.executeEdits('format', [{ range: ed.getModel().getFullModelRange(), text: out }]);
                ed.pushUndoStop();
                changed++;
            } catch { skipped++; }
        }
        if (!quiet || skipped)
            toast(changed || skipped ? `Formatted ${changed} cell${changed === 1 ? '' : 's'}${skipped ? `, ${skipped} left as they are` : ''}` : 'Every cell is already formatted');
    }

    return {
        /** Whether a Monaco model is the editor of a notebook cell. */
        isCell(model) { return cellModels.has(model); },
        /** The text of a cell's editor laid out by the formatter (throws when it cannot be). */
        formatted(model, range) {
            const entry = cellModels.get(model);
            return entry ? formattedText(entry.state, model.getValue(), range) : Promise.resolve(model.getValue());
        },
        /** Formats every code cell of the notebook on screen (format on save): quietly, unless some cells could not be. */
        async formatShown() {
            if (shown?.nb) await formatAll(shown, true);
        },
        /** Shows the notebook of a file (an open editor model) in place of the code editor. */
        show(rel, model) {
            for (const s of states.values()) s.dom.style.display = 'none';
            const state = states.get(rel) ?? open(rel, model);
            state.dom.style.display = 'flex';
            shown = state;
            showRoot(true);
            if (state.nb) state.views.forEach((v) => v.editor?.layout());
        },
        hide() { shown = null; showRoot(false); },
        /** The file was closed or deleted. */
        close(rel) {
            const s = states.get(rel);
            if (!s) return;
            flush(s);
            if (s.vopen) lsp.notify('textDocument/didClose', { textDocument: { uri: s.vuri } });
            dispose(s);
            states.delete(rel);
            if (shown === s) this.hide();
        },
        /** The headings of the notebook that has this model, as the symbols of the Structure window (null when the model is not a notebook's). */
        symbolsOf(model) {
            const s = [...states.values()].find((x) => x.model === model);
            return s?.nb ? notebookSymbols(s.nb) : null;
        },
        /** The cell selected in the notebook that has this model (-1 when there is none). */
        selectedOf(model) { const s = [...states.values()].find((x) => x.model === model); return s?.nb ? s.selected : -1; },
        /** Selects a cell of the notebook that has this model and scrolls to it (the Structure window does this). */
        revealCell(model, cell) {
            const s = [...states.values()].find((x) => x.model === model);
            if (s?.nb) select(s, cell);
        },
        /** For tests and the page: runs the section a heading cell opens. */
        runSection(rel, cell) { const s = states.get(rel); if (s?.views[cell]) runSection(s, s.views[cell]); },
        isShown: () => shown !== null,
        targetOf,
        locate,
        diagnostics,
        flushAll() { for (const s of states.values()) flush(s); },
        /** For tests and the page: the number of the last execution, the state of a notebook. */
        stateOf: (rel) => states.get(rel),
        lastCount: (rel) => (states.get(rel)?.nb ? lastExecutionCount(states.get(rel).nb) : 0),
        relOf,
    };
}

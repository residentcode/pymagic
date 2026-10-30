// Moving around a file and reading it, as PyCharm's editor does: the next and previous method, the matching bracket, the start
// and end of a block, the last edits and the places recently visited, breadcrumbs, method separators, lens mode on the
// scroll bar, context info, the type of an expression, a name's documentation on the web, and a RegExp checker.
import * as monaco from 'monaco-editor';
import { settings, onSettings } from './settings';
import { openMenu } from './menu';
import { toast } from './toast';
import { definitions, nextDefinition, pathTo, separatorLines, blockOf, addPlace, regexAt } from './editornav-model.js';
const $ = (id) => document.getElementById(id);
export function setupEditorNav(deps) {
    const { editor, lsp } = deps;
    const K = monaco.KeyCode, M = monaco.KeyMod;
    const toPos = (p) => ({ line: p.lineNumber - 1, character: p.column - 1 });
    const lineTextOf = (model) => (l) => (l >= 1 && l <= model.getLineCount() ? model.getLineContent(l) : '');
    // ---------------------------------------------------------------- the definitions of a file (classes, methods, functions)
    const defsCache = new WeakMap();   // model -> { version, defs }
    async function defsOf(model) {
        const cached = defsCache.get(model);
        if (cached && cached.version === model.getVersionId())
            return cached.defs;
        const uri = deps.uriOf(model);
        let symbols = [];
        try {
            symbols = uri ? (await lsp.request('textDocument/documentSymbol', { textDocument: { uri } })) ?? [] : [];
        }
        catch { /* the server is busy */ }
        const defs = definitions(symbols);
        defsCache.set(model, { version: model.getVersionId(), defs });
        return defs;
    }
    const go = (ed, line, column = null) => {
        const model = ed.getModel();
        const col = column ?? (model.getLineFirstNonWhitespaceColumn(line) || 1);
        ed.setPosition({ lineNumber: line, column: col });
        ed.revealLineInCenterIfOutsideViewport(line);
        ed.focus();
    };
    // ---------------------------------------------------------------- next / previous method, block start / end
    async function stepDefinition(ed, dir) {
        const model = ed.getModel(), pos = ed.getPosition();
        if (!model || !pos)
            return;
        const line = nextDefinition(await defsOf(model), pos.lineNumber, dir);
        if (line)
            go(ed, line);
    }
    function blockEdge(ed, toEnd) {
        const model = ed.getModel(), pos = ed.getPosition();
        if (!model || !pos)
            return;
        const block = blockOf(lineTextOf(model), pos.lineNumber, model.getLineCount());
        if (!block)
            return;
        if (toEnd)
            go(ed, block.end, model.getLineMaxColumn(block.end));
        else
            go(ed, block.start);
    }
    // ---------------------------------------------------------------- the last edits and the places visited
    let edits = [], visits = [], editBack = 0;
    function noteEdit(ed, e) {
        const rel = deps.relOf(ed.getModel());
        const change = e.changes[0];
        if (!rel || !change || e.isFlush)
            return;
        edits = addPlace(edits, { rel, line: change.range.startLineNumber, column: change.range.startColumn });
        editBack = 0;
    }
    let visitTimer = 0;
    function noteVisit(ed) {
        clearTimeout(visitTimer);
        visitTimer = window.setTimeout(() => {
            const rel = deps.relOf(ed.getModel()), pos = ed.getPosition();
            if (rel && pos)
                visits = addPlace(visits, { rel, line: pos.lineNumber, column: pos.column });
        }, 1000);
    }
    /** Last Edit Location (Ctrl+Shift+Backspace): each press goes one edit further back. */
    async function lastEdit() {
        const place = edits[editBack];
        if (!place) {
            toast('No edit to go back to.');
            return;
        }
        editBack = Math.min(editBack + 1, edits.length - 1);
        await deps.openFile(place.rel, new monaco.Range(place.line, place.column, place.line, place.column));
    }
    /** Recent Locations (Ctrl+Shift+E): the places changed and the places visited, newest first, each with its line. */
    async function recentLocations() {
        const row = async (p) => {
            const model = await deps.modelFor(p.rel);
            const text = model && p.line <= model.getLineCount() ? model.getLineContent(p.line).trim() : '';
            return { label: `${p.rel}:${p.line}   ${text.slice(0, 60)}`, run: () => void deps.openFile(p.rel, new monaco.Range(p.line, p.column, p.line, p.column)) };
        };
        const changed = await Promise.all(edits.slice(0, 15).map(row));
        const seen = await Promise.all(visits.filter((v) => !edits.some((e) => e.rel === v.rel && Math.abs(e.line - v.line) <= 3)).slice(0, 15).map(row));
        const items = [
            { label: 'Recently changed', disabled: true }, ...(changed.length ? changed : [{ label: '(nothing yet)', disabled: true }]),
            '-', { label: 'Recently visited', disabled: true }, ...(seen.length ? seen : [{ label: '(nothing yet)', disabled: true }]),
        ];
        const box = editor.getContainerDomNode().getBoundingClientRect();
        openMenu({ x: box.left + box.width / 2 - 200, y: box.top + 40 }, items);
    }
    // ---------------------------------------------------------------- small popups near the cursor
    let pop = null;
    function closePop() { pop?.remove(); pop = null; }
    document.addEventListener('mousedown', (e) => { if (pop && !pop.contains(e.target))
        closePop(); }, true);
    document.addEventListener('keydown', (e) => { if (pop && e.key === 'Escape')
        closePop(); }, true);
    function popAt(ed, line, column, content) {
        closePop();
        const p = ed.getScrolledVisiblePosition({ lineNumber: line, column }) ?? { left: 40, top: 0, height: 18 };
        const box = ed.getContainerDomNode().getBoundingClientRect();
        pop = document.createElement('div');
        pop.className = 'info-pop';
        pop.append(content);
        pop.style.left = `${Math.max(8, box.left + p.left)}px`;
        pop.style.top = `${box.top + p.top + p.height + 4}px`;
        document.body.appendChild(pop);
        const r = pop.getBoundingClientRect();
        if (r.bottom > window.innerHeight)
            pop.style.top = `${box.top + p.top - r.height - 4}px`;
    }
    /** Context Info (Alt+Q): the headers of the classes and functions the cursor is in, a click going to one. */
    async function contextInfo(ed) {
        const model = ed.getModel(), pos = ed.getPosition();
        if (!model || !pos)
            return;
        const path = pathTo(await defsOf(model), pos.lineNumber);
        if (!path.length) {
            toast('The cursor is not inside a class or a function.');
            return;
        }
        const list = document.createElement('div');
        for (const d of path) {
            const row = document.createElement('div');
            row.className = 'info-row code';
            row.textContent = model.getLineContent(d.nameLine).trimEnd();
            row.title = `Line ${d.nameLine}`;
            row.onclick = () => { closePop(); go(ed, d.nameLine); };
            list.appendChild(row);
        }
        const top = ed.getVisibleRanges()[0]?.startLineNumber ?? pos.lineNumber;
        popAt(ed, top, 1, list);
    }
    /** Type Info (Ctrl+Shift+P): the inferred type of the selection, or of the expression at the cursor. */
    async function expressionType(ed) {
        const model = ed.getModel(), sel = ed.getSelection();
        const uri = model && deps.uriOf(model);
        if (!uri || !sel)
            return;
        let info = null;
        try {
            info = await lsp.request('pymagic/expressionType', { textDocument: { uri }, range: { start: toPos(sel.getStartPosition()), end: toPos(sel.getEndPosition()) } });
        }
        catch { /* none */ }
        if (!info) {
            toast('No expression here.');
            return;
        }
        const box = document.createElement('div');
        box.className = 'info-row code';
        box.textContent = `${info.text}: ${info.type}`;
        popAt(ed, info.range.start.line + 1, info.range.start.character + 1, box);
    }
    /** External Documentation (Shift+F1): the page of the name at the cursor on docs.python.org or PyPI, in the browser. */
    async function externalDoc(ed) {
        const model = ed.getModel(), pos = ed.getPosition();
        const uri = model && deps.uriOf(model);
        if (!uri || !pos)
            return;
        let doc = null;
        try {
            doc = await lsp.request('pymagic/externalDoc', { textDocument: { uri }, position: toPos(pos) });
        }
        catch { /* none */ }
        if (!doc) {
            toast('No documentation on the web for this name (it is the project\'s own, or not known).');
            return;
        }
        window.open(doc.url, '_blank', 'noopener');
        toast(`Documentation of ${doc.name} opened in the browser`);
    }
    // ---------------------------------------------------------------- the RegExp checker
    monaco.editor.registerCommand('pymagic.checkRegex', (_, pattern, flags) => regexDialog(pattern, flags));
    monaco.languages.registerCodeActionProvider('python', {
        provideCodeActions(model, range) {
            const found = regexAt(model.getLineContent(range.startLineNumber), range.startColumn);
            if (!found)
                return { actions: [], dispose() { } };
            return { actions: [{ title: 'Check RegExp', kind: 'refactor.pymagic.regex', command: { id: 'pymagic.checkRegex', title: 'Check RegExp', arguments: [found.pattern, found.flags] } }], dispose() { } };
        },
    });
    function regexDialog(pattern, flags = []) {
        const overlay = document.createElement('div');
        overlay.id = 'sigdialog';
        overlay.innerHTML = '<div class="box regex-box"><h2>Check RegExp</h2>'
            + '<div class="namerow"><label>Pattern</label><input class="rx-pattern" spellcheck="false"></div>'
            + '<div class="rx-flags"></div>'
            + '<div class="namerow"><label>Sample</label><textarea class="rx-text" rows="5" spellcheck="false" placeholder="Text to try the pattern on"></textarea></div>'
            + '<div class="rx-result"></div><div class="dim">Tried by Python\'s re, as the program will run it.</div>'
            + '<div class="buttons"><span style="flex:1"></span><button class="primary rx-close">Close</button></div></div>';
        const patternBox = overlay.querySelector('.rx-pattern'), textBox = overlay.querySelector('.rx-text'), result = overlay.querySelector('.rx-result');
        patternBox.value = pattern;
        const flagBoxes = {};
        for (const f of ['IGNORECASE', 'MULTILINE', 'DOTALL', 'VERBOSE', 'ASCII']) {
            const label = document.createElement('label');
            const box = document.createElement('input');
            box.type = 'checkbox';
            box.checked = flags.includes(f);
            flagBoxes[f] = box;
            label.append(box, ' re.' + f);
            overlay.querySelector('.rx-flags').appendChild(label);
        }
        let timer = 0, asked = 0;
        const check = () => {
            clearTimeout(timer);
            timer = window.setTimeout(async () => {
                const ask = ++asked;
                let r;
                try {
                    r = await (await fetch('/api/regex', { method: 'POST', body: JSON.stringify({ pattern: patternBox.value, text: textBox.value, flags: Object.keys(flagBoxes).filter((f) => flagBoxes[f].checked) }) })).json();
                }
                catch {
                    r = { error: 'The engine did not answer.' };
                }
                if (ask !== asked)
                    return;
                result.textContent = '';
                result.className = 'rx-result' + (r.error ? ' e' : '');
                if (r.error) {
                    result.textContent = r.error + (r.pos !== undefined && r.pos !== null ? ` (at position ${r.pos})` : '');
                    return;
                }
                // the sample with its matches marked, and the groups of each match
                const marked = document.createElement('pre');
                let at = 0;
                const text = textBox.value;
                for (const m of r.matches) {
                    marked.append(text.slice(at, m.start));
                    const mark = document.createElement('mark');
                    mark.textContent = text.slice(m.start, m.end) || '∅';
                    marked.append(mark);
                    at = Math.max(at, m.end);
                }
                marked.append(text.slice(at));
                const summary = document.createElement('div');
                summary.className = r.matches.length ? 'ok' : 'dim';
                summary.textContent = r.matches.length ? `${r.matches.length} match${r.matches.length === 1 ? '' : 'es'}` : (text ? 'No match' : 'The pattern is valid');
                result.append(summary, marked);
                for (const [i, m] of r.matches.slice(0, 5).entries()) {
                    if (!r.groups)
                        break;
                    const row = document.createElement('div');
                    row.className = 'dim';
                    row.textContent = `match ${i + 1}: ` + m.groups.map(([s, e], g) => `${r.names[g + 1] ?? g + 1} = ${s < 0 ? 'None' : JSON.stringify(text.slice(s, e))}`).join(', ');
                    result.appendChild(row);
                }
            }, 250);
        };
        patternBox.oninput = check;
        textBox.oninput = check;
        for (const box of Object.values(flagBoxes))
            box.onchange = check;
        const close = () => { overlay.remove(); editor.focus(); };
        overlay.querySelector('.rx-close').onclick = close;
        overlay.onmousedown = (e) => { if (e.target === overlay)
            close(); };
        overlay.onkeydown = (e) => { if (e.key === 'Escape')
            close(); };
        document.body.appendChild(overlay);
        textBox.focus();
        check();
    }
    // ---------------------------------------------------------------- breadcrumbs
    const crumbs = document.createElement('div');
    crumbs.id = 'breadcrumbs';
    const area = $('editor-area');
    area.insertBefore(crumbs, $('editor'));
    let crumbTimer = 0;
    function scheduleCrumbs() {
        clearTimeout(crumbTimer);
        crumbTimer = window.setTimeout(() => void paintCrumbs(), 150);
    }
    async function paintCrumbs() {
        const model = editor.getModel(), pos = editor.getPosition();
        crumbs.hidden = !settings().breadcrumbs || !model;
        if (crumbs.hidden)
            return;
        const rel = deps.relOf(model) ?? '';
        const path = rel.endsWith('.py') && pos ? pathTo(await defsOf(model), pos.lineNumber) : [];
        crumbs.textContent = '';
        const parts = rel.split('/');
        parts.forEach((p, i) => {
            const span = document.createElement('span');
            span.className = 'crumb' + (i === parts.length - 1 ? ' file' : ' dir');
            span.textContent = p;
            if (i === parts.length - 1)
                span.onclick = () => void deps.reveal?.(rel);
            crumbs.append(span);
        });
        for (const d of path) {
            const span = document.createElement('span');
            span.className = 'crumb def' + (d.kind === 5 ? ' cls' : ' fn');
            span.textContent = d.name;
            span.title = `Line ${d.nameLine}; right-click for the definitions beside it`;
            span.onclick = () => go(editor, d.nameLine);
            // the definitions at the same level (the methods of the same class), to jump to one
            span.oncontextmenu = async (e) => {
                e.preventDefault();
                const all = await defsOf(model);
                const parent = pathTo(all, d.start).filter((p) => p.depth === d.depth - 1).pop();
                const siblings = all.filter((o) => o.depth === d.depth && (!parent || parent.start < o.start && o.end <= parent.end));
                openMenu({ x: e.clientX, y: e.clientY }, siblings.map((o) => ({ label: o.name, checked: o === d, run: () => go(editor, o.nameLine) })));
            };
            crumbs.append(span);
        }
    }
    // ---------------------------------------------------------------- method separators
    const separators = new Map();   // editor -> decorations collection
    async function paintSeparators(ed) {
        const model = ed.getModel();
        const col = separators.get(ed) ?? ed.createDecorationsCollection();
        separators.set(ed, col);
        if (!settings().methodSeparators || !model || !(deps.relOf(model) ?? '').endsWith('.py')) {
            col.clear();
            return;
        }
        const lines = separatorLines(await defsOf(model));
        col.set(lines.map((l) => ({ range: new monaco.Range(l, 1, l, 1), options: { isWholeLine: true, className: 'method-sep' } })));
    }
    // ---------------------------------------------------------------- lens mode: hovering the scroll bar shows that part of the file
    let lens = null, lensEditor = null;
    function hideLens() {
        if (lens)
            lens.style.display = 'none';
    }
    function lensOver(ed, e) {
        const model = ed.getModel();
        if (!settings().lensMode || !model)
            return hideLens();
        const box = ed.getContainerDomNode().getBoundingClientRect();
        const layout = ed.getLayoutInfo();
        const strip = layout.verticalScrollbarWidth + 4;
        if (e.clientX < box.right - strip || e.clientY < box.top || e.clientY > box.bottom || model.getLineCount() * ed.getOption(monaco.editor.EditorOption.lineHeight) <= box.height)
            return hideLens();
        const line = Math.max(1, Math.min(model.getLineCount(), Math.round((e.clientY - box.top) / box.height * model.getLineCount())));
        if (!lens) {
            lens = document.createElement('div');
            lens.className = 'lens-pop';
            document.body.appendChild(lens);
            lensEditor = monaco.editor.create(lens, {
                readOnly: true, minimap: { enabled: false }, lineNumbers: 'on', scrollBeyondLastLine: false, glyphMargin: false, folding: false,
                renderLineHighlight: 'none', scrollbar: { vertical: 'hidden', horizontal: 'hidden' }, overviewRulerLanes: 0, codeLens: false,
                fontSize: ed.getOption(monaco.editor.EditorOption.fontSize), domReadOnly: true, contextmenu: false,
            });
        }
        if (lensEditor.getModel() !== model)
            lensEditor.setModel(model);
        lens.style.display = 'block';
        lens.style.width = `${Math.min(640, box.width - 60)}px`;
        lens.style.left = `${box.right - strip - Math.min(640, box.width - 60) - 8}px`;
        lens.style.top = `${Math.max(box.top, Math.min(e.clientY - 80, box.bottom - 170))}px`;
        lensEditor.layout();
        lensEditor.revealLineInCenter(line);
        lensEditor.setSelection(new monaco.Range(line, 1, line, 1));
        // the first time, the popup is laid out only after this: reveal again once it is
        requestAnimationFrame(() => lensEditor?.revealLineInCenter(line));
    }
    // ---------------------------------------------------------------- Open in a new window (Shift+F4): the tab moves to its own window
    async function detach() {
        const rel = deps.currentFile();
        if (!rel)
            return;
        await deps.saveAll();
        window.open(`${location.origin}/?open=${encodeURIComponent(rel)}`, '_blank');
        void deps.closeTab(rel);
    }
    // ---------------------------------------------------------------- the keys, in the main editor and the sides of a split
    function attach(ed) {
        ed.addCommand(M.CtrlCmd | M.Shift | K.DownArrow, () => void stepDefinition(ed, 1));
        ed.addCommand(M.CtrlCmd | M.Shift | K.UpArrow, () => void stepDefinition(ed, -1));
        ed.addCommand(M.CtrlCmd | M.Shift | K.KeyM, () => ed.trigger('nav', 'editor.action.jumpToBracket', {}));
        ed.addCommand(M.Alt | K.BracketLeft, () => blockEdge(ed, false));
        ed.addCommand(M.Alt | K.BracketRight, () => blockEdge(ed, true));
        ed.addCommand(M.CtrlCmd | M.Shift | K.Backspace, () => void lastEdit());
        ed.addCommand(M.CtrlCmd | M.Shift | K.KeyE, () => void recentLocations());
        ed.addCommand(M.Alt | K.KeyQ, () => void contextInfo(ed));
        ed.addCommand(M.CtrlCmd | M.Shift | K.KeyP, () => void expressionType(ed));
        ed.addCommand(M.Shift | K.F1, () => void externalDoc(ed));
        ed.addCommand(M.Shift | K.F4, () => void detach());
        ed.onDidChangeModelContent((e) => noteEdit(ed, e));
        ed.onDidChangeCursorPosition(() => { noteVisit(ed); if (ed === editor)
            scheduleCrumbs(); });
        ed.onDidChangeModel(() => { void paintSeparators(ed); if (ed === editor)
            scheduleCrumbs(); });
        let sepTimer = 0;
        ed.onDidChangeModelContent(() => { clearTimeout(sepTimer); sepTimer = window.setTimeout(() => void paintSeparators(ed), 800); });
        const node = ed.getContainerDomNode();
        node.addEventListener('mousemove', (e) => lensOver(ed, e));
        node.addEventListener('mouseleave', hideLens);
        node.addEventListener('mousedown', hideLens);
        void paintSeparators(ed);
    }
    attach(editor);
    onSettings(() => { scheduleCrumbs(); for (const ed of separators.keys())
        void paintSeparators(ed); });
    scheduleCrumbs();
    return {
        attach,
        /** The commands, for the main menu. */
        commands: {
            nextMethod: () => void stepDefinition(editor, 1), previousMethod: () => void stepDefinition(editor, -1),
            matchingBrace: () => editor.trigger('nav', 'editor.action.jumpToBracket', {}),
            blockStart: () => blockEdge(editor, false), blockEnd: () => blockEdge(editor, true),
            lastEdit: () => void lastEdit(), recentLocations: () => void recentLocations(),
            contextInfo: () => void contextInfo(editor), expressionType: () => void expressionType(editor),
            externalDoc: () => void externalDoc(editor), detach: () => void detach(),
            checkRegex: () => {
                const model = editor.getModel(), pos = editor.getPosition();
                const found = model && pos ? regexAt(model.getLineContent(pos.lineNumber), pos.column) : null;
                regexDialog(found?.pattern ?? '', found?.flags ?? []);
            },
        },
    };
}

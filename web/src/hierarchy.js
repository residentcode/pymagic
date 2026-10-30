// Navigation by structure: the Hierarchy tab (call hierarchy Ctrl+Alt+H, type hierarchy Ctrl+H), go to implementations
// (Ctrl+F12 and the context menu), and the gutter marks for methods that override or are overridden and classes with subclasses.
import * as monaco from 'monaco-editor';
import { showTab } from './panel';
import { toast } from './toast';
const $ = (id) => document.getElementById(id);
const toRange = (r) => ({
    startLineNumber: r.start.line + 1, startColumn: r.start.character + 1, endLineNumber: r.end.line + 1, endColumn: r.end.character + 1,
});
const KIND_MARK = { 2: 'M', 5: 'C', 6: 'm', 12: 'ƒ' };
const MODE_LABELS = { callers: 'Callers', callees: 'Callees', subtypes: 'Subtypes', supertypes: 'Supertypes' };
export function setupHierarchy(deps) {
    const { editor, lsp } = deps;
    const tree = $('hier-tree');
    const title = $('hier-title');
    const modeBar = $('hier-modes');
    let root = null;
    let family = 'call';
    let mode = 'callers';
    let selectedRow = null;
    // ---------------------------------------------------------------- navigation
    async function goTo(uri, range, from) {
        const rel = deps.relOfUri(uri);
        if (!rel) {
            toast('That place is outside the project.', true);
            return;
        }
        if (from && from !== editor && deps.openIn) {
            await deps.openIn(from, rel, toRange(range));
            return;
        }
        await deps.openFile(rel, toRange(range));
    }
    function flush(model, uri) {
        lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
    }
    // ---------------------------------------------------------------- the tree
    async function children(item) {
        switch (mode) {
            case 'callers': return ((await lsp.request('callHierarchy/incomingCalls', { item })) ?? []).map((c) => ({ item: c.from, ranges: c.fromRanges }));
            case 'callees': return ((await lsp.request('callHierarchy/outgoingCalls', { item })) ?? []).map((c) => ({ item: c.to, ranges: c.fromRanges }));
            case 'subtypes': return ((await lsp.request('typeHierarchy/subtypes', { item })) ?? []).map((i) => ({ item: i, ranges: [] }));
            case 'supertypes': return ((await lsp.request('typeHierarchy/supertypes', { item })) ?? []).map((i) => ({ item: i, ranges: [] }));
        }
    }
    function emptyText() {
        return { callers: 'Nothing calls this.', callees: 'This calls nothing that can be followed.', subtypes: 'No subclasses.', supertypes: 'No base classes in the project.' }[mode];
    }
    function row(edge, depth, parent) {
        const holder = document.createElement('div');
        const line = document.createElement('div');
        line.className = 'hn';
        line.style.paddingLeft = `${8 + depth * 16}px`;
        const caret = document.createElement('span');
        caret.className = 'caret';
        caret.textContent = '▸';
        const kind = document.createElement('span');
        kind.className = 'kind';
        kind.textContent = KIND_MARK[edge.item.kind] ?? '·';
        const name = document.createElement('span');
        name.textContent = edge.item.name;
        line.append(caret, kind, name);
        if (edge.item.detail) {
            const d = document.createElement('span');
            d.className = 'detail';
            d.textContent = edge.item.detail;
            line.append(d);
        }
        if (edge.ranges.length > 1) {
            const c = document.createElement('span');
            c.className = 'count';
            c.textContent = `×${edge.ranges.length}`;
            line.append(c);
        }
        const rel = deps.relOfUri(edge.item.uri);
        if (rel) {
            const f = document.createElement('span');
            f.className = 'detail';
            f.textContent = rel;
            line.append(f);
        }
        const kids = document.createElement('div');
        kids.style.display = 'none';
        holder.append(line, kids);
        let loaded = false, open = false;
        async function toggle() {
            open = !open;
            caret.textContent = open ? '▾' : '▸';
            kids.style.display = open ? '' : 'none';
            if (open && !loaded) {
                loaded = true;
                kids.replaceChildren(textRow('Loading…', depth + 1));
                try {
                    const list = await children(edge.item);
                    kids.replaceChildren(...(list.length ? list.map((e) => row(e, depth + 1, edge.item)) : [textRow(emptyText(), depth + 1)]));
                }
                catch (e) {
                    kids.replaceChildren(textRow(String(e.message ?? e), depth + 1));
                }
            }
        }
        caret.onclick = (ev) => { ev.stopPropagation(); void toggle(); };
        line.ondblclick = () => void toggle();
        line.onclick = () => {
            selectedRow?.classList.remove('sel');
            line.classList.add('sel');
            selectedRow = line;
            // a caller is shown where it calls; everything else where it is defined
            if (mode === 'callers' && edge.ranges.length > 0)
                void goTo(edge.item.uri, edge.ranges[0]);
            else
                void goTo(edge.item.uri, edge.item.selectionRange);
        };
        holder.toggle = toggle;
        return holder;
    }
    function textRow(text, depth) {
        const d = document.createElement('div');
        d.className = 'hn-empty';
        d.style.paddingLeft = `${8 + depth * 16 + 24}px`;
        d.textContent = text;
        return d;
    }
    function renderModes() {
        const modes = family === 'call' ? ['callers', 'callees'] : ['subtypes', 'supertypes'];
        modeBar.replaceChildren(...modes.map((m) => {
            const b = document.createElement('button');
            b.textContent = MODE_LABELS[m];
            b.className = m === mode ? 'active' : '';
            b.onclick = () => { mode = m; show(); };
            return b;
        }));
    }
    function show() {
        if (!root)
            return;
        title.textContent = `${family === 'call' ? 'Call' : 'Type'} hierarchy of ${root.name}`;
        renderModes();
        tree.replaceChildren();
        selectedRow = null;
        const top = row({ item: root, ranges: [] }, 0, null);
        tree.append(top);
        void top.toggle();
        showTab('hier');
    }
    async function open(kind) {
        const model = editor.getModel(), pos = editor.getPosition();
        const uri = model && deps.uriOf(model);
        if (!model || !pos || !uri)
            return;
        flush(model, uri);
        const items = (await lsp.request(kind === 'call' ? 'textDocument/prepareCallHierarchy' : 'textDocument/prepareTypeHierarchy', {
            textDocument: { uri }, position: { line: pos.lineNumber - 1, character: pos.column - 1 },
        })) ?? [];
        if (items.length === 0) {
            toast(kind === 'call' ? 'Put the cursor on a function, method or class.' : 'Put the cursor on a class.', true);
            return;
        }
        family = kind;
        mode = kind === 'call' ? 'callers' : 'subtypes';
        root = items[0];
        show();
    }
    $('t-hier').onclick = () => showTab('hier');
    editor.addAction({
        id: 'pymagic.callHierarchy', label: 'Call Hierarchy', contextMenuGroupId: 'navigation', contextMenuOrder: 1.6,
        keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Alt | monaco.KeyCode.KeyH],
        run: () => void open('call'),
    });
    editor.addAction({
        id: 'pymagic.typeHierarchy', label: 'Type Hierarchy', contextMenuGroupId: 'navigation', contextMenuOrder: 1.7,
        keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyH],
        run: () => void open('type'),
    });
    // Go to Super Method (Ctrl+U): the method or class the definition around the cursor overrides or implements, from the same marks as the gutter
    editor.addAction({
        id: 'pymagic.superMethod', label: 'Go to Super Method', contextMenuGroupId: 'navigation', contextMenuOrder: 1.8,
        keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyU],
        precondition: 'editorLangId == python',
        run: () => void superMethod(),
    });
    async function superMethod() {
        const model = editor.getModel(), pos = editor.getPosition();
        const uri = model && deps.uriOf(model);
        if (!uri || !pos)
            return;
        flush(model, uri);
        let list = [];
        try {
            list = (await lsp.request('pymagic/inheritance', { textDocument: { uri } })) ?? [];
        }
        catch { /* none */ }
        // the nearest definition above the cursor (or on its line) that has something above it
        const up = list.filter((m) => isUp(m) && m.line <= pos.lineNumber - 1).sort((a, b) => b.line - a.line)[0];
        if (!up) {
            toast('Nothing above this: it overrides no method and has no base class in the project.', true);
            return;
        }
        const box = editor.getDomNode()?.getBoundingClientRect(), at = editor.getScrolledVisiblePosition(pos);
        pick(editor, up.targets.map((loc) => ({ label: up.label, loc })), (box?.left ?? 0) + (at?.left ?? 0), (box?.top ?? 0) + (at?.top ?? 0) + (at?.height ?? 0));
    }
    // ---------------------------------------------------------------- go to implementations
    monaco.languages.registerImplementationProvider('python', {
        async provideImplementation(model, position) {
            const uri = deps.uriOf(model);
            if (!uri)
                return null;
            flush(model, uri);
            const locs = (await lsp.request('textDocument/implementation', {
                textDocument: { uri }, position: { line: position.lineNumber - 1, character: position.column - 1 },
            })) ?? [];
            const result = [];
            for (const l of locs) {
                const target = await deps.modelForUri(l.uri);
                if (target)
                    result.push({ uri: target.uri, range: toRange(l.range) });
            }
            return result;
        },
    });
    // ---------------------------------------------------------------- gutter marks
    const Lane = monaco.editor.GlyphMarginLane.Right; // the left lane belongs to breakpoints
    let menu = null;
    const isUp = (m) => m.kind === 'overrides' || m.kind === 'implements';
    /** The marks in one editor (the first one, and each side of a split); returns what schedules a refresh. */
    function gutter(editor) {
        const decorations = editor.createDecorationsCollection();
        let byLine = new Map();
        let timer;
        async function refreshMarks() {
            const model = editor.getModel();
            const uri = model && deps.uriOf(model);
            if (!model || !uri || !model.uri.path.endsWith('.py')) {
                decorations.clear();
                byLine = new Map();
                return;
            }
            let list;
            try {
                list = (await lsp.request('pymagic/inheritance', { textDocument: { uri } })) ?? [];
            }
            catch {
                return;
            }
            if (editor.getModel() !== model)
                return;
            byLine = new Map();
            for (const m of list)
                byLine.set(m.line, [...(byLine.get(m.line) ?? []), m]);
            decorations.set([...byLine].map(([line, marks]) => ({
                range: new monaco.Range(line + 1, 1, line + 1, 1),
                options: {
                    glyphMargin: { position: Lane },
                    glyphMarginClassName: 'inh-glyph ' + (marks.some(isUp) && marks.some((m) => !isUp(m)) ? 'inh-both' : marks.some(isUp) ? 'inh-up' : 'inh-down'),
                    glyphMarginHoverMessage: { value: marks.map((m) => m.label).join('  \n') },
                },
            })));
        }
        function schedule(delay) {
            clearTimeout(timer);
            timer = window.setTimeout(() => void refreshMarks(), delay);
        }
        editor.onDidChangeModel(() => schedule(400));
        editor.onDidChangeModelContent(() => schedule(1200)); // after the edit has reached the server, and the typing has paused
        const connected = () => schedule(200);
        window.addEventListener('pymagic:connected', connected);
        editor.onDidDispose(() => { clearTimeout(timer); window.removeEventListener('pymagic:connected', connected); });
        editor.onMouseDown((e) => {
            if (e.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN || !e.target.position)
                return;
            if (e.target.detail?.glyphMarginLane !== Lane)
                return;
            const marks = byLine.get(e.target.position.lineNumber - 1);
            if (!marks)
                return;
            pick(editor, marks.flatMap((m) => m.targets.map((loc) => ({ label: m.targets.length > 1 ? `${m.label} · ${m.kind}` : m.label, loc }))), e.event.browserEvent.clientX, e.event.browserEvent.clientY);
        });
        return schedule;
    }
    function closeMenu() { menu?.remove(); menu = null; }
    function pick(editor, entries, x, y) {
        closeMenu();
        if (entries.length === 1) {
            void goTo(entries[0].loc.uri, entries[0].loc.range, editor);
            return;
        }
        menu = document.createElement('div');
        menu.className = 'inh-menu';
        for (const e of entries) {
            const item = document.createElement('div');
            const rel = deps.relOfUri(e.loc.uri) ?? e.loc.uri;
            item.textContent = e.label;
            const where = document.createElement('span');
            where.className = 'where';
            where.textContent = `${rel}:${e.loc.range.start.line + 1}`;
            item.append(where);
            item.onclick = () => { closeMenu(); void goTo(e.loc.uri, e.loc.range, editor); };
            menu.append(item);
        }
        menu.style.left = `${Math.min(x, window.innerWidth - 320)}px`;
        menu.style.top = `${Math.min(y, window.innerHeight - 200)}px`;
        document.body.append(menu);
    }
    // any press outside the menu closes it; this runs before the editor sees the same press, so a press that opens the menu cannot close it
    document.addEventListener('mousedown', (ev) => { if (menu && !menu.contains(ev.target))
        closeMenu(); }, true);
    document.addEventListener('keydown', (ev) => { if (ev.key === 'Escape')
        closeMenu(); }, true);
    const scheduleMain = gutter(editor);
    return {
        refreshMarks: () => scheduleMain(0),
        /** The same marks in another editor (a side of the split). */
        attach: (ed) => { gutter(ed)(400); },
    };
}

// The Structure view of the sidebar: the classes, functions, methods and variables of the open file as a tree, from the language server. A click goes
// there; the one the cursor is in is marked while "follow the cursor" is on.
import { markdownSymbols } from './markdown-doc.js';
const $ = (id) => document.getElementById(id);

// LSP symbol kinds -> a letter for the badge and the class that colours it
const KIND = {
    2: ['M', 'mod'], 5: ['C', 'class'], 6: ['m', 'fn'], 7: ['p', 'var'], 8: ['f', 'var'], 9: ['m', 'fn'], 10: ['E', 'class'], 11: ['I', 'class'],
    12: ['ƒ', 'fn'], 13: ['v', 'var'], 14: ['K', 'const'], 22: ['e', 'const'], 23: ['S', 'class'], 26: ['T', 'class'],
    // the Django view (the logical structure)
    'dj-model': ['M', 'class'], 'dj-serializer': ['S', 'class'], 'dj-view': ['V', 'fn'], 'dj-admin': ['A', 'class'], 'dj-form': ['F', 'class'],
    'md-heading': ['#', 'class'],
    'dj-signal': ['⚡', 'fn'], 'dj-field': ['f', 'var'], 'dj-relation': ['→', 'var'], 'dj-nested': ['S', 'class'], 'dj-endpoint': ['⇄', 'const'],
};

/** A node of the logical structure as a symbol of the tree (a line for its range, its kind marked as Django's). */
export function fromLogical(n) {
    const at = { line: n.line, character: 0 };
    return { name: n.name, detail: n.detail ?? '', kind: `dj-${n.kind}`, range: { start: at, end: at }, selectionRange: { start: at, end: at },
        children: (n.children ?? []).map(fromLogical), node: n };
}

/** The symbols from the outermost to the innermost that contain the line. */
export function pathAt(symbols, line) {
    const path = [];
    let level = symbols;
    while (level) {
        const hit = level.find((s) => line >= s.range.start.line && line <= s.range.end.line);
        if (!hit) break;
        path.push(hit);
        level = hit.children;
    }
    return path;
}

export function setupStructure({ editor, lsp, uriOf, visible, actions, notebook = () => null }) {
    const root = $('structure');
    let symbols = [];
    let django = false;   // the logical structure of the file, as Django sees it
    let ticket = 0;
    let follow = true;
    let sorted = false;
    const folded = new Set();   // names of symbols whose children are hidden (by their path)
    let timer = 0;

    const keyOf = (path) => path.map((s) => s.name).join('/');

    async function refresh() {
        const model = editor.getModel();
        // a notebook's structure is the headings of its text cells (the cell is what a click goes to)
        const nbSymbols = model && notebook()?.symbolsOf(model);
        if (nbSymbols) { symbols = nbSymbols; ticket++; draw(); return; }
        // a Markdown file's structure is its headings
        if (model && model.getLanguageId() === 'markdown') { symbols = markdownSymbols(model.getValue()); ticket++; draw(); return; }
        const uri = model && uriOf(model);
        if (!uri) { symbols = []; draw(); return; }
        const mine = ++ticket;
        let found;
        try {
            found = django
                ? ((await lsp.request('pymagic/logicalStructure', { textDocument: { uri } })) ?? []).map(fromLogical)
                : (await lsp.request('textDocument/documentSymbol', { textDocument: { uri } })) ?? [];
        }
        catch { found = []; }
        if (mine !== ticket) return;
        symbols = found;
        draw();
    }

    function draw() {
        root.replaceChildren();
        if (symbols.length === 0) {
            const none = document.createElement('div');
            none.className = 'side-empty';
            none.textContent = !editor.getModel() ? 'Open a file to see its classes and functions.' : django ? 'No models, serializers, views, admins, forms or signal handlers in this file.' : 'Nothing to show in this file.';
            root.append(none);
            return;
        }
        const list = document.createElement('ul');
        list.className = 'tree';
        const add = (level, depth, parentPath) => {
            const rows = sorted ? [...level].sort((a, b) => a.name.localeCompare(b.name)) : level;
            for (const s of rows) {
                const path = [...parentPath, s];
                const li = document.createElement('li');
                li.style.paddingLeft = `${6 + depth * 14}px`;
                li.dataset.key = keyOf(path);
                li.dataset.line = String(s.selectionRange.start.line);
                const caret = document.createElement('span');
                caret.className = 'twisty';
                const hasKids = !!s.children?.length;
                if (hasKids) {
                    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                    svg.setAttribute('class', 'i');
                    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
                    use.setAttribute('href', folded.has(keyOf(path)) ? '#i-chev-right' : '#i-chev-down');
                    svg.append(use);
                    caret.append(svg);
                    caret.onclick = (e) => {
                        e.stopPropagation();
                        const k = keyOf(path);
                        if (folded.has(k)) folded.delete(k); else folded.add(k);
                        draw();
                    };
                }
                const [letter, cls] = KIND[s.kind] ?? ['·', 'var'];
                const badge = document.createElement('span');
                badge.className = `sym sym-${cls}`;
                badge.textContent = letter;
                const name = document.createElement('span');
                name.className = 'fname';
                name.textContent = s.name;
                li.append(caret, badge, name);
                if (s.detail) {
                    const d = document.createElement('span');
                    d.className = 'sym-detail';
                    d.textContent = s.detail;
                    li.append(d);
                }
                // a model without an admin or a serializer: make one
                if (s.node?.kind === 'model' && actions) {
                    const fields = s.node.children.filter((c) => c.kind === 'field' || c.kind === 'relation').map((c) => c.name);
                    for (const [missing, label, run] of [[!s.node.hasAdmin, '+ admin', actions.createAdmin], [!s.node.hasSerializer, '+ serializer', actions.createSerializer]]) {
                        if (!missing) continue;
                        const b = document.createElement('button');
                        b.className = 'st-action';
                        b.textContent = label;
                        b.title = label === '+ admin' ? `A ModelAdmin for ${s.name} in admin.py beside this file` : `A ModelSerializer for ${s.name} in serializers.py beside this file`;
                        b.onclick = (e) => { e.stopPropagation(); void run(editor.getModel(), s.name, fields).then(() => later(300)); };
                        li.append(b);
                    }
                }
                li.onclick = () => {
                    if (s.cell !== undefined) { notebook()?.revealCell(editor.getModel(), s.cell); return; }
                    const p = s.selectionRange.start;
                    editor.setPosition({ lineNumber: p.line + 1, column: p.character + 1 });
                    editor.revealLineInCenter(p.line + 1);
                    editor.focus();
                };
                list.append(li);
                if (hasKids && !folded.has(keyOf(path))) add(s.children, depth + 1, path);
            }
        };
        add(symbols, 0, []);
        root.append(list);
        mark();
    }

    function mark() {
        for (const li of root.querySelectorAll('li.here')) li.classList.remove('here');
        if (!follow) return;
        const model = editor.getModel();
        const cell = model ? notebook()?.selectedOf(model) ?? -1 : -1;
        const pos = cell >= 0 ? { lineNumber: cell + 1 } : editor.getPosition();   // in a notebook the unit is the selected cell
        if (!pos) return;
        const path = pathAt(symbols, pos.lineNumber - 1);
        const li = path.length ? [...root.querySelectorAll('li')].find((x) => x.dataset.key === keyOf(path)) : null;
        li?.classList.add('here');
        li?.scrollIntoView({ block: 'nearest' });
    }

    const later = (ms) => { window.clearTimeout(timer); timer = window.setTimeout(() => { if (visible()) void refresh(); }, ms); };
    editor.onDidChangeModel(() => later(100));
    editor.onDidChangeModelContent(() => later(800));
    editor.onDidChangeCursorPosition(() => { if (visible()) mark(); });
    window.addEventListener('pymagic:nbselect', () => { if (visible()) mark(); });

    $('st-follow').onclick = () => { follow = !follow; $('st-follow').classList.toggle('on', follow); mark(); };
    $('st-sort').onclick = () => { sorted = !sorted; $('st-sort').classList.toggle('on', sorted); draw(); };
    $('st-django').onclick = () => { django = !django; $('st-django').classList.toggle('on', django); void refresh(); };
    $('st-collapse').onclick = () => {
        const walk = (level, parent) => { for (const s of level) { const p = [...parent, s]; if (s.children?.length) { folded.add(keyOf(p)); walk(s.children, p); } } };
        folded.clear();
        walk(symbols, []);
        draw();
    };
    $('st-follow').classList.add('on');
    return { refresh, show: () => void refresh() };
}

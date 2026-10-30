// Bookmarks: a line marked to come back to (Ctrl+F2 marks or unmarks the cursor's line, Alt+F2 and Shift+Alt+F2 go to the next and the previous), a mark
// in the glyph margin, and a list in the sidebar. They are kept in the project, in .pymagic/bookmarks.json, and follow their line while the file is edited.
const $ = (id) => document.getElementById(id);
const FILE = '.pymagic/bookmarks.json';

export function setupBookmarks({ monaco, editor, openFile, relOf, revealInTree }) {
    let list = [];                    // { rel, line (1-based; 0 for a bookmark on a whole file or folder), text, dir? }
    const marks = new Map();          // bookmark -> the id of its decoration in the model that is open
    let shownModel = null;
    let timer = 0;

    const sorted = () => [...list].sort((a, b) => a.rel.localeCompare(b.rel) || a.line - b.line);

    function persist() {
        window.clearTimeout(timer);
        timer = window.setTimeout(() => {
            fetch('/api/file?path=' + encodeURIComponent(FILE), { method: 'PUT', body: JSON.stringify(list.map(({ rel, line, text, dir }) => ({ rel, line, text, ...(dir ? { dir: true } : {}) }))) }).catch(() => { /* not kept this time */ });
        }, 600);
    }

    /** Puts the marks of the open file in the margin, and takes the others' decorations away. */
    function paintModel() {
        const model = editor.getModel();
        if (shownModel && shownModel !== model && !shownModel.isDisposed()) shownModel.deltaDecorations([...marks.values()], []);
        marks.clear();
        shownModel = model;
        const rel = model && relOf(model);
        if (!model || !rel) return;
        for (const b of list.filter((x) => x.rel === rel && x.line >= 1 && x.line <= model.getLineCount())) {
            const [id] = model.deltaDecorations([], [{
                range: new monaco.Range(b.line, 1, b.line, 1),
                options: { isWholeLine: false, glyphMarginClassName: 'bookmark-glyph', glyphMarginHoverMessage: { value: 'Bookmark' }, stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges },
            }]);
            marks.set(b, id);
        }
    }

    /** The lines the marks are on now (they move with the text) go back into the list. */
    function follow() {
        const model = editor.getModel();
        if (!model) return;
        let moved = false;
        for (const [b, id] of marks) {
            const r = model.getDecorationRange(id);
            if (!r) continue;
            if (r.startLineNumber !== b.line) { b.line = r.startLineNumber; moved = true; }
            const text = model.getLineContent(b.line).trim();
            if (text !== b.text) { b.text = text; moved = true; }
        }
        if (moved) { persist(); draw(); }
    }

    function draw() {
        const box = $('bookmarks');
        box.replaceChildren();
        if (list.length === 0) {
            const none = document.createElement('div');
            none.className = 'side-empty';
            none.textContent = 'No bookmarks. Put the cursor on a line and press Ctrl+F2.';
            box.append(none);
            return;
        }
        const ul = document.createElement('ul');
        ul.className = 'tree';
        for (const b of sorted()) {
            const li = document.createElement('li');
            li.title = b.line ? `${b.rel}:${b.line}` : b.rel;
            const where = document.createElement('span');
            where.className = 'fname';
            where.textContent = b.line ? `${b.rel.split('/').pop()}:${b.line}` : `${b.rel.split('/').pop()}${b.dir ? '/' : ''}`;
            const text = document.createElement('span');
            text.className = 'sym-detail';
            text.textContent = b.text;
            const drop = document.createElement('span');
            drop.className = 'drop';
            drop.title = 'Remove the bookmark';
            drop.textContent = '✕';
            drop.onclick = (e) => { e.stopPropagation(); remove(b); };
            li.append(where, text, drop);
            li.onclick = () => void go(b);
            ul.append(li);
        }
        box.append(ul);
    }

    function remove(b) {
        const model = editor.getModel();
        const id = marks.get(b);
        if (id && model) model.deltaDecorations([id], []);
        marks.delete(b);
        list = list.filter((x) => x !== b);
        persist();
        draw();
    }

    async function go(b) {
        if (b.dir) revealInTree?.(b.rel);
        else if (b.line === 0) await openFile(b.rel);
        else await openFile(b.rel, new monaco.Range(b.line, 1, b.line, 1));
    }

    /** A bookmark on a whole file or folder (the project tree's and the tabs' menus), or off when it has one. */
    function toggleFile(rel, isDir) {
        const here = list.find((b) => b.rel === rel && b.line === 0);
        if (here) { remove(here); return; }
        list.push({ rel, line: 0, text: isDir ? 'folder' : 'file', ...(isDir ? { dir: true } : {}) });
        persist();
        draw();
    }

    function toggle() {
        const model = editor.getModel(), pos = editor.getPosition();
        const rel = model && relOf(model);
        if (!model || !pos || !rel) return;
        follow();
        const here = list.find((b) => b.rel === rel && b.line === pos.lineNumber);
        if (here) { remove(here); return; }
        list.push({ rel, line: pos.lineNumber, text: model.getLineContent(pos.lineNumber).trim() });
        paintModel();
        persist();
        draw();
    }

    function step(direction) {
        const all = sorted();
        if (all.length === 0) return;
        const model = editor.getModel(), pos = editor.getPosition();
        const rel = (model && relOf(model)) ?? '';
        follow();
        const at = pos ? all.findIndex((b) => b.rel === rel && b.line === pos.lineNumber) : -1;
        let next;
        if (at >= 0) next = all[(at + direction + all.length) % all.length];
        else {
            // between two: the first after (or the last before) the cursor, in the order of the list
            const after = all.filter((b) => b.rel > rel || (b.rel === rel && b.line > (pos?.lineNumber ?? 0)));
            const before = all.filter((b) => b.rel < rel || (b.rel === rel && b.line < (pos?.lineNumber ?? 0)));
            next = direction > 0 ? after[0] ?? all[0] : before[before.length - 1] ?? all[all.length - 1];
        }
        void go(next);
    }

    editor.onDidChangeModel(paintModel);
    editor.onDidChangeModelContent(() => { window.clearTimeout(follow.timer); follow.timer = window.setTimeout(follow, 400); });
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.F2, toggle);
    editor.addCommand(monaco.KeyMod.Alt | monaco.KeyCode.F2, () => step(1));
    editor.addCommand(monaco.KeyMod.Shift | monaco.KeyMod.Alt | monaco.KeyCode.F2, () => step(-1));
    $('bm-clear').onclick = () => { for (const b of [...list]) remove(b); };

    fetch('/api/file?path=' + encodeURIComponent(FILE)).then((r) => (r.ok ? r.json() : [])).then((stored) => {
        if (Array.isArray(stored)) list = stored.filter((b) => b && typeof b.rel === 'string' && Number.isInteger(b.line)).map((b) => ({ rel: b.rel, line: b.line, text: String(b.text ?? ''), ...(b.dir === true ? { dir: true } : {}) }));
        paintModel();
        draw();
    }).catch(() => { draw(); });
    draw();
    return { toggle, toggleFile, hasFile: (rel) => list.some((b) => b.rel === rel && b.line === 0), next: () => step(1), previous: () => step(-1), count: () => list.length };
}

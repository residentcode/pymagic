// The changes of one file, one by one: which changelist each is in, moving some to another list, and putting some on the shelf.
// A "change" is a block of adjacent added and removed lines; the engine names it by a key made of its text (GET /api/git/changes).

const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; };

/** A line for the list: where the change is, and how many lines it adds (or, when it only takes lines away, how many). */
export function placeText(c) {
    if (!c.added) return `line ${c.oldLine} (${c.removed} line${c.removed === 1 ? '' : 's'} removed)`;
    return `line ${c.newLine}${c.added > 1 ? ` (${c.added} lines)` : ''}`;
}

/** The keys of the ticked rows, in the order of the list. */
export function checkedKeys(rows, ticked) {
    return rows.filter((r) => ticked.has(r.key)).map((r) => r.key);
}

/** Opens the window of a file's changes. `lists` is the state of /api/git/changelists; `api`, `post`, `toast` and `refresh` are the Git tab's own. */
export function openChanges({ path, lists, api, post, toast, refresh }) {
    const shade = el('div', 'ask-shade git-parts-shade');
    const box = el('div', 'ask-box git-parts');
    const ticked = new Set();
    let rows = [];
    const title = el('div', 'ask-title', `Changes of ${path}`);
    const hint = el('div', 'ask-hint', 'A change is a block of adjacent lines you added, removed or edited. Tick some to put them in another changelist, or on the shelf; the rest of the file stays as it is.');
    const list = el('div', 'git-parts-list');
    const error = el('div', 'db-form-error');
    const target = el('select');
    target.id = 'git-parts-target';
    for (const l of lists?.lists ?? []) target.append(new Option(l.name, l.name));
    const move = el('button', 'primary', 'Move ticked to this list');
    move.id = 'git-parts-move';
    const shelve = el('button', '', 'Shelve ticked…');
    shelve.id = 'git-parts-shelve';
    const close = el('button', '', 'Close');
    const row = el('div', 'ask-buttons');
    row.append(target, move, shelve, close);
    box.append(title, hint, list, error, row);
    shade.append(box);
    document.body.append(shade);
    const done = () => shade.remove();
    close.onclick = done;
    shade.onmousedown = (e) => { if (e.target === shade) done(); };
    box.onkeydown = (e) => { if (e.key === 'Escape') done(); e.stopPropagation(); };

    function paint() {
        list.replaceChildren();
        if (!rows.length) { list.append(el('div', 'git-parts-none', 'This file has no changes that can be moved one by one (it is new, deleted or binary: move or shelve it whole).')); return; }
        for (const c of rows) {
            const line = el('label', 'git-parts-row');
            line.dataset.key = c.key;
            const box1 = el('input');
            box1.type = 'checkbox';
            box1.checked = ticked.has(c.key);
            box1.onchange = () => { if (box1.checked) ticked.add(c.key); else ticked.delete(c.key); };
            line.append(box1, el('span', 'git-parts-where', placeText(c)), el('span', 'git-parts-preview', c.preview || '(blank lines)'), el('span', 'git-parts-list-name', c.list));
            list.append(line);
        }
    }

    async function load() {
        try { rows = await api('/api/git/changes?path=' + encodeURIComponent(path)); }
        catch (e) { rows = []; error.textContent = String(e.message ?? e); }
        for (const k of [...ticked]) if (!rows.some((r) => r.key === k)) ticked.delete(k);
        paint();
    }

    move.onclick = async () => {
        error.textContent = '';
        const keys = checkedKeys(rows, ticked);
        if (!keys.length) { error.textContent = 'Tick the changes to move first.'; return; }
        try { await post('/api/git/changelist', { op: 'moveChanges', path, keys, name: target.value }); }
        catch (e) { error.textContent = String(e.message ?? e); return; }
        ticked.clear();
        await refresh();
        await load();
    };
    shelve.onclick = async () => {
        error.textContent = '';
        const keys = checkedKeys(rows, ticked);
        if (!keys.length) { error.textContent = 'Tick the changes to shelve first.'; return; }
        const name = window.prompt('Name of the shelved changes:', `${path} (part)`);
        if (name === null) return;
        try { await post('/api/git/shelf', { op: 'shelveChanges', path, keys, title: name.trim() || undefined }); }
        catch (e) { error.textContent = String(e.message ?? e); return; }
        toast(`${keys.length} change${keys.length === 1 ? '' : 's'} of ${path} put on the shelf`);
        await refresh();
        done();
    };
    void load();
    return { done, reload: load };
}

// The changes of one file, one by one: which changelist each is in, moving some to another list, and putting some on the shelf.
// A "change" is a block of adjacent added and removed lines; the engine names it by a key made of its text (GET /api/git/changes).
// ▸ shows a change's lines, each with its own box: single lines can be moved to another list too (a commit of that list takes just them).

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

/** The lists a change's lines are in, as shown beside it: "Fix", or "Changes + Fix" when its lines are split. */
export function listsText(c) {
    const names = [...new Set((c.lines ?? []).map((l) => l.list))];
    return names.length > 1 ? names.join(' + ') : c.list;
}

/**
 * What a move sends: the whole changes ticked, and for the other changes the lines ticked in them (`key@n` in `tickedLines`), as
 * `{ keys, lines: [{ key, lines: [n, ...] }] }`. A line of a change that is ticked whole goes with its change.
 */
export function movePlan(rows, ticked, tickedLines) {
    const keys = checkedKeys(rows, ticked);
    const lines = [];
    for (const r of rows) {
        if (ticked.has(r.key)) continue;
        const chosen = (r.lines ?? []).map((_, n) => n).filter((n) => tickedLines.has(`${r.key}@${n}`));
        if (chosen.length) lines.push({ key: r.key, lines: chosen });
    }
    return { keys, lines };
}

/** Opens the window of a file's changes. `lists` is the state of /api/git/changelists; `api`, `post`, `toast` and `refresh` are the Git tab's own. */
export function openChanges({ path, lists, api, post, toast, refresh }) {
    const shade = el('div', 'ask-shade git-parts-shade');
    const box = el('div', 'ask-box git-parts');
    const ticked = new Set();
    const tickedLines = new Set();   // `${key}@${n}`
    const open = new Set();          // the changes whose lines are shown
    let rows = [];
    const title = el('div', 'ask-title', `Changes of ${path}`);
    const hint = el('div', 'ask-hint', 'A change is a block of adjacent lines you added, removed or edited. Tick some to put them in another changelist, or on the shelf; the rest of the file stays as it is. ▸ shows the lines of a change: single lines can go to another list too.');
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

    const tickBox = (on, change) => {
        const b = el('input');
        b.type = 'checkbox';
        b.checked = on;
        b.onchange = () => change(b.checked);
        return b;
    };

    function paint() {
        list.replaceChildren();
        if (!rows.length) { list.append(el('div', 'git-parts-none', 'This file has no changes that can be moved one by one (it is new, deleted or binary: move or shelve it whole).')); return; }
        for (const c of rows) {
            const line = el('label', 'git-parts-row');
            line.dataset.key = c.key;
            const box1 = tickBox(ticked.has(c.key), (on) => { if (on) ticked.add(c.key); else ticked.delete(c.key); });
            const more = el('button', 'git-parts-open', open.has(c.key) ? '▾' : '▸');
            more.title = open.has(c.key) ? 'Hide the lines' : 'Show the lines, to move single ones';
            more.onclick = (e) => { e.preventDefault(); if (open.has(c.key)) open.delete(c.key); else open.add(c.key); paint(); };
            line.append(box1, more, el('span', 'git-parts-where', placeText(c)), el('span', 'git-parts-preview', c.preview || '(blank lines)'), el('span', 'git-parts-list-name', listsText(c)));
            list.append(line);
            if (!open.has(c.key)) continue;
            (c.lines ?? []).forEach((l, n) => {
                const id = `${c.key}@${n}`;
                const one = el('label', 'git-parts-line ' + (l.kind === '+' ? 'add' : 'del'));
                one.dataset.line = String(n);
                one.append(
                    tickBox(tickedLines.has(id), (on) => { if (on) tickedLines.add(id); else tickedLines.delete(id); }),
                    el('span', 'git-parts-text', `${l.kind} ${l.text}`),
                    el('span', 'git-parts-list-name', l.list));
                list.append(one);
            });
        }
    }

    async function load() {
        try { rows = await api('/api/git/changes?path=' + encodeURIComponent(path)); }
        catch (e) { rows = []; error.textContent = String(e.message ?? e); }
        for (const k of [...ticked]) if (!rows.some((r) => r.key === k)) ticked.delete(k);
        for (const k of [...open]) if (!rows.some((r) => r.key === k)) open.delete(k);
        tickedLines.clear();
        paint();
    }

    move.onclick = async () => {
        error.textContent = '';
        const plan = movePlan(rows, ticked, tickedLines);
        if (!plan.keys.length && !plan.lines.length) { error.textContent = 'Tick the changes or lines to move first.'; return; }
        try {
            if (plan.keys.length) await post('/api/git/changelist', { op: 'moveChanges', path, keys: plan.keys, name: target.value });
            for (const p of plan.lines) await post('/api/git/changelist', { op: 'moveLines', path, key: p.key, lines: p.lines, name: target.value });
        }
        catch (e) { error.textContent = String(e.message ?? e); return; }
        ticked.clear();
        await refresh();
        await load();
    };
    shelve.onclick = async () => {
        error.textContent = '';
        const keys = checkedKeys(rows, ticked);
        if (tickedLines.size && !keys.length) { error.textContent = 'The shelf takes whole changes: tick the change itself.'; return; }
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

// The window of an interactive rebase: the commits from a chosen one to the current one, oldest first, each with what to do to it (pick, reword,
// squash, fixup, drop), reordered by dragging a row's grip or with ↑ ↓. Start sends the plan to the engine (rebase-model.js keeps the rules).
import { ACTIONS, startingPlan, move, moveTo, setAction, setMessage, problem, steps, summary } from './rebase-model.js';
import { toast } from './toast';

const el = (tag, cls, text) => {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
};

/**
 * Opens the window for the commits after `base` (a revision, `hash^` to include that commit). `start(steps)` runs the plan and returns when done.
 * Resolves to false, without a window, when the engine says there is nothing to rebase there (the reason is shown).
 */
export async function openRebase({ base, label, start }) {
    let commits;
    try {
        const res = await fetch('/api/git/rebase/plan?baseHash=' + encodeURIComponent(base));
        const body = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(body.error ?? `${res.status} ${res.statusText}`);
        commits = body;
    } catch (e) { toast(String(e.message ?? e), true); return false; }
    if (commits.length === 0) { toast('There is nothing after that commit.', true); return false; }

    let rows = startingPlan(commits);
    const shade = el('div', 'settings-shade');
    const box = el('div', 'hist-diff rebase-box');
    const head = el('div', 'hist-diff-head');
    const title = el('span', 'hist-diff-title', `Interactive rebase from ${label}: ${commits.length} commit${commits.length === 1 ? '' : 's'}`);
    const go = el('button', 'primary', 'Start rebasing');
    const cancel = el('button', '', 'Cancel');
    head.append(title, go, cancel);
    const note = el('div', 'rebase-note', 'The commits are replayed from the top down. Squash and fixup join a commit into the one above it.');
    const list = el('div', 'rebase-list');
    const foot = el('div', 'rebase-foot');
    box.append(head, note, list, foot);
    shade.append(box);
    document.body.append(shade);

    const messages = new Map(); // the full message of a commit, read when it is first reworded

    async function messageOf(hash) {
        if (messages.has(hash)) return messages.get(hash);
        let text = commits.find((c) => c.hash === hash)?.subject ?? '';
        try {
            const d = await (await fetch('/api/git/commit?hash=' + encodeURIComponent(hash))).json();
            text = d.commit.subject + (d.body ? '\n\n' + d.body : '');
        } catch { /* the subject has to do */ }
        messages.set(hash, text);
        return text;
    }

    // dragging a row by its grip: where it is dropped (above or below the half of the row under the pointer) is shown by a line
    let dragging = -1;
    const clearMarks = () => list.querySelectorAll('.drop-before, .drop-after').forEach((x) => x.classList.remove('drop-before', 'drop-after'));
    const dropPlace = (line, i, e) => {
        const r = line.getBoundingClientRect();
        return e.clientY < r.top + r.height / 2 ? i : i + 1;
    };

    function draw() {
        list.replaceChildren();
        rows.forEach((row, i) => {
            const line = el('div', 'rebase-row' + (row.action === 'drop' ? ' dropped' : ''));
            const grip = el('span', 'rebase-grip', '⠿');
            grip.title = 'Drag to another place in the list';
            grip.draggable = true;
            grip.ondragstart = (e) => {
                dragging = i;
                line.classList.add('dragging');
                if (e.dataTransfer) { e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', row.shortHash); e.dataTransfer.setDragImage?.(line, 10, 10); }
            };
            grip.ondragend = () => { dragging = -1; line.classList.remove('dragging'); clearMarks(); };
            line.ondragover = (e) => {
                if (dragging < 0) return;
                e.preventDefault();
                if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
                const before = dropPlace(line, i, e) === i;
                clearMarks();
                line.classList.add(before ? 'drop-before' : 'drop-after');
            };
            line.ondragleave = () => line.classList.remove('drop-before', 'drop-after');
            line.ondrop = (e) => {
                if (dragging < 0) return;
                e.preventDefault();
                const from = dragging;
                dragging = -1;
                rows = moveTo(rows, from, dropPlace(line, i, e));
                draw();
            };
            const up = el('button', 'rebase-move', '↑');
            up.title = 'Replay it earlier';
            up.disabled = i === 0;
            up.onclick = () => { rows = move(rows, i, -1); draw(); };
            const down = el('button', 'rebase-move', '↓');
            down.title = 'Replay it later';
            down.disabled = i === rows.length - 1;
            down.onclick = () => { rows = move(rows, i, 1); draw(); };
            const pick = el('select', 'rebase-action');
            for (const [value, name, tip] of ACTIONS) {
                const o = el('option', '', name);
                o.value = value;
                o.title = tip;
                pick.append(o);
            }
            pick.value = row.action;
            pick.onchange = async () => {
                const action = pick.value;
                const index = rows.indexOf(row);
                rows = setAction(rows, index, action);
                if (action === 'reword') rows = setMessage(rows, index, rows[index].message ?? await messageOf(row.hash));
                draw();
            };
            line.append(grip, up, down, pick, el('span', 'rebase-hash', row.shortHash), el('span', 'rebase-subject', row.subject), el('span', 'rebase-author', row.author));
            list.append(line);
            if (row.action === 'reword') {
                const text = el('textarea', 'rebase-message');
                text.value = row.message ?? '';
                text.rows = Math.min(8, Math.max(2, text.value.split('\n').length));
                text.oninput = () => { rows = setMessage(rows, rows.findIndex((r) => r.hash === row.hash), text.value); refresh(); };
                list.append(text);
            }
        });
        refresh();
    }

    function refresh() {
        const why = problem(rows, commits);
        go.disabled = why !== null;
        foot.textContent = why ?? summary(rows, commits);
        foot.classList.toggle('bad', why !== null && !why.startsWith('Nothing is changed'));
    }

    const done = () => { document.removeEventListener('keydown', onKey, true); shade.remove(); };
    const onKey = (e) => { if (e.key === 'Escape' && e.target.tagName !== 'TEXTAREA') { e.preventDefault(); e.stopPropagation(); done(); } };
    document.addEventListener('keydown', onKey, true);
    cancel.onclick = done;
    go.onclick = async () => {
        go.disabled = true;
        const plan = steps(rows);
        done();
        await start(plan);
    };
    draw();
    return true;
}

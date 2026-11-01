// The shelf of the Git tab: changes put away as a patch in .pymagic/shelf/ (apart from the stash), listed here with their diff,
// brought back (unshelve) or deleted. Shelving starts from a changelist's Shelve button or from the Shelf header, which takes the
// ticked files. The Git tab (git.js) owns the status and the changelists; this module asks it to read them again.
import { toast } from './toast';
import { filesLabel, groupFiles, patchLines } from './changelist-model';

const $ = (id) => document.getElementById(id);

async function api(url, init) {
    const res = await fetch(url, init);
    const body = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(body.error ?? `${res.status} ${res.statusText}`);
    return body;
}
const post = (url, body) => api(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });

const el = (tag, cls, text) => {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
};
const button = (label, title, on) => {
    const b = el('button', '', label);
    if (title) b.title = title;
    b.onclick = (ev) => { ev.stopPropagation(); void on(b); };
    return b;
};

/** A box over the page with a title, a body and a close button. */
function dialog(title, wide) {
    const shade = el('div', 'ask-shade');
    const box = el('div', 'ask-box');
    box.style.width = wide ? '760px' : '560px';
    box.style.maxHeight = '75vh';
    box.style.overflow = 'auto';
    const close = () => shade.remove();
    box.append(el('div', 'ask-title', title));
    shade.append(box);
    shade.onmousedown = (e) => { if (e.target === shade) close(); };
    shade.onkeydown = (e) => { if (e.key === 'Escape') close(); };
    document.body.append(shade);
    const body = el('div');
    box.append(body);
    const done = el('div', 'ask-buttons');
    done.append(button('Close', '', close));
    box.append(done);
    return { close, body };
}

export function setupGitShelf({ git, deps }) {
    let sets = [];

    async function load() {
        if (!git.status().available) { sets = []; paint(); return; }
        try { sets = await api('/api/git/shelf'); } catch { sets = []; }
        paint();
    }

    function paint() {
        $('git-shelf-count').textContent = sets.length ? `(${sets.length})` : '';
        const box = $('git-shelf');
        box.replaceChildren();
        if (!sets.length) {
            const none = el('div', 'gs-row');
            none.append(el('span', 'dim', 'Nothing shelved.'));
            box.append(none);
        }
        for (const s of sets) {
            const row = el('div', 'gs-row');
            row.dataset.shelf = s.id;
            row.append(el('span', 'what', s.name), el('span', 'dim', `${filesLabel(s.files.length)} · ${s.time.replace('T', ' ').slice(0, 16)}`));
            row.append(
                button('Unshelve', 'Bring the changes back and remove the shelved set', () => unshelve(s, true)),
                button('Keep', 'Bring the changes back and keep the shelved set', () => unshelve(s, false)),
                button('Diff', 'Show the shelved changes', () => showPatch(s)),
                button('Delete', 'Throw the shelved set away', () => remove(s)),
            );
            row.title = s.files.map((f) => `${f.status} ${f.path}`).join('\n');
            row.onclick = () => void showPatch(s);
            box.append(row);
        }
    }

    async function showPatch(s) {
        try {
            const { patch } = await api('/api/git/shelf/patch?id=' + encodeURIComponent(s.id));
            const { body } = dialog(`${s.name} (shelved ${s.time.replace('T', ' ').slice(0, 16)})`, true);
            body.id = 'git-shelf-patch';
            const pre = el('div', 'shelf-patch');
            for (const l of patchLines(patch)) {
                const line = el('div', 'sp-' + (l.kind === ' ' ? 'ctx' : l.kind === '+' ? 'add' : l.kind === '-' ? 'del' : l.kind === '@' ? 'hunk' : 'head'), l.text || ' ');
                pre.append(line);
            }
            body.append(pre);
        } catch (e) { toast(String(e.message ?? e), true); }
    }

    async function unshelve(s, drop) {
        try {
            await deps.saveAll();
            const r = await post('/api/git/shelf', { op: 'unshelve', id: s.id, delete: drop });
            await deps.reloadAll();
            if (r.conflicts) toast(`Unshelved with conflicts in ${r.conflictFiles.join(', ')}: resolve the markers in the files; the shelved set was kept.`, true);
            else toast(drop ? 'Unshelved' : 'Unshelved (the set is kept)');
        } catch (e) { toast(String(e.message ?? e), true); }
        await git.refresh();
    }

    async function remove(s) {
        if (!window.confirm(`Throw away the shelved set "${s.name}"? It cannot be restored.`)) return;
        try { await post('/api/git/shelf', { op: 'delete', id: s.id }); toast('Shelved set deleted'); } catch (e) { toast(String(e.message ?? e), true); }
        await load();
    }

    /** Puts these files away: the patch is saved, the files go back to the last commit. */
    async function shelve(paths, list) {
        if (!paths.length) { toast('There are no files to shelve.', true); return; }
        const suggestion = list && list !== 'Changes' ? list : '';
        const title = window.prompt(`Name for the shelved set (${filesLabel(paths.length)}):`, suggestion);
        if (title === null) return;
        try {
            await deps.saveAll();
            await post('/api/git/shelf', { op: 'shelve', paths, title: title.trim(), changelist: list || null });
            await deps.reloadAll();
            toast(`Shelved ${filesLabel(paths.length)}`);
        } catch (e) { toast(String(e.message ?? e), true); }
        await git.refresh();
    }

    window.addEventListener('pymagic:shelve', (ev) => void shelve(ev.detail.paths, ev.detail.list));
    window.addEventListener('pymagic:git-refreshed', () => void load());

    // the ticked files of the list that the Commit button commits from
    $('git-shelf-new').onclick = () => {
        const files = git.status().files ?? [];
        const list = git.commitList();
        const group = groupFiles(files, git.changelists()).find((g) => g.name === list);
        const unchecked = git.unchecked();
        const paths = (group?.files ?? []).filter((f) => !unchecked.has(f.path)).map((f) => f.path);
        void shelve(paths, list);
    };

    void load();
    return { load };
}

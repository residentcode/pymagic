// Git beyond the working tree: fetch, pull and push, the merge or rebase that stopped on conflicts (resolve each file, continue or
// abort), stashes, remotes, and branch and tag management. The Git tab (git.js) owns the status; this module reads it when it refreshes.
import { toast } from './toast';

const $ = (id) => document.getElementById(id);

async function api(url, init) {
    const res = await fetch(url, init);
    const body = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(body.error ?? `${res.status} ${res.statusText}`);
    return body;
}
const act = (body) => api('/api/git/action', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });

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

/** A box over the page with a title and a close button; `build(body, close)` fills it. */
function dialog(title, build) {
    const shade = el('div', 'ask-shade');
    const box = el('div', 'ask-box');
    box.style.width = '560px';
    box.style.maxHeight = '70vh';
    box.style.overflow = 'auto';
    const close = () => shade.remove();
    const head = el('div', 'ask-title', title);
    box.append(head);
    shade.append(box);
    shade.onmousedown = (e) => { if (e.target === shade) close(); };
    shade.onkeydown = (e) => { if (e.key === 'Escape') close(); };
    document.body.append(shade);
    const body = el('div');
    box.append(body);
    const done = el('div', 'ask-buttons');
    done.append(button('Close', '', close));
    box.append(done);
    void build(body, close);
    return { close, body };
}

export function setupGitSync({ git, deps }) {
    const out = $('git-sync-out');
    let operation = null;
    let conflicts = [];

    const say = (text, bad = false) => { out.textContent = (text ?? '').split('\n').filter(Boolean).pop() ?? ''; out.title = text ?? ''; out.className = bad ? 'bad' : ''; };

    /** Runs a git action: its buttons wait, git's own last line is shown, and every file and the status are read again afterwards. */
    async function run(label, work) {
        const buttons = ['git-fetch', 'git-pull', 'git-push'].map($);
        buttons.forEach((b) => { b.disabled = true; });
        say(label + '…');
        try {
            await deps.saveAll();
            const r = await work();
            await deps.reloadAll();
            const text = r?.output ?? '';
            say(r?.conflicts ? `${label}: conflicts to resolve below` : text || `${label}: done`);
            if (r?.conflicts) toast('Stopped on conflicts: resolve each file, then Continue (or Abort).', true);
            return r;
        } catch (e) {
            say(String(e.message ?? e), true);
            toast(String(e.message ?? e), true);
            return null;
        } finally {
            buttons.forEach((b) => { b.disabled = false; });
            await git.refresh();
        }
    }

    $('git-fetch').onclick = () => void run('Fetch', () => act({ op: 'fetch', prune: true }));
    $('git-pull').onclick = () => void run('Pull', () => act({ op: 'pull', mode: $('git-pull-mode').value }));
    $('git-push').onclick = () => void run('Push', () => act({ op: 'push' }));

    // ---- a merge, rebase, cherry-pick or revert that stopped

    const names = { merge: 'Merge', rebase: 'Rebase', 'cherry-pick': 'Cherry-pick', revert: 'Revert' };

    function paintOperation() {
        $('git-op').style.display = operation ? '' : 'none';
        if (!operation) return;
        $('git-op-text').textContent = conflicts.length
            ? `${names[operation]} in progress: ${conflicts.length} file${conflicts.length === 1 ? '' : 's'} with conflicts`
            : `${names[operation]} in progress: every conflict is resolved, continue to finish it`;
        $('git-op-continue').disabled = conflicts.length > 0;
        const box = $('git-conflicts');
        box.replaceChildren();
        for (const path of conflicts) {
            const row = el('div', 'gs-row');
            row.append(el('span', 'path', path));
            // during a rebase "ours" is the branch being rebased onto, which is the other way round to a merge
            const oursTitle = operation === 'rebase' ? 'The version of the branch you are rebasing onto' : 'Your version (the branch you are on)';
            const theirsTitle = operation === 'rebase' ? 'The version of your commit being replayed' : 'The version that comes in';
            row.append(
                button('Ours', oursTitle, () => resolve(path, 'ours')),
                button('Theirs', theirsTitle, () => resolve(path, 'theirs')),
                button('Compare', 'Ours against theirs side by side', () => compare(path)),
                button('Edit', 'Open the file with its conflict markers', () => deps.openFile(path)),
                button('Resolved', 'The file as it is now is what you want', () => markResolved(path)),
            );
            box.append(row);
        }
    }

    async function resolve(path, side) {
        try { await act({ op: 'resolve', path, side }); await deps.reloadAll(); toast(`${path}: took ${side}`); } catch (e) { toast(String(e.message ?? e), true); }
        await git.refresh();
    }

    async function markResolved(path) {
        try { await deps.saveAll(); await act({ op: 'markResolved', path }); toast(`${path}: resolved`); } catch (e) { toast(String(e.message ?? e), true); }
        await git.refresh();
    }

    async function compare(path) {
        try {
            const s = await api('/api/git/conflict?path=' + encodeURIComponent(path));
            git.showDiff(`${path} — ours (left) against theirs (right)`, s.ours ?? '', s.theirs ?? '', path);
        } catch (e) { toast(String(e.message ?? e), true); }
    }

    $('git-op-abort').onclick = async () => {
        if (!window.confirm(`Abort the ${names[operation] ?? 'operation'}? The branch and the files go back to how they were before it started.`)) return;
        try { await act({ op: 'abort' }); await deps.reloadAll(); toast('Aborted'); } catch (e) { toast(String(e.message ?? e), true); }
        await git.refresh();
    };
    $('git-op-continue').onclick = async () => {
        try {
            await deps.saveAll();
            const r = await act({ op: 'continue' });
            await deps.reloadAll();
            toast(r.conflicts ? 'More conflicts to resolve' : `${names[operation] ?? 'Operation'} finished`);
        } catch (e) { toast(String(e.message ?? e), true); }
        await git.refresh();
    };

    // ---- stashes

    function paintStashes(list) {
        $('git-stash-count').textContent = list.length ? `(${list.length})` : '';
        const box = $('git-stashes');
        box.replaceChildren();
        if (!list.length) {
            const none = el('div', 'gs-row');
            none.append(el('span', 'dim', 'No stashes.'));
            box.append(none);
        }
        for (const s of list) {
            const row = el('div', 'gs-row');
            row.append(el('span', 'what', s.message), el('span', 'dim', `${s.branch} · ${s.age}`));
            row.append(
                button('Pop', 'Bring the changes back and remove the stash', () => applyStash(s, true)),
                button('Apply', 'Bring the changes back and keep the stash', () => applyStash(s, false)),
                button('Drop', 'Throw the stash away', () => dropStash(s)),
            );
            row.title = 'Click for the files in it';
            row.onclick = () => void showFiles(s);
            box.append(row);
        }
    }

    async function showFiles(s) {
        try {
            const files = await api('/api/git/stash/files?index=' + s.index);
            toast(`${s.message}: ${files.map((f) => `${f.status} ${f.path}`).join(', ') || 'no files'}`);
        } catch (e) { toast(String(e.message ?? e), true); }
    }

    async function applyStash(s, drop) {
        try {
            await deps.saveAll();
            const r = await act({ op: 'applyStash', index: s.index, drop });
            await deps.reloadAll();
            toast(r.conflicts ? 'The stash conflicts with your changes: resolve below' : drop ? 'Stash popped' : 'Stash applied', r.conflicts);
        } catch (e) { toast(String(e.message ?? e), true); }
        await git.refresh();
    }

    async function dropStash(s) {
        if (!window.confirm(`Throw away the stash "${s.message}"? It cannot be restored.`)) return;
        try { await act({ op: 'dropStash', index: s.index }); toast('Stash dropped'); } catch (e) { toast(String(e.message ?? e), true); }
        await git.refresh();
    }

    $('git-stash-new').onclick = async () => {
        const message = window.prompt('Message for the stash (optional):');
        if (message === null) return;
        const untracked = window.confirm('Include new files that git does not track yet?');
        try {
            await deps.saveAll();
            const r = await act({ op: 'stash', message, untracked });
            await deps.reloadAll();
            toast(r.output.includes('No local changes') ? 'There was nothing to stash' : 'Changes stashed');
        } catch (e) { toast(String(e.message ?? e), true); }
        await git.refresh();
    };

    // ---- remotes

    $('git-remotes').onclick = () => dialog('Remotes', async (body) => {
        const list = el('div');
        const message = el('div', 'ask-hint');
        message.style.color = '#e5534b';
        async function paint() {
            list.replaceChildren();
            let remotes = [];
            try { remotes = await api('/api/git/remotes'); } catch (e) { message.textContent = String(e.message ?? e); }
            if (!remotes.length) list.append(el('div', 'ask-hint', 'No remotes yet: add one to fetch, pull and push.'));
            for (const r of remotes) {
                const row = el('div', 'gs-row');
                row.append(el('span', '', r.name), el('span', 'path dim', r.fetchUrl));
                row.append(button('Remove', '', async () => {
                    if (!window.confirm(`Remove the remote ${r.name}? Nothing is deleted on the server.`)) return;
                    try { await act({ op: 'removeRemote', name: r.name }); } catch (e) { message.textContent = String(e.message ?? e); }
                    await paint();
                    await git.refresh();
                }));
                list.append(row);
            }
        }
        const name = el('input'); name.placeholder = 'name (origin)'; name.value = '';
        const url = el('input'); url.placeholder = 'address (https://…, git@host:path, or a folder)';
        for (const i of [name, url]) { i.spellcheck = false; i.autocomplete = 'off'; i.style.margin = '3px 0'; }
        body.append(list, el('div', 'ask-hint', 'Add a remote'), name, url, message,
            button('Add', '', async () => {
                message.textContent = '';
                try { await act({ op: 'addRemote', name: name.value.trim() || 'origin', url: url.value.trim() }); name.value = ''; url.value = ''; } catch (e) { message.textContent = String(e.message ?? e); return; }
                await paint();
                await git.refresh();
            }));
        await paint();
    });

    // ---- branches and tags

    $('git-manage').onclick = () => dialog('Branches & tags', async (body, close) => {
        const noFf = el('input'); noFf.type = 'checkbox';
        const noFfLabel = el('label', 'ask-hint'); noFfLabel.append(noFf, ' always make a merge commit when merging');
        const sections = el('div');
        const message = el('div', 'ask-hint');
        message.style.color = '#e5534b';

        async function change(work, done) {
            message.textContent = '';
            try { await deps.saveAll(); const r = await work(); await deps.reloadAll(); if (done) toast(r?.conflicts ? 'Stopped on conflicts: resolve them in the Git tab' : done); await git.refresh(); if (r?.conflicts) close(); else await paint(); } catch (e) { message.textContent = String(e.message ?? e); }
        }

        async function paint() {
            sections.replaceChildren();
            let b, tags;
            try { [b, tags] = await Promise.all([api('/api/git/branches'), api('/api/git/tags')]); } catch (e) { message.textContent = String(e.message ?? e); return; }
            sections.append(el('div', 'ask-hint', 'Branches'));
            for (const name of b.local) {
                const row = el('div', 'gs-row');
                row.append(el('span', 'what', name + (name === b.current ? '   (current)' : '')));
                if (name !== b.current) {
                    row.append(
                        button('Merge into current', `Join ${name} into ${b.current}`, () => change(() => act({ op: 'merge', branch: name, noFf: noFf.checked }), `Merged ${name}`)),
                        button('Delete', 'Remove the branch (git refuses one with work that is not merged)', async () => {
                            try { await act({ op: 'deleteBranch', name, force: false }); toast('Deleted ' + name); await git.refresh(); await paint(); return; } catch (e) {
                                if (!/not fully merged/i.test(String(e.message))) { message.textContent = String(e.message ?? e); return; }
                            }
                            if (window.confirm(`${name} has work that is not merged anywhere else. Delete it anyway? The commits are lost.`))
                                await change(() => act({ op: 'deleteBranch', name, force: true }), 'Deleted ' + name);
                        }),
                    );
                }
                row.append(button('Rename', '', async () => {
                    const to = window.prompt(`New name for ${name}:`, name);
                    if (to && to !== name) await change(() => act({ op: 'renameBranch', name, newName: to.trim() }), 'Renamed');
                }));
                sections.append(row);
            }
            if (b.remote?.length) {
                sections.append(el('div', 'ask-hint', 'Remote branches'));
                for (const name of b.remote) {
                    const row = el('div', 'gs-row');
                    row.append(el('span', 'what', name),
                        button('Merge into current', '', () => change(() => act({ op: 'merge', branch: name, noFf: noFf.checked }), `Merged ${name}`)),
                        button('Check out', 'Make a local branch that follows it', () => change(() => act({ op: 'checkoutRemote', branch: name }), 'Checked out ' + name)));
                    sections.append(row);
                }
            }
            sections.append(el('div', 'ask-hint', 'Tags'));
            if (!tags.length) { const none = el('div', 'gs-row'); none.append(el('span', 'dim', 'No tags.')); sections.append(none); }
            for (const t of tags) {
                const row = el('div', 'gs-row');
                row.append(el('span', '', t.name), el('span', 'what dim', `${t.hash} ${t.subject}`),
                    button('Delete', '', () => change(() => act({ op: 'deleteTag', name: t.name }), 'Tag deleted')));
                sections.append(row);
            }
            sections.append(button('New tag on HEAD…', '', async () => {
                const name = window.prompt('Tag name:');
                if (!name) return;
                const msg = window.prompt('Message (leave empty for a plain tag):') ?? '';
                await change(() => act({ op: 'createTag', name: name.trim(), message: msg }), 'Tagged ' + name.trim());
            }));
        }
        body.append(noFfLabel, sections, message);
        await paint();
    });

    // ---- reading the state when the Git tab refreshes

    let reading = false;
    async function readState() {
        if (reading) return;
        reading = true;
        try {
            if (!git.status().available) { operation = null; conflicts = []; paintOperation(); paintStashes([]); return; }
            const [op, stashes] = await Promise.all([api('/api/git/operation'), api('/api/git/stashes')]);
            operation = op.operation;
            conflicts = op.conflicts ?? [];
            paintOperation();
            paintStashes(stashes);
        } catch { /* the Git tab says why it is empty */ }
        finally { reading = false; }
    }
    window.addEventListener('pymagic:git-refreshed', () => void readState());
    void readState();
}

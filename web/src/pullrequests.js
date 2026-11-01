// The Pull Requests tab of the dock: the open pull requests (GitHub) or merge requests (GitLab) of the project's remote, one shown in full (description,
// commits, changed files with a side-by-side diff, comments), and the three things that reach out: creating one from the current branch, commenting, merging.
// Every request goes through the engine. Those three never go on a click alone: the engine answers with the exact request and a token, this page shows the
// request in a window of its own, and only "Send" there carries it out (the engine accepts the token once).
import * as monaco from 'monaco-editor';
import { showTab } from './panel';
import { baseChoices, changeText, checksText, newProblem, nounOf, refOf, splitPatch, startTitle, stateText, statusLetter, subjectOf, tokenHint, whoWants } from './pr-model.js';
import { toast } from './toast';

const $ = (id) => document.getElementById(id);
const el = (tag, props = {}, ...children) => { const e = Object.assign(document.createElement(tag), props); e.append(...children); return e; };

export function setupPullRequests() {
    let info = null;        // what the engine says of the remote
    let items = [];
    let shown = null;       // the number of the request in the detail pane

    const get = (url) => fetch(url).then((r) => r.json()).catch((e) => ({ error: String(e) }));
    const post = (url, body) => fetch(url, { method: 'POST', body: JSON.stringify(body) }).then((r) => r.json()).catch((e) => ({ error: String(e) }));
    const empty = (box, text) => box.replaceChildren(el('div', { className: 'side-empty', textContent: text }));
    const ref = (n) => refOf(info?.kind, n);

    // ------------------------------------------------------------ the tab

    async function load() {
        info = await get('/api/pr/info');
        drawHead();
        if (!info.available) { empty($('pr-list'), info.reason ?? 'No remote on GitHub or GitLab.'); empty($('pr-detail'), ''); return; }
        if (!info.hasToken) { empty($('pr-list'), tokenHint(info)); empty($('pr-detail'), ''); return; }
        await refresh();
    }

    function drawHead() {
        const ok = !!info?.available;
        $('pr-remote').textContent = ok ? `${info.host}/${info.repo}` : 'Pull requests';
        $('pr-remote').title = ok ? `${nounOf(info.kind)}s of the remote "${info.remote}". Requests go to ${info.apiBase}` : '';
        $('pr-token').disabled = $('pr-token-save').disabled = !ok;
        $('pr-token').placeholder = ok ? (info.hasToken ? 'A token is kept: type a new one to replace it' : `Personal access token for ${info.host}`) : '';
        $('pr-token').title = ok ? tokenHint(info) : '';
        $('pr-token-forget').hidden = !ok || !info.hasToken;
        $('pr-new').disabled = $('pr-refresh').disabled = !ok || !info.hasToken;
    }

    async function refresh() {
        empty($('pr-list'), 'Reading…');
        const r = await get('/api/pr/list');
        if (r.error) { empty($('pr-list'), r.error); return; }
        items = r.items;
        drawList();
        if (shown !== null && items.some((i) => i.number === shown)) void open(shown);
        else { shown = null; empty($('pr-detail'), items.length ? `Choose a ${nounOf(info.kind)}.` : ''); }
    }

    function drawList() {
        const box = $('pr-list');
        box.replaceChildren();
        if (!items.length) { empty(box, `No open ${nounOf(info.kind)}s.`); return; }
        for (const pr of items) {
            const checks = checksText(pr.checks);
            const row = el('div', { className: 'pr-row' + (pr.number === shown ? ' sel' : '') });
            row.dataset.number = String(pr.number);
            row.append(
                el('span', { className: 'pr-num', textContent: ref(pr.number) }),
                el('span', { className: 'pr-title', textContent: pr.title }),
                el('span', { className: 'pr-state', textContent: stateText(pr) }),
                el('span', { className: 'pr-checks ' + checks.cls, textContent: checks.text, title: 'The checks of the branch' }),
                el('span', { className: 'pr-branches', textContent: `${pr.source} → ${pr.target}`, title: whoWants(pr) }),
                el('span', { className: 'pr-author', textContent: pr.author }));
            row.onclick = () => void open(pr.number);
            box.append(row);
        }
    }

    async function open(number) {
        shown = number;
        for (const row of $('pr-list').children) row.classList.toggle('sel', row.dataset.number === String(number));
        empty($('pr-detail'), 'Reading…');
        const d = await get(`/api/pr/get?number=${number}`);
        if (shown !== number) return;
        if (d.error) { empty($('pr-detail'), d.error); return; }
        drawDetail(d);
    }

    function section(title, count) { return el('div', { className: 'pr-section', textContent: count === undefined ? title : `${title} (${count})` }); }

    function drawDetail(d) {
        const s = d.summary;
        const box = $('pr-detail');
        box.replaceChildren();
        const checks = checksText(s.checks);
        const head = el('div', { className: 'pr-head' });
        head.append(el('div', { className: 'pr-head-title', textContent: `${ref(s.number)} ${s.title}` }),
            el('div', { className: 'pr-head-sub' }, whoWants(s) + ' · ', el('span', { className: 'pr-state', textContent: stateText(s) }), ' · checks ', el('span', { className: 'pr-checks ' + checks.cls, textContent: checks.text }),
                d.mergeable === false ? ' · cannot be merged cleanly' : ''));
        const buttons = el('div', { className: 'pr-head-buttons' });
        const comment = el('button', { id: 'pr-comment', textContent: 'Comment…', title: 'Write a comment (it is shown to you first)' });
        comment.onclick = () => commentForm(s.number);
        const method = el('select', { id: 'pr-method', title: 'How it is merged' });
        for (const m of info.kind === 'github' ? ['merge', 'squash', 'rebase'] : ['merge', 'squash']) method.append(new Option(m, m));
        const merge = el('button', { id: 'pr-merge', textContent: 'Merge…', title: 'Merge it (the request is shown to you first)', disabled: s.state !== 'open' });
        merge.onclick = () => void sendable({ action: 'merge', number: s.number, method: method.value }, () => { void refresh(); });
        buttons.append(comment, method, merge);
        if (s.url) {
            const link = el('button', { textContent: 'Open on ' + info.host, title: s.url });
            link.onclick = () => window.open(s.url, '_blank', 'noopener');
            buttons.append(link);
        }
        head.append(buttons);
        box.append(head);

        box.append(section('Description'), el('pre', { className: 'pr-body', textContent: d.body || '(no description)' }));
        if (d.checks.length) {
            box.append(section('Checks', d.checks.length));
            for (const c of d.checks) box.append(el('div', { className: 'pr-line' }, el('span', { className: 'pr-checks ' + checksText(c.state).cls, textContent: checksText(c.state).text }), ' ' + c.name));
        }
        box.append(section('Commits', d.commits.length));
        for (const c of d.commits) box.append(el('div', { className: 'pr-line' }, el('span', { className: 'pr-sha', textContent: c.id.slice(0, 8) }), ' ' + subjectOf(c.message), el('span', { className: 'pr-dim', textContent: ' · ' + c.author })));
        box.append(section('Files changed', d.files.length));
        for (const f of d.files) {
            const row = el('div', { className: 'pr-line pr-file', title: 'Open the side-by-side diff' },
                el('span', { className: `st st-${statusLetter(f.status)}`, textContent: statusLetter(f.status) }), ' ' + (f.oldPath ? `${f.oldPath} → ${f.path}` : f.path),
                el('span', { className: 'pr-dim', textContent: '  ' + changeText(f) }));
            row.onclick = () => diff(f);
            box.append(row);
        }
        box.append(section('Comments', d.comments.length));
        if (!d.comments.length) box.append(el('div', { className: 'pr-line pr-dim', textContent: 'No comments.' }));
        for (const c of d.comments) {
            box.append(el('div', { className: 'pr-comment' }, el('div', { className: 'pr-comment-head', textContent: `${c.author} · ${c.date ? new Date(c.date).toLocaleString() : ''}${c.path ? ` · ${c.path}${c.line ? ':' + c.line : ''}` : ''}` }),
                el('pre', { className: 'pr-body', textContent: c.body })));
        }
    }

    // ------------------------------------------------------------ a file's diff

    function languageOf(path) {
        const lower = path.toLowerCase();
        return monaco.languages.getLanguages().find((l) => (l.extensions ?? []).some((e) => lower.endsWith(e)))?.id ?? 'plaintext';
    }

    function diff(file) {
        if (!file.patch) { toast(`${file.path} has no text to compare (it may be binary or too large for ${info.host} to show).`); return; }
        const { original, modified } = splitPatch(file.patch);
        const shade = el('div', { className: 'settings-shade pr-diff-shade' });
        const box = el('div', { className: 'hist-diff' });
        const close = el('button', { textContent: 'Close' });
        box.append(el('div', { className: 'hist-diff-head' }, el('span', { className: 'hist-diff-title', textContent: `${file.path}: before (left) and after (right), the changed parts only` }), close));
        const body = el('div', { className: 'hist-diff-body' });
        box.append(body);
        shade.append(box);
        document.body.append(shade);
        const language = languageOf(file.path);
        const a = monaco.editor.createModel(original, language), b = monaco.editor.createModel(modified, language);
        const view = monaco.editor.createDiffEditor(body, { readOnly: true, automaticLayout: true, minimap: { enabled: false }, scrollBeyondLastLine: false, renderSideBySide: true });
        view.setModel({ original: a, modified: b });
        const done = () => { document.removeEventListener('keydown', onKey, true); view.dispose(); a.dispose(); b.dispose(); shade.remove(); };
        const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); done(); } };
        document.addEventListener('keydown', onKey, true);
        close.onclick = done;
        shade.onmousedown = (e) => { if (e.target === shade) done(); };
    }

    // ------------------------------------------------------------ what reaches out: the form, then the request itself shown, then Send

    /** Asks the engine for the exact request, shows it, and sends it only when the user presses Send there. */
    async function sendable(body, after) {
        const prepared = await post('/api/pr/prepare', body);
        if (prepared.error) { toast(prepared.error, true); return false; }
        confirmWindow(prepared, after);
        return true;
    }

    function confirmWindow(p, after) {
        const shade = el('div', { className: 'ask-shade pr-confirm-shade' });
        const box = el('div', { className: 'ask-box pr-confirm' });
        box.append(el('div', { className: 'ask-title', textContent: p.title }));
        box.append(el('div', { className: 'pr-confirm-lines' }, ...p.lines.map((l) => el('div', { textContent: l }))));
        for (const w of p.warnings) box.append(el('div', { className: 'pr-confirm-warn', textContent: w }));
        box.append(el('div', { className: 'pr-confirm-what', textContent: `This request will be sent to ${p.host}, with your token:` }),
            el('div', { className: 'pr-confirm-request', textContent: `${p.method} ${p.url}` }),
            el('pre', { className: 'pr-confirm-body', textContent: p.body || '(no body)' }));
        const error = el('div', { className: 'db-form-error' });
        const row = el('div', { className: 'ask-buttons' });
        const send = el('button', { className: 'primary pr-confirm-send', textContent: 'Send' });
        const cancel = el('button', { className: 'pr-confirm-cancel', textContent: 'Cancel' });
        row.append(send, cancel);
        box.append(error, row);
        shade.append(box);
        document.body.append(shade);
        const done = () => shade.remove();
        cancel.onclick = done;
        shade.onmousedown = (e) => { if (e.target === shade) done(); };
        box.onkeydown = (e) => { if (e.key === 'Escape') done(); e.stopPropagation(); };
        send.onclick = async () => {
            send.disabled = true;
            error.textContent = 'Sending…';
            const answer = await post('/api/pr/confirm', { token: p.token });
            if (answer.error) { error.textContent = answer.error; send.disabled = false; return; }
            done();
            toast(answer.action === 'create' ? `Created ${ref(answer.created?.number ?? '')} on ${p.host}.` : answer.action === 'merge' ? `Merged on ${p.host}.` : `Comment posted on ${p.host}.`);
            for (const f of document.querySelectorAll('.pr-form-shade')) f.remove();
            after?.(answer);
        };
        send.focus();
    }

    function formShell(titleText) {
        const shade = el('div', { className: 'ask-shade pr-form-shade' });
        const box = el('div', { className: 'ask-box pr-form' });
        box.append(el('div', { className: 'ask-title', textContent: titleText }));
        const error = el('div', { className: 'db-form-error' });
        const row = el('div', { className: 'ask-buttons' });
        const ok = el('button', { className: 'primary', textContent: 'Review…', title: 'Nothing is sent yet: the next window shows exactly what would be' });
        const cancel = el('button', { textContent: 'Cancel' });
        row.append(ok, cancel);
        shade.append(box);
        document.body.append(shade);
        const done = () => shade.remove();
        cancel.onclick = done;
        shade.onmousedown = (e) => { if (e.target === shade) done(); };
        box.onkeydown = (e) => { if (e.key === 'Escape') done(); e.stopPropagation(); };
        return { box, error, row, ok, done };
    }

    function commentForm(number) {
        const f = formShell(`Comment on ${ref(number)}`);
        const text = el('textarea', { id: 'pr-comment-text', rows: 6, spellcheck: true });
        f.box.append(text, f.error, f.row);
        f.ok.onclick = async () => {
            if (!text.value.trim()) { f.error.textContent = 'Write something first.'; return; }
            await sendable({ action: 'comment', number, body: text.value }, () => void open(number));
        };
        text.focus();
    }

    function newForm() {
        const f = formShell(`New ${nounOf(info.kind)} from ${info.branch ?? '(no branch)'}`);
        const field = (label, input) => { const row = el('label', { className: 'db-form-row' }, el('span', { textContent: label }), input); f.box.append(row); return input; };
        const title = field('Title', el('input', { id: 'pr-new-title', value: startTitle(info), spellcheck: false, autocomplete: 'off' }));
        const base = field('Into', el('select', { id: 'pr-new-base' }));
        for (const b of baseChoices(info)) base.append(new Option(b, b));
        const draft = field('Draft', el('input', { id: 'pr-new-draft', type: 'checkbox' }));
        const body = el('textarea', { id: 'pr-new-body', rows: 6, placeholder: 'Description', spellcheck: true });
        f.box.append(body, f.error, f.row);
        if (!info.pushed) f.error.textContent = `${info.branch ?? 'The branch'} has commits that are not on the remote yet: push it in the Git window first.`;
        f.ok.onclick = async () => {
            const problem = newProblem({ title: title.value, head: info.branch, base: base.value });
            if (problem) { f.error.textContent = problem; return; }
            await sendable({ action: 'create', title: title.value, body: body.value, head: info.branch, base: base.value, draft: draft.checked, pushed: info.pushed }, () => void refresh());
        };
        title.focus();
    }

    // ------------------------------------------------------------ wiring

    $('t-prs').onclick = () => { showTab('prs'); void load(); };
    $('pr-refresh').onclick = () => void load();
    $('pr-new').onclick = () => {
        if (!info?.available) return;
        // the branch and what is pushed may have changed since the tab was opened
        get('/api/pr/info').then((fresh) => { if (fresh.available) info = fresh; newForm(); });
    };
    $('pr-token-save').onclick = async () => {
        const token = $('pr-token').value.trim();
        if (!token) { toast('Type the token first.', true); return; }
        const r = await post('/api/pr/token', { token });
        $('pr-token').value = '';
        if (r.error) { toast(r.error, true); return; }
        toast(`The token for ${info.host} is kept.`);
        await load();
    };
    $('pr-token-forget').onclick = async () => {
        if (!window.confirm(`Forget the token for ${info?.host}?`)) return;
        await post('/api/pr/token', { token: '' });
        await load();
    };
    $('pr-token').onkeydown = (e) => { if (e.key === 'Enter') $('pr-token-save').click(); };
    return { load };
}

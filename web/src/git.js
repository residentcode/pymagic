// Git in the page: status colours in the file list, the change bars in the gutter (click one to see and revert the change),
// the Git tab (changes, commit, amend, rollback, history, branches), a side-by-side diff, and blame (Annotate, inline).
import * as monaco from 'monaco-editor';
import { diffLines } from './preview';
import { showTab } from './panel';
import { toast } from './toast';
import { openRebase } from './rebase';
import { changedLines, counts, hunkState, numbering, payload, selectAll, selectNone, stateOf, toggleHunk, toggleLine } from './hunks';
const $ = (id) => document.getElementById(id);
async function api(url, init) {
    const res = await fetch(url, init);
    if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        throw new Error(body.error ?? `${res.status} ${res.statusText}`);
    }
    return res.json();
}
const post = (url, body) => api(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
const lineText = (text) => text.replace(/\r/g, '');
const normalise = (text) => lineText(text);
/** The changed stretches between two texts, with their positions in both (0-based). */
export function lineHunks(oldText, newText) {
    const hunks = [];
    let o = 0, n = 0;
    let cur = null;
    for (const l of diffLines(oldText, newText)) {
        if (l.kind === ' ') {
            cur = null;
            o++;
            n++;
            continue;
        }
        if (!cur) {
            cur = { oldStart: o, oldLines: [], newStart: n, newCount: 0 };
            hunks.push(cur);
        }
        if (l.kind === '-') {
            cur.oldLines.push(l.text);
            o++;
        }
        else {
            cur.newCount++;
            n++;
        }
    }
    return hunks;
}
const hunkKind = (h) => h.oldLines.length === 0 ? 'added' : h.newCount === 0 ? 'deleted' : 'modified';
/** Where a line of the new text was in the old one, or null when it was changed or added. */
function lineInOld(hunks, newLine) {
    let shift = 0;
    for (const h of hunks) {
        if (newLine < h.newStart)
            break;
        if (newLine < h.newStart + h.newCount)
            return null;
        shift += h.oldLines.length - h.newCount;
    }
    return newLine + shift;
}
const ago = (seconds) => {
    const d = Math.max(0, Math.round((Date.now() / 1000 - seconds) / 86400));
    return d === 0 ? 'today' : d === 1 ? 'yesterday' : d < 60 ? `${d}d ago` : d < 700 ? `${Math.round(d / 30)}mo ago` : `${Math.round(d / 365)}y ago`;
};
const agoIso = (iso) => ago(Date.parse(iso) / 1000);
export function setupGit(deps) {
    const { editor } = deps;
    let status = { available: false };
    const baseText = new Map(); // rel -> text at HEAD (null: the file is not there)
    let hunks = [];
    let hunksFor = '';
    const decorations = editor.createDecorationsCollection();
    const unchecked = new Set();
    const partial = new Map(); // rel -> { diff, sel }: a file of which only some lines are to be committed
    let logSkip = 0;
    let logEnd = false;
    let selectedCommit = '';
    let popup = null;
    const closePopup = () => { popup?.remove(); popup = null; };
    document.addEventListener('mousedown', (ev) => { if (popup && !popup.contains(ev.target))
        closePopup(); }, true);
    document.addEventListener('keydown', (ev) => { if (ev.key === 'Escape') {
        closePopup();
        closeDiff();
        closeLines(false);
    } }, true);
    const fileStatus = (rel) => status.files?.find((f) => f.path === rel);
    // ------------------------------------------------------------------ status
    async function refresh() {
        try {
            status = await api('/api/git/status');
        }
        catch (e) {
            status = { available: false, reason: String(e.message ?? e) };
        }
        baseText.clear();
        for (const rel of Array.from(partial.keys()))
            if (!status.files?.some((f) => f.path === rel))
                partial.delete(rel);
        renderStatus();
        paintFileList();
        void paint();
        void loadBranches();
        window.dispatchEvent(new Event('pymagic:git-refreshed'));
        if (annotate)
            void loadBlame();
    }
    function paintFileList() {
        for (const li of Array.from($('files').children)) {
            const f = status.files?.find((x) => x.path === li.dataset.path);
            li.classList.remove('git-M', 'git-A', 'git-D', 'git-U');
            if (!f)
                continue;
            li.classList.add(f.index === '?' || f.index === 'A' ? 'git-A' : f.worktree === 'D' || f.index === 'D' ? 'git-D' : f.index === 'U' || f.worktree === 'U' ? 'git-U' : 'git-M');
            li.title = `git: ${f.index === '?' ? 'untracked' : (f.index + f.worktree).trim()}`;
        }
    }
    function renderStatus() {
        const none = $('git-none');
        const split = $('git-split');
        if (!status.available) {
            none.style.display = '';
            split.style.display = 'none';
            $('git-none-text').textContent = status.reason === 'not a git repository' ? 'This folder is not in a Git repository.' : `Git is not available: ${status.reason ?? 'unknown reason'}`;
            $('git-init').style.display = status.canInit ? '' : 'none';
            $('git-branch').textContent = '';
            $('git-branches').style.display = 'none';
            $('git-state').textContent = '';
            return;
        }
        none.style.display = 'none';
        split.style.display = '';
        $('git-branches').style.display = '';
        $('git-branch').textContent = status.detached ? 'detached HEAD' : status.branch ?? '';
        $('git-state').textContent = [status.upstream ? `tracking ${status.upstream}` : '', status.ahead ? `↑${status.ahead}` : '', status.behind ? `↓${status.behind}` : ''].filter(Boolean).join('  ');
        renderChanges();
        if (!$('git-log').childElementCount)
            void loadLog(true);
    }
    // ------------------------------------------------------------------ changes and commit
    const letter = (f) => f.index === '?' ? '?' : f.index !== ' ' && f.index !== '?' ? f.index : f.worktree;
    function renderChanges() {
        const box = $('git-changes');
        box.replaceChildren();
        const files = status.files ?? [];
        $('git-count').textContent = files.length ? `(${files.length})` : '';
        if (files.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'gl';
            empty.style.color = 'var(--dim)';
            empty.textContent = status.hasCommits === false ? 'Nothing here yet. Create a file to start.' : 'No changes.';
            box.append(empty);
        }
        for (const f of files) {
            const row = document.createElement('div');
            row.className = 'gc';
            const box1 = document.createElement('input');
            box1.type = 'checkbox';
            box1.checked = !unchecked.has(f.path);
            box1.indeterminate = partial.has(f.path);
            box1.onclick = (ev) => ev.stopPropagation();
            box1.onchange = () => {
                partial.delete(f.path); // ticking the file takes all of it again
                if (box1.checked)
                    unchecked.delete(f.path);
                else
                    unchecked.add(f.path);
                renderChanges();
            };
            const st = document.createElement('span');
            st.className = `st st-${letter(f)}`;
            st.textContent = letter(f);
            const path = document.createElement('span');
            path.className = 'path';
            path.textContent = f.originalPath ? `${f.originalPath} → ${f.path}` : f.path;
            path.title = path.textContent;
            const lines = document.createElement('button');
            lines.className = 'roll';
            lines.textContent = '☰';
            lines.title = 'Choose the hunks or lines of this file to commit';
            lines.onclick = (ev) => { ev.stopPropagation(); void chooseLines(f); };
            if (partial.has(f.path)) {
                const c = counts(partial.get(f.path).diff, partial.get(f.path).sel);
                const badge = document.createElement('span');
                badge.className = 'part';
                badge.textContent = `${c.chosen}/${c.total} lines`;
                path.append(badge);
                lines.style.visibility = 'visible';
            }
            const roll = document.createElement('button');
            roll.className = 'roll';
            roll.textContent = '↶';
            roll.title = 'Roll back this file to the last commit (cannot be undone)';
            roll.onclick = (ev) => { ev.stopPropagation(); void rollback([f]); };
            row.append(box1, st, path, lines, roll);
            row.onclick = () => void showChange(f);
            row.ondblclick = () => { if (f.worktree !== 'D' && f.index !== 'D')
                void deps.openFile(f.path); };
            box.append(row);
        }
        syncAll();
    }
    function syncAll() {
        const all = $('git-all');
        const files = status.files ?? [];
        all.checked = files.length > 0 && files.every((f) => !unchecked.has(f.path));
        all.indeterminate = partial.size > 0;
    }
    $('git-all').onchange = () => {
        partial.clear();
        const on = $('git-all').checked;
        for (const f of status.files ?? []) {
            if (on)
                unchecked.delete(f.path);
            else
                unchecked.add(f.path);
        }
        renderChanges();
    };
    async function rollback(files) {
        const names = files.map((f) => f.path).join(', ');
        if (!window.confirm(`Roll back ${names} to the last commit?\n\nThe changes are thrown away and cannot be restored.`))
            return;
        try {
            await post('/api/git/discard', { paths: files.map((f) => f.path) });
            for (const f of files)
                await deps.reload(f.path);
            toast('Rolled back');
        }
        catch (e) {
            toast(String(e.message ?? e), true);
        }
        await refresh();
    }
    const result = (text, bad = false) => { $('git-result').textContent = text; $('git-result').className = bad ? 'bad' : ''; };
    async function commit() {
        const amend = $('git-amend').checked;
        const paths = (status.files ?? []).filter((f) => !unchecked.has(f.path) && !partial.has(f.path)).map((f) => f.path);
        const message = $('git-message').value;
        const chosen = {};
        for (const [rel, p] of partial)
            chosen[rel] = payload(p.sel);
        if (!amend && paths.length === 0 && partial.size === 0) {
            result('Select the files to commit.', true);
            return;
        }
        if (!amend && !message.trim()) {
            result('Write a commit message.', true);
            $('git-message').focus();
            return;
        }
        $('git-commit').disabled = true;
        try {
            await deps.saveAll();
            const { hash } = await post('/api/git/commit', { paths, message, amend, ...(partial.size ? { partial: chosen } : {}) });
            partial.clear();
            $('git-message').value = '';
            $('git-amend').checked = false;
            result('');
            toast(`${amend ? 'Amended' : 'Committed'} ${hash.slice(0, 7)}`);
            logEnd = false;
            logSkip = 0;
            $('git-log').replaceChildren();
        }
        catch (e) {
            const text = String(e.message ?? e);
            result(text, true);
            // a file that changed under a selection of its lines: that selection is void
            if (/changed since its lines were chosen/.test(text))
                partial.clear();
        }
        finally {
            $('git-commit').disabled = false;
        }
        await refresh();
    }
    $('git-commit').onclick = () => void commit();
    // amending starts from the last message
    $('git-amend').onchange = async () => {
        if (!$('git-amend').checked || $('git-message').value)
            return;
        try {
            const [last] = await api('/api/git/log?limit=1');
            if (last)
                $('git-message').value = last.subject;
        }
        catch { /* no commit to amend */ }
    };
    // ------------------------------------------------------------------ choosing lines
    let choosing = null; // { f, diff, sel }
    function paintLines() {
        const { diff, sel } = choosing;
        const body = $('git-lines-body');
        body.replaceChildren();
        diff.hunks.forEach((hunk, h) => {
            const head = document.createElement('div');
            head.className = 'hl-head';
            const box = document.createElement('input');
            box.type = 'checkbox';
            const st = hunkState(diff, sel, h);
            box.checked = st === 'all';
            box.indeterminate = st === 'partial';
            box.title = 'The whole hunk';
            box.onchange = () => { toggleHunk(diff, sel, h); paintLines(); };
            const c = changedLines(hunk).length;
            const label = document.createElement('span');
            label.textContent = `@@ -${hunk.oldStart} +${hunk.newStart} @@`;
            const sec = document.createElement('span');
            sec.className = 'dim';
            sec.textContent = `${hunk.section}  (${c} change${c === 1 ? '' : 's'})`;
            head.append(box, label, sec);
            body.append(head);
            const nums = numbering(hunk);
            hunk.lines.forEach((l, i) => {
                const row = document.createElement('div');
                row.className = 'hl-row ' + (l.kind === '+' ? 'hl-add' : l.kind === '-' ? 'hl-del' : 'hl-ctx');
                if (l.kind === ' ') {
                    const sp = document.createElement('span');
                    sp.className = 'sp';
                    row.append(sp);
                }
                else {
                    const cb = document.createElement('input');
                    cb.type = 'checkbox';
                    cb.checked = !!sel.hunks.get(h)?.has(i);
                    cb.onchange = () => { toggleLine(diff, sel, h, i); paintLines(); };
                    row.append(cb);
                }
                const [o, n] = nums[i];
                const no = document.createElement('span');
                no.className = 'hl-num';
                no.textContent = o ?? '';
                const nn = document.createElement('span');
                nn.className = 'hl-num';
                nn.textContent = n ?? '';
                const text = document.createElement('span');
                text.textContent = l.kind + l.text.replace(/\r$/, '');
                row.append(no, nn, text);
                if (l.kind !== ' ')
                    row.onclick = (ev) => { if (ev.target.tagName !== 'INPUT') { toggleLine(diff, sel, h, i); paintLines(); } };
                body.append(row);
            });
        });
        const k = counts(diff, sel);
        $('git-lines-count').textContent = `${k.chosen} of ${k.total} changed lines`;
    }
    async function chooseLines(f) {
        let diff;
        try {
            await deps.saveAll();
            diff = await api('/api/git/diff?path=' + encodeURIComponent(f.path));
        }
        catch (e) {
            toast(String(e.message ?? e), true);
            return;
        }
        if (diff.isBinary || diff.isDeleted || diff.hunks.length === 0) {
            toast(diff.isBinary ? 'A binary file can only be committed whole.' : diff.isDeleted ? 'A deleted file can only be committed whole.' : 'There are no changed lines to choose.', true);
            return;
        }
        const known = partial.get(f.path);
        const sel = known && known.sel.id === diff.id ? known.sel : unchecked.has(f.path) ? selectNone(diff) : selectAll(diff);
        choosing = { f, diff, sel };
        $('git-lines-title').textContent = `${f.path} — choose what to commit`;
        $('git-lines').classList.add('open');
        paintLines();
    }
    function closeLines(keep) {
        if (keep && choosing) {
            const { f, diff, sel } = choosing;
            const st = stateOf(diff, sel);
            if (st === 'partial')
                partial.set(f.path, { diff, sel });
            else {
                partial.delete(f.path);
                if (st === 'all')
                    unchecked.delete(f.path);
                else
                    unchecked.add(f.path);
            }
            renderChanges();
        }
        choosing = null;
        $('git-lines').classList.remove('open');
    }
    $('git-lines-all').onclick = () => { if (choosing) { choosing.sel = selectAll(choosing.diff); paintLines(); } };
    $('git-lines-none').onclick = () => { if (choosing) { choosing.sel = selectNone(choosing.diff); paintLines(); } };
    $('git-lines-done').onclick = () => closeLines(true);
    // ------------------------------------------------------------------ diffs
    let diffEditor = null;
    let diffModels = [];
    function closeDiff() {
        $('git-diff').classList.remove('open');
        diffEditor?.setModel(null); // detach first: a diff editor must not outlive its models
        for (const m of diffModels)
            m.dispose();
        diffModels = [];
    }
    $('git-diff-close').onclick = closeDiff;
    function showDiff(title, before, after, rel) {
        $('git-diff-title').textContent = title;
        $('git-diff').classList.add('open');
        if (!diffEditor) {
            diffEditor = monaco.editor.createDiffEditor($('git-diff-body'), {
                readOnly: true, renderSideBySide: true, automaticLayout: true, originalEditable: false, minimap: { enabled: false }, scrollBeyondLastLine: false,
            });
        }
        diffEditor.setModel(null);
        for (const m of diffModels)
            m.dispose();
        const lang = rel.endsWith('.py') ? 'python' : undefined;
        const original = monaco.editor.createModel(before, lang);
        const modified = monaco.editor.createModel(after, lang);
        diffModels = [original, modified];
        diffEditor.setModel({ original, modified });
    }
    async function headText(rel) {
        if (baseText.has(rel))
            return baseText.get(rel);
        const res = await fetch('/api/git/file?path=' + encodeURIComponent(rel));
        const text = res.status === 200 ? await res.text() : null; // 204: the file is not in the last commit
        baseText.set(rel, text);
        return text;
    }
    async function workingText(rel) {
        const open = monaco.editor.getModels().find((m) => m.uri.path === '/' + rel);
        if (open)
            return open.getValue();
        const res = await fetch('/api/file?path=' + encodeURIComponent(rel));
        return res.ok ? await res.text() : '';
    }
    async function showChange(f) {
        const before = f.index === '?' ? '' : (await headTextOf(f.originalPath ?? f.path)) ?? '';
        const after = f.worktree === 'D' || f.index === 'D' ? '' : await workingText(f.path);
        showDiff(`${f.path} — working copy against the last commit`, before, after, f.path);
    }
    const headTextOf = async (rel) => { baseText.delete(rel); return headText(rel); };
    async function showCommitFile(hash, f) {
        const fetchAt = async (rev, rel) => {
            const res = await fetch(`/api/git/file?path=${encodeURIComponent(rel)}&rev=${encodeURIComponent(rev)}`);
            return res.status === 200 ? await res.text() : '';
        };
        const before = f.status === 'A' ? '' : await fetchAt(hash + '^', f.oldPath ?? f.path);
        const after = f.status === 'D' ? '' : await fetchAt(hash, f.path);
        showDiff(`${f.path} — ${hash.slice(0, 7)}`, before, after, f.path);
    }
    // ------------------------------------------------------------------ the gutter
    let paintTimer;
    /** The changes of an editor's file against the last commit, as hunks and the bars beside the line numbers; null when there is nothing to show. */
    async function barsOf(ed) {
        const model = ed.getModel();
        const rel = model && deps.relOf(model);
        if (!model || !rel || !status.available)
            return null;
        const entry = fileStatus(rel);
        let base = await headText(rel);
        if (ed.getModel() !== model)
            return undefined;   // another file was opened meanwhile: its own paint follows
        if (base === null) {
            // not in the last commit: all green when git knows the file as new, nothing when it is outside git's eyes (ignored)
            if (!entry || entry.worktree === 'D')
                return null;
            base = '';
        }
        const found = lineHunks(normalise(base), normalise(model.getValue()));
        const lines = model.getLineCount();
        const bars = found.map((h) => {
            const kind = hunkKind(h);
            const first = Math.min(h.newStart + 1, lines);
            const last = kind === 'deleted' ? first : Math.min(h.newStart + h.newCount, lines);
            return { range: new monaco.Range(first, 1, last, 1), options: { isWholeLine: false, linesDecorationsClassName: `git-bar-${kind}` } };
        });
        return { rel, hunks: found, bars };
    }
    async function paint() {
        const result = await barsOf(editor);
        if (result === undefined)
            return;
        if (result === null) {
            decorations.clear();
            hunks = [];
            hunksFor = '';
        }
        else {
            hunks = result.hunks;
            hunksFor = result.rel;
            decorations.set(result.bars);
        }
        for (const x of others)
            void paintOther(x);
    }
    // other editors (the right side of a split) show the same bars for their own file
    const others = [];
    async function paintOther(x) {
        const result = await barsOf(x.ed);
        if (result === undefined)
            return;
        if (result === null) {
            x.decorations.clear();
            x.hunks = [];
        }
        else {
            x.decorations.set(result.bars);
            x.hunks = result.hunks;
        }
    }
    function attach(ed) {
        const x = { ed, decorations: ed.createDecorationsCollection(), timer: 0, hunks: [] };
        others.push(x);
        hunkPopups(ed, () => x.hunks);
        const later = (delay) => { clearTimeout(x.timer); x.timer = window.setTimeout(() => void paintOther(x), delay); };
        ed.onDidChangeModel(() => later(50));
        ed.onDidChangeModelContent(() => later(300));
        ed.onDidDispose(() => { const i = others.indexOf(x); if (i >= 0)
            others.splice(i, 1); });
        later(50);
    }
    const schedulePaint = (delay = 300) => { clearTimeout(paintTimer); paintTimer = window.setTimeout(() => void paint(), delay); };
    editor.onDidChangeModel(() => { schedulePaint(50); if (annotate)
        void loadBlame(); });
    editor.onDidChangeModelContent(() => { schedulePaint(); if (annotate)
        editor.updateOptions({ lineNumbers: lineNumberFor }); });
    function hunkAt(ed, hunks, line1) {
        const model = ed.getModel();
        const lines = model?.getLineCount() ?? 0;
        return hunks.find((h) => {
            const kind = hunkKind(h);
            const first = Math.min(h.newStart + 1, lines);
            const last = kind === 'deleted' ? first : h.newStart + h.newCount;
            return line1 >= first && line1 <= last;
        });
    }
    /** Puts a stretch of lines back as it was: replaces the changed lines with the old ones, or inserts the deleted ones. */
    function revert(ed, h) {
        const model = ed.getModel();
        if (!model)
            return;
        const eol = model.getEOL();
        const n = model.getLineCount();
        const first = h.newStart + 1; // 1-based first changed line
        const last = h.newStart + h.newCount; // 1-based last changed line (first - 1 for a pure deletion)
        let range;
        let text;
        if (h.newCount > 0) {
            if (h.oldLines.length > 0) {
                range = new monaco.Range(first, 1, last, model.getLineMaxColumn(last));
                text = h.oldLines.join(eol);
            }
            else if (last < n) {
                range = new monaco.Range(first, 1, last + 1, 1);
                text = '';
            } // added lines: take them out
            else if (first > 1) {
                range = new monaco.Range(first - 1, model.getLineMaxColumn(first - 1), last, model.getLineMaxColumn(last));
                text = '';
            }
            else {
                range = new monaco.Range(1, 1, last, model.getLineMaxColumn(last));
                text = '';
            }
        }
        else if (first <= n) {
            range = new monaco.Range(first, 1, first, 1);
            text = h.oldLines.join(eol) + eol;
        } // deleted lines: put them back above
        else {
            range = new monaco.Range(n, model.getLineMaxColumn(n), n, model.getLineMaxColumn(n));
            text = eol + h.oldLines.join(eol);
        }
        ed.pushUndoStop();
        ed.executeEdits('git-revert', [{ range, text }]);
        ed.pushUndoStop();
    }
    /** A click on a change bar of `ed` shows what the lines were, with Revert and Show diff; `hunksOf` gives that editor's hunks. */
    function hunkPopups(ed, hunksOf) {
        ed.onMouseDown((e) => {
            if (e.target.type !== monaco.editor.MouseTargetType.GUTTER_LINE_DECORATIONS || !e.target.position)
                return;
            if (e.target.element && [...e.target.element.classList].some((c) => /^cov-(hit|part|miss)$/.test(c)))
                return;
            const h = hunkAt(ed, hunksOf(), e.target.position.lineNumber);
            if (!h)
                return;
            closePopup();
            const kind = hunkKind(h);
            popup = document.createElement('div');
            popup.className = 'git-popup';
            const title = document.createElement('div');
            title.className = 'meta';
            title.textContent = kind === 'added' ? `${h.newCount} line${h.newCount === 1 ? '' : 's'} added` : kind === 'deleted' ? `${h.oldLines.length} line${h.oldLines.length === 1 ? '' : 's'} deleted` : 'Changed from:';
            popup.append(title);
            if (h.oldLines.length) {
                const pre = document.createElement('pre');
                pre.textContent = h.oldLines.join('\n');
                popup.append(pre);
            }
            const undo = document.createElement('button');
            undo.textContent = 'Revert';
            undo.onclick = () => { closePopup(); revert(ed, h); };
            const diff = document.createElement('button');
            diff.textContent = 'Show diff';
            diff.onclick = async () => {
                closePopup();
                const model = ed.getModel();
                const rel = model && deps.relOf(model);
                if (rel)
                    showDiff(`${rel} — working copy against the last commit`, (await headText(rel)) ?? '', model.getValue(), rel);
            };
            popup.append(undo, diff);
            const ev = e.event.browserEvent;
            popup.style.left = `${Math.min(ev.clientX + 8, window.innerWidth - 360)}px`;
            popup.style.top = `${Math.min(ev.clientY, window.innerHeight - 220)}px`;
            document.body.append(popup);
        });
    }
    hunkPopups(editor, () => hunks);
    // ------------------------------------------------------------------ blame
    let annotate = false;
    let inline = false;
    let blame = [];
    let blameFor = '';
    let blameText = '';
    const inlineDecoration = editor.createDecorationsCollection();
    const blameStyle = document.head.appendChild(document.createElement('style'));
    async function loadBlame() {
        const model = editor.getModel();
        const rel = model && deps.relOf(model);
        if (!model || !rel || !status.available)
            return;
        const entry = fileStatus(rel);
        if (entry?.index === '?' || entry?.index === 'A') {
            blame = [];
            blameFor = rel;
            applyBlame();
            return;
        }
        try {
            blame = await api('/api/git/blame?path=' + encodeURIComponent(rel));
            blameFor = rel;
            blameText = deps.savedText(rel) ?? ''; // blame describes the saved file; edits since then shift the lines
        }
        catch {
            blame = [];
            blameFor = rel;
        }
        applyBlame();
    }
    const zeros = '0'.repeat(40);
    /** The blame entry of a line of the editor, allowing for unsaved edits. */
    function blameOf(line1) {
        const model = editor.getModel();
        const rel = model && deps.relOf(model);
        if (!model || rel !== blameFor || blame.length === 0)
            return null;
        const saved = blameText === '' ? model.getValue() : blameText;
        const old = lineInOld(lineHunks(normalise(saved), normalise(model.getValue())), line1 - 1);
        return old === null ? null : blame[old] ?? null;
    }
    const lineNumberFor = (line) => {
        const b = blameOf(line);
        if (!b)
            return ' '.repeat(33) + line;
        const who = b.hash === zeros ? 'not committed' : b.author.slice(0, 13);
        return `${b.hash === zeros ? '       ' : b.hash.slice(0, 7)} ${who.padEnd(13)} ${(b.hash === zeros ? '' : ago(b.time)).padEnd(10)} ${line}`;
    };
    function applyBlame() {
        if (annotate)
            editor.updateOptions({ lineNumbers: lineNumberFor, lineNumbersMinChars: 40 });
        else
            editor.updateOptions({ lineNumbers: 'on', lineNumbersMinChars: 5 });
        paintInline();
    }
    function paintInline() {
        const pos = editor.getPosition();
        const b = inline && pos ? blameOf(pos.lineNumber) : null;
        if (!pos || !b) {
            inlineDecoration.clear();
            return;
        }
        const text = b.hash === zeros ? 'Not committed yet' : `${b.author}, ${ago(b.time)} • ${b.summary}`;
        // Monaco as built here draws no `after` text of a decoration: the text is the ::after of an empty one at the line's end
        blameStyle.textContent = `.git-blame-inline::after { content: ${JSON.stringify(text)}; }`;
        const end = editor.getModel().getLineMaxColumn(pos.lineNumber);
        inlineDecoration.set([{
                range: new monaco.Range(pos.lineNumber, end, pos.lineNumber, end),
                options: { afterContentClassName: 'git-blame-inline', showIfCollapsed: true },
            }]);
    }
    editor.onDidChangeCursorPosition(() => { if (inline)
        paintInline(); });
    $('git-annotate').onchange = () => {
        annotate = $('git-annotate').checked;
        try {
            localStorage.setItem('pymagic.git.annotate', annotate ? '1' : '0');
        }
        catch { /* storage unavailable */ }
        if (annotate)
            void loadBlame();
        else
            applyBlame();
    };
    $('git-inline').onchange = () => {
        inline = $('git-inline').checked;
        try {
            localStorage.setItem('pymagic.git.inline', inline ? '1' : '0');
        }
        catch { /* storage unavailable */ }
        if (inline && blameFor !== deps.currentRel())
            void loadBlame();
        else
            paintInline();
    };
    // clicking an annotation shows the commit
    editor.onMouseDown((e) => {
        if (!annotate || e.target.type !== monaco.editor.MouseTargetType.GUTTER_LINE_NUMBERS || !e.target.position)
            return;
        const b = blameOf(e.target.position.lineNumber);
        if (!b || b.hash === zeros)
            return;
        closePopup();
        popup = document.createElement('div');
        popup.className = 'git-popup';
        popup.innerHTML = '';
        const meta = document.createElement('div');
        meta.className = 'meta';
        meta.textContent = `${b.hash.slice(0, 7)} · ${b.author} · ${new Date(b.time * 1000).toLocaleString()}`;
        const msg = document.createElement('div');
        msg.textContent = b.summary;
        const open = document.createElement('button');
        open.textContent = 'Show in history';
        open.onclick = () => { closePopup(); showTab('git'); void selectCommit(b.hash); };
        popup.append(meta, msg, open);
        const ev = e.event.browserEvent;
        popup.style.left = `${ev.clientX + 8}px`;
        popup.style.top = `${Math.min(ev.clientY, window.innerHeight - 140)}px`;
        document.body.append(popup);
    });
    // ------------------------------------------------------------------ history
    let logTicket = 0; // only the newest request may fill the list: refreshes overlap
    async function loadLog(reset) {
        if (reset) {
            logSkip = 0;
            logEnd = false;
            $('git-log').replaceChildren();
        }
        if (logEnd || !status.available)
            return;
        const ticket = ++logTicket;
        const rel = $('git-file-log').checked ? deps.currentRel() : '';
        let commits;
        try {
            commits = await api(`/api/git/log?limit=60&skip=${logSkip}` + (rel ? '&path=' + encodeURIComponent(rel) : ''));
        }
        catch {
            return;
        }
        if (ticket !== logTicket)
            return;
        logSkip += commits.length;
        if (commits.length < 60)
            logEnd = true;
        const box = $('git-log');
        for (const c of commits) {
            const row = document.createElement('div');
            row.className = 'gl';
            row.dataset.hash = c.hash;
            const hash = document.createElement('span');
            hash.className = 'hash';
            hash.textContent = c.shortHash;
            const subject = document.createElement('span');
            subject.className = 'subject';
            subject.textContent = c.subject;
            subject.title = c.subject;
            row.append(hash);
            if (c.refs) {
                const r = document.createElement('span');
                r.className = 'refs';
                r.textContent = `(${c.refs.replace(/HEAD -> /, '')})`;
                row.append(r);
            }
            const who = document.createElement('span');
            who.className = 'who';
            who.textContent = `${c.author} · ${agoIso(c.date)}`;
            row.append(subject, who);
            row.onclick = () => void selectCommit(c.hash);
            box.append(row);
        }
        if (!commits.length && reset) {
            const none = document.createElement('div');
            none.className = 'gl';
            none.style.color = 'var(--dim)';
            none.textContent = 'No commits yet.';
            box.append(none);
        }
    }
    $('git-log').onscroll = () => { const b = $('git-log'); if (b.scrollTop + b.clientHeight > b.scrollHeight - 40)
        void loadLog(false); };
    $('git-file-log').onchange = () => void loadLog(true);
    editor.onDidChangeModel(() => { if ($('git-file-log').checked)
        void loadLog(true); });
    async function selectCommit(hash) {
        selectedCommit = hash;
        for (const row of Array.from($('git-log').children))
            row.classList.toggle('sel', row.dataset.hash === hash);
        const box = $('git-detail');
        try {
            const d = await api('/api/git/commit?hash=' + encodeURIComponent(hash));
            if (selectedCommit !== hash)
                return;
            box.replaceChildren();
            const meta = document.createElement('div');
            meta.className = 'meta';
            meta.textContent = `${d.commit.hash} · ${d.commit.author} <${d.commit.email}> · ${new Date(d.commit.date).toLocaleString()}`;
            const msg = document.createElement('div');
            msg.className = 'msg';
            msg.textContent = d.commit.subject + (d.body ? '\n\n' + d.body : '');
            box.append(meta, msg);
            const acts = document.createElement('div');
            acts.className = 'git-acts';
            for (const [label, title, run] of [
                ['Revert', 'Make a new commit that undoes this one', () => commitAction({ op: 'revert', hash }, 'Reverted')],
                ['Cherry-pick', 'Apply this commit on top of the current branch', () => commitAction({ op: 'cherryPick', hash }, 'Cherry-picked')],
                ['Reset to here…', 'Move the current branch to this commit', () => resetTo(hash)],
                ['Rebase from here…', 'Reorder, reword, squash or drop this commit and the ones after it', () => rebaseFrom(hash)],
                ['Tag…', 'Put a tag on this commit', () => tagCommit(hash)],
            ]) {
                const b = document.createElement('button');
                b.textContent = label;
                b.title = title;
                b.onclick = () => void run();
                acts.append(b);
            }
            box.append(acts);
            for (const f of d.files) {
                const row = document.createElement('div');
                row.className = 'gc';
                const st = document.createElement('span');
                st.className = `st st-${f.status}`;
                st.textContent = f.status;
                const path = document.createElement('span');
                path.className = 'path';
                path.textContent = f.oldPath ? `${f.oldPath} → ${f.path}` : f.path;
                row.append(st, path);
                row.onclick = () => void showCommitFile(hash, f);
                box.append(row);
            }
        }
        catch (e) {
            box.textContent = String(e.message ?? e);
        }
    }
    // ------------------------------------------------------------------ actions on a commit
    async function commitAction(body, done) {
        try {
            await deps.saveAll();
            const r = await post('/api/git/action', body);
            await deps.reloadAll();
            toast(r.conflicts ? `${done}: stopped on conflicts, resolve them above` : r.operation === 'rebase' ? 'Rebase stopped at the commit to edit: change it and commit with Amend, then Continue' : done);
        }
        catch (e) {
            toast(String(e.message ?? e), true);
        }
        logEnd = false;
        $('git-log').replaceChildren();
        await refresh();
    }
    async function rebaseFrom(hash) {
        try { await deps.saveAll(); }
        catch (e) { toast(String(e.message ?? e), true); return; }
        await openRebase({ base: hash + '^', label: hash.slice(0, 7), start: (plan) => commitAction({ op: 'rebaseInteractive', base: hash + '^', steps: plan }, 'Rebased') });
    }
    async function resetTo(hash) {
        const mode = window.prompt(`Move the current branch to ${hash.slice(0, 7)}.\n\nsoft: keep the changes staged\nmixed: keep the changes as changes\nhard: throw the changes away (cannot be undone)\n\nWhich?`, 'mixed');
        if (!mode)
            return;
        if (!['soft', 'mixed', 'hard'].includes(mode.trim())) {
            toast('Type soft, mixed or hard.', true);
            return;
        }
        if (mode.trim() === 'hard' && !window.confirm('A hard reset throws away every change that is not committed, and the commits after this one.\n\nContinue?'))
            return;
        await commitAction({ op: 'reset', hash, mode: mode.trim() }, `Reset (${mode.trim()}) to ${hash.slice(0, 7)}`);
    }
    async function tagCommit(hash) {
        const name = window.prompt(`Tag for ${hash.slice(0, 7)}:`);
        if (!name)
            return;
        const message = window.prompt('Message (leave empty for a plain tag):') ?? '';
        await commitAction({ op: 'createTag', name: name.trim(), hash, message }, `Tagged ${name.trim()}`);
    }
    // ------------------------------------------------------------------ branches
    async function loadBranches() {
        const select = $('git-branches');
        if (!status.available)
            return;
        try {
            const b = await api('/api/git/branches');
            select.replaceChildren(...b.local.map((name) => {
                const o = document.createElement('option');
                o.value = name;
                o.textContent = name;
                o.selected = name === b.current;
                return o;
            }));
            // branches of the remotes: choosing one makes a local branch that follows it
            const others = (b.remote ?? []).filter((r) => !b.local.some((l) => r.endsWith('/' + l)));
            if (others.length) {
                const group = document.createElement('optgroup');
                group.label = 'Remote branches';
                for (const name of others) {
                    const o = document.createElement('option');
                    o.value = 'remote:' + name;
                    o.textContent = name;
                    group.append(o);
                }
                select.append(group);
            }
            if (b.detached) {
                const o = document.createElement('option');
                o.textContent = '(detached)';
                o.selected = true;
                o.disabled = true;
                select.prepend(o);
            }
        }
        catch { /* the list stays as it was */ }
    }
    $('git-branches').onchange = async () => {
        const chosen = $('git-branches').value;
        const name = chosen.replace(/^remote:/, '');
        try {
            await deps.saveAll();
            if (chosen.startsWith('remote:'))
                await post('/api/git/action', { op: 'checkoutRemote', branch: name });
            else
                await post('/api/git/checkout', { branch: name });
            await deps.reloadAll();
            toast(`Switched to ${name}`);
        }
        catch (e) {
            toast(String(e.message ?? e), true);
        }
        logEnd = false;
        $('git-log').replaceChildren();
        await refresh();
    };
    $('git-new').onclick = async () => {
        const input = $('git-new-name');
        if (input.style.display === 'none') {
            input.style.display = '';
            input.focus();
            return;
        }
        const name = input.value.trim();
        if (!name) {
            input.style.display = 'none';
            return;
        }
        try {
            await post('/api/git/branch', { name });
            input.value = '';
            input.style.display = 'none';
            toast(`Created and switched to ${name}`);
        }
        catch (e) {
            toast(String(e.message ?? e), true);
        }
        await refresh();
    };
    $('git-new-name').onkeydown = (ev) => { if (ev.key === 'Enter')
        $('git-new').click(); };
    $('git-refresh').onclick = () => void refresh();
    $('git-init').onclick = async () => {
        try {
            await post('/api/git/init', {});
            toast('Repository created');
        }
        catch (e) {
            toast(String(e.message ?? e), true);
        }
        await refresh();
    };
    // ------------------------------------------------------------------ wiring
    $('t-git').onclick = () => { showTab('git'); void refresh(); };
    editor.addCommand(monaco.KeyMod.Alt | monaco.KeyCode.Digit9, () => { showTab('git'); void refresh(); $('git-message').focus(); });
    window.addEventListener('pymagic:saved', () => void refresh());
    window.addEventListener('focus', () => void refresh());
    document.addEventListener('visibilitychange', () => { if (!document.hidden)
        void refresh(); });
    setInterval(() => { if (!document.hidden)
        void refresh(); }, 20_000);
    window.addEventListener('pymagic:files', paintFileList);
    try {
        annotate = localStorage.getItem('pymagic.git.annotate') === '1';
        inline = localStorage.getItem('pymagic.git.inline') === '1';
    }
    catch { /* storage unavailable */ }
    $('git-annotate').checked = annotate;
    $('git-inline').checked = inline;
    void refresh();
    return { refresh, showDiff, attach, status: () => status };
}

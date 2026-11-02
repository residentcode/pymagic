// Local history (the History tab of the dock): the versions the open file had at its saves, and for each one a comparison with what the editor holds
// now, from which that version can be brought back; two versions compared with each other; the history of the lines selected; the versions of a
// folder or the whole project (Recent Changes, deleted files too) with the labels among them, and going back to a label. The engine keeps the
// versions and labels (LocalHistory, in .pymagic/history).
import { showTab } from './panel';
import { ago, clock, interleave, selectionHistory, size } from './history-model.js';
import { toast } from './toast';
import { languageFor } from './file-language';

const $ = (id) => document.getElementById(id);

export function setupHistory({ monaco, editor, currentFile, openFile, askText }) {
    let mode = 'file';     // 'file' | 'folder' (a folder's history, '' for the project) | 'selection'
    let versions = [];
    let labels = [];
    let folder = '';
    let selection = null;  // { rel, from, to (0-based lines of the file as it was), found: [{id, time, text, previous}] }
    const picked = [];     // ids of the versions chosen for comparing with each other (file mode)
    let ticket = 0;
    const active = () => $('history-tab').classList.contains('active');
    const getJson = (url) => fetch(url).then((r) => (r.ok ? r.json() : [])).catch(() => []);
    const textOf = (rel, id) => fetch(`/api/history/text?path=${encodeURIComponent(rel)}&id=${id}`).then((r) => (r.ok ? r.text() : null)).catch(() => null);
    const beforeOf = (rel, id) => fetch(`/api/history/before?path=${encodeURIComponent(rel)}&id=${id}`).then((r) => (r.ok ? r.text() : null)).catch(() => null);

    async function refresh() {
        const mine = ++ticket;
        const title = $('history-title');
        $('hist-deleted-box').hidden = mode !== 'folder';
        $('hist-two').hidden = mode !== 'file';
        $('hist-selection-back').hidden = mode !== 'selection';
        const [labelList] = await Promise.all([getJson('/api/history/labels')]);
        if (mine !== ticket) return;
        labels = labelList;
        if (mode === 'selection') {
            title.textContent = `Local history of lines ${selection.from + 1}–${selection.to + 1} of ${selection.rel}`;
            draw();
            return;
        }
        if (mode === 'folder') {
            title.textContent = folder ? `Local history of ${folder}` : 'Recent changes of the project';
            const found = await getJson('/api/history/folder?path=' + encodeURIComponent(folder));
            if (mine !== ticket) return;
            versions = $('hist-deleted').checked ? found.filter((v) => !v.exists) : found;
            draw();
            return;
        }
        const rel = currentFile();
        title.textContent = rel ? `Local history of ${rel}` : 'Local history';
        if (!rel) { versions = []; draw(); return; }
        const found = await getJson('/api/history?path=' + encodeURIComponent(rel));
        if (mine !== ticket) return;
        versions = found;
        draw();
    }

    function cell(cls, text) {
        const span = document.createElement('span');
        span.className = cls;
        span.textContent = text;
        return span;
    }

    function labelRow(l, now) {
        const row = document.createElement('div');
        row.className = 'hist-row hist-label' + (l.auto ? ' auto' : '');
        row.append(cell('hist-when', clock(now, Date.parse(l.time))), cell('hist-ago', ago(now, Date.parse(l.time))), cell('hist-name', '● ' + l.name));
        const back = document.createElement('button');
        back.className = 'mini';
        back.textContent = 'Go back to it…';
        back.title = 'Write every file as the project had it at this label (a label of its own is put first, so this can be undone)';
        back.onclick = (e) => { e.stopPropagation(); void goBack(l); };
        const remove = document.createElement('button');
        remove.className = 'mini';
        remove.textContent = 'Remove';
        remove.onclick = (e) => { e.stopPropagation(); void fetch('/api/history/label?id=' + l.id, { method: 'DELETE' }).then(refresh); };
        const tag = l.auto ? cell('hist-tag', 'by PyMagic') : null;
        row.append(...[tag, back, remove].filter(Boolean));
        return row;
    }

    function draw() {
        const box = $('history-list');
        box.replaceChildren();
        const now = Date.now();
        if (mode === 'selection') {
            if (!selection.found.length) {
                box.append(cell('side-empty', 'These lines were never different in the versions kept.'));
                return;
            }
            for (const v of selection.found) {
                const row = document.createElement('div');
                row.className = 'hist-row';
                const snippet = v.text.split('\n').find((l) => l.trim()) ?? '(nothing: the lines were not there)';
                row.append(cell('hist-when', clock(now, Date.parse(v.time))), cell('hist-ago', ago(now, Date.parse(v.time))), cell('hist-snippet', snippet.trim().slice(0, 90)));
                row.onclick = () => selectionDiff(v);
                box.append(row);
            }
            return;
        }
        const rows = interleave(versions, labels);
        if (versions.length === 0 && labels.length === 0) {
            box.append(cell('side-empty', mode === 'folder' ? 'No versions of files in here yet: each save keeps one.' : currentFile() ? 'No versions yet: each save of this file keeps one.' : 'Open a file to see the versions it had.'));
            return;
        }
        let first = true;
        for (const r of rows) {
            if (r.type === 'label') { box.append(labelRow(r, now)); continue; }
            const v = r;
            const row = document.createElement('div');
            row.className = 'hist-row' + (picked.includes(v.id) ? ' picked' : '');
            row.append(cell('hist-when', clock(now, Date.parse(v.time))), cell('hist-ago', ago(now, Date.parse(v.time))), cell('hist-size', size(v.bytes)));
            if (mode === 'folder') {
                // a folder's rows name their file; clicking shows what that save changed, or writes a deleted file back
                row.append(cell('hist-tag', v.exists ? v.path : `${v.path} (deleted)`));
                row.title = v.exists ? 'What this save changed' : 'The file was deleted: click to write this version back';
                row.onclick = () => void (v.exists ? openChange(v) : restoreDeleted(v));
                box.append(row);
                continue;
            }
            if (first) row.append(cell('hist-tag', 'last save'));
            first = false;
            row.title = 'Click: compare with the editor · Ctrl+click two versions to compare them with each other';
            row.onclick = (e) => {
                if (e.ctrlKey || e.metaKey) {
                    const at = picked.indexOf(v.id);
                    if (at >= 0) picked.splice(at, 1); else { picked.push(v.id); if (picked.length > 2) picked.shift(); }
                    $('hist-two').disabled = picked.length !== 2;
                    draw();
                    return;
                }
                void compare(v);
            };
            box.append(row);
        }
    }

    // ------------------------------------------------------------ the comparisons

    /** A window with two texts side by side (or inline), and an optional button that does something with the old one. */
    function showDiff({ title, original, modified, language, action }) {
        const shade = document.createElement('div');
        shade.className = 'settings-shade';
        const box = document.createElement('div');
        box.className = 'hist-diff';
        const head = document.createElement('div');
        head.className = 'hist-diff-head';
        const heading = cell('hist-diff-title', title);
        const mode = document.createElement('button');
        mode.textContent = 'Side by side';
        head.append(heading, mode);
        let doAction = null;
        if (action) {
            doAction = document.createElement('button');
            doAction.className = 'primary';
            doAction.textContent = action.label;
            doAction.title = action.title ?? '';
            head.append(doAction);
        }
        const close = document.createElement('button');
        close.textContent = 'Close';
        head.append(close);
        const body = document.createElement('div');
        body.className = 'hist-diff-body';
        box.append(head, body);
        shade.append(box);
        document.body.append(shade);

        const oldModel = monaco.editor.createModel(original, language);
        const newModel = monaco.editor.createModel(modified, language);
        let sideBySide = true;
        const diff = monaco.editor.createDiffEditor(body, { readOnly: true, originalEditable: false, renderSideBySide: sideBySide, automaticLayout: true, minimap: { enabled: false }, scrollBeyondLastLine: false });
        diff.setModel({ original: oldModel, modified: newModel });
        const done = () => {
            document.removeEventListener('keydown', onKey, true);
            diff.dispose();
            oldModel.dispose();
            newModel.dispose();
            shade.remove();
            editor.focus();
        };
        const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); done(); } };
        document.addEventListener('keydown', onKey, true);
        close.onclick = done;
        shade.onmousedown = (e) => { if (e.target === shade) done(); };
        mode.onclick = () => { sideBySide = !sideBySide; diff.updateOptions({ renderSideBySide: sideBySide }); mode.textContent = sideBySide ? 'Side by side' : 'Inline'; };
        if (doAction) doAction.onclick = () => { if (action.run()) done(); };
    }

    async function openAndCompare(v) {
        await openFile(v.path);
        if (currentFile() === v.path) await compare(v);
    }

    /** What one save of a file changed: the version before it and the version itself. */
    async function openChange(v) {
        const [after, before] = await Promise.all([textOf(v.path, v.id), beforeOf(v.path, v.id)]);
        if (after === null) { toast('That version is gone.', true); return; }
        showDiff({
            title: `${v.path}: what the save of ${clock(Date.now(), Date.parse(v.time))} changed`,
            original: before ?? '', modified: after, language: languageOf(v.path),
            action: { label: 'Open the file', title: 'Open it in the editor', run: () => { void openFile(v.path); return true; } },
        });
    }

    const languageOf = (path) => languageFor(monaco, path);

    async function restoreDeleted(v) {
        if (!window.confirm(`${v.path} was deleted. Write this version back to it?`)) return;
        const text = await textOf(v.path, v.id);
        if (text === null) { toast('That version is gone.', true); return; }
        const res = await fetch('/api/file?path=' + encodeURIComponent(v.path), { method: 'PUT', body: text }).catch(() => null);
        if (!res?.ok) { toast('The file could not be written.', true); return; }
        toast(`${v.path} is back.`);
        await openFile(v.path);
        void refresh();
    }

    async function compare(version) {
        const rel = currentFile();
        const model = editor.getModel();
        if (!rel || !model) return;
        const old = await textOf(rel, version.id);
        if (old === null) { toast('That version is gone.', true); return; }
        showDiff({
            title: `${rel}: the version of ${clock(Date.now(), Date.parse(version.time))} and what the editor holds now`,
            original: old, modified: model.getValue(), language: model.getLanguageId(),
            action: {
                label: 'Restore this version',
                title: 'Put this version in the editor (undo brings the present one back); it is saved when the file is',
                run: () => {
                    if (editor.getModel() !== model) { toast('The file in the editor is another one now.', true); return false; }
                    editor.pushUndoStop();
                    editor.executeEdits('local-history', [{ range: model.getFullModelRange(), text: old }]);
                    editor.pushUndoStop();
                    toast('The version is in the editor. Save to keep it; undo brings back the one you had.');
                    return true;
                },
            },
        });
    }

    /** Two versions of the open file compared with each other (the older on the left). */
    async function compareTwo() {
        const rel = currentFile();
        if (!rel || picked.length !== 2) return;
        const [a, b] = picked.slice().sort();
        const [older, newer] = await Promise.all([textOf(rel, a), textOf(rel, b)]);
        if (older === null || newer === null) { toast('One of those versions is gone.', true); return; }
        const when = (id) => clock(Date.now(), Date.parse(versions.find((v) => v.id === id)?.time));
        showDiff({ title: `${rel}: the version of ${when(a)} and the version of ${when(b)}`, original: older, modified: newer, language: editor.getModel()?.getLanguageId() });
    }

    // ------------------------------------------------------------ the lines selected

    async function showSelection() {
        const rel = currentFile();
        const model = editor.getModel();
        const sel = editor.getSelection();
        if (!rel || !model || !sel) { toast('Open a file and select some lines first.', true); return; }
        let from = sel.startLineNumber - 1, to = sel.endLineNumber - 1;
        if (to > from && sel.endColumn === 1) to--;
        const list = await getJson('/api/history?path=' + encodeURIComponent(rel));
        const texts = await Promise.all(list.map(async (v) => ({ ...v, text: await textOf(rel, v.id) })));
        const found = selectionHistory(model.getValue(), from, to, texts.filter((v) => v.text !== null));
        selection = { rel, from, to, found, model };
        mode = 'selection';
        showTab('history');
        void refresh();
    }

    function selectionDiff(v) {
        const { rel, from, to, model } = selection;
        showDiff({
            title: `${rel}, lines ${from + 1}–${to + 1}: the version of ${clock(Date.now(), Date.parse(v.time))} and the next newer one`,
            original: v.text, modified: v.previous, language: model.getLanguageId(),
            action: {
                label: 'Restore these lines',
                title: 'Put the older lines in the editor where the chosen lines are now',
                run: () => {
                    if (editor.getModel() !== model) { toast('The file in the editor is another one now.', true); return false; }
                    const range = new monaco.Range(from + 1, 1, to + 1, model.getLineMaxColumn(to + 1));
                    editor.pushUndoStop();
                    editor.executeEdits('local-history', [{ range, text: v.text }]);
                    editor.pushUndoStop();
                    toast('The lines are in the editor. Save to keep them.');
                    return true;
                },
            },
        });
    }

    // ------------------------------------------------------------ labels

    async function putLabelByHand() {
        const name = await askText('Put a label', '', 'A name for the project as it is now; Local History can go back to it');
        if (!name?.trim()) return;
        const res = await fetch('/api/history/label', { method: 'POST', body: JSON.stringify({ name: name.trim(), auto: false }) }).catch(() => null);
        if (!res?.ok) { toast('The label could not be put.', true); return; }
        toast(`Label '${name.trim()}' put.`);
        void refresh();
    }

    async function goBack(label) {
        const plan = await fetch('/api/history/revert', { method: 'POST', body: JSON.stringify({ label: label.id, dry: true }) }).then((r) => (r.ok ? r.json() : null)).catch(() => null);
        if (!plan) { toast('That label is gone.', true); return; }
        if (plan.files.length === 0) { toast('The project is as it was at that label.'); return; }
        const names = plan.files.slice(0, 12).map((f) => (f.missing ? `${f.path} (written back)` : f.path)).join('\n');
        const more = plan.files.length > 12 ? `\n… and ${plan.files.length - 12} more` : '';
        if (!window.confirm(`Go back to '${label.name}'?\n\n${plan.files.length} file(s) are written as they were:\n${names}${more}\n\nA label of the present is put first, so this can be undone.`)) return;
        const done = await fetch('/api/history/revert', { method: 'POST', body: JSON.stringify({ label: label.id, dry: false }) }).then((r) => (r.ok ? r.json() : null)).catch(() => null);
        if (!done) { toast('Going back failed.', true); return; }
        toast(`${done.files.length} file(s) are as they were at '${label.name}'.`);
        window.dispatchEvent(new CustomEvent('pymagic:filesRestored', { detail: done.files.map((f) => f.path) }));
        void refresh();
    }

    function show(next, dir = '') {
        mode = next;
        folder = dir;
        picked.length = 0;
        $('hist-two').disabled = true;
        $('history-list').replaceChildren();      // not the rows of the other view while this one is read
        showTab('history');
        void refresh();
    }

    $('t-history').onclick = () => show('file');
    $('hist-refresh').onclick = () => void refresh();
    $('hist-label').onclick = () => void putLabelByHand();
    $('hist-project').onclick = () => show('folder', '');
    $('hist-selection').onclick = () => void showSelection();
    $('hist-selection-back').onclick = () => show('file');
    $('hist-two').onclick = () => void compareTwo();
    $('hist-deleted').onchange = () => void refresh();
    editor.addAction({
        id: 'pymagic.history.selection', label: 'Local History: Show History for Selection', contextMenuGroupId: 'navigation', contextMenuOrder: 9.8,
        run: () => void showSelection(),
    });
    editor.onDidChangeModel(() => { if (active() && mode === 'file') void refresh(); });
    window.addEventListener('pymagic:saved', () => { if (active() && mode !== 'selection') void refresh(); });
    window.addEventListener('pymagic:labelPut', () => { if (active()) void refresh(); });
    // Local History of the tree's and the tabs' menus: the file is opened first, so the panel is about it
    return {
        refresh,
        show: () => show('file'),
        /** The versions of every file under a folder ('' for the project), deleted ones too. */
        showFolder: (dir) => show('folder', dir),
        showSelection,
        openAndCompare,
    };
}

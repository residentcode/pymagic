// Local history (the History tab of the dock): the versions the open file had at its saves, and for each one a comparison with what the editor holds
// now, from which that version can be brought back. The engine keeps the versions (LocalHistory, in .pymagic/history).
import { showTab } from './panel';
import { ago, clock, size } from './history-model.js';
import { toast } from './toast';

const $ = (id) => document.getElementById(id);

export function setupHistory({ monaco, editor, currentFile }) {
    let versions = [];
    let ticket = 0;
    const active = () => $('history-tab').classList.contains('active');

    async function refresh() {
        const rel = currentFile();
        $('history-title').textContent = rel ? `Local history of ${rel}` : 'Local history';
        const mine = ++ticket;
        if (!rel) { versions = []; draw(); return; }
        let found = [];
        try { found = await (await fetch('/api/history?path=' + encodeURIComponent(rel))).json(); } catch { /* none */ }
        if (mine !== ticket) return;
        versions = found;
        draw();
    }

    function draw() {
        const box = $('history-list');
        box.replaceChildren();
        if (versions.length === 0) {
            const none = document.createElement('div');
            none.className = 'side-empty';
            none.textContent = currentFile() ? 'No versions yet: each save of this file keeps one.' : 'Open a file to see the versions it had.';
            box.append(none);
            return;
        }
        const now = Date.now();
        versions.forEach((v, i) => {
            const row = document.createElement('div');
            row.className = 'hist-row';
            const when = document.createElement('span');
            when.className = 'hist-when';
            when.textContent = clock(now, Date.parse(v.time));
            const rel = document.createElement('span');
            rel.className = 'hist-ago';
            rel.textContent = ago(now, Date.parse(v.time));
            const bytes = document.createElement('span');
            bytes.className = 'hist-size';
            bytes.textContent = size(v.bytes);
            row.append(when, rel, bytes);
            if (i === 0) { const tag = document.createElement('span'); tag.className = 'hist-tag'; tag.textContent = 'last save'; row.append(tag); }
            row.onclick = () => void compare(v);
            box.append(row);
        });
    }

    async function compare(version) {
        const rel = currentFile();
        const model = editor.getModel();
        if (!rel || !model) return;
        const res = await fetch(`/api/history/text?path=${encodeURIComponent(rel)}&id=${version.id}`);
        if (!res.ok) { toast('That version is gone.', true); return; }
        const old = await res.text();

        const shade = document.createElement('div');
        shade.className = 'settings-shade';
        const box = document.createElement('div');
        box.className = 'hist-diff';
        const head = document.createElement('div');
        head.className = 'hist-diff-head';
        const title = document.createElement('span');
        title.className = 'hist-diff-title';
        title.textContent = `${rel}: the version of ${clock(Date.now(), Date.parse(version.time))} and what the editor holds now`;
        const mode = document.createElement('button');
        mode.textContent = 'Side by side';
        const restore = document.createElement('button');
        restore.className = 'primary';
        restore.textContent = 'Restore this version';
        restore.title = 'Put this version in the editor (undo brings the present one back); it is saved when the file is';
        const close = document.createElement('button');
        close.textContent = 'Close';
        head.append(title, mode, restore, close);
        const body = document.createElement('div');
        body.className = 'hist-diff-body';
        box.append(head, body);
        shade.append(box);
        document.body.append(shade);

        const language = model.getLanguageId();
        const original = monaco.editor.createModel(old, language);
        const modified = monaco.editor.createModel(model.getValue(), language);
        let sideBySide = true;
        const diff = monaco.editor.createDiffEditor(body, { readOnly: true, originalEditable: false, renderSideBySide: sideBySide, automaticLayout: true, minimap: { enabled: false }, scrollBeyondLastLine: false });
        diff.setModel({ original, modified });

        const done = () => {
            document.removeEventListener('keydown', onKey, true);
            diff.dispose();
            original.dispose();
            modified.dispose();
            shade.remove();
            editor.focus();
        };
        const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); done(); } };
        document.addEventListener('keydown', onKey, true);
        close.onclick = done;
        shade.onmousedown = (e) => { if (e.target === shade) done(); };
        mode.onclick = () => { sideBySide = !sideBySide; diff.updateOptions({ renderSideBySide: sideBySide }); mode.textContent = sideBySide ? 'Side by side' : 'Inline'; };
        restore.onclick = () => {
            if (editor.getModel() !== model) { toast('The file in the editor is another one now.', true); return; }
            editor.pushUndoStop();
            editor.executeEdits('local-history', [{ range: model.getFullModelRange(), text: old }]);
            editor.pushUndoStop();
            done();
            toast('The version is in the editor. Save to keep it; undo brings back the one you had.');
        };
    }

    $('t-history').onclick = () => { showTab('history'); void refresh(); };
    $('hist-refresh').onclick = () => void refresh();
    editor.onDidChangeModel(() => { if (active()) void refresh(); });
    window.addEventListener('pymagic:saved', () => { if (active()) void refresh(); });
    // Local History of the tree's and the tabs' menus: the file is opened first, so the panel is about it
    return { refresh, show: () => { showTab('history'); void refresh(); } };
}

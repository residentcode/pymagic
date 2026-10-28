// The merge editor: a file a merge or rebase left with conflict markers, one card for each conflict (our side and theirs, with the ways to settle it)
// and the result as an editor on the right. The result is plain text: the cards are read from it, so a conflict settled by a card and one settled
// by typing in the result are the same. Apply saves the file and, when no marker is left, tells git it is resolved.
import { findConflicts, settle, settleAll, conflictLines } from './merge-model.js';
import { toast } from './toast';

const el = (tag, cls, text) => {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
};

function languageOf(monaco, path) {
    const ext = path.slice(path.lastIndexOf('.')).toLowerCase();
    return monaco.languages.getLanguages().find((l) => (l.extensions ?? []).includes(ext))?.id ?? 'plaintext';
}

/**
 * Opens the merge editor for `path` (relative to the project). `finish(resolved)` runs after the file was saved; `resolved` says whether no
 * conflict is left in it. Returns false, without opening anything, when the file has no markers (a conflict of a deleted file, say).
 */
export async function openMerge({ monaco, path, finish }) {
    const res = await fetch('/api/file?path=' + encodeURIComponent(path));
    if (!res.ok) { toast('The file cannot be read here.', true); return false; }
    const text = await res.text();
    if (findConflicts(text).length === 0) { toast('This file has no conflict markers: take ours or theirs instead.', true); return false; }

    const shade = el('div', 'settings-shade');
    const box = el('div', 'hist-diff merge-box');
    const head = el('div', 'hist-diff-head');
    const title = el('span', 'hist-diff-title');
    const allOurs = el('button', '', 'All ours');
    allOurs.title = 'Settle every conflict that is left with our side';
    const allTheirs = el('button', '', 'All theirs');
    allTheirs.title = 'Settle every conflict that is left with theirs';
    const prev = el('button', '', '↑');
    prev.title = 'Previous conflict';
    const next = el('button', '', '↓');
    next.title = 'Next conflict';
    const apply = el('button', 'primary', 'Apply');
    const cancel = el('button', '', 'Cancel');
    head.append(title, prev, next, allOurs, allTheirs, apply, cancel);
    const body = el('div', 'merge-body');
    const cards = el('div', 'merge-cards');
    const pane = el('div', 'merge-result');
    body.append(cards, pane);
    box.append(head, body);
    shade.append(box);
    document.body.append(shade);

    const model = monaco.editor.createModel(text, languageOf(monaco, path));
    const result = monaco.editor.create(pane, { model, automaticLayout: true, minimap: { enabled: false }, scrollBeyondLastLine: false, glyphMargin: false });
    let marks = result.createDecorationsCollection([]);

    const set = (next) => {
        if (next === model.getValue()) return;
        result.pushUndoStop();
        result.executeEdits('merge', [{ range: model.getFullModelRange(), text: next }]);
        result.pushUndoStop();
    };

    function draw() {
        const now = model.getValue();
        const found = findConflicts(now);
        const lines = conflictLines(now);
        title.textContent = found.length
            ? `${path}: ${found.length} conflict${found.length === 1 ? '' : 's'} left`
            : `${path}: every conflict is settled`;
        allOurs.disabled = allTheirs.disabled = prev.disabled = next.disabled = found.length === 0;
        marks.set(found.map((c) => {
            const a = model.getPositionAt(c.start), b = model.getPositionAt(Math.max(c.start, c.end - 1));
            return { range: new monaco.Range(a.lineNumber, 1, b.lineNumber, 1), options: { isWholeLine: true, className: 'merge-conflict-line' } };
        }));
        cards.replaceChildren();
        if (found.length === 0) cards.append(el('div', 'side-empty', 'Nothing is left to settle. Apply saves the file and marks it resolved; you can still edit the result.'));
        found.forEach((c, i) => {
            const card = el('div', 'merge-card');
            card.append(el('div', 'merge-card-title', `Conflict ${i + 1} · line ${lines[i]}`));
            const sides = el('div', 'merge-sides');
            const side = (label, fallback, content) => {
                const s = el('div', 'merge-side');
                s.append(el('div', 'merge-side-label', label || fallback), el('pre', '', content === '' ? '(nothing)' : content.replace(/\n$/, '')));
                return s;
            };
            sides.append(side(c.oursLabel, 'ours', c.ours), side(c.theirsLabel, 'theirs', c.theirs));
            const row = el('div', 'merge-ways');
            const way = (label, how, tip) => {
                const b = el('button', '', label);
                b.title = tip;
                b.onclick = () => set(settle(model.getValue(), i, how));
                row.append(b);
            };
            way('Ours', 'ours', 'Keep the left side');
            way('Theirs', 'theirs', 'Keep the right side');
            way('Both', 'both', 'Our side, then theirs');
            way('Both, theirs first', 'bothReversed', 'Theirs, then our side');
            if (c.base !== null) way('Original', 'base', 'The version both sides started from');
            const show = el('button', '', 'Show');
            show.title = 'Go to it in the result';
            show.onclick = () => { result.revealLineInCenter(lines[i]); result.setPosition({ lineNumber: lines[i], column: 1 }); result.focus(); };
            row.append(show);
            card.append(sides, row);
            cards.append(card);
        });
    }

    let timer = 0;
    model.onDidChangeContent(() => { clearTimeout(timer); timer = setTimeout(draw, 120); });
    draw();

    const go = (step) => {
        const lines = conflictLines(model.getValue());
        if (!lines.length) return;
        const at = result.getPosition()?.lineNumber ?? 0;
        const target = step > 0 ? (lines.find((l) => l > at) ?? lines[0]) : ([...lines].reverse().find((l) => l < at) ?? lines[lines.length - 1]);
        result.revealLineInCenter(target);
        result.setPosition({ lineNumber: target, column: 1 });
        result.focus();
    };
    prev.onclick = () => go(-1);
    next.onclick = () => go(1);
    allOurs.onclick = () => set(settleAll(model.getValue(), 'ours'));
    allTheirs.onclick = () => set(settleAll(model.getValue(), 'theirs'));

    const done = () => {
        document.removeEventListener('keydown', onKey, true);
        clearTimeout(timer);
        result.dispose();
        model.dispose();
        shade.remove();
    };
    const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); done(); } };
    document.addEventListener('keydown', onKey, true);
    cancel.onclick = done;
    apply.onclick = async () => {
        const final = model.getValue();
        const left = findConflicts(final).length;
        if (left > 0 && !window.confirm(`${left} conflict${left === 1 ? ' is' : 's are'} still marked in the file. Save it as it is and keep it unresolved?`)) return;
        const saved = await fetch('/api/file?path=' + encodeURIComponent(path), { method: 'PUT', body: final });
        if (!saved.ok) { toast('The file could not be saved.', true); return; }
        done();
        await finish(left === 0);
    };
    result.focus();
    return true;
}

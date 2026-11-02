// The merge editor: a file a merge or rebase left with conflict markers, in three editors: our side on the left, the result in the middle,
// theirs on the right. The result is plain text and the only state: the sides are read from it (each conflict that is left, settled one way),
// so a conflict settled by a button and one settled by typing in the result are the same. In a side, » / « on a conflict's first line takes
// that side, and the boxes beside its lines pick single lines (Take chosen lines settles the conflict with them, ours first). Apply saves the
// file and, when no marker is left, tells git it is resolved.
import { findConflicts, settle, settleAll, settlePicked, sideView, conflictLines } from './merge-model.js';
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
    const first = findConflicts(text);
    if (first.length === 0) { toast('This file has no conflict markers: take ours or theirs instead.', true); return false; }

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
    const column = (cls, label, tip) => {
        const c = el('div', 'merge-col ' + cls);
        const l = el('div', 'merge-col-label', label);
        l.title = tip;
        const pane = el('div', 'merge-pane');
        c.append(l, pane);
        body.append(c);
        return pane;
    };
    const oursName = first[0].oursLabel || 'ours', theirsName = first[0].theirsLabel || 'theirs';
    const oursPane = column('merge-ours', `Ours: ${oursName}`, 'The file with every conflict that is left taken from our side. » takes a conflict; the boxes pick lines.');
    const resultPane = column('merge-result', 'Result', 'What is saved. Edit it freely: the sides and the list below follow.');
    const theirsPane = column('merge-theirs', `Theirs: ${theirsName}`, 'The file with every conflict that is left taken from their side. « takes a conflict; the boxes pick lines.');
    const cards = el('div', 'merge-cards');
    box.append(head, body, cards);
    shade.append(box);
    document.body.append(shade);

    const language = languageOf(monaco, path);
    const model = monaco.editor.createModel(text, language);
    const oursModel = monaco.editor.createModel('', language);
    const theirsModel = monaco.editor.createModel('', language);
    const common = { automaticLayout: true, minimap: { enabled: false }, scrollBeyondLastLine: false };
    const result = monaco.editor.create(resultPane, { ...common, model, glyphMargin: false });
    const sideOptions = { ...common, readOnly: true, glyphMargin: true, lineDecorationsWidth: 18, folding: false, renderLineHighlight: 'none' };
    const oursEd = monaco.editor.create(oursPane, { ...sideOptions, model: oursModel });
    const theirsEd = monaco.editor.create(theirsPane, { ...sideOptions, model: theirsModel });
    const marks = result.createDecorationsCollection([]);
    const sides = {
        ours: { editor: oursEd, model: oursModel, marks: oursEd.createDecorationsCollection([]), ranges: [], take: '»' },
        theirs: { editor: theirsEd, model: theirsModel, marks: theirsEd.createDecorationsCollection([]), ranges: [], take: '«' },
    };
    // the lines picked in each side: conflict index -> the line numbers inside that conflict's side (from 0); cleared when the result changes
    let picks = { ours: new Map(), theirs: new Map() };
    const picked = (how, index) => [...(picks[how].get(index) ?? [])];

    const set = (nextText) => {
        if (nextText === model.getValue()) return;
        result.pushUndoStop();
        result.executeEdits('merge', [{ range: model.getFullModelRange(), text: nextText }]);
        result.pushUndoStop();
    };

    function paintSide(how) {
        const s = sides[how];
        const decorations = [];
        for (const r of s.ranges) {
            if (r.count === 0) {
                // an empty side: a line where it would be
                const at = Math.min(r.line, s.model.getLineCount());
                decorations.push({ range: new monaco.Range(at, 1, at, 1), options: { className: r.line > s.model.getLineCount() ? 'merge-empty-after' : 'merge-empty-before', glyphMarginClassName: `merge-take merge-take-${how}`, glyphMarginHoverMessage: { value: `Take ${how} (nothing) for conflict ${r.index + 1}` } } });
                continue;
            }
            const chosenLines = picks[how].get(r.index) ?? new Set();
            for (let k = 0; k < r.count; k++) {
                const line = r.line + k;
                decorations.push({
                    range: new monaco.Range(line, 1, line, 1),
                    options: {
                        isWholeLine: true,
                        className: `merge-side-${how}`,
                        linesDecorationsClassName: 'merge-pick' + (chosenLines.has(k) ? ' on' : ''),
                        ...(k === 0 ? { glyphMarginClassName: `merge-take merge-take-${how}`, glyphMarginHoverMessage: { value: `Take ${how} for conflict ${r.index + 1}` } } : {}),
                    },
                });
            }
        }
        s.marks.set(decorations);
    }

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
        for (const how of ['ours', 'theirs']) {
            const view = sideView(now, how);
            if (sides[how].model.getValue() !== view.text) sides[how].model.setValue(view.text);
            sides[how].ranges = view.ranges;
            paintSide(how);
        }
        cards.replaceChildren();
        if (found.length === 0) cards.append(el('div', 'side-empty', 'Nothing is left to settle. Apply saves the file and marks it resolved; you can still edit the result.'));
        found.forEach((c, i) => {
            const card = el('div', 'merge-card');
            card.append(el('span', 'merge-card-title', `Conflict ${i + 1} · line ${lines[i]}`));
            const way = (label, how, tip) => {
                const b = el('button', '', label);
                b.title = tip;
                b.onclick = () => set(settle(model.getValue(), i, how));
                card.append(b);
            };
            way('Ours', 'ours', 'Keep the left side');
            way('Theirs', 'theirs', 'Keep the right side');
            way('Both', 'both', 'Our side, then theirs');
            way('Both, theirs first', 'bothReversed', 'Theirs, then our side');
            if (c.base !== null) way('Original', 'base', 'The version both sides started from');
            const count = picked('ours', i).length + picked('theirs', i).length;
            const take = el('button', 'merge-take-picked', count ? `Take chosen lines (${count})` : 'Take chosen lines');
            take.title = 'Settle it with the lines whose boxes are ticked in the sides: ours first, then theirs, each in its order';
            take.disabled = count === 0;
            take.onclick = () => set(settlePicked(model.getValue(), i, picked('ours', i), picked('theirs', i)));
            const show = el('button', '', 'Show');
            show.title = 'Go to it in the three editors';
            show.onclick = () => reveal(i);
            card.append(take, show);
            cards.append(card);
        });
    }

    /** Brings conflict `i` into view in the result and in both sides, and puts the cursor on it in the result. */
    function reveal(i) {
        const line = conflictLines(model.getValue())[i];
        if (!line) return;
        result.revealLineInCenter(line);
        result.setPosition({ lineNumber: line, column: 1 });
        for (const s of Object.values(sides)) {
            const r = s.ranges.find((x) => x.index === i);
            if (r) s.editor.revealLineInCenter(Math.min(r.line, s.model.getLineCount()));
        }
        result.focus();
    }

    // a click in a side's margin: » / « takes the side, a box picks the line
    for (const how of ['ours', 'theirs']) {
        sides[how].editor.onMouseDown((e) => {
            const t = e.target;
            const line = t.position?.lineNumber;
            if (!line) return;
            const T = monaco.editor.MouseTargetType;
            const r = sides[how].ranges.find((x) => (x.count === 0 ? Math.min(x.line, sides[how].model.getLineCount()) === line : line >= x.line && line < x.line + x.count));
            if (!r) return;
            if (t.type === T.GUTTER_GLYPH_MARGIN && (r.count === 0 || line === r.line)) set(settle(model.getValue(), r.index, how));
            else if (t.type === T.GUTTER_LINE_DECORATIONS && r.count > 0) {
                const k = line - r.line;
                const chosenLines = new Set(picks[how].get(r.index) ?? []);
                if (chosenLines.has(k)) chosenLines.delete(k); else chosenLines.add(k);
                picks[how].set(r.index, chosenLines);
                draw();
            }
        });
    }

    let timer = 0;
    model.onDidChangeContent(() => {
        clearTimeout(timer);
        timer = setTimeout(() => { picks = { ours: new Map(), theirs: new Map() }; draw(); }, 120);
    });
    draw();

    const go = (step) => {
        const lines = conflictLines(model.getValue());
        if (!lines.length) return;
        const at = result.getPosition()?.lineNumber ?? 0;
        const target = step > 0 ? (lines.find((l) => l > at) ?? lines[0]) : ([...lines].reverse().find((l) => l < at) ?? lines[lines.length - 1]);
        reveal(lines.indexOf(target));
    };
    prev.onclick = () => go(-1);
    next.onclick = () => go(1);
    allOurs.onclick = () => set(settleAll(model.getValue(), 'ours'));
    allTheirs.onclick = () => set(settleAll(model.getValue(), 'theirs'));

    const done = () => {
        document.removeEventListener('keydown', onKey, true);
        clearTimeout(timer);
        for (const e of [result, oursEd, theirsEd]) e.dispose();
        for (const m of [model, oursModel, theirsModel]) m.dispose();
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

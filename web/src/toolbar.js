// The commands of the command bar that the editor did not have a button for: back and forward through the places you were at, save, undo and
// redo, coverage, the refactoring menu, search everywhere, find in files, and the Git actions one click from the editor. Each one presses what
// the tool already does (a key, a button of the dock, a function of its script); only the history of places is new.
import { showTab } from './panel';

const $ = (id) => document.getElementById(id);

/** A jump of at least this many lines inside a file is a new place to come back to. */
const FAR = 15;

/** A small menu under a button, in the style of the context menu. */
function menu(anchor, items) {
    document.getElementById('bar-menu')?.remove();
    const box = document.createElement('div');
    box.className = 'ctx-menu';
    box.id = 'bar-menu';
    const close = () => { box.remove(); document.removeEventListener('mousedown', outside, true); document.removeEventListener('keydown', onKey, true); };
    const outside = (e) => { if (!box.contains(e.target)) close(); };
    const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); close(); } };
    for (const item of items) {
        if (item === '-') {
            const sep = document.createElement('div');
            sep.className = 'ctx-sep';
            box.appendChild(sep);
            continue;
        }
        const row = document.createElement('div');
        row.className = 'ctx-item';
        const label = document.createElement('span');
        label.textContent = item.label;
        row.appendChild(label);
        if (item.key) {
            const key = document.createElement('span');
            key.className = 'ctx-key';
            key.textContent = item.key;
            row.appendChild(key);
        }
        row.onclick = () => { close(); item.run(); };
        box.appendChild(row);
    }
    document.body.appendChild(box);
    const r = anchor.getBoundingClientRect();
    box.style.top = `${r.bottom + 4}px`;
    box.style.left = `${Math.max(8, Math.min(r.left, window.innerWidth - box.offsetWidth - 8))}px`;
    setTimeout(() => { document.addEventListener('mousedown', outside, true); document.addEventListener('keydown', onKey, true); });
}

export function setupToolbar({ monaco, editor, save, openFile, currentFile, refactor, search }) {
    const M = monaco.KeyMod, K = monaco.KeyCode;

    // ---------------------------------------------------------------- back and forward
    const back = [];
    const forward = [];
    let here = null;           // where the cursor is: { rel, line, column }
    let jumping = false;
    const paint = () => { $('b-back').disabled = back.length === 0; $('b-forward').disabled = forward.length === 0; };
    const note = (place) => { if (back.length === 0 || back[back.length - 1].rel !== place.rel || back[back.length - 1].line !== place.line) back.push(place); if (back.length > 100) back.shift(); forward.length = 0; paint(); };
    editor.onDidChangeCursorPosition((e) => {
        const rel = currentFile();
        if (!rel) return;
        const next = { rel, line: e.position.lineNumber, column: e.position.column };
        if (!jumping && here && here.rel === rel && Math.abs(here.line - next.line) >= FAR) note(here);
        here = next;
    });
    editor.onDidChangeModel(() => {
        const rel = currentFile();
        if (!jumping && here && rel && here.rel !== rel) note(here);
        if (rel) here = { rel, line: 1, column: 1 };
    });
    async function go(place) {
        jumping = true;
        try { await openFile(place.rel, new monaco.Range(place.line, place.column, place.line, place.column)); }
        finally { setTimeout(() => { jumping = false; }, 60); }
    }
    function step(from, to) {
        const place = from.pop();
        if (!place) return;
        if (here) to.push(here);
        paint();
        void go(place);
    }
    $('b-back').onclick = () => step(back, forward);
    $('b-forward').onclick = () => step(forward, back);
    editor.addCommand(M.CtrlCmd | M.Alt | K.LeftArrow, () => step(back, forward));
    editor.addCommand(M.CtrlCmd | M.Alt | K.RightArrow, () => step(forward, back));

    // ---------------------------------------------------------------- what Run works on: the file in the editor
    const target = () => { $('run-target').lastElementChild.textContent = (currentFile() ?? '').split('/').pop() || 'no file'; };
    editor.onDidChangeModel(target);
    target();

    // ---------------------------------------------------------------- the file and its edits
    $('b-save').onclick = () => void save();
    $('b-undo').onclick = () => { editor.focus(); editor.trigger('toolbar', 'undo', null); };
    $('b-redo').onclick = () => { editor.focus(); editor.trigger('toolbar', 'redo', null); };

    // ---------------------------------------------------------------- run with coverage
    $('b-cov').onclick = () => { $('t-tests').click(); $('tests-cov').click(); };

    // ---------------------------------------------------------------- refactorings
    const act = (kind) => () => { editor.focus(); void refactor.trigger(kind); };
    $('b-refactor').onclick = () => menu($('b-refactor'), [
        { label: 'Rename…', key: 'F2', run: () => { editor.focus(); editor.trigger('toolbar', 'editor.action.rename', null); } },
        '-',
        { label: 'Extract variable', key: 'Ctrl+Alt+V', run: act('extractVariable') },
        { label: 'Extract constant', key: 'Ctrl+Alt+C', run: act('extractConstant') },
        { label: 'Extract method', key: 'Ctrl+Alt+M', run: act('extractMethod') },
        '-',
        { label: 'Inline variable', key: 'Ctrl+Alt+N', run: act('inlineVariable') },
        { label: 'Inline method', key: 'Ctrl+Alt+P', run: act('inlineMethod') },
        '-',
        { label: 'Change signature…', key: 'Ctrl+F6', run: act('changeSignature') },
        { label: 'Move to another file…', key: 'F6', run: act('moveSymbol') },
    ]);

    // ---------------------------------------------------------------- search
    $('b-find').onclick = () => search.open();
    $('b-anything').onclick = () => void search.anythingPicker();
    document.addEventListener('keydown', (e) => {
        if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && e.key.toLowerCase() === 'p') {
            e.preventDefault();
            void search.anythingPicker();
        }
    });

    // ---------------------------------------------------------------- Git, one click from the editor (the work itself is the Git panel's)
    $('b-update').onclick = () => {
        $('t-git').click();
        if (window.confirm('Fetch and join the upstream into this branch now?\n\nThe Git panel shows how it went (its "merge / rebase" setting decides how).'))
            $('git-pull').click();
    };
    $('b-commit').onclick = () => { $('t-git').click(); showTab('git'); $('git-message').focus(); };
    $('b-push').onclick = () => {
        $('t-git').click();
        if (window.confirm('Send this branch to its upstream now?'))
            $('git-push').click();
    };
    paint();
}

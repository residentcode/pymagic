// The commands of the command bar that the editor did not have a button for: back and forward through the places you were at, save, undo and
// redo, coverage, the refactoring menu, search everywhere, find in files, and the Git actions one click from the editor. Each one presses what
// the tool already does (a key, a button of the dock, a function of its script); only the history of places is new.
import { showTab } from './panel';
import { openMenu } from './menu';
import { change, settings } from './settings';
import { openSettings, setupSettingsKey } from './settings-ui';

const $ = (id) => document.getElementById(id);

/** A jump of at least this many lines inside a file is a new place to come back to. */
const FAR = 15;

export function setupToolbar({ monaco, editor, save, openFile, currentFile, refactor, search, runConfigs }) {
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

    // ---------------------------------------------------------------- what Run starts: the chosen run configuration, or the file in the editor
    const target = () => {
        const name = runConfigs.selectedName();
        $('run-config').children[1].textContent = name ?? ((currentFile() ?? '').split('/').pop() || 'no file');
        $('run-config').title = name ? `Run configuration "${name}": Run, Debug and Profile start it` : 'Run and Debug start the file in the editor; choose a run configuration to start a file with parameters';
    };
    editor.onDidChangeModel(target);
    runConfigs.onChange(target);
    $('run-config').onclick = () => runConfigs.menu($('run-config'));
    target();

    // ---------------------------------------------------------------- the file and its edits
    $('b-save').onclick = () => void save();
    $('b-undo').onclick = () => { editor.focus(); editor.trigger('toolbar', 'undo', null); };
    $('b-redo').onclick = () => { editor.focus(); editor.trigger('toolbar', 'redo', null); };

    // ---------------------------------------------------------------- run with coverage
    $('b-cov').onclick = () => { $('t-tests').click(); $('tests-cov').click(); };

    // ---------------------------------------------------------------- refactorings
    const act = (kind) => () => { editor.focus(); void refactor.trigger(kind); };
    $('b-refactor').onclick = () => openMenu($('b-refactor'), [
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
        if (!settings().confirmGit || window.confirm('Fetch and join the upstream into this branch now?\n\nThe Git panel shows how it went (its "merge / rebase" setting decides how).'))
            $('git-pull').click();
    };
    $('b-commit').onclick = () => { $('t-git').click(); showTab('git'); $('git-message').focus(); };
    $('b-push').onclick = () => {
        $('t-git').click();
        if (!settings().confirmGit || window.confirm('Send this branch to its upstream now?'))
            $('git-push').click();
    };

    // ---------------------------------------------------------------- the gear: settings and the choices people change most often
    setupSettingsKey();
    const flip = (key, on, off) => () => change({ [key]: settings()[key] === on ? off : on });
    $('b-gear').onclick = () => {
        const s = settings();
        const theme = (value, label) => ({ label, checked: s.theme === value, run: () => change({ theme: value }) });
        openMenu($('b-gear'), [
            { label: 'Settings…', key: 'Ctrl+,', run: () => openSettings() },
            '-',
            { label: 'Theme', submenu: [theme('dark', 'Dark'), theme('light', 'Light'), theme('system', 'Follow the system')] },
            { label: 'Code colours', submenu: [
                { label: 'VS Code', checked: (s.codeColors ?? 'vscode') === 'vscode', run: () => change({ codeColors: 'vscode' }) },
                { label: 'PyMagic\'s own', checked: s.codeColors === 'pymagic', run: () => change({ codeColors: 'pymagic' }) },
            ] },
            { label: 'Editor', submenu: [
                { label: 'Wrap long lines', checked: s.wordWrap === 'on', run: flip('wordWrap', 'on', 'off') },
                { label: 'Minimap', checked: s.minimap, run: flip('minimap', true, false) },
                { label: 'Keep the enclosing definition in view', checked: s.stickyScroll, run: flip('stickyScroll', true, false) },
                { label: 'Colour matching brackets', checked: s.bracketColors, run: flip('bracketColors', true, false) },
                '-',
                { label: 'Larger text', run: () => change({ fontSize: settings().fontSize + 1 }) },
                { label: 'Smaller text', run: () => change({ fontSize: settings().fontSize - 1 }) },
                { label: 'Normal text size', run: () => change({ fontSize: 14 }) },
            ] },
            '-',
            { label: 'Formatter…', run: () => openSettings('formatter') },
            { label: 'Python interpreter…', run: () => openSettings('python') },
            { label: 'Inspections…', run: () => $('t-insp').click() },
            { label: 'Git…', run: () => openSettings('git') },
            '-',
            { label: 'Keyboard shortcuts', run: () => openSettings('keymap') },
            { label: 'About PyMagic', run: () => openSettings('about') },
        ]);
    };
    paint();
}

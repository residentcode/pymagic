// The main menu: the mark at the left of the command bar opens it. Every command PyMagic has is in it, with its key, grouped as File, Edit, View,
// Navigate, Code, Run, Tools, Git and Help. A command is the editor's own action, a button the tool already has, or a function of its script.
import { openMenu } from './menu';
import { change, settings } from './settings';
import { openSettings } from './settings-ui';

const $ = (id) => document.getElementById(id);
const press = (id) => () => $(id).click();

export function setupMainMenu({ editor, save, saveAll, reload, openFile, tabs, currentFile, search, refactor, breakpoints, recent }) {
    const act = (id) => () => { editor.focus(); editor.trigger('menu', id, null); };
    const dock = (id) => () => $(`t-${id}`).click();

    const refactorings = () => [
        { label: 'Rename…', key: 'F2', run: act('editor.action.rename') },
        '-',
        { label: 'Extract variable', key: 'Ctrl+Alt+V', run: () => { editor.focus(); void refactor.trigger('extractVariable'); } },
        { label: 'Extract constant', key: 'Ctrl+Alt+C', run: () => { editor.focus(); void refactor.trigger('extractConstant'); } },
        { label: 'Extract method', key: 'Ctrl+Alt+M', run: () => { editor.focus(); void refactor.trigger('extractMethod'); } },
        '-',
        { label: 'Inline variable', key: 'Ctrl+Alt+N', run: () => { editor.focus(); void refactor.trigger('inlineVariable'); } },
        { label: 'Inline method', key: 'Ctrl+Alt+P', run: () => { editor.focus(); void refactor.trigger('inlineMethod'); } },
        '-',
        { label: 'Change signature…', key: 'Ctrl+F6', run: () => { editor.focus(); void refactor.trigger('changeSignature'); } },
        { label: 'Move to another file…', key: 'F6', run: () => { editor.focus(); void refactor.trigger('moveSymbol'); } },
    ];

    function items() {
        const s = settings();
        const theme = (value, label) => ({ label, checked: s.theme === value, run: () => change({ theme: value }) });
        const sidebarShown = !$('app').classList.contains('no-side');
        const windows = [['output', 'Output'], ['debug', 'Debugger'], ['terminal', 'Terminal'], ['tests', 'Tests'], ['coverage', 'Coverage'], ['profiler', 'Profiler'], '-',
            ['problems', 'Problems'], ['todo', 'TODO'], ['insp', 'Inspections'], ['hier', 'Hierarchy'], '-', ['search', 'Search'], ['git', 'Git'], ['packages', 'Packages'], ['routes', 'Endpoints'], ['django', 'Django']]
            .map((w) => (w === '-' ? w : { label: w[1], checked: $('panel').classList.contains('open') && $(`t-${w[0]}`).classList.contains('active'), run: dock(w[0]) }));
        const files = recent(currentFile());
        return [
            { label: 'File', submenu: [
                { label: 'New file…', run: press('ex-new-file') },
                { label: 'New folder…', run: press('ex-new-folder') },
                { label: 'Open folder…', key: 'Ctrl+Shift+O', run: () => (window.pymagicDesktop?.openFolder ? window.pymagicDesktop.openFolder() : window.alert('Open folder is in the PyMagic desktop app. In a browser, start PyMagic with the folder: PyMagic.cmd C:\\my\\project')), disabled: !window.pymagicDesktop },
                { label: 'Recent files', submenu: files.length ? files.map((rel) => ({ label: rel, run: () => void openFile(rel) })) : [{ label: 'No recent files', disabled: true }] },
                '-',
                { label: 'Save', key: 'Ctrl+S', run: () => void save() },
                { label: 'Save all', run: () => void saveAll() },
                { label: 'Reload from disk', run: () => { const rel = currentFile(); if (rel) void reload(rel); }, disabled: !currentFile() },
                { label: 'Close the file', key: 'Ctrl+W', run: () => { const rel = currentFile(); if (rel) void tabs().close(rel); }, disabled: !currentFile() },
                '-',
                { label: 'Settings…', key: 'Ctrl+,', run: () => openSettings() },
            ] },
            { label: 'Edit', submenu: [
                { label: 'Undo', key: 'Ctrl+Z', run: act('undo') },
                { label: 'Redo', key: 'Ctrl+Y', run: act('redo') },
                '-',
                { label: 'Cut', key: 'Ctrl+X', run: act('editor.action.clipboardCutAction') },
                { label: 'Copy', key: 'Ctrl+C', run: act('editor.action.clipboardCopyAction') },
                { label: 'Select all', key: 'Ctrl+A', run: act('editor.action.selectAll') },
                '-',
                { label: 'Find in the file', key: 'Ctrl+F', run: act('actions.find') },
                { label: 'Replace in the file', key: 'Ctrl+H', run: act('editor.action.startFindReplaceAction') },
                { label: 'Find in files…', key: 'Ctrl+Shift+F', run: () => search.open() },
                '-',
                { label: 'Lines', submenu: [
                    { label: 'Comment or uncomment', key: 'Ctrl+/', run: act('editor.action.commentLine') },
                    { label: 'Duplicate', key: 'Shift+Alt+Down', run: act('editor.action.copyLinesDownAction') },
                    { label: 'Delete', key: 'Ctrl+Shift+K', run: act('editor.action.deleteLines') },
                    { label: 'Move up', key: 'Alt+Up', run: act('editor.action.moveLinesUpAction') },
                    { label: 'Move down', key: 'Alt+Down', run: act('editor.action.moveLinesDownAction') },
                    { label: 'Join', key: 'Ctrl+J', run: act('editor.action.joinLines') },
                    { label: 'Sort ascending', run: act('editor.action.sortLinesAscending') },
                    { label: 'Sort descending', run: act('editor.action.sortLinesDescending') },
                ] },
                { label: 'Selection', submenu: [
                    { label: 'Expand', key: 'Shift+Alt+Right', run: act('editor.action.smartSelect.expand') },
                    { label: 'Shrink', key: 'Shift+Alt+Left', run: act('editor.action.smartSelect.shrink') },
                    { label: 'Add the next occurrence', key: 'Ctrl+D', run: act('editor.action.addSelectionToNextFindMatch') },
                    { label: 'Select all occurrences', key: 'Ctrl+Shift+L', run: act('editor.action.selectHighlights') },
                    { label: 'Column selection above', key: 'Ctrl+Alt+Up', run: act('editor.action.insertCursorAbove') },
                    { label: 'Column selection below', key: 'Ctrl+Alt+Down', run: act('editor.action.insertCursorBelow') },
                ] },
                { label: 'Text', submenu: [
                    { label: 'To upper case', run: act('editor.action.transformToUppercase') },
                    { label: 'To lower case', run: act('editor.action.transformToLowercase') },
                    { label: 'To title case', run: act('editor.action.transformToTitlecase') },
                    { label: 'Trim trailing spaces', run: act('editor.action.trimTrailingWhitespace') },
                ] },
                { label: 'Fold', submenu: [
                    { label: 'Fold', key: 'Ctrl+Shift+[', run: act('editor.fold') },
                    { label: 'Unfold', key: 'Ctrl+Shift+]', run: act('editor.unfold') },
                    { label: 'Fold all', run: act('editor.foldAll') },
                    { label: 'Unfold all', run: act('editor.unfoldAll') },
                ] },
            ] },
            { label: 'View', submenu: [
                { label: 'Files', checked: sidebarShown, run: press('r-explorer') },
                { label: 'Tool windows', submenu: windows },
                { label: 'Hide the tool window', run: press('t-close') },
                '-',
                { label: 'Theme', submenu: [theme('dark', 'Dark'), theme('light', 'Light'), theme('system', 'Follow the system')] },
                { label: 'Wrap long lines', checked: s.wordWrap === 'on', run: () => change({ wordWrap: s.wordWrap === 'on' ? 'off' : 'on' }) },
                { label: 'Minimap', checked: s.minimap, run: () => change({ minimap: !s.minimap }) },
                { label: 'Keep the enclosing definition in view', checked: s.stickyScroll, run: () => change({ stickyScroll: !s.stickyScroll }) },
                { label: 'Text size', submenu: [
                    { label: 'Larger', run: () => change({ fontSize: settings().fontSize + 1 }) },
                    { label: 'Smaller', run: () => change({ fontSize: settings().fontSize - 1 }) },
                    { label: 'Normal', run: () => change({ fontSize: 14 }) },
                ] },
            ] },
            { label: 'Navigate', submenu: [
                { label: 'Back', key: 'Ctrl+Alt+Left', run: press('b-back'), disabled: $('b-back').disabled },
                { label: 'Forward', key: 'Ctrl+Alt+Right', run: press('b-forward'), disabled: $('b-forward').disabled },
                '-',
                { label: 'Search everywhere…', key: 'Ctrl+P', run: () => void search.anythingPicker() },
                { label: 'Go to symbol…', key: 'Ctrl+Alt+O', run: () => void search.symbolPicker() },
                { label: 'Go to line…', key: 'Ctrl+G', run: act('editor.action.gotoLine') },
                '-',
                { label: 'Go to definition', key: 'F12', run: act('editor.action.revealDefinition') },
                { label: 'Go to implementations', run: act('editor.action.goToImplementation') },
                { label: 'Find usages', key: 'Shift+F12', run: act('editor.action.referenceSearch.trigger') },
                { label: 'Peek the definition', key: 'Alt+F12', run: act('editor.action.peekDefinition') },
                '-',
                { label: 'Call hierarchy', key: 'Ctrl+Alt+H', run: act('pymagic.callHierarchy') },
                { label: 'Type hierarchy', key: 'Ctrl+H (in the editor)', run: act('pymagic.typeHierarchy') },
                '-',
                { label: 'Next problem', key: 'F8', run: act('editor.action.marker.next') },
                { label: 'Previous problem', key: 'Shift+F8', run: act('editor.action.marker.prev') },
                { label: 'Next change', key: 'Alt+F3', run: act('editor.action.dirtydiff.next') },
            ] },
            { label: 'Code', submenu: [
                { label: 'Format the file or the selection', key: 'Shift+Alt+F', run: press('b-format') },
                { label: 'Remove unused imports and sort', key: 'Shift+Alt+O', run: press('b-imports') },
                '-',
                { label: 'Quick fixes and refactorings…', key: 'Ctrl+.', run: act('editor.action.quickFix') },
                { label: 'Refactor', submenu: refactorings() },
                '-',
                { label: 'Show the completions', key: 'Ctrl+Space', run: act('editor.action.triggerSuggest') },
                { label: 'Show the parameters', key: 'Ctrl+Shift+Space', run: act('editor.action.triggerParameterHints') },
                { label: 'Show the documentation', key: 'Ctrl+K Ctrl+I', run: act('editor.action.showHover') },
                '-',
                { label: 'Inspect the project…', run: dock('problems') },
                { label: 'Inspection levels…', run: dock('insp') },
                { label: 'TODO comments', run: dock('todo') },
            ] },
            { label: 'Run', submenu: [
                { label: 'Run', key: 'Ctrl+F5', run: press('b-run'), disabled: $('b-run').disabled },
                { label: 'Debug', key: 'F5', run: press('b-debug'), disabled: $('b-debug').disabled },
                { label: 'Run with coverage', run: press('b-cov') },
                { label: 'Profile', run: press('b-profile'), disabled: $('b-profile').disabled },
                { label: 'Stop', key: 'Shift+F5', run: press('b-stop'), disabled: $('b-stop').disabled },
                '-',
                { label: 'Step over', key: 'F10', run: press('b-over'), disabled: $('b-over').disabled },
                { label: 'Step into', key: 'F11', run: press('b-into'), disabled: $('b-into').disabled },
                { label: 'Step out', key: 'Shift+F11', run: press('b-out'), disabled: $('b-out').disabled },
                '-',
                { label: 'Toggle a breakpoint', key: 'F9', run: () => { editor.focus(); breakpoints.toggle(); } },
                { label: 'Edit the breakpoint…', key: 'Shift+F9', run: () => { editor.focus(); breakpoints.edit(); } },
                { label: 'Break on raised exceptions', checked: $('exc-raised').checked, run: () => $('exc-raised').click() },
                '-',
                { label: 'Run all the tests', run: () => { $('t-tests').click(); $('tests-all').click(); } },
                { label: 'Run the test at the cursor', key: 'Ctrl+Shift+F10', run: () => { $('t-tests').click(); $('tests-here').click(); } },
                { label: 'Run the failed tests again', run: () => { $('t-tests').click(); $('tests-rerun').click(); }, disabled: $('tests-rerun').disabled },
            ] },
            { label: 'Tools', submenu: [
                { label: 'Python interpreter…', run: () => openSettings('python') },
                { label: 'Create a .venv…', run: press('b-venv') },
                { label: 'WSL, Docker or SSH interpreter…', run: press('b-remote') },
                { label: 'Packages', run: dock('packages') },
                '-',
                { label: 'Terminal', key: 'Ctrl+`', run: dock('terminal') },
                { label: 'Django', run: dock('django') },
                { label: 'Endpoints', run: dock('routes') },
                { label: 'Profiler', run: dock('profiler') },
            ] },
            { label: 'Git', submenu: [
                { label: 'Update (pull)', run: press('b-update') },
                { label: 'Commit…', key: 'Alt+9', run: press('b-commit') },
                { label: 'Push…', run: press('b-push') },
                '-',
                { label: 'Fetch', run: () => { $('t-git').click(); $('git-fetch').click(); } },
                { label: 'Branches and tags…', run: () => { $('t-git').click(); $('git-manage').click(); } },
                { label: 'New branch…', run: () => { $('t-git').click(); $('git-new').click(); } },
                { label: 'Remotes…', run: () => { $('t-git').click(); $('git-remotes').click(); } },
                '-',
                { label: 'Show who last changed each line', checked: $('git-annotate').checked, run: () => $('git-annotate').click() },
                { label: 'Show who changed the current line', checked: $('git-inline').checked, run: () => $('git-inline').click() },
                { label: 'Git panel', run: dock('git') },
            ] },
            { label: 'Help', submenu: [
                { label: 'Keyboard shortcuts', run: () => openSettings('keymap') },
                { label: 'About PyMagic', run: () => openSettings('about') },
            ] },
        ];
    }

    $('brand').querySelector('.mark').onclick = () => openMenu($('brand'), items());
    $('brand').querySelector('.mark').title = 'Main menu';
}

// What the project tree, the tabs and the editor offer for a file or folder besides moving it, as PyCharm's menus do: run it or its tests,
// its local history, find, inspect, reformat and optimize imports in it, compare it, open it in Explorer or a terminal, leave it out of Git,
// clean the compiled files of a folder and choose the type a file is shown as.
import { toast } from './toast';
import { openMenu } from './menu';
import { copyLabel, includeFor, isTestFile, pythonFilesUnder, rememberCopy, testPartners, usageFiles, FILE_TYPES } from './fileactions-model';

const TYPES_KEY = 'pymagic.fileTypes';

/** The type chosen for each file with Override File Type, kept between starts: { [rel]: languageId }. */
export function fileTypes() {
    try { const v = JSON.parse(localStorage.getItem(TYPES_KEY) ?? '{}'); return v && typeof v === 'object' ? v : {}; } catch { return {}; }
}
function keepFileType(rel, id) {
    const all = fileTypes();
    if (id) all[rel] = id; else delete all[rel];
    try { localStorage.setItem(TYPES_KEY, JSON.stringify(all)); } catch { /* not kept */ }
}

export function setupFileActions(deps) {
    const isPy = (rel) => /\.pyi?$/.test(rel);
    const folderOf = (rel) => rel.slice(0, Math.max(0, rel.lastIndexOf('/')));

    async function fs(op, body) {
        const res = await fetch('/api/fs', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ op, ...body }) });
        let json = null;
        try { json = await res.json(); } catch { /* no body */ }
        if (!res.ok) { toast(json?.error ?? `${op} failed`, true); return null; }
        return json ?? {};
    }

    /** Reformat Code or Optimize Imports of every Python file at or under `path`; each changed file is saved. */
    async function tidy(path, kind) {
        const list = pythonFilesUnder(deps.pythonFiles(), path);
        if (list.length === 0) { toast(`There is no Python file in ${path || 'the project'}.`, true); return; }
        let changed = 0;
        for (const rel of list) if (await deps.tidyFile(rel, kind)) changed++;
        const what = kind === 'format' ? 'Reformatted' : 'Optimized the imports of';
        toast(changed ? `${what} ${changed} of ${list.length} file${list.length === 1 ? '' : 's'}` : `Nothing to change in ${list.length} file${list.length === 1 ? '' : 's'}`);
    }

    /** The places that import a file or folder (as a rename would change them), or null when the engine cannot say. */
    async function usagesOf(path) {
        let found;
        try { found = await deps.lsp.request('pymagic/fileUsages', { textDocument: { uri: deps.uriFor(path) } }); } catch { return null; }
        const rels = [...new Set((found ?? []).map((l) => deps.relOfUri(l.uri)).filter(Boolean))];
        const texts = {};
        for (const rel of rels) texts[rel] = (await deps.textOf(rel)) ?? '';
        return usageFiles(found ?? [], deps.relOfUri, texts);
    }

    /** Find Usages of a file or folder, shown in the Search panel. */
    async function findUsages(path) {
        const files = await usagesOf(path);
        if (!files) { toast('The usages cannot be found now.', true); return; }
        const n = files.reduce((k, f) => k + f.matches.length, 0);
        deps.search.showFound(n ? `${n} usage${n === 1 ? '' : 's'} of ${path} in ${files.length} file${files.length === 1 ? '' : 's'}` : `No usages of ${path}`, files);
    }

    async function history(rel) {
        await deps.openFile(rel);
        deps.history.show();
    }

    async function compareWithClipboard(rel) {
        let clip = '';
        try { clip = await navigator.clipboard.readText(); } catch { toast('The clipboard cannot be read here.', true); return; }
        const text = await deps.textOf(rel);
        deps.showDiff(`${rel} — the file (right) and the clipboard (left)`, clip, text ?? '', rel);
    }

    async function compareWithFile(rel) {
        const other = await deps.askText('Compare with', '', `The path of the other file, from the project folder (${rel} is on the right)`);
        if (!other) return;
        const text = await deps.textOf(other);
        if (text === null) { toast(`${other} cannot be read.`, true); return; }
        deps.showDiff(`${other} (left) and ${rel} (right)`, text, (await deps.textOf(rel)) ?? '', rel);
    }

    function reveal(rel) {
        const desktop = window.pymagicDesktop;
        if (!desktop?.reveal) { toast('Explorer opens from the PyMagic window; in a browser the page cannot reach it.', true); return; }
        void desktop.reveal(rel).then((ok) => { if (!ok) toast(`${rel} is not there any more.`, true); });
    }

    async function gitignore(rel) {
        const r = await fs('gitignore', { path: rel });
        if (!r) return;
        toast(r.added ? `Added ${r.line} to .gitignore` : `.gitignore has ${r.line} already`);
        deps.refreshGit?.();
    }

    async function clean(dir) {
        const r = await fs('clean', { path: dir });
        if (!r) return;
        toast(r.removed ? `Cleaned: ${r.removed} removed (__pycache__ folders and .pyc files)` : 'There were no compiled files');
        await deps.refreshTree?.();
    }

    function setFileType(rel, id) {
        keepFileType(rel, id);
        deps.setLanguage(rel, id ?? deps.defaultLanguage(rel));
    }

    const openIn = (rel, isDir) => ({
        label: 'Open in', submenu: [
            { label: 'Explorer', run: () => reveal(rel) },
            { label: 'Terminal', run: () => deps.terminal.openAt(isDir ? rel : folderOf(rel)) },
        ],
    });

    /** The type a file is shown as, with the one chosen first ticked. */
    function typeItems(rel) {
        const chosen = fileTypes()[rel];
        return [
            ...FILE_TYPES.map(([id, label]) => ({ label, checked: chosen === id, run: () => setFileType(rel, id) })),
            '-',
            { label: 'As its name says', checked: !chosen, disabled: !chosen, run: () => setFileType(rel, null) },
        ];
    }

    const bookmarkItem = (rel, isDir) => deps.bookmarks.hasFile(rel)
        ? { label: 'Remove the bookmark', run: () => deps.bookmarks.toggleFile(rel, isDir) }
        : { label: 'Add a bookmark', run: () => deps.bookmarks.toggleFile(rel, isDir) };

    /** More Run/Debug: the run that the plugins add, for a Python file (profiled) and for its tests or a folder's (coverage, profiled). */
    function moreRunItems(path, isDir) {
        const on = (name) => deps.plugin?.(name) !== false;
        const name = path ? `'${path.slice(path.lastIndexOf('/') + 1)}'` : 'the project';
        const items = [];
        if (!isDir && isPy(path) && on('profiler') && deps.run.profileScript) items.push({ label: `Profile ${name}`, run: () => void deps.run.profileScript(path) });
        if (isDir || isTestFile(path)) {
            if (on('coverage')) items.push({ label: `Run the tests in ${name} with coverage`, run: () => deps.tests.runPath(path, false, 'coverage') });
            if (on('profiler')) items.push({ label: `Profile the tests in ${name}`, run: () => deps.tests.runPath(path, false, 'profile') });
        }
        return items;
    }

    /** Diagrams: the classes of a Python file (it opens first), and the project's classes, modules and Django models. */
    function diagramItems(path, isDir) {
        if (deps.plugin?.('diagrams') === false) return [];
        const show = (kind) => window.dispatchEvent(new CustomEvent('pymagic:diagram', { detail: kind }));
        const items = [];
        if (!isDir && isPy(path)) items.push({ label: 'Classes of this file', run: async () => { await deps.openFile(path); show('file'); } });
        items.push({ label: 'Classes of the project', run: () => show('classes') }, { label: 'Modules and imports', run: () => show('modules') });
        if (deps.plugin?.('django') !== false) items.push({ label: 'Django models', run: () => show('models') });
        return [{ label: 'Diagrams', icon: ['hier', ''], submenu: items }];
    }

    /** The items for a file, shared by the tree and the tabs. */
    function fileItems(rel) {
        const items = [];
        if (isPy(rel)) {
            items.push({ label: `Run '${rel.slice(rel.lastIndexOf('/') + 1)}'`, icon: 'run', run: () => deps.run.runScript(rel, '', false) },
                { label: `Debug '${rel.slice(rel.lastIndexOf('/') + 1)}'`, icon: 'debug', run: () => deps.run.runScript(rel, '', true) });
            if (isTestFile(rel)) items.push({ label: 'Run the tests in it', icon: 'run', run: () => deps.tests.runPath(rel) }, { label: 'Debug the tests in it', icon: 'debug', run: () => deps.tests.runPath(rel, true) });
            const more = moreRunItems(rel, false);
            if (more.length) items.push({ label: 'More run/debug', submenu: more });
            items.push('-');
        }
        items.push(
            { label: 'Local history', run: () => history(rel) },
            { label: 'Compare with', icon: ['compare', ''], submenu: [
                { label: 'Clipboard', run: () => compareWithClipboard(rel) },
                { label: 'Another file…', run: () => compareWithFile(rel) },
            ] },
            { label: 'Reload from disk', icon: ['refresh', ''], run: () => deps.reload(rel) },
            '-',
            openIn(rel, false),
            { label: 'Add to .gitignore', run: () => gitignore(rel) },
            bookmarkItem(rel, false),
        );
        return items;
    }

    /** The items the tree adds for a file or folder ('' and isDir: the project folder). */
    function treeItems(path, isDir) {
        const items = [];
        if ((isDir && path) || isPy(path)) items.push({ label: 'Find usages', key: 'Alt+F7', run: () => findUsages(path) });
        items.push(
            { label: 'Find in files…', run: () => deps.search.open(undefined, { include: isDir ? includeFor(path) : path }) },
            { label: 'Replace in files…', run: () => deps.search.open(undefined, { include: isDir ? includeFor(path) : path, replace: true }) },
            { label: 'Inspect code', run: () => deps.problems.inspect(path, isDir) },
            '-',
            { label: 'Reformat code', icon: ['format', ''], run: () => tidy(path, 'format') },
            { label: 'Optimize imports', run: () => tidy(path, 'imports') },
        );
        if (isDir) {
            items.push({ label: `Run the tests in ${path ? `'${path.slice(path.lastIndexOf('/') + 1)}'` : 'the project'}`, icon: 'run', run: () => deps.tests.runPath(path) },
                { label: `Debug the tests in ${path ? `'${path.slice(path.lastIndexOf('/') + 1)}'` : 'the project'}`, icon: 'debug', run: () => deps.tests.runPath(path, true) });
            const more = moreRunItems(path, true);
            if (more.length) items.push({ label: 'More run/debug', submenu: more });
            items.push('-', openIn(path, true));
            items.push({ label: 'Local history', run: () => deps.history.showFolder(path) });
            if (path) items.push({ label: 'Add to .gitignore', run: () => gitignore(path) }, bookmarkItem(path, true));
            items.push({ label: 'Clean Python compiled files', run: () => clean(path) });
        } else {
            items.push('-', ...fileItems(path));
        }
        const diagrams = diagramItems(path, isDir);
        if (diagrams.length) items.push('-', ...diagrams);
        return items;
    }

    /** Open Tab in New Window: the page again, in a window of its own (of PyMagic in the desktop app, a browser tab otherwise), showing this file. */
    async function newWindow(rel) {
        // the new window asks this one for the file's unsaved text (sharedtext.js), so nothing is saved first
        window.open(`${location.origin}/?open=${encodeURIComponent(rel)}`, '_blank');
    }

    /** The items the tabs add: the file's own, Open Tab in New Window and Override File Type. */
    function tabItems(rel) {
        return [{ label: 'Open in a new window', run: () => newWindow(rel) }, '-', ...fileItems(rel), '-', { label: 'Override file type', submenu: typeItems(rel) }];
    }

    /** The entries PyCharm's editor menu has and Monaco's lacks, on an editor (the main one, and each side of a split). `refactorItems` is the
     *  Refactor submenu, given for the main editor only (the refactorings work on it). */
    /** Generate… (Alt+Insert): what the engine offers where the cursor is, in a menu there; a test goes into its own file, which opens. */
    async function generate(rel, atCursor, ed = deps.editor) {
        const model = ed.getModel(), pos = ed.getPosition();
        if (!model || !pos) return;
        const uri = deps.uriFor(rel);
        deps.lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
        let options = [];
        try { options = (await deps.lsp.request('pymagic/generate', { textDocument: { uri }, position: { line: pos.lineNumber - 1, character: pos.column - 1 } })) ?? []; } catch { /* none */ }
        if (options.length === 0) { toast('Nothing to generate here: put the cursor in a class, or in a function for its test.', true); return; }
        const item = (o) => ({ label: o.title, run: () => void use(o) });
        const overrides = options.filter((o) => o.kind === 'override');
        atCursor([
            ...options.filter((o) => o.kind !== 'override' && o.kind !== 'test').map(item),
            ...(overrides.length ? [{ label: 'Override methods', submenu: overrides.map(item) }] : []),
            ...options.filter((o) => o.kind === 'test').map(item),
        ]);
    }
    async function use(o) {
        if (o.changes) { await deps.applyEdits(o.title, o.changes); return; }
        const rel = deps.relOfUri(o.newFile);
        if (!rel) { toast('The test file would be outside the project.', true); return; }
        const there = (await deps.textOf(rel)) !== null;
        if (!there) {
            const res = await fetch('/api/fs', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ op: 'create', path: rel }) });
            if (!res.ok) { toast(`${rel} cannot be made.`, true); return; }
            await deps.refreshTree?.();
        }
        // the whole text as it becomes (the import with the others, the test at the end), as one edit that undo takes back
        await deps.openFile(rel);
        const model = deps.editor.getModel();
        if (!model) return;
        deps.editor.pushUndoStop();
        deps.editor.executeEdits('generate', [{ range: model.getFullModelRange(), text: o.newText }]);
        deps.editor.pushUndoStop();
        const last = model.getLineCount();
        deps.editor.revealLineInCenter(last);
        deps.editor.setPosition({ lineNumber: last, column: model.getLineMaxColumn(last) });
        await deps.saveFile(rel);
        toast(`${o.title}: added`);
    }

    let copies = [];   // Paste from History: shared by every editor, the newest first
    function attachEditor(ed, monaco, refactorItems) {
        const rel = () => deps.relOfModel(ed.getModel());
        // a menu of PyMagic's own, opened where the cursor is
        const atCursor = (items) => {
            const pos = ed.getPosition(), box = ed.getDomNode()?.getBoundingClientRect();
            const p = pos && ed.getScrolledVisiblePosition(pos);
            openMenu(box && p ? { x: box.left + p.left, y: box.top + p.top + p.height } : { x: 200, y: 200 }, items);
        };
        const add = (id, label, group, order, run, extra = {}) =>
            ed.addAction({ id, label, contextMenuGroupId: group, contextMenuOrder: order, run: () => void run(rel()), ...extra });
        // the entries about the file do nothing in an editor without one
        const file = (fn) => (r) => (r ? fn(r) : undefined);
        add('pymagic.contextActions', 'Show Context Actions', 'navigation', 0.5, () => ed.getAction('editor.action.quickFix')?.run(),
            { keybindings: [monaco.KeyMod.Alt | monaco.KeyCode.Enter] });
        if (refactorItems) add('pymagic.refactorThis', 'Refactor…', '1_modification', 0.5, () => atCursor(refactorItems()),
            { keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Alt | monaco.KeyMod.Shift | monaco.KeyCode.KeyT] });
        if (refactorItems) add('pymagic.generate', 'Generate…', '1_modification', 0.6, file((r) => generate(r, atCursor, ed)),
            { keybindings: [monaco.KeyMod.Alt | monaco.KeyCode.Insert], precondition: 'editorLangId == python' });
        add('pymagic.columnSelection', 'Column Selection Mode (on / off)', '9_cutcopypaste', 10,
            () => ed.updateOptions({ columnSelection: !ed.getOption(monaco.editor.EditorOption.columnSelection) }),
            { keybindings: [monaco.KeyMod.Alt | monaco.KeyMod.Shift | monaco.KeyCode.Insert] });
        add('pymagic.pasteHistory', 'Paste from History…', '9_cutcopypaste', 11, () => {
            if (copies.length === 0) { toast('Nothing was copied in the editor yet.', true); return; }
            atCursor(copies.map((text) => ({ label: copyLabel(text), run: () => { ed.focus(); ed.pushUndoStop(); ed.executeEdits('paste-history', ed.getSelections().map((range) => ({ range, text, forceMoveMarkers: true }))); ed.pushUndoStop(); } })));
        }, { keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyV] });
        // what is copied or cut in this editor goes into the history (the whole lines, as Monaco copies them, when nothing is selected)
        const remember = () => {
            const model = ed.getModel();
            if (!model) return;
            const text = ed.getSelections().map((s) => (s.isEmpty() ? model.getLineContent(s.startLineNumber) + model.getEOL() : model.getValueInRange(s))).join(model.getEOL());
            copies = rememberCopy(copies, text);
        };
        ed.getContainerDomNode().addEventListener('copy', remember, true);
        ed.getContainerDomNode().addEventListener('cut', remember, true);
        add('pymagic.goToLine', 'Go to Line…', 'navigation', 1.9, () => ed.getAction('editor.action.gotoLine')?.run());
        add('pymagic.goToTest', 'Go to Test', 'navigation', 1.95, file(async (r) => {
            const found = testPartners(r, deps.pythonFiles());
            if (found.length === 0) { toast(isTestFile(r) ? 'No module of the name this test has was found.' : `No test file for ${r} (test_${r.slice(r.lastIndexOf('/') + 1)}) was found.`, true); return; }
            if (found.length === 1) await deps.openFile(found[0]);
            else atCursor(found.map((f) => ({ label: f, run: () => deps.openFile(f) })));
        }), { keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyT], precondition: 'editorLangId == python' });
        add('pymagic.foldAll', 'Fold All', 'y_folding', 1, () => ed.getAction('editor.foldAll')?.run());
        add('pymagic.unfoldAll', 'Unfold All', 'y_folding', 2, () => ed.getAction('editor.unfoldAll')?.run());
        const py = { precondition: 'editorLangId == python' };
        add('pymagic.runFile', 'Run This File', 'z_run', 1, file((r) => deps.run.runScript(r, '', false)), py);
        add('pymagic.debugFile', 'Debug This File', 'z_run', 2, file((r) => deps.run.runScript(r, '', true)), py);
        add('pymagic.fileHistory', 'Local History', 'z_run', 3, file((r) => history(r)));
        add('pymagic.compareClipboard', 'Compare with Clipboard', 'z_run', 4, file((r) => compareWithClipboard(r)));
        add('pymagic.openInExplorer', 'Open in Explorer', 'z_run', 5, file((r) => reveal(r)));
        add('pymagic.openInTerminal', 'Open in Terminal', 'z_run', 6, file((r) => deps.terminal.openAt(folderOf(r))));
    }

    return { fileItems, treeItems, tabItems, typeItems, history, reveal, compareWithClipboard, openIn, attachEditor, usagesOf, findUsages };
}

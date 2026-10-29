// The editor split in two: a second editor to the right with its own tabs. Both show the same open files (a change made on one side is on the
// other at once, with the same squiggles, hover and completion); the left editor stays the one the rest of the page works with (run, debug,
// breakpoints, the structure view). A tab's menu has "Split right", a tab dragged to the right edge of the editor opens there, and dragging it
// onto the other bar moves it across. The divider between the two can be dragged; closing the last tab on the right closes the split.
import { setupTabs, draggedTab, TAB_TYPE } from './tabs';
import { movedPath } from './explorer-model';

const $ = (id) => document.getElementById(id);

/**
 * deps: createEditor(element) -> a Monaco editor with the page's options, modelFor(rel), openMain(rel, reveal), isDirty(rel), askOnClose(rel),
 * save(rel), revert(rel), mainHas(rel) (the left bar shows it), onCursor(position).
 */
export function setupSplit(deps) {
    const groups = $('editor-groups');
    const side = $('split-area');
    const divider = $('split-divider');
    const zone = $('split-drop');
    let editor = null;
    let current = '';
    let focused = 'main';
    let tabs = null;
    let reopen = null;   // the new path of the file shown, while it is being moved

    function ratio(value) {
        if (value !== undefined) { try { localStorage.setItem('pymagic.split.ratio', String(value)); } catch { /* storage unavailable */ } }
        try { return Math.min(0.8, Math.max(0.2, Number(localStorage.getItem('pymagic.split.ratio')) || 0.5)); } catch { return 0.5; }
    }
    function layout() { side.style.flexBasis = `${(1 - ratio()) * 100}%`; }

    function ensureEditor() {
        if (editor) return editor;
        side.hidden = false;
        divider.hidden = false;
        layout();
        editor = deps.createEditor($('split-editor'));
        editor.onDidFocusEditorText(() => { focused = 'side'; groups.classList.add('side-focused'); });
        editor.onDidChangeCursorPosition((e) => { if (focused === 'side') deps.onCursor(e.position); });
        return editor;
    }

    async function open(rel, reveal) {
        if (rel.endsWith('.ipynb')) { await deps.openMain(rel, reveal); return; }   // the notebook view lives on the left
        const model = await deps.modelFor(rel);
        if (!model) return;
        ensureEditor();
        current = rel;
        editor.setModel(model);
        tabs.opened(rel);
        if (reveal) { editor.setSelection(reveal); editor.revealRangeInCenter(reveal); }
        editor.focus();
    }

    function close() {
        if (!editor) return;
        editor.dispose();
        editor = null;
        current = '';
        side.hidden = true;
        divider.hidden = true;
        focused = 'main';
        groups.classList.remove('side-focused');
        tabs.clear();
    }

    tabs = setupTabs({
        bar: $('split-tabs'),
        listButton: $('split-list'),
        storageKey: 'pymagic.split.tabs',
        isActive: () => focused === 'side' && !!editor,
        openFile: (rel) => open(rel),
        isDirty: deps.isDirty,
        askOnClose: deps.askOnClose,
        save: deps.save,
        revert: deps.revert,
        empty: close,
        split: (rel) => deps.openMain(rel),
        splitLabel: 'Open on the left',
    });

    // the left editor takes the focus back
    deps.mainEditor.onDidFocusEditorText(() => { focused = 'main'; groups.classList.remove('side-focused'); });

    // ------------------------------------------------------------ the divider
    divider.addEventListener('mousedown', (e) => {
        e.preventDefault();
        const box = groups.getBoundingClientRect();
        const move = (ev) => { ratio(Math.min(0.8, Math.max(0.2, (ev.clientX - box.left) / box.width))); layout(); };
        const up = () => { window.removeEventListener('mousemove', move); window.removeEventListener('mouseup', up); document.body.classList.remove('col-resizing'); };
        document.body.classList.add('col-resizing');
        window.addEventListener('mousemove', move);
        window.addEventListener('mouseup', up);
    });
    divider.addEventListener('dblclick', () => { ratio(0.5); layout(); });

    // ------------------------------------------------------------ a tab dragged to the right edge of the editor
    zone.dataset.tabDrop = '1';
    window.addEventListener('pymagic:tabdrag', (e) => {
        // only a tab of the left bar splits; one of the right bar is already there
        zone.hidden = !(e.detail && draggedTab() && draggedTab().from !== tabs);
        zone.classList.remove('over');
    });
    zone.addEventListener('dragover', (e) => { if (!e.dataTransfer.types.includes(TAB_TYPE)) return; e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; zone.classList.add('over'); });
    zone.addEventListener('dragleave', () => zone.classList.remove('over'));
    zone.addEventListener('drop', (e) => {
        const tab = draggedTab();
        zone.hidden = true;
        if (!tab) return;
        e.preventDefault();
        void open(tab.rel);
    });

    // the split of the last session comes back
    function restore(exists) {
        const rel = tabs.restore(exists);
        if (rel) void open(rel).then(() => deps.mainEditor.focus());
    }

    return {
        open, close, restore,
        editor: () => editor,
        current: () => current,
        focused: () => focused,
        has: (rel) => tabs.has(rel),
        /** A file or folder moved or renamed: the tabs follow, and the file shown comes back under its new path once the old one is dropped. */
        rename(from, to) {
            tabs.rename(from, to);
            if (editor && movedPath(current, from, to) !== current) reopen = movedPath(current, from, to);
        },
        /** A file renamed or deleted: its tabs go, and the split closes when nothing is left. */
        drop(rel) {
            tabs.drop(rel);
            if (editor && (current === rel || current.startsWith(rel + '/'))) {
                const next = reopen ?? tabs.mruFirst();
                reopen = null;
                if (next) void open(next); else close();
            }
        },
        refresh: () => tabs.refresh(),
    };
}

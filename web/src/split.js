// The editor split in two: a second editor to the right or below, with its own tabs. Both show the same open files (a change made on one side is on
// the other at once, with the same squiggles, hover and completion, breakpoints and the debugger's line); breakpoints are set with a click or F9 on
// either side, the run and debug keys work on both, and the Git change bars are drawn on both. The first editor stays the one the rest of the page
// works with (what Run runs, the structure view). A tab's menu has "Split right" and "Split down", a tab dragged to the right edge or the bottom of
// the editor opens there, and dragging it onto the other bar moves it across. The divider between the two can be dragged; closing the last tab of
// the second side closes the split.
import { setupTabs, draggedTab, TAB_TYPE } from './tabs';
import { movedPath } from './explorer-model';

const $ = (id) => document.getElementById(id);

/** 'right' or 'down', from what was stored (anything else is 'right'). */
export function orientationOf(stored) { return stored === 'down' ? 'down' : 'right'; }

/**
 * deps: createEditor(element) -> a Monaco editor with the page's options, modelFor(rel), openMain(rel, reveal), isDirty(rel), askOnClose(rel),
 * save(rel), revert(rel), onCursor(position), mainEditor.
 */
export function setupSplit(deps) {
    const groups = $('editor-groups');
    const side = $('split-area');
    const divider = $('split-divider');
    const zones = { right: $('split-drop'), down: $('split-drop-down') };
    let editor = null;
    let current = '';
    let focused = 'main';
    let tabs = null;
    let reopen = null;   // the new path of the file shown, while it is being moved
    let orientation = 'right';
    try { orientation = orientationOf(localStorage.getItem('pymagic.split.orientation')); } catch { /* storage unavailable */ }

    function ratio(value) {
        if (value !== undefined) { try { localStorage.setItem('pymagic.split.ratio', String(value)); } catch { /* storage unavailable */ } }
        try { return Math.min(0.8, Math.max(0.2, Number(localStorage.getItem('pymagic.split.ratio')) || 0.5)); } catch { return 0.5; }
    }
    function layout() {
        groups.classList.toggle('split-down', orientation === 'down');
        side.style.flexBasis = `${(1 - ratio()) * 100}%`;
    }
    function orient(where) {
        if (!where || where === orientation) return;
        orientation = where;
        try { localStorage.setItem('pymagic.split.orientation', where); } catch { /* storage unavailable */ }
        layout();
    }

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

    /** Opens a file on the second side; `where` ('right' or 'down') places that side. */
    async function open(rel, reveal, where) {
        if (rel.endsWith('.ipynb')) { await deps.openMain(rel, reveal); return; }   // the notebook view lives in the first editor
        const model = await deps.modelFor(rel);
        if (!model) return;
        orient(where);
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
        splitLabel: 'Open in the first editor',
    });

    // the first editor takes the focus back
    deps.mainEditor.onDidFocusEditorText(() => { focused = 'main'; groups.classList.remove('side-focused'); });

    // ------------------------------------------------------------ the divider
    divider.addEventListener('mousedown', (e) => {
        e.preventDefault();
        const box = groups.getBoundingClientRect();
        const down = orientation === 'down';
        const move = (ev) => {
            const share = down ? (ev.clientY - box.top) / box.height : (ev.clientX - box.left) / box.width;
            ratio(Math.min(0.8, Math.max(0.2, share)));
            layout();
        };
        const cls = down ? 'row-resizing' : 'col-resizing';
        const up = () => { window.removeEventListener('mousemove', move); window.removeEventListener('mouseup', up); document.body.classList.remove(cls); };
        document.body.classList.add(cls);
        window.addEventListener('mousemove', move);
        window.addEventListener('mouseup', up);
    });
    divider.addEventListener('dblclick', () => { ratio(0.5); layout(); });

    // ------------------------------------------------------------ a tab dragged to the right edge or the bottom of the editor
    for (const [where, zone] of Object.entries(zones)) {
        if (!zone) continue;
        zone.dataset.tabDrop = '1';
        zone.addEventListener('dragover', (e) => { if (!e.dataTransfer.types.includes(TAB_TYPE)) return; e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; zone.classList.add('over'); });
        zone.addEventListener('dragleave', () => zone.classList.remove('over'));
        zone.addEventListener('drop', (e) => {
            const tab = draggedTab();
            for (const z of Object.values(zones)) if (z) z.hidden = true;
            if (!tab) return;
            e.preventDefault();
            void open(tab.rel, undefined, where);
        });
    }
    window.addEventListener('pymagic:tabdrag', (e) => {
        // only a tab of the first bar splits; one of the second bar is already there
        const show = !!(e.detail && draggedTab() && draggedTab().from !== tabs);
        for (const z of Object.values(zones)) if (z) { z.hidden = !show; z.classList.remove('over'); }
    });

    // the split of the last session comes back
    function restore(exists) {
        layout();
        const rel = tabs.restore(exists);
        if (rel) void open(rel).then(() => deps.mainEditor.focus());
    }

    return {
        open, close, restore,
        editor: () => editor,
        current: () => current,
        focused: () => focused,
        orientation: () => orientation,
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

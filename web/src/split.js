// The editor split: up to three more editors beside the first one, to the right or below, each with its own tabs. All show the same open files (a
// change made on one side is on the others at once, with the same squiggles, hover and completion, breakpoints and the debugger's line, Git change
// bars with their Revert popup, coverage and inheritance marks); breakpoints are set with a click or F9 on any side and the run and debug keys work
// on all. The first editor stays the one the rest of the page works with (what Run runs, the structure view). A tab's menu has "Split right" and
// "Split down" (a new side after the one the tab is on), a tab dragged to the right edge or the bottom of the editor opens on a new side, and
// dragging it onto another bar moves it across. The dividers can be dragged; closing the last tab of a side closes that side.
import { setupTabs, draggedTab, TAB_TYPE } from './tabs';
import { movedPath } from './explorer-model';
import { splitWeights, resizePair } from './splitsizes';

const $ = (id) => document.getElementById(id);

export const MAX_SIDES = 3;
// where each side keeps its tabs; the first one keeps the key a single split always had
const KEYS = ['pymagic.split.tabs', 'pymagic.split.tabs.2', 'pymagic.split.tabs.3'];

/** 'right' or 'down', from what was stored (anything else is 'right'). */
export function orientationOf(stored) { return stored === 'down' ? 'down' : 'right'; }

/**
 * deps: createEditor(element) -> a Monaco editor with the page's options, modelFor(rel), openMain(rel, reveal), isDirty(rel), askOnClose(rel),
 * save(rel), revert(rel), onCursor(position), mainEditor.
 */
export function setupSplit(deps) {
    const groups = $('editor-groups');
    const mainArea = $('editor-area');
    const zones = { right: $('split-drop'), down: $('split-drop-down') };
    const sides = [];          // in their order: { key, area, divider, editor, current, tabs, reopen }
    let focused = null;        // the side that has the focus; null for the first editor
    let orientation = 'right';
    let weights = [];          // the share of the first editor and of each side
    const read = (key) => { try { return localStorage.getItem(key); } catch { return null; } };
    const write = (key, value) => { try { localStorage.setItem(key, value); } catch { /* storage unavailable */ } };
    orientation = orientationOf(read('pymagic.split.orientation'));

    function layout() {
        groups.classList.toggle('split-down', orientation === 'down');
        weights = splitWeights(weights, sides.length + 1);
        [mainArea, ...sides.map((s) => s.area)].forEach((el, i) => { el.style.flex = `${weights[i]} 1 0`; });
    }
    function saveLayout() {
        write('pymagic.split.sides', JSON.stringify(sides.map((s) => s.key)));
        write('pymagic.split.weights', JSON.stringify(weights));
    }
    function orient(where) {
        if (!where || where === orientation) return;
        orientation = where;
        write('pymagic.split.orientation', where);
        layout();
    }
    function markFocus() {
        groups.classList.toggle('side-focused', !!focused);
        mainArea.classList.toggle('focused-group', !focused);
        for (const s of sides) s.area.classList.toggle('focused-group', s === focused);
    }

    /** A new side after `after` (the end when not given): its divider, tab bar and editor. */
    function addSide(after, key) {
        key ??= KEYS.find((k) => !sides.some((s) => s.key === k));
        const divider = document.createElement('div');
        divider.className = 'split-divider';
        const area = document.createElement('div');
        area.className = 'split-area';
        const row = document.createElement('div');
        row.className = 'tabs-row';
        const bar = document.createElement('div');
        bar.className = 'tabs-bar';
        const list = document.createElement('button');
        list.className = 'tabs-list';
        list.hidden = true;
        list.innerHTML = '<svg class="i"><use href="#i-chev-down"/></svg>';
        row.append(bar, list);
        const host = document.createElement('div');
        host.className = 'split-editor';
        area.append(row, host);
        const at = after ? sides.indexOf(after) + 1 : sides.length;
        const before = sides[at]?.divider ?? zones.right;
        groups.insertBefore(divider, before);
        groups.insertBefore(area, before);
        const side = { key, area, divider, editor: null, current: '', tabs: null, reopen: null };
        sides.splice(at, 0, side);
        // the new side takes half of the one it opens from
        const from = at;   // the index of the group before it among [first, ...sides]
        weights = splitWeights(weights, sides.length);
        const half = (weights[from] ?? 1) / 2;
        weights.splice(from, 1, half, half);
        layout();
        side.editor = deps.createEditor(host);
        side.editor.onDidFocusEditorText(() => { focused = side; markFocus(); });
        side.editor.onDidChangeCursorPosition((e) => { if (focused === side) deps.onCursor(e.position); });
        side.tabs = setupTabs({
            bar, listButton: list, storageKey: key,
            isActive: () => focused === side,
            openFile: (rel) => show(side, rel),
            isDirty: deps.isDirty,
            askOnClose: (rel) => deps.askOnClose(rel) && !sides.some((s) => s !== side && s.tabs.has(rel)),
            save: deps.save,
            revert: deps.revert,
            empty: () => closeSide(side),
            // read when the menu opens: a new side while there is room for one, else back to the first editor
            get split() { return sides.length < MAX_SIDES ? (rel) => open(rel, undefined, 'right', side) : (rel) => deps.openMain(rel); },
            get splitLabel() { return sides.length < MAX_SIDES ? 'Split right' : 'Open in the first editor'; },
            get splitDown() { return sides.length < MAX_SIDES ? (rel) => open(rel, undefined, 'down', side) : undefined; },
        });
        divider.addEventListener('mousedown', (e) => dragDivider(e, side));
        divider.addEventListener('dblclick', () => { const i = sides.indexOf(side); weights = resizePair(weights, i, 0.5); layout(); saveLayout(); });
        saveLayout();
        return side;
    }

    /** Shows a file on a side. */
    async function show(side, rel, reveal) {
        if (rel.endsWith('.ipynb')) { await deps.openMain(rel, reveal); return; }   // the notebook view lives in the first editor
        const model = await deps.modelFor(rel);
        if (!model || !sides.includes(side)) return;
        side.current = rel;
        side.editor.setModel(model);
        side.tabs.opened(rel);
        if (reveal) { side.editor.setSelection(reveal); side.editor.revealRangeInCenter(reveal); }
        side.editor.focus();
    }

    /**
     * Opens a file beside: `where` ('right' or 'down') makes a new side (after `from`, a side, when it is given) and places them all that way;
     * with no room for another side the last one shows it. Without `where`, the side that has the focus shows it (or the last, or a new one).
     */
    async function open(rel, reveal, where, from) {
        if (rel.endsWith('.ipynb')) { await deps.openMain(rel, reveal); return; }
        if (!(await deps.modelFor(rel))) return;
        orient(where);
        let side;
        if (where && sides.length < MAX_SIDES) side = addSide(from ?? sides.at(-1) ?? null);
        else side = focused ?? sides.at(-1) ?? addSide(null);
        await show(side, rel, reveal);
    }

    /** Opens a file on the side that `editor` is (from a jump made there); the first editor when it is not a side. */
    async function openIn(editor, rel, reveal) {
        const side = sides.find((s) => s.editor === editor);
        if (side) await show(side, rel, reveal); else await deps.openMain(rel, reveal);
    }

    function closeSide(side) {
        const i = sides.indexOf(side);
        if (i < 0) return;
        side.editor.dispose();
        side.divider.remove();
        side.area.remove();
        side.tabs.clear();
        sides.splice(i, 1);
        // its share goes to the group before it
        weights = splitWeights(weights, sides.length + 2);
        weights[i] += weights[i + 1];
        weights.splice(i + 1, 1);
        if (focused === side) { focused = null; deps.mainEditor.focus(); }
        markFocus();
        layout();
        saveLayout();
    }
    function close() { for (const s of [...sides].reverse()) closeSide(s); }

    // ------------------------------------------------------------ the dividers
    function dragDivider(e, side) {
        e.preventDefault();
        const i = sides.indexOf(side);   // between group i and group i + 1 of [first, ...sides]
        const down = orientation === 'down';
        const before = i === 0 ? mainArea : sides[i - 1].area;
        const a = before.getBoundingClientRect(), b = side.area.getBoundingClientRect();
        const start = down ? a.top : a.left, size = down ? b.bottom - a.top : b.right - a.left;
        const move = (ev) => {
            weights = resizePair(weights, i, ((down ? ev.clientY : ev.clientX) - start) / size);
            layout();
        };
        const cls = down ? 'row-resizing' : 'col-resizing';
        const up = () => { window.removeEventListener('mousemove', move); window.removeEventListener('mouseup', up); document.body.classList.remove(cls); saveLayout(); };
        document.body.classList.add(cls);
        window.addEventListener('mousemove', move);
        window.addEventListener('mouseup', up);
    }

    // the first editor takes the focus back
    deps.mainEditor.onDidFocusEditorText(() => { focused = null; markFocus(); });
    markFocus();

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
        const show = !!(e.detail && draggedTab());
        for (const z of Object.values(zones)) if (z) { z.hidden = !show; z.classList.remove('over'); }
    });

    // the split of the last session comes back: its sides, their tabs and sizes
    function restore(exists) {
        let keys = [];
        try { keys = JSON.parse(read('pymagic.split.sides') ?? 'null') ?? [KEYS[0]]; } catch { keys = [KEYS[0]]; }
        try { weights = JSON.parse(read('pymagic.split.weights') ?? '[]'); } catch { weights = []; }
        const wanted = weights;
        for (const key of keys.filter((k) => KEYS.includes(k)).slice(0, MAX_SIDES)) {
            const side = addSide(null, key);
            const rel = side.tabs.restore(exists);
            if (rel) void show(side, rel).then(() => deps.mainEditor.focus());
            else closeSide(side);
        }
        if (Array.isArray(wanted) && wanted.length === sides.length + 1) weights = wanted;
        layout();
    }

    const editorOfFocus = () => (focused ?? sides.at(-1))?.editor ?? null;
    return {
        open, openIn, close, restore,
        /** The editor of the side that has the focus (or the last side); null when the editor is not split. */
        editor: editorOfFocus,
        editors: () => sides.map((s) => s.editor),
        current: () => (focused ?? sides.at(-1))?.current ?? '',
        focused: () => (focused ? 'side' : 'main'),
        orientation: () => orientation,
        has: (rel) => sides.some((s) => s.tabs.has(rel)),
        /** A file or folder moved or renamed: the tabs follow, and each file shown comes back under its new path once the old one is dropped. */
        rename(from, to) {
            for (const s of sides) {
                s.tabs.rename(from, to);
                if (movedPath(s.current, from, to) !== s.current) s.reopen = movedPath(s.current, from, to);
            }
        },
        /** A file renamed or deleted: its tabs go, and a side closes when nothing is left on it. */
        drop(rel) {
            for (const s of [...sides]) {
                s.tabs.drop(rel);
                if (sides.includes(s) && (s.current === rel || s.current.startsWith(rel + '/'))) {
                    const next = s.reopen ?? s.tabs.mruFirst();
                    s.reopen = null;
                    if (next) void show(s, next); else closeSide(s);
                }
            }
        },
        refresh: () => { for (const s of sides) s.tabs.refresh(); },
    };
}

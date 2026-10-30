// The tab bar above an editor: the files being worked on, a mark for unsaved ones, close buttons, middle-click to close, Ctrl+Tab to switch
// (most recently used first), dragging to reorder, pinned tabs that stay first and are not closed with the others, and a list of every tab
// for a bar too narrow to show them. The editor split in two has one bar for each side; a tab dragged onto the other bar moves there.
import { clampToGroup, dropIndex, moveBy, moveTo, pinFirst, staysPut } from './taborder';
import { movedPath } from './explorer-model';
import { openMenu } from './menu';

const $ = (id) => document.getElementById(id);
export const TAB_TYPE = 'application/x-pymagic-tab';   // not text/plain, so a tab dropped on the editor inserts nothing

function baseName(path) { return path.slice(path.lastIndexOf('/') + 1); }

/** Save / Don't save / Cancel for a file with unsaved changes. Resolves to 'save', 'discard' or 'cancel'. */
function askSave(name) {
    return new Promise((resolve) => {
        const shade = document.createElement('div');
        shade.className = 'ask-shade';
        const box = document.createElement('div');
        box.className = 'ask-box';
        const title = document.createElement('div');
        title.className = 'ask-title';
        title.textContent = 'Unsaved changes';
        const text = document.createElement('div');
        text.className = 'ask-hint';
        text.textContent = `${name} has changes that are not saved.`;
        const row = document.createElement('div');
        row.className = 'ask-buttons';
        const done = (value) => { shade.remove(); resolve(value); };
        for (const [label, value, cls] of [['Save', 'save', ''], ["Don't save", 'discard', 'danger'], ['Cancel', 'cancel', '']]) {
            const b = document.createElement('button');
            b.textContent = label;
            if (cls) b.className = cls;
            b.onclick = () => done(value);
            row.append(b);
        }
        box.append(title, text, row);
        shade.append(box);
        shade.onmousedown = (e) => { if (e.target === shade) done('cancel'); };
        shade.onkeydown = (e) => { if (e.key === 'Escape') done('cancel'); };
        document.body.append(shade);
        row.firstChild.focus();
    });
}

function pinIcon() {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', 'i');
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', '#i-pin');
    svg.append(use);
    return svg;
}

// the tab being dragged, whichever bar it comes from: a bar that is not its own takes it in
let dragged = null;   // { rel, from: the bar's api }

/**
 * deps: openFile, isDirty, save, revert, empty; and for a second bar `bar` (its element), `listButton`, `storageKey`, `isActive` (the keys act on
 * the bar of the side that has the focus), `askOnClose` (closing asks about unsaved changes only when no other editor shows the file),
 * `split` / `splitLabel` (the menu entry that opens a tab on the other side).
 */
export function setupTabs(deps) {
    const bar = deps.bar ?? $('tabs-bar');
    const listButton = deps.listButton ?? $('tabs-list');
    const storageKey = deps.storageKey ?? 'pymagic.tabs';
    const isActive = deps.isActive ?? (() => true);
    const askOnClose = deps.askOnClose ?? deps.isDirty;
    let tabs = [];            // relative paths, in the order of the bar
    let mru = [];             // the same, the most recently used first
    let pinned = new Set();
    let active = '';
    let cycling = -1;         // while Ctrl is held and Tab pressed: where in mru we are
    let menuEl = null;
    let api = null;
    bar.dataset.tabDrop = '1';

    function remember() {
        try { localStorage.setItem(storageKey, JSON.stringify({ tabs, active, pinned: [...pinned] })); } catch { /* storage unavailable */ }
    }

    function render() {
        tabs = pinFirst(tabs, pinned);
        bar.replaceChildren(...tabs.map((rel) => {
            const tab = document.createElement('div');
            tab.className = 'etab';
            tab.dataset.path = rel;
            tab.title = rel;
            const name = document.createElement('span');
            name.className = 'etab-name';
            name.textContent = baseName(rel);
            const close = document.createElement('span');
            close.className = 'etab-close';
            if (pinned.has(rel)) {
                tab.classList.add('pinned');
                close.classList.add('etab-pin');
                close.append(pinIcon());
                close.title = 'Pinned: click to unpin';
                close.onclick = (e) => { e.stopPropagation(); pin(rel, false); };
            } else {
                close.textContent = '✕';
                close.title = 'Close (Ctrl+W)';
                close.onclick = (e) => { e.stopPropagation(); void closeTab(rel); };
            }
            tab.append(name, close);
            tab.onmousedown = (e) => {
                if (e.button === 1) e.preventDefault();
                // a tab opens when it is pressed, as in PyCharm, so a click that slips into a short drag still opens it;
                // deferred so the browser's own focus handling of the press is done before the editor takes the focus
                else if (e.button === 0 && !close.contains(e.target)) setTimeout(() => void deps.openFile(rel));
            };
            // middle-click leaves a pinned tab alone, as the close button does
            tab.onmouseup = (e) => { if (e.button === 1 && !pinned.has(rel)) { e.preventDefault(); void closeTab(rel); } };
            tab.oncontextmenu = (e) => { e.preventDefault(); menu(e.clientX, e.clientY, rel); };
            tab.draggable = true;
            tab.ondragstart = (e) => {
                dragged = { rel, from: api };
                e.dataTransfer.effectAllowed = 'move';
                e.dataTransfer.setData(TAB_TYPE, rel);
                window.dispatchEvent(new CustomEvent('pymagic:tabdrag', { detail: true }));
                // faded only after the browser has taken its picture of the tab for the drag image
                requestAnimationFrame(() => { if (dragged?.rel === rel) tab.classList.add('dragging'); });
            };
            tab.ondragend = () => {
                dragged = null;
                tab.classList.remove('dragging');
                markDrop(-1);
                window.dispatchEvent(new CustomEvent('pymagic:tabdrag', { detail: false }));
            };
            return tab;
        }));
        refresh();
    }

    /** Puts the tabs in a new order; `reveal` is a tab to scroll into view instead of leaving the bar on the open one. */
    function reorder(next, reveal = null) {
        next = pinFirst(next, pinned);
        if (next === tabs) return;
        tabs = next;
        remember();
        render();
        if (reveal) Array.from(bar.children).find((el) => el.dataset.path === reveal)?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }

    function pin(rel, on) {
        if (on) pinned.add(rel); else pinned.delete(rel);
        remember();
        render();
    }

    // ------------------------------------------------------------ dragging a tab along the bar, or onto the other one

    function dropAt(x) {
        return dropIndex(Array.from(bar.children, (el) => { const r = el.getBoundingClientRect(); return r.left + r.width / 2; }), x);
    }

    const own = () => dragged && dragged.from === api;

    /** The line where the dragged tab would land; -1 removes it. */
    function markDrop(index) {
        const kids = Array.from(bar.children);
        const show = index >= 0 && dragged && (!own() || !staysPut(tabs, dragged.rel, index));
        kids.forEach((el, i) => {
            el.classList.toggle('drop-before', !!show && i === index);
            el.classList.toggle('drop-after', !!show && index === kids.length && i === kids.length - 1);
        });
        bar.classList.toggle('drop-into', !!show && kids.length === 0);
    }

    // the type check keeps other drags (files from Explorer) out, even if a tab's dragend never arrived
    const isTabDrag = (e) => dragged && e.dataTransfer.types.includes(TAB_TYPE);
    bar.addEventListener('dragover', (e) => {
        if (!isTabDrag(e)) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        markDrop(own() ? clampToGroup(tabs, pinned, dragged.rel, dropAt(e.clientX)) : dropAt(e.clientX));
        // near either end the bar scrolls, so a tab can be taken to a place that is out of sight
        const r = bar.getBoundingClientRect();
        if (e.clientX < r.left + 32) bar.scrollLeft -= 16;
        else if (e.clientX > r.right - 32) bar.scrollLeft += 16;
    });
    bar.addEventListener('dragleave', (e) => { if (!bar.contains(e.relatedTarget)) markDrop(-1); });
    bar.addEventListener('drop', (e) => {
        if (!isTabDrag(e)) return;
        e.preventDefault();
        const { rel, from } = dragged;
        dragged = null;
        markDrop(-1);
        if (from === api) { reorder(moveTo(tabs, rel, clampToGroup(tabs, pinned, rel, dropAt(e.clientX))), rel); return; }
        // from the other side: the tab moves here, where it was dropped, pinned if it was
        const at = dropAt(e.clientX);
        const wasPinned = from.isPinned(rel);
        from.release(rel);
        void deps.openFile(rel).then(() => {
            if (wasPinned) pinned.add(rel);
            reorder(moveTo(tabs, rel, clampToGroup(tabs, pinned, rel, at)), rel);
            remember();
        });
    });
    // a tab can only be dropped on a bar or a place made for it; elsewhere it is refused, above all on the editor, which would move its caret
    if (!window.__pymagicTabGuard) {
        window.__pymagicTabGuard = true;
        for (const type of ['dragover', 'drop'])
            window.addEventListener(type, (e) => { if (dragged && e.dataTransfer.types.includes(TAB_TYPE) && !e.target.closest?.('[data-tab-drop]')) e.stopPropagation(); }, true);
    }

    /** Only the marks: which tab is the open one and which have unsaved changes; and whether the list button is needed. */
    function refresh() {
        for (const tab of Array.from(bar.children)) {
            const rel = tab.dataset.path;
            tab.classList.toggle('active', rel === active);
            tab.classList.toggle('dirty', deps.isDirty(rel));
        }
        const current = bar.querySelector('.etab.active');
        current?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        if (listButton) listButton.hidden = bar.scrollWidth <= bar.clientWidth + 1;
    }
    if (listButton && 'ResizeObserver' in window) new ResizeObserver(() => refresh()).observe(bar);

    /** Called when a file is shown in the editor. */
    function opened(rel) {
        const known = tabs.includes(rel);
        if (!known) tabs.push(rel);
        active = rel;
        if (cycling < 0) mru = [rel, ...mru.filter((r) => r !== rel)];
        remember();
        // a tab that is already there keeps its element, so a press that is turning into a drag is not cut off
        if (known) refresh(); else render();
    }

    /** A file or folder moved or was renamed: its tabs keep their places, pins and the open one under the new paths. */
    function rename(from, to) {
        const move = (r) => movedPath(r, from, to);
        if (!tabs.some((r) => move(r) !== r)) return;
        tabs = tabs.map(move);
        mru = mru.map(move);
        pinned = new Set([...pinned].map(move));
        active = move(active);
        remember();
        render();
    }

    /** Takes tabs away without asking (the file or folder was renamed or deleted). */
    function drop(rel) {
        const hit = (r) => r === rel || r.startsWith(rel + '/');
        if (!tabs.some(hit)) return;
        tabs = tabs.filter((r) => !hit(r));
        mru = mru.filter((r) => !hit(r));
        for (const r of [...pinned]) if (hit(r)) pinned.delete(r);
        if (hit(active)) active = '';
        remember();
        render();
    }

    /** What is left after a tab is gone: the most recently used other tab is shown, or nothing. */
    async function afterRemoving(wasActive, at) {
        if (!wasActive) return;
        active = '';
        const next = mru[0] ?? tabs[Math.min(at, tabs.length - 1)];
        if (next) await deps.openFile(next);
        else deps.empty();
    }

    /** A tab that moved to the other side leaves this bar, without asking about its changes (the other editor has them). */
    async function release(rel) {
        if (!tabs.includes(rel)) return;
        const wasActive = rel === active;
        const at = tabs.indexOf(rel);
        tabs = tabs.filter((r) => r !== rel);
        mru = mru.filter((r) => r !== rel);
        pinned.delete(rel);
        remember();
        render();
        await afterRemoving(wasActive, at);
    }

    async function closeTab(rel) {
        if (askOnClose(rel)) {
            const answer = await askSave(baseName(rel));
            if (answer === 'cancel') return;
            if (answer === 'save') await deps.save(rel);
            else await deps.revert(rel);
        }
        const wasActive = rel === active;
        const at = tabs.indexOf(rel);
        tabs = tabs.filter((r) => r !== rel);
        mru = mru.filter((r) => r !== rel);
        pinned.delete(rel);
        remember();
        await afterRemoving(wasActive, at);
        render();
    }

    /** Closes every tab but those kept; pinned tabs stay unless `evenPinned`. */
    async function closeMany(keep, evenPinned = false) {
        for (const rel of [...tabs]) {
            if (keep.includes(rel) || pinned.has(rel) && !evenPinned) continue;
            if (askOnClose(rel)) {
                await deps.openFile(rel);
                const answer = await askSave(baseName(rel));
                if (answer === 'cancel') return;
                if (answer === 'save') await deps.save(rel); else await deps.revert(rel);
            }
            tabs = tabs.filter((r) => r !== rel);
            mru = mru.filter((r) => r !== rel);
            pinned.delete(rel);
        }
        remember();
        if (!tabs.includes(active)) {
            active = '';
            const next = mru[0] ?? tabs[0];
            if (next) await deps.openFile(next); else deps.empty();
        }
        render();
    }

    function closeMenu() { menuEl?.remove(); menuEl = null; }
    function showMenu(x, y, entries) {
        closeMenu();
        menuEl = document.createElement('div');
        menuEl.className = 'ctx-menu';
        for (const [label, action, cls] of entries) {
            if (label === '—') { const hr = document.createElement('div'); hr.className = 'ctx-sep'; menuEl.append(hr); continue; }
            const item = document.createElement('div');
            item.className = 'ctx-item' + (cls ? ' ' + cls : '');
            item.textContent = label;
            item.onclick = () => { closeMenu(); void action(); };
            menuEl.append(item);
        }
        document.body.append(menuEl);
        menuEl.style.left = `${Math.max(4, Math.min(x, window.innerWidth - menuEl.offsetWidth - 4))}px`;
        menuEl.style.top = `${Math.min(y, window.innerHeight - menuEl.offsetHeight - 4)}px`;
    }
    function menu(x, y, rel) {
        closeMenu();
        const at = tabs.indexOf(rel);
        const left = tabs.slice(0, at), right = tabs.slice(at + 1);
        const items = [
            { label: 'Close', run: () => closeTab(rel) },
            { label: 'Close others', run: () => closeMany([rel]) },
            { label: 'Close tabs to the left', disabled: left.length === 0, run: () => closeMany([rel, ...right]) },
            { label: 'Close tabs to the right', disabled: right.length === 0, run: () => closeMany([...left, rel]) },
            { label: 'Close all', run: () => closeMany([]) },
        ];
        if (pinned.size > 0) items.push({ label: 'Close all, even pinned', run: () => closeMany([], true) });
        items.push('-', pinned.has(rel) ? { label: 'Unpin tab', run: () => pin(rel, false) } : { label: 'Pin tab', run: () => pin(rel, true) });
        if (deps.split || deps.splitDown) items.push('-');
        if (deps.split) items.push({ label: deps.splitLabel ?? 'Split right', run: () => deps.split(rel) });
        if (deps.splitDown) items.push({ label: 'Split down', run: () => deps.splitDown(rel) });
        // the file moves to the new side: this tab closes without asking, the other side has its changes (the first editor is never left empty)
        const alone = tabs.length < 2;
        if (deps.split) items.push({ label: 'Split and move right', disabled: alone, run: async () => { await deps.split(rel); await closeTab(rel); } });
        if (deps.splitDown) items.push({ label: 'Split and move down', disabled: alone, run: async () => { await deps.splitDown(rel); await closeTab(rel); } });
        if (deps.renameFile) items.push('-', { label: 'Rename file…', run: () => deps.renameFile(rel) });
        if (deps.items) items.push('-', ...deps.items(rel));
        items.push('-', { label: 'Copy path', run: () => navigator.clipboard?.writeText(rel) });
        openMenu({ x, y }, items);
    }
    document.addEventListener('mousedown', (e) => { if (menuEl && !menuEl.contains(e.target) && e.target !== listButton && !listButton?.contains(e.target)) closeMenu(); });

    /** Every tab of the bar, for when they do not fit: pinned ones marked, the open one first in bold. */
    function showList() {
        if (menuEl) { closeMenu(); return; }
        const r = listButton.getBoundingClientRect();
        showMenu(r.right - 240, r.bottom + 2, tabs.map((rel) => [`${pinned.has(rel) ? '📌 ' : ''}${baseName(rel)}${deps.isDirty(rel) ? ' •' : ''}  —  ${rel}`, () => deps.openFile(rel), rel === active ? 'current' : '']));
    }
    if (listButton) { listButton.onclick = showList; listButton.title = 'All open tabs'; }

    function step(offset) {
        if (tabs.length < 2) return;
        const at = tabs.indexOf(active);
        void deps.openFile(tabs[(Math.max(at, 0) + offset + tabs.length) % tabs.length]);
    }

    window.addEventListener('keydown', (e) => {
        if (!isActive()) return;
        // in a terminal, Ctrl+W and the PageUp/PageDown keys belong to the shell (Ctrl+Tab still switches the editor's tabs)
        if (e.target?.closest?.('.xterm') && !(e.ctrlKey && e.key === 'Tab')) return;
        if (e.ctrlKey && e.key === 'Tab') {
            e.preventDefault();
            if (mru.length < 2) return;
            if (cycling < 0) cycling = 0;
            cycling = e.shiftKey ? (cycling - 1 + mru.length) % mru.length : (cycling + 1) % mru.length;
            void deps.openFile(mru[cycling]);
        } else if (e.ctrlKey && !e.altKey && !e.shiftKey && (e.key === 'w' || e.key === 'W') && active) {
            e.preventDefault();
            void closeTab(active);
        } else if (e.altKey && !e.ctrlKey && e.key === 'PageDown') { e.preventDefault(); step(1); }
        else if (e.altKey && !e.ctrlKey && e.key === 'PageUp') { e.preventDefault(); step(-1); }
        else if (e.ctrlKey && e.shiftKey && !e.altKey && (e.key === 'PageDown' || e.key === 'PageUp') && active) {
            e.preventDefault();
            reorder(moveBy(tabs, active, e.key === 'PageDown' ? 1 : -1));
        }
    }, true);
    window.addEventListener('keyup', (e) => {
        if (e.key === 'Control' && cycling >= 0) {
            // Ctrl released: the file we stopped on becomes the most recently used
            cycling = -1;
            if (active) mru = [active, ...mru.filter((r) => r !== active)];
        }
    });

    api = {
        opened, refresh, drop, release, rename,
        close: closeTab,
        pin,
        isPinned: (rel) => pinned.has(rel),
        has: (rel) => tabs.includes(rel),
        active: () => active,
        mruFirst: () => mru[0] ?? tabs[0] ?? null,
        /** The tabs of the last session that still exist, and the one that was open. */
        restore(exists) {
            try {
                const saved = JSON.parse(localStorage.getItem(storageKey) ?? 'null');
                if (!saved) return null;
                tabs = (saved.tabs ?? []).filter(exists);
                pinned = new Set((saved.pinned ?? []).filter((r) => tabs.includes(r)));
                mru = [...tabs];
                render();
                return exists(saved.active) ? saved.active : tabs[0] ?? null;
            } catch { return null; }
        },
        /** Forgets the tabs of the last session (a split that was closed). */
        clear() { tabs = []; mru = []; pinned = new Set(); active = ''; remember(); render(); },
        list: () => [...tabs],
    };
    return api;
}

/** The tab being dragged right now, from either bar: what a place that takes tabs (the edge of the editor) reads. */
export function draggedTab() { return dragged; }

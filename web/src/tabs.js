// The tab bar above the editor: the files being worked on, a mark for unsaved ones, close buttons,
// middle-click to close, Ctrl+Tab to switch (most recently used first) and dragging to reorder.
import { dropIndex, moveBy, moveTo, staysPut } from './taborder';

const $ = (id) => document.getElementById(id);
const TAB_TYPE = 'application/x-pymagic-tab';   // not text/plain, so a tab dropped on the editor inserts nothing

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

export function setupTabs(deps) {
    const bar = $('tabs-bar');
    let tabs = [];            // relative paths, in the order of the bar
    let mru = [];             // the same, the most recently used first
    let active = '';
    let cycling = -1;         // while Ctrl is held and Tab pressed: where in mru we are
    let menuEl = null;
    let dragging = null;      // the tab being dragged along the bar

    function remember() {
        try { localStorage.setItem('pymagic.tabs', JSON.stringify({ tabs, active })); } catch { /* storage unavailable */ }
    }

    function render() {
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
            close.textContent = '✕';
            close.title = 'Close (Ctrl+W)';
            close.onclick = (e) => { e.stopPropagation(); void closeTab(rel); };
            tab.append(name, close);
            tab.onmousedown = (e) => {
                if (e.button === 1) e.preventDefault();
                // a tab opens when it is pressed, as in PyCharm, so a click that slips into a short drag still opens it;
                // deferred so the browser's own focus handling of the press is done before the editor takes the focus
                else if (e.button === 0 && !close.contains(e.target)) setTimeout(() => void deps.openFile(rel));
            };
            tab.onmouseup = (e) => { if (e.button === 1) { e.preventDefault(); void closeTab(rel); } };
            tab.oncontextmenu = (e) => { e.preventDefault(); menu(e.clientX, e.clientY, rel); };
            tab.draggable = true;
            tab.ondragstart = (e) => {
                dragging = rel;
                e.dataTransfer.effectAllowed = 'move';
                e.dataTransfer.setData(TAB_TYPE, rel);
                // faded only after the browser has taken its picture of the tab for the drag image
                requestAnimationFrame(() => { if (dragging === rel) tab.classList.add('dragging'); });
            };
            tab.ondragend = () => { dragging = null; tab.classList.remove('dragging'); markDrop(-1); };
            return tab;
        }));
        refresh();
    }

    /** Puts the tabs in a new order; `reveal` is a tab to scroll into view instead of leaving the bar on the open one. */
    function reorder(next, reveal = null) {
        if (next === tabs) return;
        tabs = next;
        remember();
        render();
        if (reveal) Array.from(bar.children).find((el) => el.dataset.path === reveal)?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }

    // ------------------------------------------------------------ dragging a tab along the bar

    function dropAt(x) {
        return dropIndex(Array.from(bar.children, (el) => { const r = el.getBoundingClientRect(); return r.left + r.width / 2; }), x);
    }

    /** The line where the dragged tab would land; -1 removes it. */
    function markDrop(index) {
        const kids = Array.from(bar.children);
        const show = index >= 0 && dragging && !staysPut(tabs, dragging, index);
        kids.forEach((el, i) => {
            el.classList.toggle('drop-before', !!show && i === index);
            el.classList.toggle('drop-after', !!show && index === kids.length && i === kids.length - 1);
        });
    }

    // the type check keeps other drags (files from Explorer) out, even if a tab's dragend never arrived
    const isTabDrag = (e) => dragging && e.dataTransfer.types.includes(TAB_TYPE);
    bar.addEventListener('dragover', (e) => {
        if (!isTabDrag(e)) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        markDrop(dropAt(e.clientX));
        // near either end the bar scrolls, so a tab can be taken to a place that is out of sight
        const r = bar.getBoundingClientRect();
        if (e.clientX < r.left + 32) bar.scrollLeft -= 16;
        else if (e.clientX > r.right - 32) bar.scrollLeft += 16;
    });
    bar.addEventListener('dragleave', (e) => { if (!bar.contains(e.relatedTarget)) markDrop(-1); });
    bar.addEventListener('drop', (e) => {
        if (!isTabDrag(e)) return;
        e.preventDefault();
        const rel = dragging;
        dragging = null;
        markDrop(-1);
        reorder(moveTo(tabs, rel, dropAt(e.clientX)), rel);
    });
    // a tab can only be dropped on the bar; elsewhere it is refused, above all on the editor, which would move its caret
    for (const type of ['dragover', 'drop'])
        window.addEventListener(type, (e) => { if (isTabDrag(e) && !bar.contains(e.target)) e.stopPropagation(); }, true);

    /** Only the marks: which tab is the open one and which have unsaved changes. */
    function refresh() {
        for (const tab of Array.from(bar.children)) {
            const rel = tab.dataset.path;
            tab.classList.toggle('active', rel === active);
            tab.classList.toggle('dirty', deps.isDirty(rel));
        }
        const current = bar.querySelector('.etab.active');
        current?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }

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

    function drop(rel) {
        const hit = (r) => r === rel || r.startsWith(rel + '/');
        if (!tabs.some(hit)) return;
        tabs = tabs.filter((r) => !hit(r));
        mru = mru.filter((r) => !hit(r));
        if (hit(active)) active = '';
        remember();
        render();
    }

    async function closeTab(rel) {
        if (deps.isDirty(rel)) {
            const answer = await askSave(baseName(rel));
            if (answer === 'cancel') return;
            if (answer === 'save') await deps.save(rel);
            else await deps.revert(rel);
        }
        const wasActive = rel === active;
        const at = tabs.indexOf(rel);
        tabs = tabs.filter((r) => r !== rel);
        mru = mru.filter((r) => r !== rel);
        remember();
        if (wasActive) {
            active = '';
            const next = mru[0] ?? tabs[Math.min(at, tabs.length - 1)];
            if (next) await deps.openFile(next);
            else deps.empty();
        }
        render();
    }

    async function closeMany(keep) {
        for (const rel of [...tabs]) {
            if (keep.includes(rel)) continue;
            if (deps.isDirty(rel)) {
                await deps.openFile(rel);
                const answer = await askSave(baseName(rel));
                if (answer === 'cancel') return;
                if (answer === 'save') await deps.save(rel); else await deps.revert(rel);
            }
            tabs = tabs.filter((r) => r !== rel);
            mru = mru.filter((r) => r !== rel);
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
    function menu(x, y, rel) {
        closeMenu();
        const entries = [
            ['Close', () => closeTab(rel)],
            ['Close others', () => closeMany([rel])],
            ['Close all', () => closeMany([])],
            ['—'],
            ['Copy path', () => navigator.clipboard?.writeText(rel)],
        ];
        menuEl = document.createElement('div');
        menuEl.className = 'ctx-menu';
        for (const [label, action] of entries) {
            if (label === '—') { const hr = document.createElement('div'); hr.className = 'ctx-sep'; menuEl.append(hr); continue; }
            const item = document.createElement('div');
            item.className = 'ctx-item';
            item.textContent = label;
            item.onclick = () => { closeMenu(); void action(); };
            menuEl.append(item);
        }
        document.body.append(menuEl);
        menuEl.style.left = `${Math.min(x, window.innerWidth - menuEl.offsetWidth - 4)}px`;
        menuEl.style.top = `${Math.min(y, window.innerHeight - menuEl.offsetHeight - 4)}px`;
    }
    document.addEventListener('mousedown', (e) => { if (menuEl && !menuEl.contains(e.target)) closeMenu(); });

    function step(offset) {
        if (tabs.length < 2) return;
        const at = tabs.indexOf(active);
        void deps.openFile(tabs[(Math.max(at, 0) + offset + tabs.length) % tabs.length]);
    }

    window.addEventListener('keydown', (e) => {
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

    return {
        opened, refresh, drop,
        close: closeTab,
        mruFirst: () => mru[0] ?? tabs[0] ?? null,
        /** The tabs of the last session that still exist, and the one that was open. */
        restore(exists) {
            try {
                const saved = JSON.parse(localStorage.getItem('pymagic.tabs') ?? 'null');
                if (!saved) return null;
                tabs = (saved.tabs ?? []).filter(exists);
                mru = [...tabs];
                render();
                return exists(saved.active) ? saved.active : tabs[0] ?? null;
            } catch { return null; }
        },
        list: () => [...tabs],
    };
}

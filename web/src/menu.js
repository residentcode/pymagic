// A menu that opens under a button or at a point: rows with a shortcut, a check mark or a submenu, separators, and the keys.
//
// items: '-' (a separator) or { label, key?, run?, checked?, disabled?, submenu?, onActive? }; a submenu is another list of items;
// onActive runs when the row becomes the active one (by the mouse or the arrow keys).
// Returns the function that closes it (all levels). Only one menu is open at a time.

let closeOpen = null;

/** The key shown for a row: the person's key when the command that starts with it was given another (Settings > Keymap); set by main.js. */
let keyLabel = (key) => key;
export function setKeyLabels(fn) { keyLabel = fn; }

function icon(name) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', 'i');
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', `#i-${name}`);
    svg.append(use);
    return svg;
}

export function openMenu(anchor, items) {
    closeOpen?.();
    const levels = [];   // the open boxes, the outermost first

    function close() {
        for (const l of levels) l.box.remove();
        levels.length = 0;
        document.removeEventListener('mousedown', outside, true);
        document.removeEventListener('keydown', onKey, true);
        if (closeOpen === close) closeOpen = null;
    }
    closeOpen = close;
    const outside = (e) => { if (!levels.some((l) => l.box.contains(e.target))) close(); };

    function trimTo(depth) {
        while (levels.length > depth) levels.pop().box.remove();
    }

    function build(list, depth) {
        const box = document.createElement('div');
        box.className = 'ctx-menu';
        const level = { box, rows: [], active: -1 };
        for (const item of list) {
            if (item === '-') {
                const sep = document.createElement('div');
                sep.className = 'ctx-sep';
                box.append(sep);
                continue;
            }
            const row = document.createElement('div');
            row.className = 'ctx-item' + (item.disabled ? ' disabled' : '') + (item.submenu ? ' has-sub' : '');
            const check = document.createElement('span');
            check.className = 'ctx-check';
            if (item.checked) check.append(icon('tick'));
            const label = document.createElement('span');
            label.className = 'ctx-label';
            label.textContent = item.label;
            row.append(check, label);
            const shown = item.key ? keyLabel(item.key) : '';
            if (shown) {
                const key = document.createElement('span');
                key.className = 'ctx-key';
                key.textContent = shown;
                row.append(key);
            }
            if (item.submenu) {
                const arrow = document.createElement('span');
                arrow.className = 'ctx-arrow';
                arrow.append(icon('chev-right'));
                row.append(arrow);
            }
            const index = level.rows.length;
            level.rows.push({ row, item });
            row.onmouseenter = () => activate(depth, index, !!item.submenu);
            row.onclick = (e) => {
                e.stopPropagation();
                if (item.disabled) return;
                if (item.submenu) { activate(depth, index, true); return; }
                close();
                item.run?.();
            };
            box.append(row);
        }
        return level;
    }

    // x, y: where the box starts; parentLeft: the left edge of the box it opens from, so that it can open to the left when there is no room on the right
    function place(level, x, y, parentLeft) {
        document.body.append(level.box);
        const w = level.box.offsetWidth, h = level.box.offsetHeight;
        const left = x + w > window.innerWidth - 8 ? Math.max(8, parentLeft !== undefined ? parentLeft - w + 4 : window.innerWidth - w - 8) : x;
        const top = Math.max(8, Math.min(y, window.innerHeight - h - 8));
        level.box.style.left = `${left}px`;
        level.box.style.top = `${top}px`;
    }

    function activate(depth, index, openSub) {
        trimTo(depth + 1);
        const level = levels[depth];
        level.active = index;
        level.rows.forEach((r, i) => r.row.classList.toggle('active', i === index));
        const { row, item } = level.rows[index];
        item.onActive?.();
        if (openSub && item.submenu && !item.disabled) {
            const sub = build(item.submenu, depth + 1);
            levels.push(sub);
            const r = row.getBoundingClientRect();
            place(sub, r.right - 4, r.top - 5, r.left);
        }
    }

    const first = build(items, 0);
    levels.push(first);
    if (anchor instanceof Element) {
        const r = anchor.getBoundingClientRect();
        place(first, r.left, r.bottom + 4);
    }
    else place(first, anchor.x, anchor.y);

    function onKey(e) {
        const depth = levels.length - 1;
        const level = levels[depth];
        const usable = level.rows.map((r, i) => (r.item.disabled ? -1 : i)).filter((i) => i >= 0);
        const at = usable.indexOf(level.active);
        if (e.key === 'Escape') { e.preventDefault(); close(); }
        else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
            e.preventDefault();
            if (usable.length === 0) return;
            const next = e.key === 'ArrowDown' ? usable[(at + 1) % usable.length] : usable[(at - 1 + usable.length) % usable.length];
            activate(depth, next, false);
        }
        else if (e.key === 'ArrowRight') {
            const cur = level.rows[level.active];
            if (cur?.item.submenu) { e.preventDefault(); activate(depth, level.active, true); const sub = levels[levels.length - 1]; const firstUsable = sub.rows.findIndex((r) => !r.item.disabled); if (firstUsable >= 0) activate(levels.length - 1, firstUsable, false); }
        }
        else if (e.key === 'ArrowLeft' && depth > 0) { e.preventDefault(); trimTo(depth); }
        else if (e.key === 'Enter') {
            const cur = level.rows[level.active];
            if (!cur || cur.item.disabled) return;
            e.preventDefault();
            if (cur.item.submenu) activate(depth, level.active, true);
            else { close(); cur.item.run?.(); }
        }
    }
    setTimeout(() => {
        if (closeOpen !== close) return;
        document.addEventListener('mousedown', outside, true);
        document.addEventListener('keydown', onKey, true);
    });
    return close;
}

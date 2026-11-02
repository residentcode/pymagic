// Keeps the command bar inside the window: when it is too wide, the optional labels and then the least needed commands are hidden
// (toolbar-fit-model.js has the order), and ⋯ lists what is hidden. Also: the tool-window tabs scroll with the wheel, the open one is
// brought into view, and their close button stays at the right.
import { fit, menuLabel } from './toolbar-fit-model.js';
import { openMenu } from './menu.js';

export function setupToolbarFit() {
    const bar = document.getElementById('toolbar');
    const more = document.getElementById('b-more');
    if (!bar || !more) return;
    let hidden = [];

    const element = (id) => document.getElementById(id) ?? bar.querySelector('.' + id);
    const overflows = () => bar.scrollWidth > bar.clientWidth + 1;

    function refit() {
        // ⋯ is counted in from the start whenever something has to go, so the bar fits with it
        more.classList.add('tb-off');
        const r = fit({
            overflows: () => { if (!overflows()) return false; more.classList.remove('tb-off'); return overflows(); },
            setCompact: (on) => bar.classList.toggle('compact', on),
            hide: (id, on) => element(id)?.classList.toggle('tb-off', on),
        });
        hidden = r.hidden.map(element).filter(Boolean);
        if (!hidden.length) more.classList.add('tb-off');
    }

    // what ⋯ offers: each hidden command as it is in the bar (a group gives each of its buttons; a field, its choices)
    function items() {
        const list = [];
        for (const e of hidden) {
            if (e.tagName === 'SELECT') {
                list.push({ label: menuLabel(e.title, e.id), submenu: [...e.options].map((o) => ({ label: o.textContent, checked: o.selected, run: () => { e.value = o.value; e.dispatchEvent(new Event('change', { bubbles: true })); } })) });
                continue;
            }
            const box = e.querySelector?.('input[type=checkbox]');
            if (e.tagName === 'LABEL' && box) {
                list.push({ label: menuLabel(e.title, 'Option'), checked: box.checked, run: () => box.click() });
                continue;
            }
            const buttons = e.tagName === 'BUTTON' ? [e] : [...e.querySelectorAll('button')];
            // a chip that is clicked (the branch) is offered by its title; the project's name is not a command
            if (!buttons.length && e.title && e.classList.contains('on')) list.push({ label: menuLabel(e.title), run: () => e.click() });
            for (const b of buttons) list.push({ label: menuLabel(b.title, b.textContent.trim()), disabled: b.disabled, run: () => b.click() });
        }
        return list;
    }
    more.onclick = () => openMenu(more, items());

    let frame = 0;
    const later = () => { cancelAnimationFrame(frame); frame = requestAnimationFrame(refit); };
    new ResizeObserver(later).observe(bar);
    // buttons that come and go (the steps while debugging, Pause), the interpreter's name, the branch: all change what the bar needs
    new MutationObserver(later).observe(bar, { subtree: true, attributes: true, attributeFilter: ['disabled'], childList: true, characterData: true });
    later();

    // the tool-window tabs: the wheel scrolls them sideways, and the open one is brought into view
    const tabs = document.getElementById('tabs');
    if (tabs) {
        tabs.addEventListener('wheel', (e) => {
            if (tabs.scrollWidth <= tabs.clientWidth || Math.abs(e.deltaY) < Math.abs(e.deltaX)) return;
            tabs.scrollLeft += e.deltaY;
            e.preventDefault();
        }, { passive: false });
        // after the layout: the panel may only be opening in the same click, and the open tab's label makes it wider
        let pending = 0;
        const bringOpenTab = () => {
            cancelAnimationFrame(pending);
            pending = requestAnimationFrame(() => tabs.querySelector(':scope > button.active')?.scrollIntoView({ block: 'nearest', inline: 'nearest' }));
        };
        new MutationObserver(bringOpenTab).observe(tabs, { subtree: true, attributes: true, attributeFilter: ['class'] });
        new ResizeObserver(bringOpenTab).observe(tabs);
    }
}

// Things that are clicked but are not <button>s (a breadcrumb, the project's name): a screen reader announces them as buttons, and the
// keyboard reaches them with Tab and presses them with Enter or Space.

/** Makes `el` act as a button for the keyboard; `focusable: false` keeps it out of the Tab order (a tab's ✕, which Ctrl+W already does). */
export function asButton(el, { label, focusable = true } = {}) {
    el.setAttribute('role', 'button');
    el.tabIndex = focusable ? 0 : -1;
    if (label) el.setAttribute('aria-label', label);
    el.addEventListener('keydown', (e) => {
        if (e.key !== 'Enter' && e.key !== ' ') return;
        e.preventDefault();
        e.stopPropagation();
        el.click();
    });
    return el;
}

// The command bar when the window is too narrow for all of it: labels go first, then whole commands, least needed first, until it fits.
// No DOM here: web/test/toolbar-fit.test.mjs checks it. toolbar-fit.js measures the real bar and puts what is hidden in the ⋯ menu.

/**
 * What the bar can do without, in the order it gives it up: the id of an element of the bar (a button, a group of buttons, a field).
 * Run, Debug, Stop, the run configuration, the interpreter and the settings are never in it.
 */
export const HIDE_ORDER = [
    'fmt-save-label', 'fmt-engine', 'b-imports', 'b-refactor', 'b-anything', 'b-profile', 'b-cov',
    'grp-vcs', 'b-tests', 'grp-file', 'grp-nav', 'b-format', 'branch-chip', 'b-find', 'b-venv', 'b-remote', 'project',
];

/**
 * Fits the bar: `overflows()` says whether it is too wide as it is now, `setCompact(on)` hides or shows the optional labels, `hide(id, on)` hides or
 * shows one element. Everything is shown first; then the labels go, then the elements of `order` one by one, until it fits (or nothing is left
 * to hide). Returns `{ compact, hidden }`: whether the labels went, and the ids hidden, in order.
 */
export function fit({ overflows, setCompact, hide, order = HIDE_ORDER }) {
    for (const id of order) hide(id, false);
    setCompact(false);
    const hidden = [];
    if (!overflows()) return { compact: false, hidden };
    setCompact(true);
    for (const id of order) {
        if (!overflows()) break;
        hide(id, true);
        hidden.push(id);
    }
    return { compact: true, hidden };
}

/** The words of a button for the ⋯ menu: its title up to the first "(" or ":" (the key and the details are left out). */
export function menuLabel(title, fallback = '') {
    const text = (title ?? '').split(/[(:]/)[0].trim();
    return text || fallback;
}

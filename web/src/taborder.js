// Where a dragged editor tab lands and the order that results. No DOM and no imports, so the checks in
// web/test/taborder.test.mjs can run it under Node.

/** The insertion index (0 to mids.length) for a pointer at x, given the horizontal middles of the tabs in bar order. */
export function dropIndex(mids, x) {
    let i = 0;
    while (i < mids.length && x > mids[i]) i++;
    return i;
}

/** True when putting rel at insertion index `index` would leave the order as it is. */
export function staysPut(tabs, rel, index) {
    const from = tabs.indexOf(rel);
    return from < 0 || index === from || index === from + 1;
}

/** The tabs with rel moved in front of the tab that is now at `index` (tabs.length: to the end). The same array when nothing moves. */
export function moveTo(tabs, rel, index) {
    const to = Math.max(0, Math.min(index, tabs.length));
    if (staysPut(tabs, rel, to)) return tabs;
    const from = tabs.indexOf(rel);
    const rest = tabs.filter((r) => r !== rel);
    rest.splice(to > from ? to - 1 : to, 0, rel);
    return rest;
}

/** The pinned tabs first, each group keeping its order. The same array when that is already the order. */
export function pinFirst(tabs, pinned) {
    const next = [...tabs.filter((r) => pinned.has(r)), ...tabs.filter((r) => !pinned.has(r))];
    return next.every((r, i) => r === tabs[i]) ? tabs : next;
}

/** Where a dragged tab may land: a pinned one among the pinned tabs, another after them. */
export function clampToGroup(tabs, pinned, rel, index) {
    const count = tabs.filter((r) => pinned.has(r)).length;
    return pinned.has(rel) ? Math.min(index, count) : Math.max(index, count);
}

/** The tabs with rel moved `offset` places along the bar, stopping at either end. The same array when nothing moves. */
export function moveBy(tabs, rel, offset) {
    const from = tabs.indexOf(rel);
    if (from < 0) return tabs;
    const to = Math.max(0, Math.min(tabs.length - 1, from + offset));
    if (to === from) return tabs;
    const rest = tabs.filter((r) => r !== rel);
    rest.splice(to, 0, rel);
    return rest;
}

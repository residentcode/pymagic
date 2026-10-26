// Names of the terminal tabs and which one is open after a close. No DOM and no imports, so the checks in
// web/test/termtabs.test.mjs can run it under Node.

/** "Terminal N" with the lowest N that no open terminal is called. */
export function nextName(names) {
    const taken = new Set(names);
    for (let n = 1; ; n++) if (!taken.has(`Terminal ${n}`)) return `Terminal ${n}`;
}

/**
 * The terminal to show after `closing` goes: the open one stays open; when the open one is the one closing, its
 * right-hand neighbour, or the left one at the end of the row. Null when no terminal is left.
 */
export function afterClose(ids, closing, active) {
    const rest = ids.filter((id) => id !== closing);
    if (rest.length === 0) return null;
    if (active !== closing && rest.includes(active)) return active;
    const at = ids.indexOf(closing);
    return rest[Math.min(Math.max(at, 0), rest.length - 1)];
}

/** The name typed when renaming a tab: spaces folded, at most 40 characters; null keeps the old name. */
export function cleanName(typed) {
    const name = String(typed ?? '').replace(/\s+/g, ' ').trim().slice(0, 40).trim();
    return name || null;
}

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

export const MAX_PANES = 4;     // terminals side by side in one tab
export const KEPT_LINES = 1000; // lines of each terminal's screen kept for the next start
export const RESTORED = '[restored from the last session]';

/** The last `max` lines of a screen, without the empty ones at its end and without the line that marked an earlier restore. */
export function keptLines(lines, max = KEPT_LINES) {
    lines = lines.filter((l) => String(l).trim() !== RESTORED);
    let end = lines.length;
    while (end > 0 && !String(lines[end - 1]).trim()) end--;
    return lines.slice(Math.max(0, end - max), end).map(String);
}

/**
 * The terminal tabs to open again from what was kept (`raw`, parsed JSON): only for the same project folder, each tab with its name and its
 * terminals (the shell's id and the screen's lines); anything of the wrong shape is dropped.
 */
export function restoredTabs(raw, root) {
    if (!raw || typeof raw !== 'object' || raw.root !== root || !Array.isArray(raw.tabs)) return [];
    const tabs = [];
    for (const t of raw.tabs) {
        const panes = (Array.isArray(t?.panes) ? t.panes : []).filter((p) => p && typeof p === 'object').slice(0, MAX_PANES).map((p) => ({
            shell: typeof p.shell === 'string' && /^[\w-]{1,40}$/.test(p.shell) ? p.shell : null,
            lines: Array.isArray(p.lines) ? keptLines(p.lines.filter((l) => typeof l === 'string')) : [],
            // the folder it started in, under the project (Open In > Terminal)
            ...(typeof p.cwd === 'string' && p.cwd && p.cwd.length < 400 && !/(^|[\\/])\.\.([\\/]|$)|^[\\/]|:/.test(p.cwd) ? { cwd: p.cwd } : {}),
        }));
        if (panes.length) tabs.push({ name: cleanName(t.name) ?? 'Terminal', panes });
    }
    return tabs;
}

/** The name typed when renaming a tab: spaces folded, at most 40 characters; null keeps the old name. */
export function cleanName(typed) {
    const name = String(typed ?? '').replace(/\s+/g, ' ').trim().slice(0, 40).trim();
    return name || null;
}

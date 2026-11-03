// The menu rows of the recent projects (File > Recent projects and the project name's menu): each project, then Remove from recent projects
// (a submenu of them) and Clear recent projects. No DOM here: web/test/recentprojects.test.mjs checks it.

/** The rows for the list (open, remove and clear are what the rows run); `none` is the dim row of an empty list. */
export function recentProjectItems(list, { open, remove, clear }, none = 'No recent projects') {
    if (!list.length) return [{ label: none, disabled: true }];
    return [
        ...list.map((p) => ({ label: p, run: () => open(p) })),
        '-',
        { label: 'Remove from recent projects', submenu: list.map((p) => ({ label: p, run: () => remove(p) })) },
        { label: 'Clear recent projects', run: () => clear() },
    ];
}

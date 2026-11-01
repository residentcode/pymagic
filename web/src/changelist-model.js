// Changelists in the Git tab: which changed files sit in which list, and what a commit of one list carries. A state is
// { active, lists: [{ name, files: [path] }] } as /api/git/changelists sends it; the first list is the default one.
// No DOM here: web/test/changelist.test.mjs checks it.

export const DEFAULT_LIST = 'Changes';

/**
 * The changed files grouped by list, in the order of the lists. A file no list holds (a new one the server has not
 * placed yet, or one of PyMagic's own such as .pymagic/changelists.json) is shown in the default list. A file that is in
 * part in several lists (`state.partCounts`) is in each of them as a copy with `part: { here, total }`.
 */
export function groupFiles(files, state) {
    const lists = state?.lists?.length ? state.lists : [{ name: DEFAULT_LIST, files: [] }];
    const owner = new Map();
    for (const l of lists) for (const p of l.files) owner.set(p, l.name);
    const def = lists[0].name;
    const byName = new Map(lists.map((l) => [l.name, []]));
    // a file of which some changes were moved to other lists is in each of those lists, with how many of its changes that list holds
    const counts = state?.partCounts ?? {};
    for (const f of files) {
        const per = counts[f.path];
        if (!per) { (byName.get(owner.get(f.path)) ?? byName.get(def)).push(f); continue; }
        const total = Object.values(per).reduce((a, b) => a + b, 0);
        for (const [name, here] of Object.entries(per)) (byName.get(name) ?? byName.get(def)).push({ ...f, part: { here, total } });
    }
    const active = lists.some((l) => l.name === state?.active) ? state.active : def;
    return lists.map((l) => ({ name: l.name, files: byName.get(l.name), active: l.name === active, isDefault: l.name === def }));
}

/** The list a commit goes from: the one chosen if it still exists, else the active one. */
export function commitList(state, chosen) {
    const lists = state?.lists ?? [];
    if (chosen && lists.some((l) => l.name === chosen)) return chosen;
    return lists.some((l) => l.name === state?.active) ? state.active : lists[0]?.name ?? DEFAULT_LIST;
}

/**
 * The files of one list to commit whole: ticked, and not one of which only some lines are chosen (those go as `partial`).
 * `unchecked` is the set of paths whose box is empty, `partial` a Map keyed by path.
 */
export function commitPaths(files, state, list, unchecked, partial) {
    const group = groupFiles(files, state).find((g) => g.name === list);
    if (!group) return [];
    return group.files.filter((f) => !unchecked.has(f.path) && !partial.has(f.path)).map((f) => f.path);
}

/** The files of one list that have only some of their lines chosen. */
export function partialPaths(files, state, list, partial) {
    const group = groupFiles(files, state).find((g) => g.name === list);
    return group ? group.files.filter((f) => partial.has(f.path)).map((f) => f.path) : [];
}

/** A short line for the shelf: "3 files". */
export function filesLabel(n) {
    return `${n} file${n === 1 ? '' : 's'}`;
}

/** The lines of a shelved set's patch told apart for colouring: [{ kind: '+'|'-'|'@'|'h'|' ', text }]. */
export function patchLines(patch) {
    const out = [];
    let inHunk = false;
    for (const raw of patch.split('\n')) {
        if (raw.startsWith('diff --git')) { inHunk = false; out.push({ kind: 'h', text: raw }); continue; }
        if (raw.startsWith('@@')) { inHunk = true; out.push({ kind: '@', text: raw }); continue; }
        if (!inHunk) { if (raw) out.push({ kind: 'h', text: raw }); continue; }
        if (raw.startsWith('+')) out.push({ kind: '+', text: raw });
        else if (raw.startsWith('-')) out.push({ kind: '-', text: raw });
        else if (raw || out.length) out.push({ kind: ' ', text: raw });
    }
    while (out.length && out[out.length - 1].text === '') out.pop();
    return out;
}

/**
 * Which lines of a file's diff (as /api/git/diff sends it) a change bar of the editor stands for. The bar's change is
 * { oldStart, oldLines: [text], newStart, newCount } with 0-based starts. Returns Map(hunkIndex -> Set(lineIndex)) in a
 * selection's form: the added lines inside the bar's new range and the removed lines inside its old range.
 */
export function linesOfChange(diff, change) {
    const hunks = new Map();
    const newFrom = change.newStart + 1, newTo = change.newStart + change.newCount;
    const oldFrom = change.oldStart + 1, oldTo = change.oldStart + change.oldLines.length;
    diff.hunks.forEach((hunk, h) => {
        let o = hunk.oldStart, n = hunk.newStart;
        const chosen = new Set();
        hunk.lines.forEach((l, i) => {
            if (l.kind === '+') { if (n >= newFrom && n <= newTo) chosen.add(i); n++; }
            else if (l.kind === '-') { if (o >= oldFrom && o <= oldTo) chosen.add(i); o++; }
            else { o++; n++; }
        });
        if (chosen.size) hunks.set(h, chosen);
    });
    return hunks;
}

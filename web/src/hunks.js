// Choosing single lines and hunks of a file to commit. A diff is { id, isNew, hunks: [{ oldStart, newStart, section, lines: [{ kind, text }] }] }
// as /api/git/diff sends it; a selection is { id, hunks: Map(hunkIndex -> Set(lineIndex)) } holding only changed lines (kind "+" or "-").
// No DOM here: web/test/hunks.test.mjs checks it.

/** The positions of the changed lines of a hunk. */
export function changedLines(hunk) {
    const out = [];
    hunk.lines.forEach((l, i) => { if (l.kind !== ' ') out.push(i); });
    return out;
}

/** A selection with every changed line chosen (the starting point: the file is committed whole). */
export function selectAll(diff) {
    const hunks = new Map();
    diff.hunks.forEach((h, i) => hunks.set(i, new Set(changedLines(h))));
    return { id: diff.id, hunks };
}

export function selectNone(diff) {
    return { id: diff.id, hunks: new Map() };
}

export function counts(diff, sel) {
    let total = 0, chosen = 0;
    diff.hunks.forEach((h, i) => {
        const all = changedLines(h);
        total += all.length;
        chosen += all.filter((n) => sel.hunks.get(i)?.has(n)).length;
    });
    return { chosen, total };
}

/** "all", "none" or "partial". */
export function stateOf(diff, sel) {
    const { chosen, total } = counts(diff, sel);
    return total > 0 && chosen === total ? 'all' : chosen === 0 ? 'none' : 'partial';
}

export function hunkState(diff, sel, h) {
    const all = changedLines(diff.hunks[h]);
    const chosen = all.filter((n) => sel.hunks.get(h)?.has(n)).length;
    return chosen === 0 ? 'none' : chosen === all.length ? 'all' : 'partial';
}

export function toggleLine(diff, sel, h, line) {
    if (diff.hunks[h]?.lines[line]?.kind === ' ' || !diff.hunks[h]?.lines[line]) return;
    const set = sel.hunks.get(h) ?? new Set();
    if (set.has(line)) set.delete(line); else set.add(line);
    sel.hunks.set(h, set);
}

/** Takes every changed line of the hunk, or none when all of them were taken. */
export function toggleHunk(diff, sel, h) {
    sel.hunks.set(h, hunkState(diff, sel, h) === 'all' ? new Set() : new Set(changedLines(diff.hunks[h])));
}

/** What the commit request carries for a file: the diff it was made on and the chosen lines per hunk (empty hunks left out). */
export function payload(sel) {
    const hunks = {};
    for (const [h, set] of sel.hunks) if (set.size) hunks[String(h)] = [...set].sort((a, b) => a - b);
    return { id: sel.id, hunks };
}

/** Line numbers to show beside a hunk's lines: [old, new] for each, null where the line is not in that version. */
export function numbering(hunk) {
    let o = hunk.oldStart, n = hunk.newStart;
    return hunk.lines.map((l) => {
        const row = [l.kind === '+' ? null : o, l.kind === '-' ? null : n];
        if (l.kind !== '+') o++;
        if (l.kind !== '-') n++;
        return row;
    });
}

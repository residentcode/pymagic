// Interactive rebase without the page: the plan as a list of rows (one for each commit, oldest first, replayed from the top down), what can be
// done to a row, and what is wrong with a plan before git is asked to run it.

export const ACTIONS = [
    ['pick', 'Pick', 'Keep the commit as it is'],
    ['reword', 'Reword', 'Keep the changes, write the message again'],
    ['squash', 'Squash', 'Join into the commit above, and join the messages'],
    ['fixup', 'Fixup', 'Join into the commit above, and drop this message'],
    ['drop', 'Drop', 'Leave the commit out'],
];

/** The rows of a plan that changes nothing yet: `commits` as the engine lists them (`hash`, `shortHash`, `author`, `subject`). */
export function startingPlan(commits) {
    return commits.map((c) => ({ hash: c.hash, shortHash: c.shortHash, author: c.author, subject: c.subject, action: 'pick', message: null }));
}

/** The plan with row `index` moved `delta` places (negative: up); the same plan when that would leave the list. */
export function move(rows, index, delta) {
    const to = index + delta;
    if (index < 0 || index >= rows.length || to < 0 || to >= rows.length || delta === 0) return rows;
    const out = rows.slice();
    [out[index], out[to]] = [out[to], out[index]];
    return out;
}

/** The plan with row `index` given another action; a reword keeps the message already typed, others do not carry one. */
export function setAction(rows, index, action) {
    return rows.map((r, i) => (i === index ? { ...r, action, message: action === 'reword' ? r.message : null } : r));
}

/** The plan with the message of row `index` set (for a reword). */
export function setMessage(rows, index, message) {
    return rows.map((r, i) => (i === index ? { ...r, message } : r));
}

/** What is wrong with the plan, in words, or null when git can be asked to run it. */
export function problem(rows, commits) {
    const kept = rows.filter((r) => r.action !== 'drop');
    if (kept.length === 0) return 'Every commit is dropped: reset the branch instead.';
    if (kept[0].action === 'squash' || kept[0].action === 'fixup') return `The first commit that is kept cannot be ${kept[0].action === 'squash' ? 'squashed' : 'a fixup'}: there is nothing above it to join.`;
    const empty = rows.find((r) => r.action === 'reword' && !(r.message ?? '').trim());
    if (empty) return `The message of ${empty.shortHash} is empty.`;
    if (!changed(rows, commits)) return 'Nothing is changed yet: reorder, reword, squash or drop something.';
    return null;
}

/** Whether the plan differs from replaying the commits as they are. */
export function changed(rows, commits) {
    return rows.length !== commits.length || rows.some((r, i) => r.action !== 'pick' || r.hash !== commits[i].hash);
}

/** The plan as the engine takes it: a message goes along only with a reword. */
export function steps(rows) {
    return rows.map((r) => (r.action === 'reword' ? { hash: r.hash, action: r.action, message: r.message } : { hash: r.hash, action: r.action }));
}

/** One line on what the plan does: "2 reordered", "1 squashed", ... (empty when nothing). */
export function summary(rows, commits) {
    const count = (a) => rows.filter((r) => r.action === a).length;
    const parts = [];
    // a commit is moved when it is not where it was among the commits that are kept
    const now = rows.filter((r) => r.action !== 'drop').map((r) => r.hash);
    const then = commits.map((c) => c.hash).filter((h) => now.includes(h));
    const moved = now.filter((h, i) => then[i] !== h).length;
    if (moved > 0) parts.push(`${moved} moved`);
    for (const [a, word] of [['reword', 'reworded'], ['squash', 'squashed'], ['fixup', 'fixed up'], ['drop', 'dropped']]) if (count(a)) parts.push(`${count(a)} ${word}`);
    return parts.join(', ');
}

// Pull requests without the page: how a number, a state and a check are told, and how a forge's patch becomes the two sides of a diff.
// No DOM here: web/test/pr.test.mjs checks it.

/** "pull request" on GitHub, "merge request" on GitLab. */
export function nounOf(kind) { return kind === 'gitlab' ? 'merge request' : 'pull request'; }

/** The number as the forge writes it: #7 on GitHub, !3 on GitLab. */
export function refOf(kind, number) { return (kind === 'gitlab' ? '!' : '#') + number; }

const CHECKS = {
    success: ['passed', 'ok'], failure: ['failed', 'bad'], pending: ['running', 'wait'], none: ['no checks', 'none'], unknown: ['unknown', 'none'],
};

/** The checks of a request in words, and the class that colours them. */
export function checksText(state) {
    const [text, cls] = CHECKS[state] ?? CHECKS.unknown;
    return { text, cls };
}

/** A line about a request: "ada wants to merge cart into main". */
export function whoWants(pr) { return `${pr.author || 'someone'} wants to merge ${pr.source} into ${pr.target}`; }

/** The state of a request in words, drafts told as such. */
export function stateText(pr) {
    if (pr.state === 'merged') return 'merged';
    if (pr.state === 'closed') return 'closed';
    return pr.draft ? 'draft' : 'open';
}

/** The first line of a commit message. */
export function subjectOf(message) { return String(message ?? '').split('\n', 1)[0]; }

/** "+3 −1" for a file of a request. */
export function changeText(file) { return `+${file.additions} −${file.deletions}`; }

/** The letter beside a changed file. */
export function statusLetter(status) { return { added: 'A', removed: 'D', renamed: 'R' }[status] ?? 'M'; }

/**
 * The two sides of a patch for a side-by-side diff. A forge sends only the changed parts of a file (hunks); the left side is what those parts were,
 * the right what they became, and the hunk headers stand on both sides (so they never count as changes). Line numbers are those of the pieces, not of the file.
 */
export function splitPatch(patch) {
    const original = [];
    const modified = [];
    for (const line of String(patch ?? '').split('\n')) {
        if (line.startsWith('@@')) { original.push(line); modified.push(line); }
        else if (line.startsWith('+')) modified.push(line.slice(1));
        else if (line.startsWith('-')) original.push(line.slice(1));
        else if (line.startsWith('\\')) continue;   // "\ No newline at end of file"
        else if (line.startsWith(' ')) { original.push(line.slice(1)); modified.push(line.slice(1)); }
        else if (line === '') continue;
    }
    return { original: original.join('\n'), modified: modified.join('\n') };
}

/** What is wrong with the form of a new request, or '' when it can go to the confirmation. */
export function newProblem({ title, head, base }) {
    if (!String(title ?? '').trim()) return 'Give it a title.';
    if (!head) return 'There is no current branch to merge: switch to a branch first.';
    if (!base) return 'Choose the branch to merge into.';
    if (head === base) return `${head} is the branch to merge into: switch to another branch first.`;
    return '';
}

/** The title the form starts with: the subject of the last commit, unless the branch is the base. */
export function startTitle(info) { return info.suggestedTitle || ''; }

/** The branches to merge into, the repository's usual ones first. */
export function baseChoices(info) {
    const all = (info.bases ?? []).filter((b) => b && b !== 'HEAD' && b !== info.branch);
    const rank = (b) => { const i = ['main', 'master', 'develop', 'trunk'].indexOf(b); return i < 0 ? 9 : i; };
    return [...all].sort((a, b) => rank(a) - rank(b) || a.localeCompare(b));
}

/** The words of the token field: what is kept, and where the token goes. */
export function tokenHint(info) {
    return info.hasToken
        ? `A token for ${info.host} is kept on this computer (in .pymagic/pullrequests.private.json, outside version control). Type a new one to replace it, or leave the field empty and press Forget.`
        : `Type a personal access token for ${info.host}. It is kept on this computer in .pymagic/pullrequests.private.json (outside version control) and only sent to ${info.apiBase}.`;
}

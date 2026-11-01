// Links in the Output of a run: the places of a traceback (File "x.py", line 3) and of a pytest or flake8 line (x.py:3:) that are files
// of the project. A program in WSL, Docker, a container or on an SSH host names the files as that machine has them (/workspace/a.py),
// so those paths are taken back to the project with the folder the engine said the project is in there (`remoteRoot` of the started message).

const PATTERNS = [
    /File "([^"\r\n]+)", line (\d+)/g,                                  // Python's traceback
    /(?<![\w./\\:-])((?:[A-Za-z]:)?[^\s:"'()<>|*?]*\.pyi?):(\d+)(?=[:\s]|$)/g,   // pytest, flake8, mypy: path.py:12: or path.py:12:5:
];

const norm = (p) => p.replace(/\\/g, '/');

/** The places in `text`: [{ start, end, path, line }] (start and end cover the whole mention), in order and without overlaps. */
export function traceMentions(text) {
    const found = [];
    for (const re of PATTERNS) {
        re.lastIndex = 0;
        for (let m = re.exec(text); m; m = re.exec(text)) {
            const start = m.index, end = start + m[0].length;
            if (!found.some((f) => start < f.end && end > f.start))
                found.push({ start, end, path: m[1], line: Number(m[2]) });
        }
    }
    return found.sort((a, b) => a.start - b.start);
}

/**
 * The file of the project a path in a trace stands for, relative to the project, or null when it is somewhere else (the standard library,
 * site-packages, a file that is not shared with the project).
 * `root` is the project folder of this computer, `remoteRoot` where the interpreter has it (null for an interpreter on this computer).
 */
export function projectPathOf(path, root, remoteRoot) {
    const p = norm(path);
    const under = (base) => {
        const b = norm(base).replace(/\/+$/, '');
        if (b === '') return p.startsWith('/') ? p.slice(1) : null;
        if (p.length > b.length + 1 && p.slice(0, b.length).toLowerCase() === b.toLowerCase() && p[b.length] === '/') return p.slice(b.length + 1);
        return null;
    };
    const rel = (remoteRoot ? under(remoteRoot) : null) ?? (root ? under(root) : null);
    if (rel !== null) return rel.split('/').some((part) => part === '..') ? null : rel;
    // a relative path is the project's when the program ran in its folder
    if (!p.startsWith('/') && !/^[A-Za-z]:/.test(p) && !p.startsWith('<') && !p.split('/').includes('..')) return p.replace(/^\.\//, '');
    return null;
}

/** Pieces of `text`: [{ text }] and [{ text, rel, line }] for the mentions that are files of the project. */
export function linkPieces(text, root, remoteRoot) {
    const pieces = [];
    let at = 0;
    for (const m of traceMentions(text)) {
        const rel = projectPathOf(m.path, root, remoteRoot);
        if (rel === null) continue;
        if (m.start > at) pieces.push({ text: text.slice(at, m.start) });
        pieces.push({ text: text.slice(m.start, m.end), rel, line: m.line });
        at = m.end;
    }
    if (at < text.length) pieces.push({ text: text.slice(at) });
    return pieces;
}

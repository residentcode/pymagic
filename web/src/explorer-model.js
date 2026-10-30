// Where Explorer's moves and copies land. No DOM and no imports, so the checks in web/test/explorer-model.test.mjs can run it under Node.

export function baseName(path) { return path.slice(path.lastIndexOf('/') + 1); }
export function parentOf(path) { const i = path.lastIndexOf('/'); return i < 0 ? '' : path.slice(0, i); }
export function join(dir, name) { return dir ? `${dir}/${name}` : name; }

/** Why `path` cannot be moved into the folder `dir` ('' is the project folder), or null when it can. */
export function moveProblem(path, dir) {
    if (!path) return 'nothing to move';
    if (parentOf(path) === dir) return 'it is in that folder already';
    if (dir === path || dir.startsWith(path + '/')) return 'a folder cannot be moved into itself';
    return null;
}

/**
 * A name for a copy of `name` in a folder that already holds `taken` (names): the name itself when it is free, else `a_copy.py`, `a_copy2.py`, ...
 * Underscores, not spaces, so a copied module can still be imported.
 */
export function copyName(name, taken) {
    const used = new Set(taken);
    if (!used.has(name)) return name;
    const dot = name.lastIndexOf('.');
    const stem = dot > 0 ? name.slice(0, dot) : name;
    const ext = dot > 0 ? name.slice(dot) : '';
    for (let i = 1; ; i++) {
        const candidate = `${stem}_copy${i === 1 ? '' : i}${ext}`;
        if (!used.has(candidate)) return candidate;
    }
}

/** The new path of something under `from` once `from` became `to`: open files and folders follow a move. */
export function movedPath(path, from, to) {
    if (path === from) return to;
    return path.startsWith(from + '/') ? to + path.slice(from.length) : path;
}

/** Convert to Package: `a/b.py` becomes `a/b/__init__.py`; null when the file is not a module or the folder exists. */
export function packageTarget(path, exists) {
    if (!path.endsWith('.py') || baseName(path) === '__init__.py') return null;
    const dir = path.slice(0, -3);
    return exists(dir) ? null : `${dir}/__init__.py`;
}

/** Convert to Module: a folder holding only its `__init__.py` becomes `folder.py`; null when it holds more, or that file exists. */
export function moduleTarget(dir, filesIn, exists) {
    if (!dir || filesIn.length !== 1 || filesIn[0] !== `${dir}/__init__.py`) return null;
    return exists(`${dir}.py`) ? null : `${dir}.py`;
}

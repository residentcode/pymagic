// Library files (the standard library, site-packages, typeshed's stubs) shown read only: the path of a location the engine gives outside the
// project, and the address their read-only models have in the editor. No DOM here: web/test/libview.test.mjs checks it.

/** The scheme of a library file's model (go to definition into a library: peek shows it, opening it shows the read-only window). */
export const LIB_SCHEME = 'pymagic-lib';

/** The path of a file: URI outside the project (C:/Python/Lib/json/__init__.py), or null when it is not a file or is in the project. */
export function libraryPathOf(uri, rootUri) {
    const norm = (u) => decodeURIComponent(String(u)).replace(/\\/g, '/').toLowerCase();
    if (typeof uri !== 'string' || !uri.startsWith('file:')) return null;
    if (rootUri && norm(uri).startsWith(norm(rootUri).replace(/\/?$/, '/'))) return null;
    let path = decodeURIComponent(uri.replace(/^file:\/\//, ''));
    // file:///C:/x gives /C:/x: a drive letter loses its slash
    if (/^\/[A-Za-z]:/.test(path)) path = path.slice(1);
    return path.replace(/\\/g, '/');
}

/** The path back from a library model's address (its path is /C:/... or /usr/...). */
export function pathOfLibraryUri(path) {
    return /^\/[A-Za-z]:/.test(path) ? path.slice(1) : path;
}

/** The short title of the window: the last three parts of the path. */
export function libraryTitle(path) {
    return path.split('/').slice(-3).join('/');
}

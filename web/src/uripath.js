// The project-relative path of a file address. No DOM and no imports, so web/test/uripath.test.mjs can run it under Node.

/**
 * `shop/views.py` for `file:///C:/proj/shop/views.py` when the project is `file:///C:/proj/`; null for an address outside it.
 * The engine and the page may write the same address differently (`%3A` or `:`, `C:` or `c:`), so they are compared decoded and in lower case.
 */
export function relUnderRoot(uri, rootUri) {
    if (!uri || !rootUri) return null;
    let file, root;
    try {
        file = decodeURIComponent(uri);
        root = decodeURIComponent(rootUri);
    } catch {
        return null;
    }
    if (!root.endsWith('/')) root += '/';
    if (!file.toLowerCase().startsWith(root.toLowerCase())) return null;
    const rel = file.slice(root.length);
    return rel.length > 0 && !rel.split('/').includes('..') ? rel : null;
}

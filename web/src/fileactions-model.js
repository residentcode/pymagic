// The parts of the file menus (fileactions.js) that need no page: which files a folder's action reaches, and the types a file can be shown as.

/** The languages a file can be shown as (Override File Type): those the page colours without a language worker. */
export const FILE_TYPES = [
    ['python', 'Python'], ['django-html', 'Django / Jinja template'], ['plaintext', 'Plain text'], ['markdown', 'Markdown'], ['ini', 'INI'], ['toml', 'TOML'],
    ['yaml', 'YAML'], ['xml', 'XML'], ['sql', 'SQL'], ['shell', 'Shell script'], ['powershell', 'PowerShell'], ['bat', 'Batch file'], ['dockerfile', 'Dockerfile'],
];

/** The Python files at `path` or under it (a file, a folder, or '' for the whole project), in their order. */
export function pythonFilesUnder(files, path) {
    return files.filter((f) => /\.pyi?$/.test(f) && (!path || f === path || f.startsWith(path + '/')));
}

/** The include pattern of Find in Files that searches only `dir` ('' is the whole project). */
export function includeFor(dir) {
    return dir ? `${dir}/**` : '';
}

/** Whether a file is one pytest or unittest would collect, by its name. */
export function isTestFile(rel) {
    const name = rel.slice(rel.lastIndexOf('/') + 1);
    return /^test_.*\.py$|_test\.py$|^tests?\.py$/.test(name);
}


/** The usages of a file (LSP locations) as Find in Files shows its results: by file, each with its line's text; `relOf` gives a location's file
 *  from its address (null: outside the project, left out), `texts` the files' texts by path. */
export function usageFiles(locations, relOf, texts) {
    const byFile = new Map();
    for (const loc of locations) {
        const rel = relOf(loc.uri);
        if (!rel) continue;
        const lines = (texts[rel] ?? '').split(/\r?\n/);
        const { start, end } = loc.range;
        const text = lines[start.line] ?? '';
        const length = Math.max(1, (end.line === start.line ? end.character : text.length) - start.character);
        if (!byFile.has(rel)) byFile.set(rel, []);
        byFile.get(rel).push({ line: start.line, column: start.character, length, text, replacement: null });
    }
    return [...byFile].map(([path, matches]) => ({ path, matches: matches.sort((a, b) => a.line - b.line || a.column - b.column) }));
}

/** Go to Test: from a module, its test files (`test_x.py`, `x_test.py`, anywhere in the project); from a test file, the modules it tests. */
export function testPartners(rel, files) {
    const name = rel.slice(rel.lastIndexOf('/') + 1).replace(/\.pyi?$/, '');
    const base = (f) => f.slice(f.lastIndexOf('/') + 1).replace(/\.py$/, '');
    const subject = /^test_(.+)$/.exec(name)?.[1] ?? /^(.+)_test$/.exec(name)?.[1];
    if (subject) return files.filter((f) => f !== rel && f.endsWith('.py') && base(f) === subject);
    return files.filter((f) => f !== rel && f.endsWith('.py') && (base(f) === `test_${name}` || base(f) === `${name}_test`));
}

/** Paste from History: the texts copied or cut last, the newest first, each once, at most `max`. */
export function rememberCopy(history, text, max = 15) {
    if (!text) return history;
    return [text, ...history.filter((t) => t !== text)].slice(0, max);
}

/** How a copied text is shown in the Paste from History menu: its first lines joined, shortened. */
export function copyLabel(text, width = 60) {
    const flat = text.replace(/\s+/g, ' ').trim();
    const lines = text.split('\n').length;
    const shown = flat.length > width ? flat.slice(0, width - 1) + '…' : flat;
    return lines > 1 ? `${shown}  (${lines} lines)` : shown;
}

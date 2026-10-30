// The parts of the file menus (fileactions.js) that need no page: which files a folder's action reaches, and the types a file can be shown as.

/** The languages a file can be shown as (Override File Type): those the page colours without a language worker. */
export const FILE_TYPES = [
    ['python', 'Python'], ['django-html', 'Django / Jinja template'], ['plaintext', 'Plain text'], ['markdown', 'Markdown'], ['ini', 'INI / TOML'],
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


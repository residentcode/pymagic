// The project tree's New submenu, as PyCharm has it: what each entry makes, the name it starts from and the text a new file gets when no file
// template of the project fits its name (Settings > Templates wins). No DOM here: web/test/newfile.test.mjs checks it.

const EMPTY_NOTEBOOK = JSON.stringify({
    cells: [{ cell_type: 'code', execution_count: null, metadata: {}, outputs: [], source: [] }],
    metadata: { kernelspec: { display_name: 'Python 3', language: 'python', name: 'python3' }, language_info: { name: 'python' } },
    nbformat: 4, nbformat_minor: 5,
}, null, 1) + '\n';

const DOCKERFILE = 'FROM python:3.13-slim\n\nWORKDIR /app\n\nCOPY requirements.txt .\nRUN pip install --no-cache-dir -r requirements.txt\n\nCOPY . .\n\nCMD ["python", "main.py"]\n';

const DEVCONTAINER = '{\n  "name": "Python",\n  "image": "mcr.microsoft.com/devcontainers/python:3.13",\n  "postCreateCommand": "pip install -r requirements.txt",\n  "customizations": {\n    "vscode": {\n      "extensions": ["ms-python.python"]\n    }\n  }\n}\n';

const COMPOSE = 'services:\n  app:\n    build: .\n    ports:\n      - "8000:8000"\n';

const OPENAPI = 'openapi: 3.1.0\ninfo:\n  title: ${NAME}\n  version: 1.0.0\npaths:\n  /:\n    get:\n      summary: The root\n      responses:\n        "200":\n          description: OK\n';

const EDITORCONFIG = 'root = true\n\n[*]\ncharset = utf-8\nend_of_line = lf\ninsert_final_newline = true\ntrim_trailing_whitespace = true\nindent_style = space\nindent_size = 4\n\n[*.{js,ts,json,yml,yaml,html,css}]\nindent_size = 2\n\n[*.md]\ntrim_trailing_whitespace = false\n';

const GITIGNORE = '__pycache__/\n*.py[cod]\n.venv/\nvenv/\n.env\nbuild/\ndist/\n*.egg-info/\n.pytest_cache/\n.coverage\nhtmlcov/\n.idea/\n.pymagic/\n';

/** The .ignore File submenu: the ignore files PyCharm's .ignore support makes, with the start of each. */
export const IGNORE_FILES = [
    ['.gitignore', 'Git', GITIGNORE],
    ['.dockerignore', 'Docker', '.git\n.venv\n__pycache__\n*.pyc\n.env\n'],
    ['.npmignore', 'npm', 'node_modules\n'],
    ['.prettierignore', 'Prettier', 'node_modules\ndist\n'],
    ['.eslintignore', 'ESLint', 'node_modules\ndist\n'],
];

/**
 * The entries in PyCharm's order. `ext`: the extension a typed name gets when it has none; `initial`: the name the box starts with; `text`: the
 * new file's text (${NAME} is the name without its extension). `dir`, `package`, `scratch`, `ignore`, `dataSource`: what is not a plain file.
 * `plugin`: shown only with that plugin on.
 */
export const NEW_KINDS = [
    { label: 'Python File', ext: '.py', icon: 'py' },
    { label: 'Jupyter Notebook', ext: '.ipynb', initial: 'Untitled', text: EMPTY_NOTEBOOK, plugin: 'notebooks' },
    { label: 'File' },
    { label: 'Scratch File', scratch: true, key: 'Ctrl+Shift+Alt+Insert' },
    { label: 'Directory', dir: true },
    { label: 'Python Package', package: true },
    { label: 'JavaScript File', ext: '.js' },
    { label: 'TypeScript File', ext: '.ts' },
    { label: 'HTML File', ext: '.html' },
    { label: 'Stylesheet', ext: '.css' },
    { label: 'Dockerfile', initial: 'Dockerfile', text: DOCKERFILE },
    { label: 'Dev Container Config…', initial: '.devcontainer/devcontainer.json', text: DEVCONTAINER },
    { label: 'Docker Compose File', initial: 'compose.yaml', text: COMPOSE },
    { label: 'HTTP Request', ext: '.http', text: '### ${NAME}\nGET http://localhost:8000/\n' },
    { label: 'OpenAPI Specification', initial: 'openapi.yaml', text: OPENAPI },
    { label: 'EditorConfig File', initial: '.editorconfig', text: EDITORCONFIG },
    { label: 'Resource Bundle', ext: '.properties', text: '# ${NAME}: key = text\n' },
    { label: '.ignore File', ignore: true },
    '-',
    { label: 'Data Source in Path', dataSource: true, plugin: 'database' },
];

/** The name a kind makes from what was typed: its extension added when the typed name has none of its own. */
export function newFileName(kind, typed) {
    const name = typed.trim().replace(/\\/g, '/');
    if (!kind.ext) return name;
    const base = name.slice(name.lastIndexOf('/') + 1);
    return base.includes('.') ? name : name + kind.ext;
}

/** A new file's text for a kind (the project's template, when one fits, is used instead by the caller). */
export function newFileText(kind, name) {
    const base = name.slice(name.lastIndexOf('/') + 1);
    const stem = base.replace(/\.[^.]*$/, '') || base;
    return (kind.text ?? '').replace(/\$\{NAME\}/g, stem);
}

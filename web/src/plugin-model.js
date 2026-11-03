// The plugins of PyMagic: features that come with it and can be switched off, as PyCharm's bundled plugins are (Settings > Plugins). Nothing is
// downloaded: a plugin that is off is not set up when the window opens (its commands, checks and keys are not there) and its panel tab, buttons and
// menu entries are hidden. A change takes effect when the window is reloaded. No DOM here: web/test/plugins.test.mjs checks it.

/** The plugins: id, name, what it does, the panel tab it owns (t-<tab>) and the other elements it owns (ids), hidden while it is off. */
export const PLUGINS = [
    { id: 'django', name: 'Django', about: "Django's inspections, completion, navigation and rename (URL names, models, settings, templates' views, REST framework), Django templates, its routes in the Endpoints panel, the Django panel and Structure's Django view, and Django in New Project.", tab: 'django', owns: ['st-django'], kind: 'django' },
    { id: 'flask', name: 'Flask', about: "Flask's routes: their checks (parameters, request.args and forms against the templates), url_for completion and navigation, and in the Endpoints panel; Flask in New Project.", kind: 'flask' },
    { id: 'fastapi', name: 'FastAPI', about: "FastAPI's routes and their checks (path parameters, Depends, request bodies, responses), url_path_for completion and navigation, and in the Endpoints panel; FastAPI in New Project.", kind: 'fastapi' },
    { id: 'javascript', name: 'JavaScript projects', about: 'The other kinds of New Project, made with Node.js: HTML5 Boilerplate, Bootstrap, Vite, React Native, Next.js, Node.js and Express.', kind: ['html5boilerplate', 'bootstrap', 'vite', 'reactnative', 'nextjs', 'nodejs', 'express'] },
    { id: 'notebooks', name: 'Jupyter Notebooks', about: '.ipynb files opened as notebooks: cells run and debugged in a kernel, their outputs, the Variables window and Jupyter servers, and Jupyter in New Project. Off, a notebook opens as its JSON text.', kind: 'jupyter' },
    { id: 'docker', name: 'Docker', about: 'Interpreters in a Docker image, a running container or a Docker Compose service; off, docker is not run to list them and one chosen before is not used.' },
    { id: 'wsl', name: 'WSL', about: 'Interpreters in a WSL distribution; off, wsl is not run to list them and one chosen before is not used.' },
    { id: 'ssh', name: 'SSH', about: 'Interpreters on an SSH host; off, its hosts are not read and one chosen before is not used.' },
    { id: 'git', name: 'Git', about: 'The Git panel, the commit, update and push buttons, the branch, change bars in the gutter, the log, changelists and the shelf, who wrote each function, and Git in New Project. Off, the project is treated as one without Git.', tab: 'git', owns: ['r-git', 'branch-chip', 'b-update', 'b-commit', 'b-push'] },
    { id: 'fullline', name: 'Full line completion', about: 'The rest of the line suggested in grey as you type (Tab takes it, Ctrl+Right its next word), written by a small code model that comes with PyMagic (Qwen2.5-Coder 0.5B) and runs on this computer: nothing is sent anywhere. Lines with unknown names or brackets that do not fit are not suggested. It uses about 600 MB of memory once it has made a suggestion; off, that is given back.' },
    { id: 'tailwind', name: 'Tailwind CSS', about: 'In a project that uses Tailwind CSS, its classes completed in class="", className="", :class, clsx() and cn(), with their variants (md:, hover:, dark:), a swatch for each colour and the CSS each class makes; hovering a class shows its CSS. The project\'s own @theme counts, and saving a file that brings Tailwind in turns it on; Settings > Editor can offer the classes in every project. Tailwind\'s class list comes with PyMagic.' },
    { id: 'tagpairs', name: 'HTML tag pairs', about: 'Renaming an opening tag renames its closing tag as you type, and back (<p>...</p> to <h2>...</h2>), in HTML, templates, XML and JSX.' },
    { id: 'emmet', name: 'Emmet', about: 'Abbreviations expanded by completion: ul>li*3, div.card>h2+p, ! for a whole page in HTML and templates (Django, Jinja, EJS, Handlebars, Twig, Razor, PHP); m10 for margin: 10px in CSS, SCSS and LESS; JSX (className) in .jsx and .tsx and in the .js and .ts files that use React.' },
    { id: 'schemas', name: 'JSON Schema', about: 'package.json, pyproject.toml, GitHub workflows, docker-compose, GitLab CI, pre-commit, mkdocs, dependabot, codecov, tox, Renovate, OpenAPI / Swagger and Kubernetes files checked against their JSON Schema (built in).' },
    { id: 'httpclient', name: 'HTTP Client', about: '.http files: requests sent from the editor, their responses, environments and history.', tab: 'httpclient' },
    { id: 'database', name: 'Database Tools and SQL', about: "The Database panel (the project's SQLite files, their tables and an SQL console) and SQL completed from its tables in Python strings.", tab: 'database' },
    { id: 'coverage', name: 'Coverage', about: 'Run the tests with coverage: the lines that ran marked in the editor, and the Coverage panel.', tab: 'coverage', owns: ['b-cov'] },
    { id: 'diagrams', name: 'Diagrams', about: "Diagrams of the project's classes, modules and imports, and Django models (Tools > Diagrams)." },
    { id: 'profiler', name: 'Profiler', about: 'Profile a file: where the time went, the call graph, kept snapshots.', tab: 'profiler', owns: ['b-profile'] },
    { id: 'prs', name: 'Pull Requests', about: "The pull requests (GitHub) or merge requests (GitLab) of the project's remote, reviewed and merged here.", tab: 'prs', requires: 'git' },
    { id: 'deployment', name: 'Deployment', about: 'The servers the project goes to (a folder, FTP, SFTP): upload, download, compare and sync.', tab: 'deployment' },
    { id: 'markdown', name: 'Markdown', about: 'The preview of Markdown and reStructuredText files beside the editor (Ctrl+Shift+V), its HTML export, and the Markdown editing tools.' },
    { id: 'macros', name: 'Macros', about: 'Record the keys typed in the editor and play them back.' },
];

/** The ids of the plugins that are off, from the setting (id -> false), and those that need one that is off (Pull Requests needs Git); an unknown id or a value that is not false counts for nothing. */
export function pluginsOff(setting) {
    const off = new Set();
    for (const p of PLUGINS) if (setting && setting[p.id] === false) off.add(p.id);
    for (const p of PLUGINS) if (p.requires && off.has(p.requires)) off.add(p.id);
    return off;
}

/** The plugin a plugin needs, by its name (Pull Requests needs Git), or null. */
export function requiredName(id) {
    const needs = PLUGINS.find((p) => p.id === id)?.requires;
    return needs ? PLUGINS.find((p) => p.id === needs)?.name ?? null : null;
}

/** The New Project kinds of the plugins that are off ('django', 'jupyter'; a plugin may bring several, as JavaScript projects does). */
export function kindsOff(setting) {
    return PLUGINS.filter((p) => p.kind && pluginsOff(setting).has(p.id)).flatMap((p) => p.kind);
}

/** Whether a plugin is on (every plugin is, unless the setting says false). */
export function pluginOn(setting, id) {
    return !pluginsOff(setting).has(id);
}

/** The setting with one plugin switched: an on plugin is left out (the default), an off one is kept as false. */
export function switchPlugin(setting, id, on) {
    const next = Object.fromEntries(Object.entries(setting ?? {}).filter(([k, v]) => v === false && k !== id && PLUGINS.some((p) => p.id === k)));
    if (!on) next[id] = false;
    return next;
}

/** The ids of the elements to hide for the plugins that are off: each one's panel tab (t-<tab>, <tab>-tab) and what else it owns. */
export function hiddenElements(setting) {
    const ids = [];
    for (const p of PLUGINS) {
        if (!pluginsOff(setting).has(p.id)) continue;
        if (p.tab) ids.push('t-' + p.tab, p.tab + '-tab');
        ids.push(...(p.owns ?? []));
    }
    // the button that adds a remote interpreter, when none of its kinds is left
    if (!remoteKindsLeft(setting))
        ids.push('b-remote');
    return ids;
}

/** Whether any kind of remote interpreter is left (Docker, WSL or SSH on). */
export function remoteKindsLeft(setting) {
    return ['docker', 'wsl', 'ssh'].some((id) => pluginOn(setting, id));
}

/** The plugins whose name or description has the words of a search (each word, in any order, without regard to case). */
export function findPlugins(query) {
    const words = (query ?? '').toLowerCase().split(/\s+/).filter(Boolean);
    return PLUGINS.filter((p) => words.every((w) => (p.name + ' ' + p.about).toLowerCase().includes(w)));
}

/** Whether a tool window (by its tab name: 'profiler', 'database') belongs to a plugin that is off. */
export function toolOff(setting, tab) {
    return PLUGINS.some((p) => p.tab === tab && pluginsOff(setting).has(p.id));
}

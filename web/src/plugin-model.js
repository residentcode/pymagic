// The plugins of PyMagic: features that come with it and can be switched off, as PyCharm's bundled plugins are (Settings > Plugins). Nothing is
// downloaded: a plugin that is off is not set up when the window opens (its commands, checks and keys are not there) and its panel tab, buttons and
// menu entries are hidden. A change takes effect when the window is reloaded. No DOM here: web/test/plugins.test.mjs checks it.

/** The plugins: id, name, what it does, the panel tab it owns (t-<tab>) and the other elements it owns (ids), hidden while it is off. */
export const PLUGINS = [
    { id: 'schemas', name: 'JSON Schema', about: 'package.json, pyproject.toml, GitHub workflows, docker-compose, GitLab CI, pre-commit, mkdocs, dependabot, codecov, tox, Renovate, OpenAPI / Swagger and Kubernetes files checked against their JSON Schema (built in).' },
    { id: 'httpclient', name: 'HTTP Client', about: '.http files: requests sent from the editor, their responses, environments and history.', tab: 'httpclient' },
    { id: 'database', name: 'Database Tools and SQL', about: "The Database panel (the project's SQLite files, their tables and an SQL console) and SQL completed from its tables in Python strings.", tab: 'database' },
    { id: 'profiler', name: 'Profiler', about: 'Profile a file: where the time went, the call graph, kept snapshots.', tab: 'profiler', owns: ['b-profile'] },
    { id: 'prs', name: 'Pull Requests', about: "The pull requests (GitHub) or merge requests (GitLab) of the project's remote, reviewed and merged here.", tab: 'prs' },
    { id: 'deployment', name: 'Deployment', about: 'The servers the project goes to (a folder, FTP, SFTP): upload, download, compare and sync.', tab: 'deployment' },
    { id: 'markdown', name: 'Markdown', about: 'The preview of Markdown and reStructuredText files beside the editor (Ctrl+Shift+V), its HTML export, and the Markdown editing tools.' },
    { id: 'macros', name: 'Macros', about: 'Record the keys typed in the editor and play them back.' },
];

/** The ids of the plugins that are off, from the setting (id -> false); an unknown id or a value that is not false counts for nothing. */
export function pluginsOff(setting) {
    const off = new Set();
    for (const p of PLUGINS) if (setting && setting[p.id] === false) off.add(p.id);
    return off;
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
    return ids;
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

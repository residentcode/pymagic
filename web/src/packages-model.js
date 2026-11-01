// The parts of the Packages tab that need no page: tree rows, version lists, settings text, what looks like a source to install from.

/** Lines (or commas) of a text box as a list: trimmed, no empty ones, no repeats. */
export function listText(text) {
    const seen = new Set();
    const out = [];
    for (const part of String(text ?? '').split(/[\r\n,]+/)) {
        const t = part.trim();
        if (t && !seen.has(t)) { seen.add(t); out.push(t); }
    }
    return out;
}

/** The settings object the host keeps, from the four fields of the form. */
export function settingsFromForm(form) {
    return { indexUrl: String(form.indexUrl ?? '').trim(), extraIndexUrls: listText(form.extraIndexUrls), trustedHosts: listText(form.trustedHosts), proxy: String(form.proxy ?? '').trim() };
}

/** One line telling which settings are in use ('' when none). */
export function settingsSummary(s) {
    const parts = [];
    if (s?.indexUrl) parts.push('index ' + s.indexUrl);
    if (s?.extraIndexUrls?.length) parts.push(s.extraIndexUrls.length + ' extra index' + (s.extraIndexUrls.length > 1 ? 'es' : ''));
    if (s?.trustedHosts?.length) parts.push('trusting ' + s.trustedHosts.join(', '));
    if (s?.proxy) parts.push('proxy ' + s.proxy);
    return parts.join('; ');
}

/** Whether text typed into the install box is a path, a git address or a link rather than a package name or requirement. */
export function looksLikeSource(text) {
    const t = String(text ?? '').trim().replace(/^["']|["']$/g, '');
    if (!t) return false;
    if (/^git\+/i.test(t) || /^[a-z][a-z0-9+.-]*:\/\//i.test(t)) return true;
    if (/^[A-Za-z]:[\\/]/.test(t) || t.startsWith('\\\\')) return true;
    if (/^\.{1,2}([\\/]|$)/.test(t) || t.startsWith('/') || t.startsWith('~')) return true;
    return /[\\/]/.test(t) && /\.(whl|zip|tar\.gz|tgz)$/i.test(t);
}

/** The choices of a version picker: newest first; yanked ones are left out, pre-releases only when asked for. */
export function versionOptions(versions, { pre = false, installed = '' } = {}) {
    const options = [];
    for (const v of versions ?? []) {
        if (v.yanked) continue;
        if (v.pre && !pre && v.version !== installed) continue;
        let label = v.version;
        if (v.pre) label += ' (pre-release)';
        if (v.version === installed) label += ' (installed)';
        options.push({ value: v.version, label });
    }
    return options;
}

/** The requirement given to pip for a chosen version. */
export function pinned(name, version) {
    return `${name}==${version}`;
}

/** Key of a node on its way down the tree: the names above it joined, so the same package in two places opens separately. */
const keyOf = (parent, node, index) => `${parent}/${index}:${node.name}`;

/** The visible rows of a tree: [{ key, depth, node, hasChildren, open }]. A row of depth below `openDepth` starts open, others closed; `flipped` holds the keys the person turned the other way. */
export function treeRows(roots, flipped = new Set(), openDepth = 1) {
    const rows = [];
    const walk = (nodes, depth, parent) => {
        nodes.forEach((node, i) => {
            const key = keyOf(parent, node, i);
            const hasChildren = (node.children?.length ?? 0) > 0;
            const open = hasChildren && ((depth < openDepth) !== flipped.has(key));
            rows.push({ key, depth, node, hasChildren, open });
            if (open) walk(node.children, depth + 1, key);
        });
    };
    walk(roots ?? [], 0, '');
    return rows;
}

/** Opening or closing a row: the new set of flipped keys. */
export function toggled(flipped, row) {
    const next = new Set(flipped);
    if (next.has(row.key)) next.delete(row.key); else next.add(row.key);
    return next;
}

/** The text of a tree row. */
export function treeLabel(node) {
    if (node.missing) return `${node.name} (not installed)`;
    let text = node.version ? `${node.name} ${node.version}` : node.name;
    if (node.cycle) text += ' (circular)';
    return text;
}

/** How many packages a tree names, each once. */
export function countNodes(roots) {
    const names = new Set();
    const walk = (nodes) => { for (const n of nodes ?? []) { names.add(n.name.toLowerCase()); walk(n.children); } };
    walk(roots);
    return names.size;
}

/** The line of a lock file: what it is, which tool, whether it can be synced. */
export function lockLabel(lock) {
    return `${lock.file} (${lock.tool})`;
}

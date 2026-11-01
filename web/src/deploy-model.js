// Deployment without the page: what a sync does by default with each kind of difference, and how a server is told in words.
// No DOM here: web/test/deploy.test.mjs checks it.

/** What a sync proposes for a file by its state: the side that has it or has the newer one wins; files that are the same are left. */
export function defaultAction(status) {
    return { 'local-only': 'upload', 'local-newer': 'upload', 'remote-only': 'download', 'remote-newer': 'download' }[status] ?? 'skip';
}

export const STATUS_TEXT = {
    'local-only': 'only here', 'local-newer': 'newer here', 'remote-only': 'only on the server', 'remote-newer': 'newer on the server', same: 'the same',
};

/** The actions a sync offers for a state: [action, label]. */
export function actionsFor(status) {
    if (status === 'local-only') return [['upload', 'Upload'], ['skip', 'Skip']];
    if (status === 'remote-only') return [['download', 'Download'], ['skip', 'Skip']];
    if (status === 'same') return [['skip', 'Skip']];
    return [['upload', 'Upload'], ['download', 'Download'], ['skip', 'Skip']];
}

/** A server in a line: "me@example.org (SFTP)", "FTP ftp.site.org", "a folder D:/share". */
export function describeServer(s) {
    if (s.type === 'folder') return `a folder ${s.root}`;
    return `${{ sftp: 'SFTP', ftps: 'FTPS' }[s.type] ?? 'FTP'} ${s.user ? s.user + '@' : ''}${s.host}${(s.type === 'sftp' && s.port !== 22) || (s.type !== 'sftp' && s.port !== 21) ? ':' + s.port : ''}`;
}

/** The default port of a kind of server. */
export function defaultPort(type) { return type === 'sftp' ? 22 : 21; }

/** The question asked before something is deleted on a server: what goes, where, and that a folder takes what is in it. */
export function deleteQuestion(entry, serverName, path) {
    const where = '/' + (path ? path + '/' : '') + entry.name;
    return entry.isDir
        ? `Delete the folder ${where} on ${serverName}, with everything in it? This cannot be undone.`
        : `Delete ${where} on ${serverName}? This cannot be undone.`;
}

/** The mappings as "src → /app/src" lines, and back. A mapping with nothing is the whole project at the server's root. */
export function mappingText(m) { return `${m.local || '.'} → ${m.remote || '.'}`; }
export function parseMappings(text) {
    const lines = text.split('\n').map((l) => l.trim()).filter(Boolean);
    const out = [];
    for (const line of lines) {
        const [a, b] = line.split(/\s*(?:→|->|=>|=)\s*/);
        const clean = (x) => { const c = (x ?? '').replace(/\\/g, '/').replace(/^(\.?\/)+|\/+$/g, ''); return c === '.' ? '' : c; };
        out.push({ local: clean(a), remote: clean(b ?? a) });
    }
    return out.length ? out : [{ local: '', remote: '' }];
}

export function sizeText(bytes) {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/** The folder above a path of the server: "" for the top. */
export function parentOf(path) { const i = path.lastIndexOf('/'); return i < 0 ? '' : path.slice(0, i); }

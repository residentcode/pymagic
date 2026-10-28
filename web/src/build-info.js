// Which build of the page this is. The bundler replaces __BUILD__ with the commit and the time it was made; run from source files it is "dev".
export const buildInfo = typeof __BUILD__ === 'undefined' ? { commit: 'dev', builtAt: null } : __BUILD__;

/** "29 Oct 14:05" for an ISO time, or "" when there is none. */
export function shortTime(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    const pad = (n) => String(n).padStart(2, '0');
    return `${d.getDate()} ${months[d.getMonth()]} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** The line of the status bar: "build 8fc7660 · 29 Oct 14:05". */
export function buildLabel(info = buildInfo) {
    const when = shortTime(info.builtAt);
    return `build ${info.commit}${when ? ' · ' + when : ''}`;
}

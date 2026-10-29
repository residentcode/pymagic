// The Endpoints tab without the page: what an endpoint's detail says, as plain rows.

/** The kinds a parameter can come from, in the order the detail lists them. */
export const KIND_ORDER = ['path', 'query', 'body', 'form', 'file', 'header', 'cookie', 'dependency'];

/** "Response ItemOut · status 201 · tags items, admin", or "" when the code says none of it. */
export function summaryLine(route) {
    const parts = [];
    if (route.response) parts.push(`response ${route.response}`);
    if (route.status) parts.push(`status ${route.status}`);
    if (route.tags && route.tags.length) parts.push(`tags ${route.tags.join(', ')}`);
    return parts.join(' · ');
}

/**
 * The parameters of a route as rows to draw, each with the depth it is at (a dependency's own parameters are one deeper): `{ depth, kind, name, type,
 * required, detail }`. Within a level the parameters keep the order of the code, except that the route's own dependencies stay first.
 */
export function parameterRows(params, depth = 0) {
    const rows = [];
    for (const p of params ?? []) {
        rows.push({ depth, kind: p.kind, name: p.name, type: p.type ?? '', required: !!p.required, detail: p.detail ?? '' });
        if (p.children && p.children.length) rows.push(...parameterRows(p.children, depth + 1));
    }
    return rows;
}

/** How many parameters of each kind a route takes (dependencies not counted into their own), for the one-line summary of a closed route: "2 path · 1 body". */
export function countsLine(params) {
    const counts = new Map();
    for (const p of params ?? []) counts.set(p.kind, (counts.get(p.kind) ?? 0) + 1);
    return KIND_ORDER.filter((k) => counts.has(k)).map((k) => `${counts.get(k)} ${k === 'dependency' ? (counts.get(k) === 1 ? 'dependency' : 'dependencies') : k}`).join(' · ');
}

/** A key that stays the same while a route is edited elsewhere, to remember which ones are open. */
export function routeKey(route) {
    return `${route.methods} ${route.path} ${route.name} ${route.uri}`;
}

/** The text of a parameter row for the eye: "q: str | None", optional marked with a question mark. */
export function rowLabel(row) {
    return row.type ? `${row.name}: ${row.type}` : row.name;
}

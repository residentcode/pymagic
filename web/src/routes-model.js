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

// ------------------------------------------------------------ requests, examples, OpenAPI, filters

/** A value of a parameter's type for a request to try. */
export function sampleValue(type) {
    const t = (type ?? '').replace(/\s*\|\s*None$/, '').trim();
    if (/^int\b|^float\b|^Decimal/.test(t)) return t.startsWith('float') ? 1.5 : 1;
    if (/^bool\b/.test(t)) return true;
    if (/^(list|List|Sequence|set|tuple)\b/.test(t)) return [];
    if (/^(dict|Dict|Mapping)\b/.test(t)) return {};
    return 'string';
}

/** The path with its parameters filled: Django's <int:pk>, <slug>, and FastAPI's or Flask's {id} and <id>. */
export function fillPath(path) {
    return path
        .replace(/<(?:(\w+):)?(\w+)>/g, (_, conv) => (conv === 'int' ? '1' : conv === 'uuid' ? '00000000-0000-0000-0000-000000000000' : 'value'))
        .replace(/\{(\w+)(?::[^}]*)?\}/g, () => '1')
        .replace(/…$/, '');
}

/** The request generated for an endpoint: its first method (GET for ANY), the path filled, the required query parameters, and a JSON body of its body fields. */
export function requestFor(route, base = 'http://127.0.0.1:8000') {
    const method = (route.methods.split(',')[0].trim().toUpperCase() || 'GET').replace('ANY', 'GET');
    const params = route.params ?? [];
    const query = params.filter((p) => p.kind === 'query' && p.required).map((p) => `${encodeURIComponent(p.name)}=${encodeURIComponent(String(sampleValue(p.type)))}`);
    const path = fillPath(route.path).replace(/^\/?/, '/');
    const url = base.replace(/\/+$/, '') + path + (query.length ? '?' + query.join('&') : '');
    const bodyParams = params.filter((p) => p.kind === 'body');
    let body = '';
    const headers = { Accept: 'application/json' };
    if (bodyParams.length) {
        // one body parameter of a model type is the whole body; several are its fields
        const value = bodyParams.length === 1 && bodyParams[0].children?.length
            ? Object.fromEntries(bodyParams[0].children.map((c) => [c.name, sampleValue(c.type)]))
            : Object.fromEntries(bodyParams.map((p) => [p.name, sampleValue(p.type)]));
        body = JSON.stringify(value, null, 2);
        headers['Content-Type'] = 'application/json';
    }
    else if (['POST', 'PUT', 'PATCH'].includes(method)) {
        body = '{}';
        headers['Content-Type'] = 'application/json';
    }
    return { method, url, headers, body };
}

/** The request as text to edit: "METHOD url", a header per line, a blank line, the body. */
export function requestText(r) {
    return `${r.method} ${r.url}\n${Object.entries(r.headers).map(([k, v]) => `${k}: ${v}`).join('\n')}${r.body ? '\n\n' + r.body : ''}`;
}

/** That text read back; null when its first line is not "METHOD url". */
export function parseRequest(text) {
    const lines = text.replace(/\r\n/g, '\n').split('\n');
    const first = /^([A-Za-z]+)\s+(\S+)\s*$/.exec(lines[0] ?? '');
    if (!first) return null;
    const headers = {};
    let i = 1;
    for (; i < lines.length && lines[i].trim(); i++) {
        const at = lines[i].indexOf(':');
        if (at > 0) headers[lines[i].slice(0, at).trim()] = lines[i].slice(at + 1).trim();
    }
    return { method: first[1].toUpperCase(), url: first[2], headers, body: lines.slice(i + 1).join('\n').trim() };
}

const quote = (s) => `'${String(s).replace(/'/g, `'\\''`)}'`;
/** The request as client code: curl, Python's requests, or JavaScript's fetch. */
export function clientCode(r, language) {
    const headerLines = Object.entries(r.headers);
    if (language === 'curl')
        return [`curl -X ${r.method} ${quote(r.url)}`, ...headerLines.map(([k, v]) => `  -H ${quote(`${k}: ${v}`)}`), ...(r.body ? [`  -d ${quote(r.body)}`] : [])].join(' \\\n');
    if (language === 'python') {
        const args = [`"${r.url}"`, `headers=${JSON.stringify(Object.fromEntries(headerLines))}`];
        if (r.body) args.push(`json=${JSON.stringify(JSON.parse(r.body || '{}'))}`.replace(/\btrue\b/g, 'True').replace(/\bfalse\b/g, 'False').replace(/\bnull\b/g, 'None'));
        return `import requests\n\nresponse = requests.${r.method.toLowerCase()}(${args.join(', ')})\nprint(response.status_code, response.json())\n`;
    }
    const init = { method: r.method, headers: Object.fromEntries(headerLines), ...(r.body ? { body: '__BODY__' } : {}) };
    return `const response = await fetch(${JSON.stringify(r.url)}, ${JSON.stringify(init, null, 2).replace('"__BODY__"', `JSON.stringify(${r.body.replace(/\n/g, '\n  ')})`)});\nconsole.log(response.status, await response.json());\n`;
}

/** The docstring of the function or class whose name is on a line of the source (0-based), or ''. */
export function docOf(source, line) {
    const lines = source.replace(/\r\n/g, '\n').split('\n');
    let i = line;
    while (i < lines.length && !/:\s*(#.*)?$/.test(lines[i])) i++;   // the end of the header (it may span lines)
    const rest = lines.slice(i + 1).join('\n');
    const m = /^\s*[rRuU]?("""|''')([\s\S]*?)\1/.exec(rest) ?? /^\s*[rRuU]?(["'])(.*?)\1/.exec(rest);
    if (!m) return '';
    const body = m[2].split('\n');
    const indent = Math.min(...body.slice(1).filter((l) => l.trim()).map((l) => l.length - l.trimStart().length), Infinity);
    return [body[0].trim(), ...body.slice(1).map((l) => (Number.isFinite(indent) ? l.slice(indent) : l))].join('\n').trim();
}

/** The OpenAPI 3 document of the routes (the project's own ones): paths in OpenAPI's {name} form, an operation per method with its parameters and body. */
export function openApi(routes, title = 'API') {
    const paths = {};
    for (const r of routes.filter((x) => x.framework !== 'library' && x.framework !== 'test')) {
        const path = '/' + r.path.replace(/^\//, '').replace(/<(?:\w+:)?(\w+)>/g, '{$1}').replace(/\{(\w+):[^}]*\}/g, '{$1}');
        const names = [...path.matchAll(/\{(\w+)\}/g)].map((x) => x[1]);
        for (const method of r.methods.split(',').map((x) => x.trim().toLowerCase()).map((x) => (x === 'any' ? 'get' : x))) {
            const params = r.params ?? [];
            const parameters = [
                ...names.map((n) => ({ name: n, in: 'path', required: true, schema: { type: params.find((p) => p.name === n)?.type?.startsWith('int') ? 'integer' : 'string' } })),
                ...params.filter((p) => ['query', 'header', 'cookie'].includes(p.kind)).map((p) => ({ name: p.name, in: p.kind, required: !!p.required, schema: { type: openApiType(p.type) } })),
            ];
            const body = params.filter((p) => p.kind === 'body');
            const op = { operationId: r.name, summary: r.name.replace(/_/g, ' '), ...(r.tags ? { tags: r.tags } : {}), parameters,
                responses: { [r.status ?? '200']: { description: r.response ? `${r.response}` : 'Successful response' } } };
            if (body.length) op.requestBody = { required: body.some((p) => p.required), content: { 'application/json': { schema: { type: 'object', properties: Object.fromEntries((body.length === 1 && body[0].children?.length ? body[0].children : body).map((p) => [p.name, { type: openApiType(p.type) }])) } } } };
            paths[path] = { ...(paths[path] ?? {}), [method]: op };
        }
    }
    return { openapi: '3.0.3', info: { title, version: '1.0.0' }, paths };
}

function openApiType(type) {
    const t = (type ?? '').replace(/\s*\|\s*None$/, '');
    return /^int\b/.test(t) ? 'integer' : /^(float|Decimal)\b/.test(t) ? 'number' : /^bool\b/.test(t) ? 'boolean' : /^(list|List)\b/.test(t) ? 'array' : /^(dict|Dict)\b/.test(t) ? 'object' : 'string';
}

/** Where an endpoint comes from: the project's code, a library (Django's admin and the like), or a test that requests it. */
export const originOf = (route) => (route.framework === 'library' ? 'library' : route.framework === 'test' ? 'test' : 'project');

/** The routes a filter shows: words in the path, method, name or file, a method, and where they come from. */
export function filterRoutes(routes, { text = '', method = '', origins = ['project', 'library', 'test'] }, fileOf = () => '') {
    const needle = text.trim().toLowerCase();
    return routes.filter((r) => origins.includes(originOf(r))
        && (!method || r.methods.toUpperCase().split(',').map((m) => m.trim()).some((m) => m === method || m === 'ANY'))
        && (!needle || `${r.methods} ${r.path} ${r.name} ${fileOf(r)}`.toLowerCase().includes(needle)));
}

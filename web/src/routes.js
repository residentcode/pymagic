// The Endpoints tab: the project's routes (FastAPI, Flask, Django and REST framework), with the endpoints of libraries (Django's admin) and the URLs
// the tests request. Each opens on what it takes and gives, a request to send to the running server, the view's documentation, its OpenAPI and
// client code; the whole project's OpenAPI document is a button away. Filters: words, a method, and where an endpoint comes from.
import { showTab } from './panel';
import { toast } from './toast';
import { countsLine, parameterRows, routeKey, rowLabel, summaryLine, requestFor, requestText, parseRequest, clientCode, docOf, openApi, filterRoutes, originOf } from './routes-model.js';

const $ = (id) => document.getElementById(id);

const el = (tag, cls, text) => {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
};

export function setupRoutes(deps) {
    const list = $('routes-list');
    const summary = $('routes-summary');
    const filter = $('routes-filter');
    const methodBox = $('routes-method');
    const baseBox = $('routes-base');
    let routes = [];
    let ticket = 0;
    const open = new Set();          // the routes whose detail is showing
    const pane = new Map();          // which part of an open route's detail is shown
    const origins = new Set(['project', 'library', 'test']);
    try { baseBox.value = localStorage.getItem('pymagic.routesBase') || baseBox.value; } catch { /* the default */ }
    baseBox.onchange = () => { try { localStorage.setItem('pymagic.routesBase', baseBox.value); } catch { /* this time */ } };

    function parametersOf(r) {
        const box = el('div', 'route-detail');
        const line = summaryLine(r);
        if (line) box.append(el('div', 'rd-line', line));
        const rows = parameterRows(r.params);
        if (rows.length === 0) box.append(el('div', 'rd-line', Array.isArray(r.params) ? 'It takes no parameters.' : 'What it takes is not read for this framework.'));
        for (const row of rows) {
            const item = el('div', 'rp');
            item.style.paddingLeft = `${12 + row.depth * 18}px`;
            item.append(el('span', `rk rk-${row.kind}`, row.kind === 'dependency' ? 'depends' : row.kind));
            item.append(el('span', 'rn', rowLabel(row)));
            if (row.kind !== 'dependency') item.append(el('span', row.required ? 'rq' : 'ro', row.required ? 'required' : 'optional'));
            if (row.detail) item.append(el('span', 'detail', row.detail));
            box.append(item);
        }
        return box;
    }

    /** The request to try: generated, editable, sent by the engine to the server running on this computer. */
    function requestOf(r) {
        const box = el('div', 'route-detail');
        const text = el('textarea', 'route-request');
        text.spellcheck = false;
        text.value = requestText(requestFor(r, baseBox.value));
        text.rows = Math.min(14, text.value.split('\n').length + 1);
        const send = el('button', 'primary', 'Send');
        const reset = el('button', '', 'Generate again');
        const result = el('div', 'route-result');
        reset.onclick = () => { text.value = requestText(requestFor(r, baseBox.value)); };
        send.onclick = async () => {
            const req = parseRequest(text.value);
            if (!req) { toast('The first line is "METHOD url".', true); return; }
            result.replaceChildren(el('div', 'rd-line', 'Sending…'));
            const res = await fetch('/api/http', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(req) }).catch(() => null);
            const answer = res ? await res.json().catch(() => ({})) : { error: 'The engine did not answer.' };
            if (!res?.ok) { result.replaceChildren(el('div', 'rd-line bad', answer.error ?? 'The request failed.')); return; }
            let body = answer.body ?? '';
            try { body = JSON.stringify(JSON.parse(body), null, 2); } catch { /* not JSON: as it came */ }
            const head = el('div', `rd-line ${answer.status < 400 ? 'good' : 'bad'}`, `${answer.status} ${answer.reason ?? ''} · ${answer.ms} ms${answer.truncated ? ' · the first megabyte' : ''}`);
            const headers = el('details', 'route-headers');
            headers.append(el('summary', '', 'Headers'), el('pre', '', Object.entries(answer.headers ?? {}).map(([k, v]) => `${k}: ${v}`).join('\n')));
            result.replaceChildren(head, headers, el('pre', 'route-body', body));
        };
        const buttons = el('div', 'route-buttons');
        buttons.append(send, reset);
        box.append(text, buttons, result);
        return box;
    }

    function docsOf(r) {
        const box = el('div', 'route-detail');
        const rel = deps.relOfUri(r.uri);
        box.append(el('div', 'rd-line', 'Reading…'));
        if (rel) fetch('/api/file?path=' + encodeURIComponent(rel)).then((x) => (x.ok ? x.text() : '')).then((source) => {
            const doc = docOf(source, r.line);
            box.replaceChildren(doc ? el('pre', 'route-doc', doc) : el('div', 'rd-line', `${r.name}() has no docstring.`));
        }).catch(() => { box.replaceChildren(el('div', 'rd-line', 'The file could not be read.')); });
        return box;
    }

    function openApiOf(r) {
        const box = el('div', 'route-detail');
        box.append(el('pre', 'route-doc', JSON.stringify(openApi([r]).paths, null, 2)));
        return box;
    }

    function codeOf(r) {
        const box = el('div', 'route-detail');
        const choose = el('select');
        for (const [v, label] of [['curl', 'curl'], ['python', 'Python (requests)'], ['js', 'JavaScript (fetch)']]) choose.add(new Option(label, v));
        const pre = el('pre', 'route-doc');
        const copy = el('button', '', 'Copy');
        const draw = () => { pre.textContent = clientCode(requestFor(r, baseBox.value), choose.value); };
        choose.onchange = draw;
        copy.onclick = () => void navigator.clipboard?.writeText(pre.textContent).then(() => toast('Copied.'));
        draw();
        const bar = el('div', 'route-buttons');
        bar.append(choose, copy);
        box.append(bar, pre);
        return box;
    }

    const PANES = [['params', 'Parameters', parametersOf], ['request', 'Request', requestOf], ['docs', 'Documentation', docsOf], ['openapi', 'OpenAPI', openApiOf], ['code', 'Client code', codeOf]];

    function detailOf(r, key) {
        const holder = el('div', 'route-panes');
        const tabs = el('div', 'route-tabs');
        const current = pane.get(key) ?? (Array.isArray(r.params) ? 'params' : 'request');
        for (const [id, label] of PANES) {
            const b = el('button', id === current ? 'active' : '', label);
            b.onclick = (e) => { e.stopPropagation(); pane.set(key, id); paint(); };
            tabs.append(b);
        }
        holder.append(tabs, PANES.find(([id]) => id === current)[2](r));
        return holder;
    }

    function paint() {
        const shown = filterRoutes(routes, { text: filter.value, method: methodBox.value, origins: [...origins] }, (r) => deps.relOfUri(r.uri) ?? '');
        summary.textContent = routes.length === 0
            ? 'No routes found. Routes are functions decorated with @app.get("/path"), @router.post(...) or @bp.route(...), and Django\'s urlpatterns.'
            : `${shown.length} of ${routes.length} routes`;
        list.replaceChildren(...shown.map((r) => {
            const item = el('div', `route-item route-${originOf(r)}`);
            const row = el('div', 'route');
            const key = routeKey(r);
            const twist = el('span', 'twist', open.has(key) ? '▾' : '▸');
            const methods = el('span', `verb verb-${r.methods.split(',')[0].trim().toLowerCase()}`, r.methods);
            const path = el('span', 'rpath', r.path);
            const name = el('span', 'detail', originOf(r) === 'test' ? `from ${r.name}()` : originOf(r) === 'library' ? r.name : `${r.name}()`);
            const counts = Array.isArray(r.params) && !open.has(key) ? countsLine(r.params) : '';
            const file = el('span', 'detail', `${deps.relOfUri(r.uri) ?? r.uri}:${r.line + 1}`);
            row.append(twist, methods, path, name);
            if (originOf(r) !== 'project') row.append(el('span', 'route-origin', originOf(r)));
            if (counts) row.append(el('span', 'detail rcount', counts));
            row.append(file);
            item.append(row);
            if (open.has(key)) item.append(detailOf(r, key));
            twist.onclick = (e) => {
                e.stopPropagation();
                if (open.has(key)) open.delete(key); else open.add(key);
                paint();
            };
            row.onclick = async () => {
                const rel = deps.relOfUri(r.uri);
                if (!rel) {
                    toast('That place is outside the project.', true);
                    return;
                }
                await deps.openFile(rel, { startLineNumber: r.line + 1, startColumn: 1, endLineNumber: r.line + 1, endColumn: 1 });
            };
            return item;
        }));
    }

    /** The whole project's OpenAPI document, to read, copy or save. */
    function showOpenApi() {
        const text = JSON.stringify(openApi(routes, 'PyMagic project'), null, 2);
        const shade = el('div', 'settings-shade');
        const box = el('div', 'trace-box');
        const pre = el('pre', 'route-doc openapi-all', text);
        const buttons = el('div', 'set-buttons');
        const copy = el('button', '', 'Copy');
        const save = el('button', '', 'Save as openapi.json');
        const close = el('button', 'primary', 'Close');
        copy.onclick = () => void navigator.clipboard?.writeText(text).then(() => toast('Copied.'));
        save.onclick = async () => {
            const res = await fetch('/api/file?path=openapi.json', { method: 'PUT', body: text + '\n' }).catch(() => null);
            toast(res?.ok ? 'openapi.json is in the project folder.' : 'It could not be saved.', !res?.ok);
        };
        const done = () => shade.remove();
        close.onclick = done;
        shade.onmousedown = (e) => { if (e.target === shade) done(); };
        buttons.append(copy, save, close);
        box.append(el('h3', '', 'OpenAPI of the project'), pre, buttons);
        shade.append(box);
        document.body.append(shade);
    }

    async function refresh() {
        const mine = ++ticket;
        try {
            const found = (await deps.lsp.request('pymagic/routes', {}));
            if (mine !== ticket)
                return;
            routes = (found ?? []).slice().sort((a, b) => a.path.localeCompare(b.path) || a.methods.localeCompare(b.methods));
            paint();
        }
        catch (e) {
            summary.textContent = String(e.message ?? e);
        }
    }
    $('t-routes').onclick = () => { showTab('routes'); void refresh(); };
    $('routes-refresh').onclick = () => void refresh();
    $('routes-openapi').onclick = showOpenApi;
    for (const o of ['project', 'library', 'test']) {
        const box = $(`routes-o-${o}`);
        box.onchange = () => { if (box.checked) origins.add(o); else origins.delete(o); paint(); };
    }
    filter.oninput = paint;
    methodBox.onchange = paint;
    window.addEventListener('pymagic:saved', () => { if ($('routes-tab').classList.contains('active'))
        void refresh(); });
}

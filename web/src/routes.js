import { showTab } from './panel';
import { toast } from './toast';
import { countsLine, parameterRows, routeKey, rowLabel, summaryLine } from './routes-model.js';

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
    let routes = [];
    let ticket = 0;
    const open = new Set();   // the routes whose detail is showing

    /** What an endpoint takes and gives: the response line and a row for each parameter, a dependency's own ones below it. */
    function detailOf(r) {
        const box = el('div', 'route-detail');
        const line = summaryLine(r);
        if (line) box.append(el('div', 'rd-line', line));
        const rows = parameterRows(r.params);
        if (rows.length === 0) box.append(el('div', 'rd-line', 'It takes no parameters.'));
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

    function paint() {
        const needle = filter.value.trim().toLowerCase();
        const shown = routes.filter((r) => !needle || `${r.methods} ${r.path} ${r.name} ${deps.relOfUri(r.uri) ?? ''}`.toLowerCase().includes(needle));
        summary.textContent = routes.length === 0
            ? 'No routes found. Routes are functions decorated with @app.get("/path"), @router.post(...) or @bp.route(...).'
            : `${shown.length} of ${routes.length} routes`;
        list.replaceChildren(...shown.map((r) => {
            const item = el('div', 'route-item');
            const row = el('div', 'route');
            const key = routeKey(r);
            const expandable = Array.isArray(r.params);
            const twist = el('span', 'twist', expandable ? (open.has(key) ? '▾' : '▸') : '');
            const methods = el('span', `verb verb-${r.methods.split(',')[0].trim().toLowerCase()}`, r.methods);
            const path = el('span', 'rpath', r.path);
            const name = el('span', 'detail', `${r.name}()`);
            const counts = expandable && !open.has(key) ? countsLine(r.params) : '';
            const file = el('span', 'detail', `${deps.relOfUri(r.uri) ?? r.uri}:${r.line + 1}`);
            row.append(twist, methods, path, name);
            if (counts) row.append(el('span', 'detail rcount', counts));
            row.append(file);
            item.append(row);
            if (expandable && open.has(key)) item.append(detailOf(r));
            twist.onclick = (e) => {
                if (!expandable) return;
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
    filter.oninput = paint;
    window.addEventListener('pymagic:saved', () => { if ($('routes-tab').classList.contains('active'))
        void refresh(); });
}

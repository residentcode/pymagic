// The Problems and TODO panels: what the whole project reports, not only the open file.
import { showTab } from './panel';
import { toast } from './toast';

const $ = (id) => document.getElementById(id);

const SEVERITY_MARK = { 1: '✖', 2: '⚠', 3: 'ⓘ', 4: '·' };
const SEVERITY_CLASS = { 1: 'sev-error', 2: 'sev-warning', 3: 'sev-weak', 4: 'sev-hint' };

export function setupProblems(deps) {
    const list = $('problems-list');
    const summary = $('problems-summary');
    const filter = $('problems-filter');
    const level = $('problems-level');
    const tab = $('t-problems');
    let problems = [];
    let ticket = 0;

    async function open(uri, line, column) {
        const rel = deps.relOfUri(uri);
        if (!rel) {
            toast('That place is outside the project.', true);
            return;
        }
        await deps.openFile(rel, { startLineNumber: line + 1, startColumn: column + 1, endLineNumber: line + 1, endColumn: column + 1 });
    }

    function paint() {
        const needle = filter.value.trim().toLowerCase();
        const shown = problems.filter((p) => !needle || `${p.message} ${p.code ?? ''} ${deps.relOfUri(p.uri) ?? p.uri}`.toLowerCase().includes(needle));
        const byFile = new Map();
        for (const p of shown) {
            const key = deps.relOfUri(p.uri) ?? p.uri;
            if (!byFile.has(key)) byFile.set(key, []);
            byFile.get(key).push(p);
        }
        const counts = [1, 2, 3, 4].map((s) => shown.filter((p) => p.severity === s).length);
        summary.textContent = problems.length === 0
            ? 'No problems found.'
            : `${counts[0]} errors, ${counts[1]} warnings, ${counts[2]} weak warnings${counts[3] ? `, ${counts[3]} hints` : ''} in ${byFile.size} files`;
        const serious = problems.filter((p) => p.severity <= 2).length;
        tab.querySelector('.lbl').textContent = 'Problems';
        tab.dataset.count = serious > 0 ? String(serious) : '';
        document.getElementById('r-problems')?.querySelector('.badge')?.replaceChildren(serious > 0 ? String(serious) : '');
        const rows = [];
        for (const [file, items] of [...byFile.entries()].sort((a, b) => Math.min(...a[1].map((p) => p.severity)) - Math.min(...b[1].map((p) => p.severity)) || a[0].localeCompare(b[0]))) {
            const head = document.createElement('div');
            head.className = 'prob-file';
            head.textContent = `${file}  (${items.length})`;
            rows.push(head);
            for (const p of items.sort((a, b) => a.severity - b.severity || a.line - b.line)) {
                const row = document.createElement('div');
                row.className = `prob ${SEVERITY_CLASS[p.severity] ?? ''}`;
                const mark = document.createElement('span');
                mark.className = 'mark';
                mark.textContent = SEVERITY_MARK[p.severity] ?? '·';
                const text = document.createElement('span');
                text.className = 'ptext';
                text.textContent = p.message;
                const where = document.createElement('span');
                where.className = 'detail';
                where.textContent = `${p.line + 1}:${p.column + 1}${p.code ? '  ' + p.code : ''}`;
                row.append(mark, text, where);
                row.onclick = () => void open(p.uri, p.line, p.column);
                rows.push(row);
            }
        }
        list.replaceChildren(...rows);
    }

    async function refresh() {
        const mine = ++ticket;
        summary.textContent = 'Checking the project…';
        try {
            const found = await deps.lsp.request('pymagic/problems', { leastSevere: Number(level.value) });
            if (mine !== ticket) return;
            problems = found ?? [];
            paint();
        } catch (e) {
            summary.textContent = String(e.message ?? e);
        }
    }

    tab.onclick = () => { showTab('problems'); void refresh(); };
    $('problems-refresh').onclick = () => void refresh();
    level.onchange = () => void refresh();
    filter.oninput = paint;
    window.addEventListener('pymagic:saved', () => { if ($('problems-tab').classList.contains('active')) void refresh(); });
}

export function setupTodo(deps) {
    const list = $('todo-list');
    const summary = $('todo-summary');
    const filter = $('todo-filter');
    const tab = $('t-todo');
    let todos = [];
    let ticket = 0;

    function paint() {
        const needle = filter.value.trim().toLowerCase();
        const shown = todos.filter((t) => !needle || `${t.tag} ${t.text} ${deps.relOfUri(t.uri) ?? t.uri}`.toLowerCase().includes(needle));
        summary.textContent = todos.length === 0 ? 'No TODO, FIXME, XXX, HACK or BUG comments.' : `${shown.length} of ${todos.length} comments`;
        tab.dataset.count = todos.length > 0 ? String(todos.length) : '';
        list.replaceChildren(...shown.map((t) => {
            const row = document.createElement('div');
            row.className = 'prob';
            const tag = document.createElement('span');
            tag.className = `todo-tag tag-${t.tag.toLowerCase()}`;
            tag.textContent = t.tag;
            const text = document.createElement('span');
            text.className = 'ptext';
            text.textContent = t.text;
            const where = document.createElement('span');
            where.className = 'detail';
            where.textContent = `${deps.relOfUri(t.uri) ?? t.uri}:${t.line + 1}`;
            row.append(tag, text, where);
            row.onclick = async () => {
                const rel = deps.relOfUri(t.uri);
                if (!rel) {
                    toast('That place is outside the project.', true);
                    return;
                }
                await deps.openFile(rel, { startLineNumber: t.line + 1, startColumn: 1, endLineNumber: t.line + 1, endColumn: 1 });
            };
            return row;
        }));
    }

    async function refresh() {
        const mine = ++ticket;
        try {
            const found = await deps.lsp.request('pymagic/todos', {});
            if (mine !== ticket) return;
            todos = found ?? [];
            paint();
        } catch (e) {
            summary.textContent = String(e.message ?? e);
        }
    }

    tab.onclick = () => { showTab('todo'); void refresh(); };
    $('todo-refresh').onclick = () => void refresh();
    filter.oninput = paint;
    window.addEventListener('pymagic:saved', () => { if ($('todo-tab').classList.contains('active')) void refresh(); });
}

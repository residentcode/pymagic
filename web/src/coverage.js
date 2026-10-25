// The Coverage panel and the marks it puts next to the lines of the open file.
import { showTab } from './panel';
import { toast } from './toast';

const $ = (id) => document.getElementById(id);

export function setupCoverage(deps) {
    const { editor } = deps;
    const list = $('cov-list');
    const summary = $('cov-summary');
    const filter = $('cov-filter');
    let data = null;           // { when, files: { rel: { executable, executed } } }
    let decorations = [];
    let show = true;

    function percent(f) {
        return f.executable.length === 0 ? 100 : (100 * f.executed.length) / f.executable.length;
    }

    function totals() {
        let total = 0, hit = 0;
        for (const f of Object.values(data.files)) { total += f.executable.length; hit += f.executed.length; }
        return { total, hit, percent: total === 0 ? 100 : (100 * hit) / total };
    }

    function mark() {
        const model = editor.getModel();
        const rel = model && deps.relOf(model);
        const file = data && show && rel ? data.files[rel] : null;
        if (!file || !model) {
            decorations = editor.deltaDecorations(decorations, []);
            return;
        }
        const ran = new Set(file.executed);
        const next = file.executable
            .filter((n) => n <= model.getLineCount())
            .map((n) => ({
                range: { startLineNumber: n, startColumn: 1, endLineNumber: n, endColumn: 1 },
                options: { isWholeLine: true, linesDecorationsClassName: ran.has(n) ? 'cov-hit' : 'cov-miss', className: ran.has(n) ? '' : 'cov-miss-line' },
            }));
        decorations = editor.deltaDecorations(decorations, next);
    }

    function paint() {
        if (!data) {
            summary.textContent = 'No coverage yet. Run the tests with coverage (🧪 Tests → ▶ Coverage).';
            list.replaceChildren();
            return;
        }
        const t = totals();
        const when = data.when ? new Date(data.when).toLocaleTimeString() : '';
        summary.textContent = `${t.percent.toFixed(1)}% of ${t.total} lines (${t.hit} ran) in ${Object.keys(data.files).length} files${when ? ', ' + when : ''}`;
        const needle = filter.value.trim().toLowerCase();
        const rows = Object.entries(data.files)
            .filter(([rel, f]) => f.executable.length > 0 && (!needle || rel.toLowerCase().includes(needle)))
            .sort((a, b) => percent(a[1]) - percent(b[1]) || a[0].localeCompare(b[0]))
            .map(([rel, f]) => {
                const row = document.createElement('div');
                row.className = 'cov-row';
                const pct = percent(f);
                const bar = document.createElement('span');
                bar.className = 'cov-bar';
                const fill = document.createElement('span');
                fill.style.width = `${pct}%`;
                fill.className = pct >= 80 ? 'good' : pct >= 50 ? 'fair' : 'poor';
                bar.append(fill);
                const name = document.createElement('span');
                name.className = 'cov-name';
                name.textContent = rel;
                const number = document.createElement('span');
                number.className = 'detail';
                number.textContent = `${pct.toFixed(0)}%  (${f.executed.length}/${f.executable.length})`;
                row.append(bar, name, number);
                row.onclick = async () => {
                    const first = f.executable.find((n) => !f.executed.includes(n)) ?? 1;
                    await deps.openFile(rel, { startLineNumber: first, startColumn: 1, endLineNumber: first, endColumn: 1 });
                    mark();
                };
                return row;
            });
        list.replaceChildren(...rows);
    }

    async function load(announce) {
        try {
            const res = await fetch('/api/coverage');
            if (!res.ok) { data = null; paint(); mark(); return; }
            data = await res.json();
            paint();
            mark();
            if (announce) {
                showTab('coverage');
                toast(`Coverage: ${totals().percent.toFixed(1)}% of the lines ran.`);
            }
        } catch (e) {
            summary.textContent = String(e.message ?? e);
        }
    }

    $('t-coverage').onclick = () => { showTab('coverage'); void load(false); };
    $('cov-refresh').onclick = () => void load(false);
    $('cov-show').onchange = (e) => { show = e.target.checked; mark(); };
    $('cov-clear').onclick = () => { data = null; paint(); mark(); };
    filter.oninput = paint;
    editor.onDidChangeModel(mark);
    window.addEventListener('pymagic:coverage', () => void load(true));
    paint();
}

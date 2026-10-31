// The Coverage panel and the marks it puts next to the lines of the open file: the suites (each coverage run kept) shown one at a
// time or several merged (Ctrl+Alt+F6), the line popup with its branches, the next / previous uncovered line, the percentages in
// the project tree, and the HTML report.
import * as monaco from 'monaco-editor';
import { showTab } from './panel';
import { toast } from './toast';
import { openMenu } from './menu';
import { mergeSuites, percentOf, countsOf, treeTotals, lineState, lineInfo, nextUncovered, reportHtml } from './coverage-model';

const $ = (id) => document.getElementById(id);

export function setupCoverage(deps) {
    const { editor } = deps;
    const list = $('cov-list');
    const summary = $('cov-summary');
    const filter = $('cov-filter');
    let data = null;           // { when, label, files: { rel: { executable, executed, branches? } } }: the suites shown, merged
    let suites = [];           // [{ name, label, when, percent }], the newest first
    let chosen = new Set();    // the names of the suites shown
    const loaded = new Map();  // suite name -> its data
    const editors = new Map(); // every editor that shows the marks (the sides of a split too), with its decorations
    let show = true;
    let popup = null;

    const fileOf = (ed) => {
        const model = ed.getModel();
        const rel = model && deps.relOf(model);
        return data && rel ? data.files[rel] ?? null : null;
    };

    function totals() {
        const t = treeTotals(data)[''];
        return t ?? { lines: 0, hit: 0, branches: 0, taken: 0, percent: 100 };
    }

    function mark() {
        for (const ed of editors.keys()) markIn(ed);
        // kept on window as well: the project tree may start after the first run is read
        window.pymagicCoverageTree = data && show ? treeTotals(data) : null;
        window.dispatchEvent(new CustomEvent('pymagic:coverageTree', { detail: window.pymagicCoverageTree }));
    }

    function markIn(ed) {
        const model = ed.getModel();
        const file = show ? fileOf(ed) : null;
        const decorations = editors.get(ed);
        if (!file || !model) {
            decorations.clear();
            return;
        }
        const next = file.executable
            .filter((n) => n <= model.getLineCount())
            .map((n) => {
                const state = lineState(file, n);
                return {
                    range: { startLineNumber: n, startColumn: 1, endLineNumber: n, endColumn: 1 },
                    options: { isWholeLine: true, linesDecorationsClassName: `cov-${state}`, className: state === 'hit' ? '' : `cov-${state}-line` },
                };
            });
        decorations.set(next);
    }

    function closePopup() {
        popup?.remove();
        popup = null;
    }

    function goTo(ed, line) {
        ed.setPosition({ lineNumber: line, column: 1 });
        ed.revealLineInCenterIfOutsideViewport(line);
        ed.focus();
    }

    /** The next (1) or previous (-1) line that did not run or did not take every way of its branch; says so when there is none. */
    function step(ed, dir) {
        const file = fileOf(ed);
        if (!file) { toast('No coverage for this file. Run the tests with coverage first.'); return null; }
        const line = nextUncovered(file, ed.getPosition()?.lineNumber ?? 1, dir);
        if (line == null) { toast(dir > 0 ? 'No uncovered line below.' : 'No uncovered line above.'); return null; }
        goTo(ed, line);
        return line;
    }

    /** The popup of a line's mark: whether it ran, its branches, and the buttons to the previous and next uncovered lines. */
    function openPopup(ed, line, x, y) {
        closePopup();
        const file = fileOf(ed);
        const info = file && lineInfo(file, line);
        if (!info) return;
        popup = document.createElement('div');
        popup.className = 'git-popup cov-popup';
        const text = document.createElement('div');
        text.textContent = `Line ${line}: ${info}`;
        const suite = document.createElement('div');
        suite.className = 'meta';
        suite.textContent = chosen.size > 1 ? `${chosen.size} runs together` : data?.label ? `Run: ${data.label}` : '';
        const prev = document.createElement('button');
        prev.textContent = '↑ Previous uncovered';
        prev.title = 'Alt+Shift+F6';
        prev.onclick = () => { const l = step(ed, -1); closePopup(); if (l) openPopupAtLine(ed, l); };
        const next = document.createElement('button');
        next.textContent = '↓ Next uncovered';
        next.title = 'Alt+F6';
        next.onclick = () => { const l = step(ed, 1); closePopup(); if (l) openPopupAtLine(ed, l); };
        const hide = document.createElement('button');
        hide.textContent = 'Hide marks';
        hide.onclick = () => { closePopup(); $('cov-show').checked = false; show = false; mark(); };
        popup.append(text, suite, prev, next, hide);
        popup.style.left = `${Math.min(x + 8, window.innerWidth - 380)}px`;
        popup.style.top = `${Math.min(y, window.innerHeight - 120)}px`;
        document.body.append(popup);
    }

    function openPopupAtLine(ed, line) {
        const top = ed.getTopForLineNumber(line) - ed.getScrollTop();
        const box = ed.getDomNode().getBoundingClientRect();
        openPopup(ed, line, box.left + 40, box.top + top + 18);
    }

    /** Shows the marks in another editor (a side of the split) for the file it shows. */
    function attach(ed) {
        editors.set(ed, ed.createDecorationsCollection());
        ed.onDidChangeModel(() => markIn(ed));
        ed.onDidDispose(() => editors.delete(ed));
        ed.onMouseDown((e) => {
            const el = e.target.element;
            if (e.target.type !== monaco.editor.MouseTargetType.GUTTER_LINE_DECORATIONS || !e.target.position || !el || ![...el.classList].some((c) => /^cov-(hit|part|miss)$/.test(c)))
                return;
            const ev = e.event.browserEvent;
            openPopup(ed, e.target.position.lineNumber, ev.clientX, ev.clientY);
        });
        ed.addAction({ id: 'pymagic.coverage.next', label: 'Coverage: Next Uncovered Line', keybindings: [monaco.KeyMod.Alt | monaco.KeyCode.F6], run: () => { step(ed, 1); } });
        ed.addAction({ id: 'pymagic.coverage.previous', label: 'Coverage: Previous Uncovered Line', keybindings: [monaco.KeyMod.Alt | monaco.KeyMod.Shift | monaco.KeyCode.F6], run: () => { step(ed, -1); } });
        ed.addAction({ id: 'pymagic.coverage.suites', label: 'Coverage: Show Coverage Data…', keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Alt | monaco.KeyCode.F6], run: () => { showTab('coverage'); void chooseSuites($('cov-suites')); } });
        markIn(ed);
    }

    function paint() {
        if (!data) {
            summary.textContent = 'No coverage yet. Run the tests with coverage (🧪 Tests → ▶ Coverage, or a test\'s menu).';
            list.replaceChildren();
            return;
        }
        const t = totals();
        const when = data.when ? new Date(data.when).toLocaleTimeString() : '';
        const which = chosen.size > 1 ? `${chosen.size} runs together` : data.label ?? '';
        summary.textContent = `${t.percent.toFixed(1)}% of ${t.lines} lines (${t.hit} ran)${t.branches ? `, ${t.taken} of ${t.branches} branch ways` : ''} in ${Object.values(data.files).filter((f) => f.executable.length > 0).length} files`
            + `${which ? ' — ' + which : ''}${when && chosen.size <= 1 ? ', ' + when : ''}`;
        const needle = filter.value.trim().toLowerCase();
        const rows = Object.entries(data.files)
            .filter(([rel, f]) => f.executable.length > 0 && (!needle || rel.toLowerCase().includes(needle)))
            .sort((a, b) => percentOf(a[1]) - percentOf(b[1]) || a[0].localeCompare(b[0]))
            .map(([rel, f]) => {
                const row = document.createElement('div');
                row.className = 'cov-row';
                const pct = percentOf(f);
                const bar = document.createElement('span');
                bar.className = 'cov-bar';
                const fill = document.createElement('span');
                fill.style.width = `${pct}%`;
                fill.className = pct >= 80 ? 'good' : pct >= 50 ? 'fair' : 'poor';
                bar.append(fill);
                const name = document.createElement('span');
                name.className = 'cov-name';
                name.textContent = rel;
                const c = countsOf(f);
                const number = document.createElement('span');
                number.className = 'detail';
                number.textContent = `${pct.toFixed(0)}%  (${c.hit}/${c.lines}${c.branches ? `, branches ${c.taken}/${c.branches}` : ''})`;
                row.append(bar, name, number);
                row.onclick = async () => {
                    const first = f.executable.find((n) => lineState(f, n) !== 'hit') ?? 1;
                    await deps.openFile(rel, { startLineNumber: first, startColumn: 1, endLineNumber: first, endColumn: 1 });
                    mark();
                };
                return row;
            });
        list.replaceChildren(...rows);
    }

    async function suiteData(name) {
        if (!loaded.has(name)) {
            const res = await fetch('/api/coverage/suite?name=' + encodeURIComponent(name));
            loaded.set(name, res.ok ? await res.json() : null);
        }
        return loaded.get(name);
    }

    /** Shows the chosen suites (merged), or the last run when there are no suites (a run from before suites were kept). */
    async function showChosen() {
        const names = suites.map((s) => s.name).filter((n) => chosen.has(n));
        if (names.length) data = mergeSuites(await Promise.all(names.map(suiteData)));
        else {
            const res = await fetch('/api/coverage');
            data = res.ok ? await res.json() : null;
        }
        closePopup();
        paint();
        mark();
    }

    async function loadSuites() {
        const res = await fetch('/api/coverage/suites');
        suites = res.ok ? await res.json() : [];
        chosen = new Set([...chosen].filter((n) => suites.some((s) => s.name === n)));
        if (chosen.size === 0 && suites.length) chosen.add(suites[0].name);
    }

    async function load(announce) {
        try {
            if (announce) chosen = new Set();   // a new run: show it alone
            await loadSuites();
            await showChosen();
            if (announce && data) {
                showTab('coverage');
                toast(`Coverage: ${totals().percent.toFixed(1)}% of the lines ran.`);
            }
        } catch (e) {
            summary.textContent = String(e.message ?? e);
        }
    }

    /** Show Coverage Data (Ctrl+Alt+F6): each kept run, ticked to show it; several ticked are shown together. */
    async function chooseSuites(anchor) {
        await loadSuites();
        if (suites.length === 0) { toast('No coverage runs are kept yet.'); return; }
        const items = suites.map((s) => ({
            label: `${new Date(s.when).toLocaleString()} — ${s.label} (${s.percent.toFixed(1)}%)`,
            checked: chosen.has(s.name),
            run: () => {
                if (chosen.has(s.name)) chosen.delete(s.name); else chosen.add(s.name);
                if (chosen.size === 0) chosen.add(s.name);
                void showChosen().then(() => chooseSuites(anchor));
            },
        }));
        items.push('-', { label: 'Only the newest run', run: () => { chosen = new Set([suites[0].name]); void showChosen(); } },
            { label: 'All runs together', run: () => { chosen = new Set(suites.map((s) => s.name)); void showChosen(); } },
            { label: 'Forget the runs not shown', run: () => void forgetOthers() });
        openMenu(anchor, items);
    }

    async function forgetOthers() {
        for (const s of suites.filter((x) => !chosen.has(x.name))) {
            await fetch('/api/coverage/suite?name=' + encodeURIComponent(s.name), { method: 'DELETE' });
            loaded.delete(s.name);
        }
        await loadSuites();
        paint();
    }

    /** Export report: htmlcov/index.html in the project (as coverage.py names it), with every file's source, opened in the browser. */
    async function exportReport() {
        if (!data) { toast('No coverage to export yet.'); return; }
        const sources = {};
        for (const rel of Object.keys(data.files).filter((r) => data.files[r].executable.length > 0)) {
            const res = await fetch('/api/file?path=' + encodeURIComponent(rel));
            if (res.ok) sources[rel] = await res.text();
        }
        const title = `Coverage of ${$('project')?.textContent || 'the project'}${chosen.size > 1 ? ` (${chosen.size} runs)` : data.label ? ` — ${data.label}` : ''}`;
        const res = await fetch('/api/file?path=' + encodeURIComponent('htmlcov/index.html'), { method: 'PUT', body: reportHtml(data, sources, title) });
        if (!res.ok) { toast(`The report could not be written: ${await res.text()}`, true); return; }
        toast('Coverage report written to htmlcov/index.html');
        window.open(`${location.origin}/api/coverage/report`, '_blank');
    }

    $('t-coverage').onclick = () => { showTab('coverage'); void load(false); };
    $('cov-refresh').onclick = () => void load(false);
    $('cov-show').onchange = (e) => { show = e.target.checked; mark(); };
    $('cov-clear').onclick = () => { data = null; chosen = new Set(); paint(); mark(); };
    $('cov-suites').onclick = (e) => void chooseSuites(e.currentTarget);
    $('cov-report').onclick = () => void exportReport();
    filter.oninput = paint;
    document.addEventListener('mousedown', (e) => { if (popup && !popup.contains(e.target)) closePopup(); }, true);
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closePopup(); });
    attach(editor);
    window.addEventListener('pymagic:coverage', () => void load(true));
    paint();
    void load(false).catch(() => { });
    return { attach };
}

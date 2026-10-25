// The Profiler panel: the functions of the last profiled run, hottest first. A click opens the function in the editor
// and lists who called it.
import { showTab } from './panel';
import { toast } from './toast';
import { formatTime, hottest, labelOf, locationOf, rowsOf, sameFunction } from './profile-model';

const $ = (id) => document.getElementById(id);

export function setupProfiler(deps) {
    const list = $('prof-list');
    const summary = $('prof-summary');
    const filter = $('prof-filter');
    const project = $('prof-project');
    const sort = $('prof-sort');
    let data = null;           // the answer of /api/profile
    let opened = null;         // the row whose callers are listed

    function cell(text, cls, title) {
        const c = document.createElement('span');
        c.className = cls;
        c.textContent = text;
        if (title) c.title = title;
        return c;
    }

    function openAt(fn) {
        if (!fn.rel) return;
        void deps.openFile(fn.rel, { startLineNumber: fn.line, startColumn: 1, endLineNumber: fn.line, endColumn: 1 });
    }

    function callerRows(fn) {
        const box = document.createElement('div');
        box.className = 'prof-callers';
        if (!fn.callers.length) {
            box.append(cell('not called from profiled code', 'detail'));
            return box;
        }
        box.append(cell(`called from (${fn.callers.length}):`, 'detail'));
        for (const c of fn.callers) {
            const row = document.createElement('div');
            row.className = 'prof-caller';
            row.append(cell(labelOf(c), 'prof-name'), cell(locationOf(c), 'detail'), cell(`${c.calls}×`, 'prof-num'),
                cell(formatTime(c.own), 'prof-num'), cell(formatTime(c.cumulative), 'prof-num'));
            row.onclick = (e) => {
                e.stopPropagation();
                const target = data.functions.find((f) => sameFunction(f, c));
                if (target) { opened = target; paint(); }
                openAt(c);
            };
            box.append(row);
        }
        return box;
    }

    function paint() {
        if (!data) {
            summary.textContent = 'No profile yet. Open a script and press ⏱ Profile.';
            list.replaceChildren();
            return;
        }
        const when = data.when ? new Date(data.when).toLocaleTimeString() : '';
        const hot = hottest(data);
        summary.textContent = `${data.rel ?? data.script}: ${formatTime(data.total)} in ${data.calls} calls`
            + (hot ? `, most time in ${hot.name} (${formatTime(hot.own)} itself)` : '') + (when ? `, ${when}` : '')
            + (data.exit ? `, exit code ${data.exit}` : '');
        const rows = rowsOf(data, { projectOnly: project.checked, filter: filter.value, sort: sort.value });
        const head = document.createElement('div');
        head.className = 'prof-row prof-head';
        head.append(cell('Function', 'prof-name'), cell('Where', 'detail'), cell('Calls', 'prof-num'), cell('Itself', 'prof-num'), cell('With callees', 'prof-num'), cell('', 'prof-bar'));
        const items = [head];
        for (const fn of rows.slice(0, 600)) {
            const row = document.createElement('div');
            row.className = 'prof-row' + (opened && sameFunction(opened, fn) ? ' open' : '');
            const bar = document.createElement('span');
            bar.className = 'prof-bar';
            const self = document.createElement('span');
            self.className = 'self';
            self.style.width = `${Math.min(100, fn.selfPercent)}%`;
            const rest = document.createElement('span');
            rest.className = 'rest';
            rest.style.width = `${Math.max(0, Math.min(100, fn.cumulativePercent) - Math.min(100, fn.selfPercent))}%`;
            bar.append(self, rest);
            bar.title = `${fn.selfPercent.toFixed(1)}% itself, ${fn.cumulativePercent.toFixed(1)}% with callees`;
            row.append(cell(labelOf(fn), 'prof-name', fn.name), cell(locationOf(fn), 'detail', fn.file), cell(String(fn.calls), 'prof-num'),
                cell(formatTime(fn.own), 'prof-num'), cell(formatTime(fn.cumulative), 'prof-num'), bar);
            row.onclick = () => {
                opened = opened && sameFunction(opened, fn) ? null : fn;
                openAt(fn);
                paint();
            };
            items.push(row);
            if (opened && sameFunction(opened, fn)) items.push(callerRows(fn));
        }
        if (rows.length > 600) items.push(cell(`… and ${rows.length - 600} more (use the filter)`, 'detail'));
        if (!rows.length) items.push(cell('Nothing matches. Uncheck "project code only" or clear the filter.', 'detail'));
        list.replaceChildren(...items);
    }

    async function load(announce) {
        try {
            const res = await fetch('/api/profile');
            if (!res.ok) { data = null; paint(); return; }
            data = await res.json();
            opened = null;
            paint();
            if (announce) {
                showTab('profiler');
                toast(`Profile: ${formatTime(data.total)} in ${data.calls} calls.`);
            }
        } catch (e) {
            summary.textContent = String(e.message ?? e);
        }
    }

    $('t-profiler').onclick = () => { showTab('profiler'); void load(false); };
    $('prof-refresh').onclick = () => void load(false);
    $('prof-clear').onclick = () => { data = null; opened = null; paint(); };
    filter.oninput = paint;
    project.onchange = paint;
    sort.onchange = paint;
    window.addEventListener('pymagic:profile', () => void load(true));
    paint();
}

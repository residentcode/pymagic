// The Profiler panel: the functions of the last profiled run, hottest first. A click opens the function in the editor
// and lists who called it.
import { showTab } from './panel';
import { toast } from './toast';
import { callGraph, formatTime, hottest, labelOf, locationOf, rowsOf, sameFunction, wholeGraph } from './profile-model';

const $ = (id) => document.getElementById(id);

export function setupProfiler(deps) {
    const list = $('prof-list');
    const summary = $('prof-summary');
    const filter = $('prof-filter');
    const project = $('prof-project');
    const sort = $('prof-sort');
    let data = null;           // the answer of /api/profile
    let opened = null;         // the row whose callers and callees are listed (and drawn in the graph)
    let graph = false;         // the call graph instead of the list
    let whole = false;         // the graph of the whole program, not one function's neighbours
    let picked = null;         // the box chosen in the whole graph: its arrows stand out
    const snapshotBox = $('prof-snapshot');

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

    /** The functions that called it and those it called, side by side; a click goes to one. */
    function linkRows(fn) {
        const both = document.createElement('div');
        both.className = 'prof-links';
        both.append(callerRows(fn, fn.callers ?? [], 'called from', 'not called from profiled code'),
            callerRows(fn, fn.callees ?? [], 'calls', 'calls nothing that was profiled'));
        return both;
    }
    function callerRows(fn, links, title, none) {
        const box = document.createElement('div');
        box.className = 'prof-callers';
        if (!links.length) {
            box.append(cell(none, 'detail'));
            return box;
        }
        box.append(cell(`${title} (${links.length}):`, 'detail'));
        for (const c of links) {
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

    const SVG = 'http://www.w3.org/2000/svg';
    const svg = (tag, attrs, text) => {
        const el = document.createElementNS(SVG, tag);
        for (const [k, v] of Object.entries(attrs))
            el.setAttribute(k, String(v));
        if (text !== undefined)
            el.textContent = text;
        return el;
    };
    /** Above the graph: the neighbours of one function, or the whole program. */
    function graphSwitch() {
        const bar = document.createElement('div');
        bar.className = 'prof-graph-switch';
        const one = document.createElement('button');
        one.textContent = 'Around one function';
        one.title = 'Who called the chosen function and what it called';
        const all = document.createElement('button');
        all.textContent = 'The whole program';
        all.title = 'Every function that took time, the entry points on the left (the 60 most expensive; "project code only" leaves out libraries)';
        one.classList.toggle('active', !whole);
        all.classList.toggle('active', whole);
        one.onclick = () => { whole = false; if (picked) opened = picked; paint(); };
        all.onclick = () => { whole = true; picked = opened; paint(); };
        bar.append(one, all);
        return bar;
    }
    function arrowDefs(pic) {
        const defs = svg('defs', {});
        const marker = svg('marker', { id: 'prof-arrow', viewBox: '0 0 10 10', refX: 10, refY: 5, markerWidth: 8, markerHeight: 8, markerUnits: 'userSpaceOnUse', orient: 'auto' });   // the same size whatever the line's width
        marker.append(svg('path', { d: 'M 0 0 L 10 5 L 0 10 z', class: 'prof-arrowhead' }));
        defs.append(marker);
        pic.append(defs);
    }

    /**
     * The whole program's graph: a box for each function that took time, in columns by how deep it is called. A click on a box makes its
     * arrows stand out (and is the function "Around one function" shows); a double click opens it.
     */
    function paintWhole() {
        const g = wholeGraph(data, { projectOnly: project.checked });
        if (!g.boxes.length) {
            list.replaceChildren(graphSwitch(), cell('Nothing to draw. Uncheck "project code only".', 'detail'));
            return;
        }
        const pad = 16, total = data.total > 0 ? data.total : 1;
        const top = pad + 40;   // room above the boxes for the arrows that go back
        const pic = svg('svg', { width: g.width + pad * 2, height: g.height + top + pad + 18, class: 'prof-graph' });
        arrowDefs(pic);
        const isPicked = (fn) => !!picked && sameFunction(picked, fn);
        for (const e of g.edges) {
            const lit = isPicked(e.from.fn) || isPicked(e.to.fn);
            const cls = 'prof-edge' + (e.back ? ' back' : '') + (picked ? (lit ? ' lit' : ' faded') : '');
            const width = 1 + Math.min(5, (5 * e.time) / total);
            let d, lx, ly;
            if (e.back) {
                // a call back to the left (recursion through others): over the boxes, from the top of one to the top of the other
                const x1 = pad + e.from.x + e.from.width / 2, y1 = top + e.from.y, x2 = pad + e.to.x + e.to.width / 2 + 12, y2 = top + e.to.y;
                const lift = Math.min(y1, y2) - 30;
                d = `M ${x1} ${y1} C ${x1} ${lift}, ${x2} ${lift}, ${x2} ${y2}`;
                lx = (x1 + x2) / 2;
                ly = lift + 4;
            } else {
                const x1 = pad + e.from.x + e.from.width, y1 = top + e.from.y + e.from.height / 2, x2 = pad + e.to.x, y2 = top + e.to.y + e.to.height / 2;
                const mid = (x1 + x2) / 2;
                d = `M ${x1} ${y1} C ${mid} ${y1}, ${mid} ${y2}, ${x2} ${y2}`;
                lx = x2 - 6;
                ly = y2 - 5;
            }
            pic.append(svg('path', { d, class: cls, 'stroke-width': width, 'marker-end': 'url(#prof-arrow)' }));
            // the counts on the arrows of the chosen box, or on every arrow while there are few
            if (lit || (!picked && g.edges.length <= 40))
                pic.append(svg('text', { x: lx, y: ly, class: 'prof-edge-label', 'text-anchor': e.back ? 'middle' : 'end' }, `${e.calls}× · ${formatTime(e.time)}`));
        }
        for (const b of g.boxes) {
            const chosen = isPicked(b.fn);
            const group = svg('g', { class: 'prof-node' + (chosen ? ' center' : ''), transform: `translate(${pad + b.x},${top + b.y})` });
            group.append(svg('rect', { width: b.width, height: b.height, rx: 5 }));
            group.append(svg('rect', { width: Math.max(2, b.width * Math.min(1, b.fn.cumulative / total)), height: 3, y: b.height - 3, class: 'prof-share' }));
            group.append(svg('text', { x: 8, y: 14, class: 'prof-node-name' }, (labelOf(b.fn) + (b.self ? ' ↻' : '')).slice(0, 28)));
            group.append(svg('text', { x: 8, y: 27, class: 'prof-node-detail' }, `${formatTime(b.fn.cumulative)} · ${formatTime(b.fn.own)} itself`.slice(0, 36)));
            group.append(svg('title', {}, `${labelOf(b.fn)}, ${locationOf(b.fn)}: ${formatTime(b.fn.cumulative)} with callees, ${formatTime(b.fn.own)} itself, ${b.fn.calls} calls`
                + (b.self ? `; calls itself ${b.self}×` : '') + ' (click to pick it; double-click to open it)'));
            group.onclick = () => { picked = chosen ? null : b.fn; opened = picked ?? opened; paint(); };
            group.ondblclick = () => openAt(b.fn);
            pic.append(group);
        }
        if (g.hidden)
            pic.append(svg('text', { x: pad, y: top + g.height + pad + 10, class: 'prof-edge-label' }, `… ${g.hidden} cheaper functions are left out (the list has them all)`));
        const holder = document.createElement('div');
        holder.className = 'prof-graph-holder';
        holder.append(pic);
        list.replaceChildren(graphSwitch(), holder);
    }

    /** The call graph around the opened function (else the hottest of the project): a click on a box goes there. */
    function paintGraph() {
        if (whole) {
            paintWhole();
            return;
        }
        const center = opened ?? hottest(data) ?? data.functions[0];
        if (!center) {
            list.replaceChildren(cell('Nothing was profiled.', 'detail'));
            return;
        }
        const g = callGraph(center);
        const pad = 16, total = data.total > 0 ? data.total : 1;
        const pic = svg('svg', { width: g.width + pad * 2, height: g.height + pad * 2 + 18, class: 'prof-graph' });
        arrowDefs(pic);
        for (const e of g.edges) {
            const x1 = pad + e.from.x + e.from.width, y1 = pad + e.from.y + e.from.height / 2, x2 = pad + e.to.x, y2 = pad + e.to.y + e.to.height / 2;
            const mid = (x1 + x2) / 2;
            pic.append(svg('path', { d: `M ${x1} ${y1} C ${mid} ${y1}, ${mid} ${y2}, ${x2} ${y2}`, class: 'prof-edge', 'stroke-width': 1 + Math.min(5, (5 * e.time) / total), 'marker-end': 'url(#prof-arrow)' }));
            // the label above the line's end at the box it points to, where the lines of one side are apart
            pic.append(svg('text', { x: x2 - 6, y: y2 - 5, class: 'prof-edge-label', 'text-anchor': 'end' }, `${e.calls}× · ${formatTime(e.time)}`));
        }
        for (const b of g.boxes) {
            const known = data.functions.find((f) => sameFunction(f, b.fn));
            const group = svg('g', { class: 'prof-node' + (b.fn === center ? ' center' : '') + (known ? '' : ' unknown'), transform: `translate(${pad + b.x},${pad + b.y})` });
            const share = (known ?? b.fn).cumulative / total;
            group.append(svg('rect', { width: b.width, height: b.height, rx: 5 }));
            group.append(svg('rect', { width: Math.max(2, b.width * Math.min(1, share)), height: 3, y: b.height - 3, class: 'prof-share' }));
            group.append(svg('text', { x: 8, y: 14, class: 'prof-node-name' }, labelOf(b.fn).slice(0, 30)));
            group.append(svg('text', { x: 8, y: 27, class: 'prof-node-detail' }, `${locationOf(b.fn)} · ${formatTime((known ?? b.fn).cumulative)}`.slice(0, 40)));
            const title = svg('title', {}, `${labelOf(b.fn)}, ${locationOf(b.fn)}: ${formatTime((known ?? b.fn).cumulative)} with callees${b.fn === center ? '' : ' (click to go there; double-click to open it)'}`);
            group.append(title);
            group.onclick = () => { if (known && b.fn !== center) { opened = known; paint(); } };
            group.ondblclick = () => openAt(b.fn);
            pic.append(group);
        }
        const more = [g.more.callers ? `${g.more.callers} more callers` : '', g.more.callees ? `${g.more.callees} more callees` : ''].filter(Boolean).join(', ');
        if (more)
            pic.append(svg('text', { x: pad, y: g.height + pad * 2 + 10, class: 'prof-edge-label' }, `… ${more} (the list has them all)`));
        const holder = document.createElement('div');
        holder.className = 'prof-graph-holder';
        holder.append(pic);
        list.replaceChildren(graphSwitch(), holder);
    }

    function paint() {
        $('prof-graph').classList.toggle('active', graph);
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
        if (data.engine === 'yappi')
            summary.textContent += ' · yappi, wall time of every thread';
        if (data.loaded)
            summary.textContent += ' · opened from a .pstats file';
        if (graph) {
            paintGraph();
            return;
        }
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
            if (opened && sameFunction(opened, fn)) items.push(linkRows(fn));
        }
        if (rows.length > 600) items.push(cell(`… and ${rows.length - 600} more (use the filter)`, 'detail'));
        if (!rows.length) items.push(cell('Nothing matches. Uncheck "project code only" or clear the filter.', 'detail'));
        list.replaceChildren(...items);
    }

    async function loadSnapshots() {
        const list = await fetch('/api/profile/snapshots').then((r) => (r.ok ? r.json() : [])).catch(() => []);
        const chosen = snapshotBox.value;
        snapshotBox.replaceChildren(new Option('The last run', ''), ...list.map((s) => new Option(`${new Date(s.when).toLocaleString()} · ${s.label}`, s.name)));
        snapshotBox.value = list.some((s) => s.name === chosen) ? chosen : '';
    }
    async function load(announce, snapshot = snapshotBox.value) {
        void loadSnapshots();
        try {
            const res = await fetch('/api/profile' + (snapshot ? '?snapshot=' + encodeURIComponent(snapshot) : ''));
            if (!res.ok) { data = null; paint(); return; }
            data = await res.json();
            opened = null;
            picked = null;
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
    snapshotBox.onchange = () => void load(false, snapshotBox.value);
    $('prof-graph').onclick = () => { graph = !graph; paint(); };
    $('prof-open').onclick = async () => {
        const path = await deps.askText('Open a .pstats file', 'profile.pstats', 'A file of the project, written by cProfile (python -m cProfile -o), yappi or PyCharm');
        if (!path?.trim())
            return;
        const r = await fetch('/api/profile/open', { method: 'POST', body: JSON.stringify({ path: path.trim() }) }).then((x) => x.json()).catch((e) => ({ error: String(e) }));
        if (r.error) {
            toast(r.error);
            return;
        }
        await loadSnapshots();
        snapshotBox.value = r.snapshot;
        await load(true, r.snapshot);
    };
    $('prof-clear').onclick = () => { data = null; opened = null; picked = null; paint(); };
    filter.oninput = paint;
    project.onchange = paint;
    sort.onchange = paint;
    window.addEventListener('pymagic:profile', () => { snapshotBox.value = ''; void load(true, ''); });
    paint();
}

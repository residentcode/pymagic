// The Services window (a tab of the dock): every program, console and terminal that is running, in one place, each one shown, restarted or stopped.
// The modules that start things say what runs with a `services()` function: a list of {id, group, name, state, started?, detail?, done?, show, stop?, restart?, groupActions?}.
// `done` is something that has ended but stays listed (a member of a compound) so it can be started again: it is not counted. `groupActions` ({label, title, run}) are buttons of the group's heading,
// `actions` (the same) more buttons of the row (Debug, Profile). `output()` gives the text of its own output: a row that has one shows it beside the list when it is clicked.
import { showTab } from './panel';
import { toast } from './toast';
import { elapsed } from './services-model.js';

const $ = (id) => document.getElementById(id);

export function setupServices({ providers }) {
    let timer = 0;
    const gather = () => providers.flatMap((p) => { try { return p() ?? []; } catch { return []; } });
    const active = () => $('services-tab').classList.contains('active');
    // the row whose output is shown beside the list (null: none)
    let chosen = null;
    function paintOutput(found) {
        const pane = $('services-detail');
        const s = chosen && found.find((x) => x.id === chosen);
        if (!s?.output) { pane.hidden = true; if (chosen && !s) chosen = null; return; }
        pane.hidden = false;
        $('services-detail-title').textContent = `${s.name}: output`;
        const out = $('services-output');
        const text = String(s.output() ?? '');
        if (out.textContent !== text) {
            const atEnd = out.scrollTop + out.clientHeight >= out.scrollHeight - 4;
            out.textContent = text;
            if (atEnd) out.scrollTop = out.scrollHeight;
        }
    }
    /** Shows the output of a row beside the list (the Services window open on it). */
    function choose(id) {
        chosen = id;
        showTab('services');
        draw();
    }

    function draw() {
        const list = $('services-list');
        const found = gather();
        const badge = $('services-count');
        const counted = found.filter((s) => !s.done).length;
        badge.textContent = counted ? String(counted) : '';
        badge.hidden = !counted;
        if (!active()) return;
        paintOutput(found);
        list.replaceChildren();
        if (!found.length) {
            const none = document.createElement('div');
            none.className = 'side-empty';
            none.textContent = 'Nothing is running. A program started with Run, the members of a compound, the Python console, the notebook kernels and the terminals show up here.';
            list.append(none);
            return;
        }
        const now = Date.now();
        let group = null;
        for (const s of found) {
            if (s.group !== group) {
                group = s.group;
                const head = document.createElement('div');
                head.className = 'svc-group';
                const title = document.createElement('span');
                title.textContent = group;
                head.append(title);
                for (const a of s.groupActions ?? []) {
                    const b = document.createElement('button');
                    b.className = 'mini';
                    b.textContent = a.label;
                    b.title = a.title;
                    b.onclick = () => { void Promise.resolve(a.run()).then(() => setTimeout(draw, 250)); };
                    head.append(b);
                }
                list.append(head);
            }
            const row = document.createElement('div');
            row.className = 'svc-row' + (s.id === chosen && s.output ? ' chosen' : '');
            row.dataset.id = s.id;
            const dot = document.createElement('span');
            dot.className = 'svc-dot ' + s.state;
            dot.title = s.state;
            const name = document.createElement('span');
            name.className = 'svc-name';
            name.textContent = s.name;
            const state = document.createElement('span');
            state.className = 'svc-state';
            state.textContent = s.state + (s.started ? ` · ${elapsed(now - s.started)}` : '') + (s.detail ? ` · ${s.detail}` : '');
            row.append(dot, name, state);
            const button = (label, title, run, cls) => {
                const b = document.createElement('button');
                b.className = 'mini' + (cls ? ' ' + cls : '');
                b.textContent = label;
                b.title = title;
                b.onclick = (e) => { e.stopPropagation(); void Promise.resolve(run()).then(() => setTimeout(draw, 250)); };
                row.append(b);
            };
            button('Show', 'Open where it runs', s.show);
            for (const a of s.actions ?? []) button(a.label, a.title, a.run);
            if (s.restart) button('Restart', 'Stop it and start it again as it was', s.restart);
            if (s.stop) button('Stop', 'End it', s.stop, 'danger');
            row.onclick = () => { if (s.output) choose(s.id); else s.show(); };
            list.append(row);
        }
    }

    $('t-services').onclick = () => { showTab('services'); draw(); };
    $('svc-refresh').onclick = draw;
    $('svc-detail-close').onclick = () => { chosen = null; draw(); };
    // a module asks for one of its rows to be shown with its output (the Show of a compound's member)
    window.addEventListener('pymagic:showService', (e) => choose(e.detail));
    $('svc-stop-all').onclick = async () => {
        const found = gather().filter((s) => s.stop);
        if (!found.length) { toast('Nothing to stop.'); return; }
        if (!window.confirm(`Stop ${found.length} running ${found.length === 1 ? 'thing' : 'things'}?`)) return;
        for (const s of found) await Promise.resolve(s.stop());
        setTimeout(draw, 300);
    };
    // the list follows what runs: a second apart while the tab is open, slower for the count in the tab otherwise
    const tick = () => { draw(); timer = window.setTimeout(tick, active() ? 1000 : 3000); };
    tick();
    return { draw, gather, choose, stop: () => clearTimeout(timer) };
}

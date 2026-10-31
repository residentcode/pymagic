// The live preview of a template (a tab of the dock): the page of the running server that renders the open template, found through the
// view that renders it and that view's route, shown in a frame through the engine's /preview/ relay (Program.cs). The path can be edited
// (URL parameters), with back, forward and refresh; it is shown again after each save.
import { showTab } from './panel';
import { toast } from './toast';
import { fillPath } from './routes-model.js';

const $ = (id) => document.getElementById(id);

export function setupTemplatePreview(deps) {
    const frame = $('tp-frame');
    const pathBox = $('tp-path');
    const baseBox = $('tp-base');
    try { baseBox.value = localStorage.getItem('pymagic.routesBase') || baseBox.value; } catch { /* the default */ }

    async function useBase() {
        try { localStorage.setItem('pymagic.routesBase', baseBox.value); } catch { /* this time */ }
        const res = await fetch('/api/preview/base', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ url: baseBox.value }) }).catch(() => null);
        if (!res?.ok) toast((await res?.json().catch(() => ({})))?.error ?? 'That address cannot be previewed.', true);
        return !!res?.ok;
    }
    const go = (path) => { frame.src = '/preview/' + path.replace(/^\/+/, ''); pathBox.value = '/' + path.replace(/^\/+/, ''); };

    /** The path of the open template's page: its view's route, the path's parameters filled. */
    async function pathOfTemplate() {
        const model = deps.editor.getModel();
        const uri = model && deps.uriOf(model);
        if (!uri || model.getLanguageId() !== 'django-html') return null;
        const links = (await deps.lsp.request('pymagic/templateLinks', { textDocument: { uri } }).catch(() => null)) ?? [];
        if (!links.length) return null;
        const routes = (await deps.lsp.request('pymagic/routes', {}).catch(() => null)) ?? [];
        for (const l of links) {
            const r = routes.find((x) => x.uri === l.uri && x.line === l.targetLine) ?? routes.find((x) => x.uri === l.uri && l.label.startsWith(x.name));
            if (r) return fillPath(r.path);
        }
        return null;
    }

    async function open() {
        showTab('tpreview');
        if (!(await useBase())) return;
        const path = await pathOfTemplate();
        if (path !== null) go(path);
        else if (!frame.src) {
            go(pathBox.value || '/');
            toast('No route renders this file: type the path to show.', false);
        }
    }

    $('tp-go').onclick = () => void useBase().then((ok) => ok && go(pathBox.value || '/'));
    pathBox.onkeydown = (e) => { if (e.key === 'Enter') $('tp-go').click(); };
    baseBox.onchange = () => void useBase().then((ok) => ok && go(pathBox.value || '/'));
    $('tp-back').onclick = () => { try { frame.contentWindow.history.back(); } catch { /* nothing before */ } };
    $('tp-forward').onclick = () => { try { frame.contentWindow.history.forward(); } catch { /* nothing after */ } };
    $('tp-refresh').onclick = () => { try { frame.contentWindow.location.reload(); } catch { go(pathBox.value || '/'); } };
    $('tp-template').onclick = () => void open();
    // the path shown follows the frame's own navigation
    frame.onload = () => {
        try { pathBox.value = frame.contentWindow.location.pathname.replace(/^\/preview/, '') + frame.contentWindow.location.search; } catch { /* another origin */ }
    };
    // after a save the server's reloader needs a moment to take the change: shown again after one second, and once more at two
    let timers = [];
    window.addEventListener('pymagic:saved', () => {
        if (!$('tpreview-tab').classList.contains('active') || !frame.src) return;
        for (const t of timers) clearTimeout(t);
        timers = [1000, 2200].map((ms) => window.setTimeout(() => $('tp-refresh').click(), ms));
    });
    $('t-tpreview').onclick = () => void open();
    return { open };
}

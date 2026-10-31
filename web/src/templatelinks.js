// Between views and templates: above a line of a view that names a template, a lens to it ("→ shop/list.html"); above a template's first
// line, the views that render it ("rendered by items(), About"). And Create template, the fix of a template name no folder has: the folder is
// chosen from the project's templates folders and the file is made there.
import * as monaco from 'monaco-editor';
import { openMenu } from './menu';
import { toast } from './toast';

export function setupTemplateLinks(deps) {
    const { lsp } = deps;
    const cache = new WeakMap();   // model -> { version, links }

    async function linksOf(model) {
        const cached = cache.get(model);
        if (cached && cached.version === model.getVersionId()) return cached.links;
        const uri = deps.uriOf(model);
        let links = [];
        try { links = uri ? (await lsp.request('pymagic/templateLinks', { textDocument: { uri } })) ?? [] : []; } catch { /* none */ }
        cache.set(model, { version: model.getVersionId(), links });
        return links;
    }

    const go = async (uri, line) => {
        const rel = deps.relOfUri(uri);
        if (rel) await deps.openFile(rel, new monaco.Range(line + 1, 1, line + 1, 1));
    };
    monaco.editor.registerCommand('pymagic.templateLink', (_, links, at) => {
        if (links.length === 1) { void go(links[0].uri, links[0].targetLine); return; }
        openMenu(at ?? { x: 300, y: 200 }, links.map((l) => ({ label: l.label, run: () => void go(l.uri, l.targetLine) })));
    });

    const provider = {
        provideCodeLenses: async (model) => {
            const links = await linksOf(model);
            const byLine = new Map();
            for (const l of links) {
                if (!byLine.has(l.line)) byLine.set(l.line, []);
                byLine.get(l.line).push(l);
            }
            const isTemplate = model.getLanguageId() !== 'python';
            const lenses = [...byLine].map(([line, list]) => ({
                range: new monaco.Range(line + 1, 1, line + 1, 1),
                command: {
                    id: 'pymagic.templateLink',
                    title: isTemplate ? `rendered by ${list.map((l) => l.label.replace(/ \(.*\)$/, '')).join(', ')}` : `→ ${list.map((l) => l.label).join(', ')}`,
                    tooltip: isTemplate ? 'Go to the view that renders this template' : 'Open the template',
                    arguments: [list],
                },
            }));
            return { lenses, dispose() { } };
        },
    };
    monaco.languages.registerCodeLensProvider('python', provider);
    monaco.languages.registerCodeLensProvider('django-html', provider);

    // Create template: which folder, then the file (empty, or extending base.html when the folder has one)
    monaco.editor.registerCommand('pymagic.createTemplate', (_, name, folders) => {
        const rels = folders.map((f) => ({ full: f, rel: deps.relOfPath(f) })).filter((f) => f.rel !== null);
        if (rels.length === 0) { toast('The project has no templates folder: make one (templates/) or mark a folder as a template folder.', true); return; }
        const make = async (folder) => {
            const path = `${folder.rel}/${name}`;
            const base = await fetch('/api/file?path=' + encodeURIComponent(`${folder.rel}/base.html`)).then((r) => r.ok).catch(() => false);
            const content = /\.html?$/.test(name) && base && name !== 'base.html' ? '{% extends "base.html" %}\n\n{% block content %}\n{% endblock %}\n' : '';
            const res = await fetch('/api/fs', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ op: 'create', path, content }) });
            if (!res.ok) { toast((await res.json().catch(() => ({}))).error ?? 'The template could not be made.', true); return; }
            await deps.refreshTree?.();
            await deps.openFile(path);
            toast(`${path} is made.`);
        };
        if (rels.length === 1) { void make(rels[0]); return; }
        const pos = deps.editor.getPosition(), box = deps.editor.getDomNode()?.getBoundingClientRect(), p = pos && deps.editor.getScrolledVisiblePosition(pos);
        openMenu(box && p ? { x: box.left + p.left, y: box.top + p.top + p.height } : { x: 300, y: 200 }, rels.map((f) => ({ label: `${f.rel}/${name}`, run: () => void make(f) })));
    });
}

// Diagrams (Tools > Diagrams): the classes of the open file (with their bases and what derives from them) or of the project, the
// modules and their imports, and Django's models with their relations, drawn in a window of their own. A click on a box opens its
// source; the wheel with Ctrl, or the buttons, zoom; the diagram can be saved as an SVG file.
import { layout, arrow, BOX } from './diagram-model.js';
import { toast } from './toast';

const SVG = 'http://www.w3.org/2000/svg';
const KINDS = [['file', 'Classes of this file'], ['classes', 'Classes of the project'], ['modules', 'Modules and imports'], ['models', 'Django models']];

function el(name, attrs = {}, text) {
    const e = document.createElementNS(SVG, name);
    for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, String(v));
    if (text !== undefined) e.textContent = text;
    return e;
}

export function setupDiagrams(deps) {
    async function open(kind = 'file') {
        const uri = deps.currentUri();
        if (kind === 'file' && !uri) {
            toast('Open a Python file for the diagram of its classes.', true);
            return;
        }
        let diagram;
        try {
            diagram = await deps.lsp.request('pymagic/diagram', { kind: kind === 'file' ? 'classes' : kind, textDocument: kind === 'file' ? { uri } : undefined });
        }
        catch (e) {
            toast(e.message ?? 'The diagram could not be made.', true);
            return;
        }
        show(kind, diagram);
    }

    function show(kind, diagram) {
        document.querySelector('.diagram-shade')?.remove();
        const shade = document.createElement('div');
        shade.className = 'settings-shade diagram-shade';
        const box = document.createElement('div');
        box.className = 'diagram-box';
        const bar = document.createElement('div');
        bar.className = 'diagram-bar';
        const title = document.createElement('span');
        title.className = 'diagram-title';
        title.textContent = diagram.title + (diagram.truncated ? ' (the first 120)' : '');
        const choose = document.createElement('select');
        for (const [k, label] of KINDS) choose.add(new Option(label, k));
        choose.value = kind;
        choose.onchange = () => void open(choose.value);
        const zoomOut = document.createElement('button');
        zoomOut.textContent = '−';
        const zoomIn = document.createElement('button');
        zoomIn.textContent = '+';
        const fit = document.createElement('button');
        fit.textContent = 'Fit';
        const save = document.createElement('button');
        save.textContent = 'Save as SVG…';
        const close = document.createElement('button');
        close.textContent = 'Close';
        bar.append(title, choose, zoomOut, zoomIn, fit, save, close);
        const view = document.createElement('div');
        view.className = 'diagram-view';
        box.append(bar, view);
        shade.append(box);
        document.body.append(shade);
        const done = () => { shade.remove(); document.removeEventListener('keydown', onKey, true); };
        const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); done(); } };
        document.addEventListener('keydown', onKey, true);
        close.onclick = done;
        shade.onmousedown = (e) => { if (e.target === shade) done(); };

        if (diagram.nodes.length === 0) {
            const none = document.createElement('p');
            none.className = 'set-note';
            none.textContent = kind === 'models' ? 'No Django models in the project.' : kind === 'file' ? 'This file has no classes.' : 'Nothing to draw.';
            view.append(none);
            return;
        }
        const { boxes, width, height } = layout(diagram.nodes, diagram.edges);
        const svg = el('svg', { xmlns: SVG, width, height, viewBox: `0 0 ${width} ${height}`, class: 'diagram-svg' });
        const defs = el('defs');
        // a hollow triangle for "extends", an open arrow for imports and relations
        const triangle = el('marker', { id: 'dg-extends', viewBox: '0 0 12 12', refX: 11, refY: 6, markerWidth: 12, markerHeight: 12, orient: 'auto-start-reverse' });
        triangle.append(el('path', { d: 'M1,1 L11,6 L1,11 Z', class: 'dg-head-hollow' }));
        const open_ = el('marker', { id: 'dg-arrow', viewBox: '0 0 12 12', refX: 11, refY: 6, markerWidth: 10, markerHeight: 10, orient: 'auto-start-reverse' });
        open_.append(el('path', { d: 'M1,1 L11,6 L1,11', class: 'dg-head-open' }));
        defs.append(triangle, open_);
        svg.append(defs);
        for (const e of diagram.edges) {
            const a = boxes.get(e.from), b = boxes.get(e.to);
            if (!a || !b) continue;
            const line = arrow(a, b);
            svg.append(el('line', { ...line, class: `dg-edge dg-${e.kind}`, 'marker-end': `url(#${e.kind === 'extends' ? 'dg-extends' : 'dg-arrow'})` }));
            if (e.label || (e.kind !== 'extends' && e.kind !== 'imports'))
                svg.append(el('text', { x: (line.x1 + line.x2) / 2 + 4, y: (line.y1 + line.y2) / 2, class: 'dg-edge-label' }, `${e.label ?? ''}${e.kind === 'manyToMany' ? ' *' : e.kind === 'oneToOne' ? ' 1' : ''}`));
        }
        for (const n of diagram.nodes) {
            const b = boxes.get(n.id);
            const g = el('g', { class: `dg-node dg-${n.kind}${n.outside ? ' dg-outside' : ''}`, transform: `translate(${b.x},${b.y})` });
            g.append(el('rect', { width: b.width, height: b.height, rx: 5 }));
            g.append(el('text', { x: 10, y: 17, class: 'dg-label' }, n.label));
            if (n.members.length) g.append(el('line', { x1: 0, y1: BOX.head, x2: b.width, y2: BOX.head, class: 'dg-sep' }));
            const shown = n.members.slice(0, BOX.members);
            shown.forEach((m, i) => g.append(el('text', { x: 10, y: BOX.head + 14 + i * BOX.line, class: 'dg-member' }, m.length > 38 ? m.slice(0, 37) + '…' : m)));
            if (n.members.length > BOX.members)
                g.append(el('text', { x: 10, y: BOX.head + 14 + shown.length * BOX.line, class: 'dg-member' }, `… ${n.members.length - BOX.members} more`));
            const tip = el('title', {}, `${n.label}${n.members.length ? '\n' + n.members.join('\n') : ''}\n(click to open)`);
            g.append(tip);
            g.onclick = () => {
                const rel = deps.relOfUri(n.uri);
                if (!rel) return;
                done();
                void deps.openAt(rel, n.line + 1);
            };
            svg.append(g);
        }
        view.append(svg);
        let zoom = 1;
        const apply = () => { svg.setAttribute('width', String(width * zoom)); svg.setAttribute('height', String(height * zoom)); };
        zoomIn.onclick = () => { zoom = Math.min(3, zoom * 1.25); apply(); };
        zoomOut.onclick = () => { zoom = Math.max(0.2, zoom / 1.25); apply(); };
        fit.onclick = () => { zoom = Math.min(1, (view.clientWidth - 20) / width, (view.clientHeight - 20) / height); apply(); };
        view.onwheel = (e) => { if (!e.ctrlKey) return; e.preventDefault(); zoom = Math.max(0.2, Math.min(3, zoom * (e.deltaY < 0 ? 1.1 : 1 / 1.1))); apply(); };
        save.onclick = () => {
            // the colours of the page's theme go into the file, so it reads the same outside PyMagic
            const copy = svg.cloneNode(true);
            const style = el('style', {}, [...document.styleSheets].flatMap((s) => { try { return [...s.cssRules]; } catch { return []; } })
                .map((r) => r.cssText).filter((t) => t.includes('.dg-')).join('\n').replace(/var\(--[\w-]+\)/g, (v) => getComputedStyle(document.documentElement).getPropertyValue(v.slice(4, -1)).trim() || '#888'));
            copy.prepend(style);
            const a = document.createElement('a');
            a.href = URL.createObjectURL(new Blob([new XMLSerializer().serializeToString(copy)], { type: 'image/svg+xml' }));
            a.download = `${kind}-diagram.svg`;
            a.click();
            window.setTimeout(() => URL.revokeObjectURL(a.href), 5000);
        };
        if (width > view.clientWidth || height > view.clientHeight) fit.onclick();
    }
    return { open };
}

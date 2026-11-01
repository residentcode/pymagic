// The live preview of a Markdown or reStructuredText file, beside the editor: it follows the typing, scrolls with the editor, opens the files a
// link names, draws Mermaid flowcharts and sequence diagrams (mermaid-lite.js), and exports the page as an HTML file. The page is built from the
// tree markdown-doc.js reads (never from text as HTML); reStructuredText comes back from docutils as HTML and is cut down to a list of safe tags.
import * as monaco from 'monaco-editor';
import { blocksDoc } from './markdown-doc';
import { safeHref } from './markdown-lite';
import { parseMermaid, layoutFlow, clip } from './mermaid-lite';
import { toast } from './toast';

const SVG = 'http://www.w3.org/2000/svg';
const kindOf = (rel) => (/\.(md|markdown)$/i.test(rel ?? '') ? 'md' : /\.rst$/i.test(rel ?? '') ? 'rst' : null);

function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
}
function svg(tag, attrs = {}, text) {
    const e = document.createElementNS(SVG, tag);
    for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, String(v));
    if (text !== undefined) e.textContent = text;
    return e;
}

/** A path as the project knows it: `from` is the folder (a list) of the file the link is in. */
export function resolveLink(rel, href) {
    const parts = href.startsWith('/') ? [] : rel.split('/').slice(0, -1);
    for (const seg of href.split('/')) {
        if (!seg || seg === '.') continue;
        if (seg === '..') parts.pop(); else parts.push(decodeURIComponent(seg));
    }
    return parts.join('/');
}

// ------------------------------------------------------------------------------------------------------------------ diagrams

function flowSvg(diagram) {
    const L = layoutFlow(diagram);
    const pad = 16;
    const root = svg('svg', { class: 'md-diagram', width: L.width + pad * 2 + 90, height: L.height + pad * 2 + 90, viewBox: `${-pad} ${-pad} ${L.width + pad * 2 + 90} ${L.height + pad * 2 + 90}` });
    const defs = svg('defs');
    const marker = svg('marker', { id: 'md-arrow', viewBox: '0 0 10 10', refX: 9, refY: 5, markerWidth: 7, markerHeight: 7, orient: 'auto-start-reverse' });
    marker.append(svg('path', { d: 'M 0 0 L 10 5 L 0 10 z', class: 'md-arrowhead' }));
    defs.append(marker);
    root.append(defs);
    for (const e of L.edges) {
        if (e.a === e.b) continue;
        // an edge that goes against the direction of the chart bows out to the side, so it does not run through the nodes between
        const backwards = (diagram.direction === 'TD' && e.to.y <= e.from.y) || (diagram.direction === 'BT' && e.to.y >= e.from.y)
            || (diagram.direction === 'LR' && e.to.x <= e.from.x) || (diagram.direction === 'RL' && e.to.x >= e.from.x);
        const bow = backwards ? 70 : 0;
        const vertical = diagram.direction === 'TD' || diagram.direction === 'BT';
        const side = vertical ? { x: bow, y: 0 } : { x: 0, y: bow };
        const aim = { x: (e.from.x + e.to.x) / 2 + side.x, y: (e.from.y + e.to.y) / 2 + side.y };
        const p = clip(e.a, backwards ? aim : e.to), q = clip(e.b, backwards ? aim : e.from);
        const control = { x: (p.x + q.x) / 2 + side.x * 1.2, y: (p.y + q.y) / 2 + side.y * 1.2 };
        if (backwards) root.append(svg('path', { d: `M ${p.x} ${p.y} Q ${control.x} ${control.y} ${q.x} ${q.y}`, class: 'md-edge' + (e.dashed ? ' dashed' : '') + (e.thick ? ' thick' : ''), 'marker-end': e.arrow ? 'url(#md-arrow)' : '' }));
        else root.append(svg('line', { x1: p.x, y1: p.y, x2: q.x, y2: q.y, class: 'md-edge' + (e.dashed ? ' dashed' : '') + (e.thick ? ' thick' : ''), 'marker-end': e.arrow ? 'url(#md-arrow)' : '' }));
        if (e.label) {
            const mx = backwards ? control.x - side.x * 0.1 : (p.x + q.x) / 2, my = backwards ? control.y - side.y * 0.1 : (p.y + q.y) / 2;
            const w = e.label.length * 6.4 + 10;
            root.append(svg('rect', { x: mx - w / 2, y: my - 9, width: w, height: 17, rx: 4, class: 'md-edge-label-bg' }));
            root.append(svg('text', { x: mx, y: my + 4, class: 'md-edge-label', 'text-anchor': 'middle' }, e.label));
        }
    }
    for (const n of L.nodes) {
        const g = svg('g', { class: 'md-node md-' + n.shape });
        const cx = n.x + n.w / 2, cy = n.y + n.h / 2;
        if (n.shape === 'diamond') g.append(svg('polygon', { points: `${cx},${n.y - 6} ${n.x + n.w + 10},${cy} ${cx},${n.y + n.h + 6} ${n.x - 10},${cy}` }));
        else if (n.shape === 'circle') g.append(svg('ellipse', { cx, cy, rx: n.w / 2 + 2, ry: n.h / 2 + 4 }));
        else g.append(svg('rect', { x: n.x, y: n.y, width: n.w, height: n.h, rx: n.shape === 'stadium' ? n.h / 2 : n.shape === 'round' ? 12 : n.shape === 'database' ? 10 : 4 }));
        g.append(svg('text', { x: cx, y: cy + 4, 'text-anchor': 'middle' }, n.label));
        root.append(g);
    }
    return root;
}

function sequenceSvg(diagram) {
    const COL = 150, TOP = 20, ROW = 44;
    const ps = diagram.participants;
    const x = (id) => 70 + ps.findIndex((p) => p.id === id) * COL;
    const height = TOP + 40 + diagram.steps.length * ROW + 30;
    const root = svg('svg', { class: 'md-diagram', width: Math.max(160, ps.length * COL), height, viewBox: `0 0 ${Math.max(160, ps.length * COL)} ${height}` });
    const defs = svg('defs');
    const marker = svg('marker', { id: 'md-arrow-seq', viewBox: '0 0 10 10', refX: 9, refY: 5, markerWidth: 7, markerHeight: 7, orient: 'auto-start-reverse' });
    marker.append(svg('path', { d: 'M 0 0 L 10 5 L 0 10 z', class: 'md-arrowhead' }));
    defs.append(marker);
    root.append(defs);
    for (const p of ps) {
        root.append(svg('line', { x1: x(p.id), y1: TOP + 30, x2: x(p.id), y2: height - 10, class: 'md-lifeline' }));
        const w = Math.max(64, p.label.length * 7.4 + 20);
        const g = svg('g', { class: 'md-node md-box' });
        g.append(svg('rect', { x: x(p.id) - w / 2, y: TOP, width: w, height: 30, rx: 4 }), svg('text', { x: x(p.id), y: TOP + 20, 'text-anchor': 'middle' }, p.label));
        root.append(g);
    }
    diagram.steps.forEach((s, k) => {
        const y = TOP + 40 + (k + 1) * ROW - 12;
        if (s.type === 'note') {
            const xs = s.over.map(x);
            const a = Math.min(...xs) - 50, b = Math.max(...xs) + 50;
            root.append(svg('rect', { x: a, y: y - 14, width: b - a, height: 26, class: 'md-note' }), svg('text', { x: (a + b) / 2, y: y + 4, 'text-anchor': 'middle', class: 'md-edge-label' }, s.text));
            return;
        }
        const a = x(s.from), b = x(s.to);
        if (a === b) { root.append(svg('text', { x: a + 8, y: y - 4, class: 'md-edge-label' }, s.text)); return; }
        root.append(svg('line', { x1: a, y1: y, x2: b, y2: y, class: 'md-edge' + (s.dashed ? ' dashed' : ''), 'marker-end': s.arrow ? 'url(#md-arrow-seq)' : '' }));
        root.append(svg('text', { x: (a + b) / 2, y: y - 6, 'text-anchor': 'middle', class: 'md-edge-label' }, s.text));
    });
    return root;
}

/** A Mermaid diagram as an element: a drawing for the kinds that are read, the text with a note for the others. */
export function diagramElement(text) {
    const d = parseMermaid(text);
    const box = el('div', 'md-mermaid');
    if (d.type === 'flow' && d.nodes.length) box.append(flowSvg(d));
    else if (d.type === 'sequence' && d.participants.length) box.append(sequenceSvg(d));
    else {
        box.append(el('div', 'md-note-line', d.type === 'unsupported' ? `A Mermaid "${d.kind}" diagram is shown as its text: PyMagic draws flowcharts and sequence diagrams.` : 'An empty diagram.'));
        box.append(el('pre', null, text));
    }
    return box;
}

// ------------------------------------------------------------------------------------------------------------------ the page

export function setupMarkdownPreview({ editor, currentFile, openFile, askText }) {
    const pane = el('div', 'md-pane');
    pane.hidden = true;
    const head = el('div', 'md-head');
    const title = el('span', 'md-title', 'Preview');
    const exportButton = el('button', null, 'Export HTML…');
    exportButton.title = 'Save this page as an HTML file next to the document';
    const close = el('button', null, '✕');
    close.title = 'Close the preview';
    head.append(title, exportButton, close);
    const body = el('div', 'md-body');
    pane.append(head, body);
    document.getElementById('editor-groups').append(pane);

    const toggle = el('button', 'md-toggle');
    toggle.id = 'md-toggle';
    toggle.textContent = 'Preview';
    toggle.title = 'The live preview of this document beside the editor (Ctrl+Shift+V)';
    toggle.hidden = true;
    document.querySelector('.tabs-row').append(toggle);

    let on = false;
    let timer = 0;
    let ticket = 0;
    let lines = [];      // [{line, el}] of the blocks drawn, for following the editor

    const rel = () => currentFile();

    // ---- pieces of the page

    function inline(list, into) {
        for (const n of list) {
            if (n.t === 'text') into.append(document.createTextNode(n.text));
            else if (n.t === 'code') into.append(el('code', null, n.text));
            else if (n.t === 'br') into.append(document.createElement('br'));
            else if (n.t === 'bold') { const e = el('strong'); inline(n.children, e); into.append(e); }
            else if (n.t === 'italic') { const e = el('em'); inline(n.children, e); into.append(e); }
            else if (n.t === 'strike') { const e = el('del'); inline(n.children, e); into.append(e); }
            else if (n.t === 'link') {
                const a = el('a');
                a.dataset.href = n.href;
                a.href = '#';
                if (n.title) a.title = n.title;
                inline(n.children, a);
                into.append(a);
            } else if (n.t === 'image') {
                const img = el('img');
                img.alt = n.text;
                if (n.title) img.title = n.title;
                img.src = /^https?:\/\//i.test(n.href) ? n.href : '/api/raw?path=' + encodeURIComponent(resolveLink(rel() ?? '', n.href.split('#')[0]));
                into.append(img);
            }
        }
    }

    const slugs = new Map();
    function block(b, into) {
        let e;
        if (b.type === 'heading') {
            e = el('h' + b.level);
            const n = slugs.get(b.id) ?? 0;
            slugs.set(b.id, n + 1);
            e.id = 'md-' + (n ? `${b.id}-${n}` : b.id);
            inline(b.inline, e);
        } else if (b.type === 'paragraph') { e = el('p'); inline(b.inline, e); }
        else if (b.type === 'code') {
            if (b.lang === 'mermaid') e = diagramElement(b.text);
            else {
                e = el('pre');
                const code = el('code', null, b.text);
                e.append(code);
                if (b.lang && monaco.languages.getLanguages().some((l) => l.id === b.lang || l.aliases?.some((a) => a.toLowerCase() === b.lang.toLowerCase()))) {
                    void monaco.editor.colorize(b.text, b.lang, {}).then((html) => { code.innerHTML = html; }).catch(() => { });   // Monaco's own, escaped, output
                }
            }
        } else if (b.type === 'rule') e = el('hr');
        else if (b.type === 'quote') { e = el('blockquote'); for (const c of b.blocks) block(c, e); }
        else if (b.type === 'list') {
            e = el(b.ordered ? 'ol' : 'ul');
            if (b.ordered && b.start !== 1) e.start = b.start;
            for (const item of b.items) {
                const li = el('li');
                if (item.task !== null) {
                    li.className = 'md-task';
                    const box = el('input');
                    box.type = 'checkbox';
                    box.checked = item.task;
                    box.disabled = true;
                    li.append(box);
                }
                for (const c of item.blocks) block(c, li);
                // a paragraph alone in an item is shown without its own space
                if (item.blocks.length === 1 && item.blocks[0].type === 'paragraph') li.querySelector(':scope > p')?.classList.add('tight');
                e.append(li);
            }
        } else if (b.type === 'table') {
            e = el('table');
            const thead = el('thead'), tr = el('tr');
            b.head.forEach((cell, k) => { const th = el('th'); if (b.align[k]) th.style.textAlign = b.align[k]; inline(cell, th); tr.append(th); });
            thead.append(tr);
            const tbody = el('tbody');
            for (const row of b.rows) {
                const r = el('tr');
                row.forEach((cell, k) => { const td = el('td'); if (b.align[k]) td.style.textAlign = b.align[k]; inline(cell, td); r.append(td); });
                tbody.append(r);
            }
            e.append(thead, tbody);
        } else return;
        e.dataset.line = String(b.line);
        if (into === body) lines.push({ line: b.line, el: e });
        into.append(e);
    }

    // ---- reStructuredText from docutils, cut down to safe tags

    const SAFE_TAGS = new Set('h1 h2 h3 h4 h5 h6 p a ul ol li em strong code pre blockquote table thead tbody tr th td hr br div span dl dt dd img sup sub section cite abbr caption figure figcaption tt kbd b i del ins'.split(' '));
    const DROP_TAGS = new Set(['script', 'style', 'iframe', 'object', 'embed', 'link', 'meta', 'form', 'input', 'button', 'svg', 'math', 'base', 'frame', 'frameset', 'template', 'noscript']);
    function clean(node, into) {
        for (const child of Array.from(node.childNodes)) {
            if (child.nodeType === 3) { into.append(document.createTextNode(child.textContent)); continue; }
            if (child.nodeType !== 1) continue;
            const tag = child.tagName.toLowerCase();
            if (DROP_TAGS.has(tag)) continue;
            if (!SAFE_TAGS.has(tag)) { clean(child, into); continue; }
            const copy = document.createElement(tag);
            if (child.id) copy.id = 'md-' + child.id.replace(/[^\w:.-]/g, '');
            for (const c of Array.from(child.classList)) if (/^[\w-]{1,40}$/.test(c)) copy.classList.add(c);
            if (tag === 'a') {
                const href = child.getAttribute('href') ?? '';
                if (href.startsWith('#')) { copy.dataset.href = '#md-' + href.slice(1); copy.href = '#'; }
                else if (safeHref(href)) { copy.dataset.href = href; copy.href = '#'; }
            }
            if (tag === 'img') {
                const src = child.getAttribute('src') ?? '';
                copy.alt = child.getAttribute('alt') ?? '';
                if (/^https?:\/\//i.test(src)) copy.src = src;
                else if (src && !/^[a-z][a-z0-9+.-]*:/i.test(src)) copy.src = '/api/raw?path=' + encodeURIComponent(resolveLink(rel() ?? '', src));
            }
            for (const a of ['colspan', 'rowspan']) if (child.hasAttribute(a) && /^\d{1,2}$/.test(child.getAttribute(a))) copy.setAttribute(a, child.getAttribute(a));
            clean(child, copy);
            into.append(copy);
        }
    }

    async function render() {
        const mine = ++ticket;
        const path = rel();
        const kind = kindOf(path);
        if (!on || !kind) return;
        title.textContent = path ? `Preview of ${path.split('/').pop()}` : 'Preview';
        const text = editor.getValue();
        if (kind === 'md') {
            slugs.clear();
            lines = [];
            const scroll = body.scrollTop;
            body.replaceChildren();
            for (const b of blocksDoc(text)) block(b, body);
            body.scrollTop = scroll;
            follow();
            return;
        }
        // reStructuredText: docutils on the engine's side, when the project's Python has it
        let answer;
        try { answer = await fetch('/api/rst', { method: 'POST', body: JSON.stringify({ text }) }).then((r) => r.json()); } catch { answer = { error: 'The engine did not answer.' }; }
        if (mine !== ticket) return;
        lines = [];
        body.replaceChildren();
        if (answer.error) {
            body.append(el('div', 'md-note-line', answer.error), el('pre', null, text));
            return;
        }
        const doc = new DOMParser().parseFromString(answer.html ?? '', 'text/html');
        clean(doc.body, body);
    }

    function later() { clearTimeout(timer); timer = window.setTimeout(() => void render(), 250); }

    // ---- the editor and the preview follow each other

    /** The preview scrolled to where the editor's first visible line is. */
    function follow() {
        if (!on || !lines.length) return;
        const top = editor.getVisibleRanges()[0]?.startLineNumber ?? 1;
        let k = 0;
        while (k + 1 < lines.length && lines[k + 1].line <= top) k++;
        const a = lines[k], b = lines[k + 1];
        const top0 = a.el.offsetTop - body.offsetTop;
        const span = b ? b.el.offsetTop - a.el.offsetTop : a.el.offsetHeight;
        const lineSpan = b ? b.line - a.line : Math.max(1, editor.getModel().getLineCount() - a.line + 1);
        body.scrollTop = Math.max(0, top0 + Math.min(1, Math.max(0, (top - a.line) / lineSpan)) * span - 6);
    }

    function setOn(next) {
        on = next;
        pane.hidden = !on;
        toggle.classList.toggle('on', on);
        try { sessionStorage.setItem('pymagic.mdPreview', on ? '1' : '0'); } catch { /* not kept */ }
        if (on) void render();
    }

    function refreshToggle() {
        const kind = kindOf(rel());
        toggle.hidden = !kind;
        if (!kind) { pane.hidden = true; return; }
        pane.hidden = !on;
        if (on) later();
    }

    // a click on a link: a heading of the page, a file of the project, or the web
    pane.addEventListener('click', (e) => {
        const a = e.target.closest?.('a[data-href]');
        if (a) {
            e.preventDefault();
            const href = a.dataset.href;
            if (href.startsWith('#')) { body.querySelector('#' + CSS.escape(href.startsWith('#md-') ? href.slice(1) : 'md-' + href.slice(1)))?.scrollIntoView({ block: 'start' }); return; }
            if (/^[a-z][a-z0-9+.-]*:/i.test(href)) { window.open(href, '_blank', 'noopener'); return; }
            const [path, hash] = href.split('#');
            if (path) void openFile(resolveLink(rel() ?? '', path)).then(() => { if (hash) window.dispatchEvent(new CustomEvent('pymagic:gotoHeading', { detail: hash })); });
            else if (hash) body.querySelector('#' + CSS.escape('md-' + hash))?.scrollIntoView({ block: 'start' });
            return;
        }
        // a click on a block shows its line in the editor
        const block = e.target.closest?.('[data-line]');
        if (block && !window.getSelection()?.toString()) { editor.revealLineInCenter(Number(block.dataset.line)); }
    });

    // ---- exporting the page

    const EXPORT_CSS = 'body{font:16px/1.6 system-ui,sans-serif;max-width:860px;margin:2rem auto;padding:0 1rem;color:#1f2328}pre{background:#f6f8fa;padding:12px;overflow:auto;border-radius:6px}code{font-family:Consolas,monospace;background:#f6f8fa;padding:.1em .3em;border-radius:4px}pre code{background:none;padding:0}table{border-collapse:collapse}th,td{border:1px solid #d0d7de;padding:6px 12px}blockquote{margin:0;padding:0 1em;color:#59636e;border-left:4px solid #d0d7de}img{max-width:100%}hr{border:0;border-top:1px solid #d0d7de}.md-diagram{max-width:100%;height:auto}.md-node rect,.md-node polygon,.md-node ellipse{fill:#f6f8fa;stroke:#8c959f}.md-edge{stroke:#59636e;stroke-width:1.5}.md-edge.dashed{stroke-dasharray:5 4}.md-arrowhead{fill:#59636e}.md-edge-label-bg{fill:#fff}text{font:13px system-ui,sans-serif;fill:#1f2328}.md-lifeline{stroke:#8c959f;stroke-dasharray:4 4}.md-note{fill:#fff8c5;stroke:#d4a72c}li.md-task{list-style:none}';
    async function exportHtml() {
        const path = rel();
        if (!path || !body.childNodes.length) return;
        const base = path.replace(/\.[^.]+$/, '') + '.html';
        const name = await askText('Export as HTML', base, 'A file of the project');
        if (!name?.trim()) return;
        const copy = body.cloneNode(true);
        // an image of the project is written into the page, so the file stands alone
        for (const img of copy.querySelectorAll('img[src^="/api/raw"]')) {
            try {
                const blob = await fetch(img.getAttribute('src')).then((r) => (r.ok ? r.blob() : null));
                if (blob) img.src = await new Promise((ok) => { const fr = new FileReader(); fr.onload = () => ok(fr.result); fr.readAsDataURL(blob); });
            } catch { /* the image stays as a path */ }
        }
        for (const a of copy.querySelectorAll('a[data-href]')) { a.href = a.dataset.href.startsWith('#') ? '#' + a.dataset.href.replace(/^#md-/, '') : a.dataset.href; a.removeAttribute('data-href'); }
        for (const e of copy.querySelectorAll('[data-line]')) e.removeAttribute('data-line');
        for (const h of copy.querySelectorAll('[id^="md-"]')) h.id = h.id.slice(3);
        const html = `<!doctype html>\n<html><head><meta charset="utf-8"><title>${path.split('/').pop().replace(/[<>&]/g, '')}</title><style>${EXPORT_CSS}</style></head><body>\n${copy.innerHTML}\n</body></html>\n`;
        const res = await fetch('/api/file?path=' + encodeURIComponent(name.trim()), { method: 'PUT', body: html }).catch(() => null);
        toast(res?.ok ? `Saved ${name.trim()}` : 'The file could not be written.', !res?.ok);
    }

    toggle.onclick = () => setOn(!on);
    close.onclick = () => setOn(false);
    exportButton.onclick = () => void exportHtml();
    editor.onDidChangeModelContent(() => { if (on && kindOf(rel())) later(); });
    editor.onDidChangeModel(() => { refreshToggle(); });
    editor.onDidScrollChange(() => follow());
    editor.addAction({ id: 'pymagic.markdown.preview', label: 'Markdown: Toggle Preview', keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyV], run: () => { if (kindOf(rel())) setOn(!on); } });
    editor.addAction({ id: 'pymagic.markdown.export', label: 'Markdown: Export to HTML…', run: () => { if (!on) setOn(true); setTimeout(() => void exportHtml(), 400); } });
    try { if (sessionStorage.getItem('pymagic.mdPreview') === '1') on = true; } catch { /* not kept */ }
    refreshToggle();
    return { toggle: () => setOn(!on), isOn: () => on };
}

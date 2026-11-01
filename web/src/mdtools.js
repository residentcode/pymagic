// Markdown editing helpers: links to files and headings completed, tables formatted, and a link into another document landing on its heading.
import * as monaco from 'monaco-editor';
import { formatTables, headingsOf } from './markdown-doc';

/** The path of `target` as seen from the file `from` (both from the project's root): `docs/a.md` from `README.md` is `docs/a.md`, `../x.md` from `docs/a.md`. */
export function relativePath(from, target) {
    const a = from.split('/').slice(0, -1), b = target.split('/');
    let k = 0;
    while (k < a.length && k < b.length - 1 && a[k] === b[k]) k++;
    return [...Array(a.length - k).fill('..'), ...b.slice(k)].join('/');
}

export function setupMarkdownTools({ editor, currentFile, relOfModel }) {
    let files = null, filesAt = 0;
    async function projectFiles() {
        if (files && Date.now() - filesAt < 5000) return files;
        try { files = (await (await fetch('/api/tree')).json()).files.map((f) => f.path); } catch { files = []; }
        filesAt = Date.now();
        return files;
    }
    const headingsOfFile = async (path) => {
        const res = await fetch('/api/file?path=' + encodeURIComponent(path)).catch(() => null);
        return res?.ok ? headingsOf(await res.text()) : [];
    };

    monaco.languages.registerCompletionItemProvider('markdown', {
        triggerCharacters: ['(', '/', '#'],
        async provideCompletionItems(model, position) {
            const before = model.getLineContent(position.lineNumber).slice(0, position.column - 1);
            const m = /\]\(([^)\s]*)$/.exec(before);
            if (!m) return { suggestions: [] };
            const rel = relOfModel(model) ?? currentFile();
            if (!rel) return { suggestions: [] };
            const typed = m[1];
            const K = monaco.languages.CompletionItemKind;
            const start = position.column - typed.length;
            const hash = typed.indexOf('#');
            if (hash >= 0) {
                // a heading of this file, or of the one named before the #
                const path = typed.slice(0, hash);
                const target = path ? (await projectFiles()).find((f) => relativePath(rel, f) === path) : rel;
                const headings = target ? (target === rel ? headingsOf(model.getValue()) : await headingsOfFile(target)) : [];
                const range = new monaco.Range(position.lineNumber, start + hash + 1, position.lineNumber, position.column);
                return { suggestions: headings.map((h, i) => ({ label: h.id, kind: K.Reference, detail: h.text, insertText: h.id, range, sortText: String(i).padStart(4, '0') })) };
            }
            const range = new monaco.Range(position.lineNumber, start, position.lineNumber, position.column);
            const all = await projectFiles();
            return {
                suggestions: all.filter((f) => f !== rel).map((f) => relativePath(rel, f)).filter((p) => p.startsWith(typed.replace(/^\.\//, '')) || typed === '')
                    .slice(0, 300).map((p, i) => ({ label: p, kind: /\.(md|markdown|rst|txt)$/i.test(p) ? K.File : K.Reference, insertText: p, range, sortText: String(i).padStart(4, '0') })),
            };
        },
    });

    const format = (model) => {
        const full = model.getFullModelRange();
        const text = model.getValue();
        const next = formatTables(text);
        return next === text ? [] : [{ range: full, text: next }];
    };
    monaco.languages.registerDocumentFormattingEditProvider('markdown', { provideDocumentFormattingEdits: (model) => format(model) });
    editor.addAction({
        id: 'pymagic.markdown.formatTables', label: 'Markdown: Format Tables',
        run: (ed) => { const m = ed.getModel(); if (m?.getLanguageId() !== 'markdown') return; const edits = format(m); if (edits.length) { ed.pushUndoStop(); ed.executeEdits('md-tables', edits); ed.pushUndoStop(); } },
    });

    // a link `other.md#heading` opened from the preview: the editor goes to the heading
    window.addEventListener('pymagic:gotoHeading', (e) => {
        setTimeout(() => {
            const h = headingsOf(editor.getValue()).find((x) => x.id === e.detail);
            if (h) { editor.revealLineInCenter(h.line); editor.setPosition({ lineNumber: h.line, column: 1 }); }
        }, 300);
    });
}

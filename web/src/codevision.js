// Code vision and inlay hints, as PyCharm shows them: above each function, method and class the number of its usages,
// of a class its inheritors, and who wrote most of it; in the code the names of parameters before arguments and the
// inferred types (pymagic/inlayHints on the server).
import * as monaco from 'monaco-editor';
import { settings } from './settings';
import { lensSymbols, usagesText, inheritorsText, mainAuthor } from './codevision-model.js';
export function setupCodeVision(deps) {
    const { lsp } = deps;
    const toPos = (p) => ({ line: p.lineNumber - 1, character: p.column - 1 });
    // the editor whose model a lens belongs to: the main one, or a side of the split
    const editors = new Set([deps.editor]);
    const editorOf = (model) => [...editors].find((e) => e.getModel() === model) ?? deps.editor;
    // the blame of a file, for the authors (asked once per saved version)
    const blames = new Map();
    async function blameOf(rel) {
        const key = rel + '#' + (deps.savedText(rel)?.length ?? 0);
        if (!blames.has(key)) {
            blames.set(key, fetch('/api/git/blame?path=' + encodeURIComponent(rel)).then((r) => (r.ok ? r.json() : [])).catch(() => []));
        }
        return blames.get(key);
    }
    /** How many classes derive from a class, at any depth (the type hierarchy's subtypes, followed down). */
    async function inheritorCount(uri, position) {
        const roots = (await lsp.request('textDocument/prepareTypeHierarchy', { textDocument: { uri }, position })) ?? [];
        const seen = new Set();
        const queue = [...roots];
        while (queue.length && seen.size < 200) {
            const item = queue.shift();
            for (const sub of (await lsp.request('typeHierarchy/subtypes', { item })) ?? []) {
                const key = sub.uri + '#' + sub.selectionRange.start.line;
                if (!seen.has(key)) {
                    seen.add(key);
                    queue.push(sub);
                }
            }
        }
        return seen.size;
    }
    monaco.editor.registerCommand('pymagic.codevision.usages', (_, uri, line, column) => {
        const ed = [...editors].find((e) => deps.uriOf(e.getModel()) === uri) ?? deps.editor;
        ed.setPosition({ lineNumber: line, column });
        ed.focus();
        ed.trigger('codevision', 'editor.action.referenceSearch.trigger', {});
    });
    monaco.editor.registerCommand('pymagic.codevision.inheritors', (_, uri, line, column) => {
        const ed = [...editors].find((e) => deps.uriOf(e.getModel()) === uri) ?? deps.editor;
        ed.setPosition({ lineNumber: line, column });
        ed.focus();
        void ed.getAction('pymagic.typeHierarchy')?.run();
    });
    monaco.languages.registerCodeLensProvider('python', {
        async provideCodeLenses(model) {
            const s = settings();
            const uri = deps.uriOf(model);
            if (!uri || !(s.codeVision || s.codeVisionAuthor))
                return { lenses: [], dispose() { } };
            let symbols = [];
            try {
                symbols = lensSymbols((await lsp.request('textDocument/documentSymbol', { textDocument: { uri } })) ?? []);
            }
            catch { /* the server is busy or gone */ }
            const lenses = [];
            for (const sym of symbols) {
                const at = sym.selectionRange.start;
                const range = new monaco.Range(sym.range.start.line + 1, 1, sym.range.start.line + 1, 1);
                const data = { uri, position: at, line: at.line + 1, column: at.character + 1, kind: sym.kind, first: sym.range.start.line, last: sym.range.end.line };
                if (s.codeVision) {
                    lenses.push({ range, id: `u${at.line}`, data: { ...data, what: 'usages' } });
                    // a class without inheritors gets no lens at all (an empty one would leave a lone separator)
                    const n = sym.kind === 5 ? await inheritorCount(uri, at).catch(() => 0) : 0;
                    if (n)
                        lenses.push({ range, id: `i${at.line}`, data, command: { id: 'pymagic.codevision.inheritors', title: inheritorsText(n), arguments: [uri, data.line, data.column] } });
                }
                if (s.codeVisionAuthor)
                    lenses.push({ range, id: `a${at.line}`, data: { ...data, what: 'author' } });
            }
            return { lenses, dispose() { } };
        },
        // each lens is worked out when it comes into view
        async resolveCodeLens(model, lens) {
            const d = lens.data;
            if (lens.command)
                return lens;   // worked out already (the inheritors)
            try {
                if (d.what === 'usages') {
                    const refs = (await lsp.request('textDocument/references', { textDocument: { uri: d.uri }, position: d.position, context: { includeDeclaration: false } })) ?? [];
                    lens.command = { id: 'pymagic.codevision.usages', title: usagesText(refs.length), arguments: [d.uri, d.line, d.column] };
                }
                else {
                    const rel = deps.relOf(model);
                    const who = rel ? mainAuthor(await blameOf(rel), d.first, d.last) : null;
                    lens.command = { id: '', title: who ?? '', tooltip: who ? 'Wrote most of its lines (Git blame)' : undefined };
                }
            }
            catch {
                lens.command = { id: '', title: '' };
            }
            return lens;
        },
    });
    monaco.languages.registerInlayHintsProvider('python', {
        async provideInlayHints(model, range) {
            const s = settings();
            const uri = deps.uriOf(model);
            if (!uri || !(s.hintParams || s.hintTypes))
                return { hints: [], dispose() { } };
            let found = [];
            try {
                found = (await lsp.request('pymagic/inlayHints', { textDocument: { uri }, range: { start: toPos(range.getStartPosition()), end: toPos(range.getEndPosition()) } })) ?? [];
            }
            catch { /* none */ }
            const hints = found.filter((h) => (h.kind === 2 ? s.hintParams : s.hintTypes)).map((h) => ({
                position: { lineNumber: h.position.line + 1, column: h.position.character + 1 },
                label: h.label, kind: h.kind, paddingLeft: h.paddingLeft, paddingRight: h.paddingRight,
            }));
            return { hints, dispose() { } };
        },
    });
    return {
        /** The lenses and hints in another editor (a side of the split). */
        attach: (ed) => { editors.add(ed); ed.onDidDispose(() => editors.delete(ed)); },
    };
}

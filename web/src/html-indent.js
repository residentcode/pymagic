// Indentation that does not follow the elements and blocks of markup, told while typing (html-indent-model.js) in HTML files and Django, Jinja
// and Nunjucks templates, Blade views and Vue and Svelte components: a weak warning on each such line, a little after the last key;
// Alt+Enter: Fix indentation of the file (the wrong lines move, each with what it holds). The { } languages have brace-indent.js.
import * as monaco from 'monaco-editor';
import { markupKind, htmlIndentProblems, fixHtmlIndentation } from './html-indent-model.js';

const OWNER = 'markup-indent';      // (brace-indent.js clears its own 'indent' marks in these files)
const LANGUAGES = ['django-html', 'twig', 'blade', 'html'];

export function setupHtmlIndent() {
    const timers = new Map();
    const kindOf = (model) => markupKind(model.getLanguageId(), model.uri.path);
    function check(model) {
        if (model.isDisposed()) return;
        const kind = kindOf(model);
        if (!kind) { monaco.editor.setModelMarkers(model, OWNER, []); return; }
        let found = [];
        const text = model.getValue();
        if (text.length <= 1_000_000) { try { found = htmlIndentProblems(text, kind); } catch { found = []; } }
        monaco.editor.setModelMarkers(model, OWNER, found.map((p) => ({
            startLineNumber: p.line, startColumn: 1, endLineNumber: p.line,
            endColumn: Math.max(2, model.getLineFirstNonWhitespaceColumn(p.line) || model.getLineMaxColumn(p.line)),
            severity: monaco.MarkerSeverity.Info, source: 'Indentation', code: OWNER, message: p.message,
        })));
    }
    const soon = (model, delay = 700) => {
        clearTimeout(timers.get(model));
        timers.set(model, window.setTimeout(() => { timers.delete(model); check(model); }, delay));
    };
    const watch = (model) => {
        soon(model, 0);
        model.onDidChangeContent(() => soon(model));
        model.onDidChangeLanguage(() => soon(model, 0));
        model.onWillDispose(() => { clearTimeout(timers.get(model)); timers.delete(model); });
    };
    for (const m of monaco.editor.getModels()) watch(m);
    monaco.editor.onDidCreateModel(watch);

    monaco.languages.registerCodeActionProvider(LANGUAGES, {
        provideCodeActions(model, _range, context) {
            const marks = context.markers.filter((m) => m.code === OWNER);
            const kind = kindOf(model);
            if (!marks.length || !kind) return { actions: [], dispose() { } };
            const text = model.getValue();
            const fixed = fixHtmlIndentation(text, kind);
            if (fixed === text) return { actions: [], dispose() { } };
            return {
                actions: [{
                    title: 'Fix indentation of the file', kind: 'quickfix', isPreferred: true, diagnostics: marks,
                    edit: { edits: [{ resource: model.uri, versionId: model.getVersionId(), textEdit: { range: model.getFullModelRange(), text: fixed } }] },
                }],
                dispose() { },
            };
        },
    });
}

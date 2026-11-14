// Indentation that does not follow the { } blocks, told while typing (brace-indent-model.js) in JavaScript, TypeScript, CSS, SCSS, Less and JSON:
// a weak warning on each such line, a little after the last key; Alt+Enter: Fix indentation of the file (only the wrong lines move, each with
// what goes with it). PHP has its own (php-analysis.js); Python's indentation is its syntax.
import * as monaco from 'monaco-editor';
import { braceKind, braceIndentProblems, fixBraceIndentation } from './brace-indent-model.js';

const OWNER = 'indent';
const LANGUAGES = ['javascript', 'typescript', 'css', 'scss', 'less', 'json'];

export function setupBraceIndent() {
    const timers = new Map();
    function check(model) {
        if (model.isDisposed()) return;
        const kind = braceKind(model.getLanguageId());
        let found = [];
        if (kind) { try { found = braceIndentProblems(model.getValue(), kind); } catch { found = []; } }
        monaco.editor.setModelMarkers(model, OWNER, found.map((p) => ({
            startLineNumber: p.line, startColumn: 1, endLineNumber: p.line,
            endColumn: Math.max(2, model.getLineFirstNonWhitespaceColumn(p.line) || model.getLineMaxColumn(p.line)),
            severity: monaco.MarkerSeverity.Info, source: 'Indentation', code: OWNER, message: p.message,
        })));
    }
    const soon = (model, delay = 600) => {
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
            const kind = braceKind(model.getLanguageId());
            if (!marks.length || !kind) return { actions: [], dispose() { } };
            const text = model.getValue();
            const fixed = fixBraceIndentation(text, kind);
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

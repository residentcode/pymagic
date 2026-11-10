// `/* … */` in a template's HTML (html-comment-model.js): a warning on it, since it is no comment there, and the quick fix that makes it the
// template's own comment ({{-- … --}} in Blade, <!-- … --> in HTML and the other templates).
import * as monaco from 'monaco-editor';
import { COMMENT_OF, strayBlockComments, asTemplateComment } from './html-comment-model.js';

const OWNER = 'html-comment';
const CODE = 'html-block-comment';
const MAX_CHARS = 2_000_000;

export function setupHtmlComments() {
    const timers = new Map();
    const watched = new Set();

    function check(model) {
        if (model.isDisposed()) return;
        const language = model.getLanguageId();
        if (!(language in COMMENT_OF) || model.getValueLength() > MAX_CHARS) { monaco.editor.setModelMarkers(model, OWNER, []); return; }
        const [open, close] = COMMENT_OF[language];
        monaco.editor.setModelMarkers(model, OWNER, strayBlockComments(model.getValue()).map(({ start, end }) => {
            const s = model.getPositionAt(start), e = model.getPositionAt(end);
            return {
                startLineNumber: s.lineNumber, startColumn: s.column, endLineNumber: e.lineNumber, endColumn: e.column, severity: monaco.MarkerSeverity.Warning, code: CODE, source: 'HTML',
                message: `/* */ is not a comment in HTML: the browser shows it as text, and the tags inside still count. Use ${open} ${close} (Ctrl+/).`,
            };
        }));
    }

    function schedule(model, ms = 500) {
        clearTimeout(timers.get(model));
        timers.set(model, setTimeout(() => check(model), ms));
    }

    function watch(model) {
        schedule(model, 100);
        if (watched.has(model)) return;
        watched.add(model);
        model.onDidChangeContent(() => schedule(model));
        model.onWillDispose(() => { clearTimeout(timers.get(model)); timers.delete(model); watched.delete(model); });
    }

    for (const model of monaco.editor.getModels()) watch(model);
    monaco.editor.onDidCreateModel(watch);
    monaco.editor.onDidChangeModelLanguage((e) => watch(e.model));

    monaco.languages.registerCodeActionProvider(Object.keys(COMMENT_OF), {
        provideCodeActions(model, _range, context) {
            const language = model.getLanguageId();
            const actions = context.markers.filter((m) => m.code === CODE).map((m) => {
                const where = new monaco.Range(m.startLineNumber, m.startColumn, m.endLineNumber, m.endColumn);
                const text = model.getValueInRange(where);
                const [open, close] = COMMENT_OF[language] ?? COMMENT_OF.html;
                return {
                    title: `Change to a ${language === 'blade' ? 'Blade' : 'HTML'} comment ${open} ${close}`, kind: 'quickfix', isPreferred: true, diagnostics: [m],
                    edit: { edits: [{ resource: model.uri, versionId: model.getVersionId(), textEdit: { range: where, text: asTemplateComment(text.slice(2, -2), language) } }] },
                };
            });
            return { actions, dispose() { } };
        },
    });
}

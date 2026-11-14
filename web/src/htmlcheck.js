// HTML's and YAML's syntax errors on the editor while typing (htmlcheck-model.js), a little after the last key: an HTML file's or template's
// tags (django-html: every .html, Django's and Jinja's templates too), and a YAML file's errors. Components (.vue, .svelte) and Blade are left
// out: their markup has syntax of its own.
import * as monaco from 'monaco-editor';
import { templateProblems, yamlProblems, isYamlTemplate } from './htmlcheck-model.js';

const OWNER = 'syntax';
const KINDS = { 'django-html': ['HTML', templateProblems], yaml: ['YAML', yamlProblems] };

export function setupSyntaxChecks() {
    const timers = new Map();
    function check(model) {
        if (model.isDisposed()) return;
        const kind = KINDS[model.getLanguageId()];
        const text = model.getValue();
        if (!kind || text.length > 2_000_000 || (model.getLanguageId() === 'yaml' && isYamlTemplate(model.uri.path, text))) {
            monaco.editor.setModelMarkers(model, OWNER, []);
            return;
        }
        const [source, find] = kind;
        let found = [];
        try { found = find(text); } catch { found = []; }
        monaco.editor.setModelMarkers(model, OWNER, found.slice(0, 100).map((p) => {
            const a = model.getPositionAt(p.start), b = model.getPositionAt(p.end);
            return { startLineNumber: a.lineNumber, startColumn: a.column, endLineNumber: b.lineNumber, endColumn: b.column, severity: monaco.MarkerSeverity.Error, source, message: p.message };
        }));
    }
    const soon = (model, delay = 400) => {
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
}

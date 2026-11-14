// HTML's and YAML's syntax errors on the editor while typing (htmlcheck-model.js), a little after the last key: an HTML file's or template's
// tags (django-html: every .html, Django's and Jinja's templates too), a Vue or Svelte component's and a Blade view's markup (each with its
// own syntax around the tags), and a YAML file's errors.
import * as monaco from 'monaco-editor';
import { templateProblems, yamlProblems, isYamlTemplate, vueProblems, svelteProblems, bladeProblems } from './htmlcheck-model.js';
import { pugProblems } from './pug-model.js';

const OWNER = 'syntax';
const KINDS = { 'django-html': ['HTML', templateProblems], yaml: ['YAML', yamlProblems], pug: ['Pug', pugProblems], blade: ['Blade', bladeProblems] };   // Pug: its indentation (pug-model.js)
/** Components are HTML to the editor: told by their file's name. */
const COMPONENTS = [[/\.vue$/i, ['Vue', vueProblems]], [/\.svelte$/i, ['Svelte', svelteProblems]]];

export function setupSyntaxChecks() {
    const timers = new Map();
    function check(model) {
        if (model.isDisposed()) return;
        const kind = KINDS[model.getLanguageId()] ?? (model.getLanguageId() === 'html' ? COMPONENTS.find(([name]) => name.test(model.uri.path))?.[1] : undefined);
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

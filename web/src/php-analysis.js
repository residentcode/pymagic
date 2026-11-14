// PHP's problems in the editor beside Phpactor's: PHP's syntax errors while typing (PhpLint.cs, php -l), and PHPStan's (PhpAnalysis.cs): the
// project's own PHPStan (Larastan too, through its phpstan.neon) on a .php file when it is opened and each time it is saved, its findings as
// warnings of the file with PHPStan's identifier and tip. A project without PHPStan asks nothing.
import * as monaco from 'monaco-editor';
import { toast } from './toast';
import { phpSyntaxError } from './php-syntax-model.js';
import { indentProblems, fixIndentation } from './php-format-model.js';

const OWNER = 'phpstan';
const LINT = 'php-lint';
const INDENT = 'php-indent';

export function setupPhpAnalysis({ editor, relOf }) {
    let available = true;            // false once the server said the project has no PHPStan (asked again when a file is opened)
    const running = new Map();       // model -> the text the last request was for
    const told = new Set();          // reasons already shown

    async function analyse(model, opened = false) {
        if (!model || model.isDisposed()) return;
        const rel = relOf(model);
        if (!rel || !/\.php$/i.test(rel) || /\.blade\.php$/i.test(rel) || model.getLanguageId() !== 'php') return;
        if (!available && !opened) return;
        const text = model.getValue();
        if (running.get(model) === text) return;
        running.set(model, text);
        const res = await fetch('/api/php/analyse', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ file: rel }) }).catch(() => null);
        const body = await res?.json().catch(() => null);
        if (running.get(model) === text) running.delete(model);
        if (model.isDisposed() || !body) return;
        if (body.none) { available = false; monaco.editor.setModelMarkers(model, OWNER, []); return; }
        available = true;
        if (body.error) {
            if (!told.has(body.error)) { told.add(body.error); toast(body.error, true); }
            return;
        }
        // the problems of the file as it was saved: they stand on its lines until the next save
        monaco.editor.setModelMarkers(model, OWNER, (body.problems ?? []).map((p) => {
            const line = Math.min(Math.max(1, p.line ?? 1), model.getLineCount());
            const first = model.getLineFirstNonWhitespaceColumn(line) || 1;
            return {
                startLineNumber: line, startColumn: first, endLineNumber: line, endColumn: Math.max(first + 1, model.getLineMaxColumn(line)),
                severity: monaco.MarkerSeverity.Warning, source: 'PHPStan', code: p.identifier ?? undefined,
                message: p.tip ? `${p.message}\n💡 ${p.tip}` : p.message,
            };
        }));
    }

    // PHP's syntax check while typing (PhpLint.cs: php -l on the editor's text), half a second after the last key: PHP's first error at its line
    let lintTimer = 0, lintTicket = 0, saidNoPhp = false;
    const isPhp = (model) => { const rel = model && relOf(model); return !!rel && /\.php$/i.test(rel) && !/\.blade\.php$/i.test(rel) && model.getLanguageId() === 'php'; };
    async function lint(model, tries = 0) {
        if (!model || model.isDisposed()) return;
        if (!isPhp(model)) {
            // a file just opened may not have its project path (or its language) yet: asked again shortly, while it is the editor's
            if (tries < 20 && (!relOf(model) || /\.php$/i.test(relOf(model))) && editor.getModel() === model) window.setTimeout(() => void lint(model, tries + 1), 250);
            return;
        }
        const mine = ++lintTicket, version = model.getVersionId();
        const res = await fetch('/api/php/lint', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text: model.getValue() }) }).catch(() => null);
        const body = await res?.json().catch(() => null);
        if (!body || mine !== lintTicket || model.isDisposed() || model.getVersionId() !== version) return;
        let found = body.line ? { line: body.line, column: 0, message: body.message.replace(/^syntax error, /, 'Syntax error: ') } : null;
        if (body.error) {
            // no PHP to run (or Sail's container stopped): the bundled parser checks instead (php-syntax-model.js)
            try { found = phpSyntaxError(model.getValue()); } catch { found = null; }
            if (!body.noPhp && !saidNoPhp) { saidNoPhp = true; toast(body.error, true); }
        }
        if (!found) { monaco.editor.setModelMarkers(model, LINT, []); markIndentation(model); return; }
        monaco.editor.setModelMarkers(model, INDENT, []);   // a file with a syntax error: its indentation is told once it reads again
        const line = Math.min(Math.max(1, found.line), model.getLineCount());
        const end = model.getLineMaxColumn(line);
        // the parser's column when it gives one inside the line, else the line from its first non-blank character
        const first = found.column > 1 && found.column < end ? found.column : (model.getLineFirstNonWhitespaceColumn(line) || 1);
        monaco.editor.setModelMarkers(model, LINT, [{
            startLineNumber: line, startColumn: first, endLineNumber: line, endColumn: Math.max(first + 1, end),
            severity: monaco.MarkerSeverity.Error, source: 'PHP', message: found.message,
        }]);
    }
    // indentation that does not follow the file's { } blocks (php-format-model.js), as a weak warning; Alt+Enter: Fix indentation
    function markIndentation(model) {
        let found = [];
        try { found = indentProblems(model.getValue()); } catch { found = []; }
        monaco.editor.setModelMarkers(model, INDENT, found.map((p) => {
            const end = model.getLineFirstNonWhitespaceColumn(p.line) || model.getLineMaxColumn(p.line);
            return {
                startLineNumber: p.line, startColumn: 1, endLineNumber: p.line, endColumn: Math.max(2, end),
                severity: monaco.MarkerSeverity.Info, source: 'PHP', code: INDENT, message: p.message,
            };
        }));
    }
    monaco.languages.registerCodeActionProvider('php', {
        provideCodeActions(model, _range, context) {
            const marks = context.markers.filter((m) => m.code === INDENT);
            if (!marks.length) return { actions: [], dispose() { } };
            const text = model.getValue();
            const fixed = fixIndentation(text);
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

    const lintSoon = (delay = 500) => { clearTimeout(lintTimer); const model = editor.getModel(); lintTimer = window.setTimeout(() => void lint(model), delay); };
    editor.onDidChangeModelContent(() => lintSoon());

    window.addEventListener('pymagic:saved', () => void analyse(editor.getModel()));
    editor.onDidChangeModel(() => { void analyse(editor.getModel(), true); lintSoon(0); });
    void analyse(editor.getModel(), true);
    lintSoon(0);
    return { analyse: () => analyse(editor.getModel(), true) };
}

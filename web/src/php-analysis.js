// PHP's problems in the editor beside Phpactor's: PHP's syntax errors while typing (PhpLint.cs, php -l), and PHPStan's (PhpAnalysis.cs): the
// project's own PHPStan (Larastan too, through its phpstan.neon) on a .php file when it is opened and each time it is saved, its findings as
// warnings of the file with PHPStan's identifier and tip. A project without PHPStan asks nothing.
import * as monaco from 'monaco-editor';
import { toast } from './toast';

const OWNER = 'phpstan';
const LINT = 'php-lint';

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
    async function lint(model) {
        if (!isPhp(model) || model.isDisposed()) return;
        const mine = ++lintTicket, version = model.getVersionId();
        const res = await fetch('/api/php/lint', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text: model.getValue() }) }).catch(() => null);
        const body = await res?.json().catch(() => null);
        if (!body || mine !== lintTicket || model.isDisposed() || model.getVersionId() !== version) return;
        if (body.error) {
            monaco.editor.setModelMarkers(model, LINT, []);
            if (!saidNoPhp) { saidNoPhp = true; toast(body.error, true); }
            return;
        }
        if (!body.line) { monaco.editor.setModelMarkers(model, LINT, []); return; }
        const line = Math.min(Math.max(1, body.line), model.getLineCount());
        const first = model.getLineFirstNonWhitespaceColumn(line) || 1;
        monaco.editor.setModelMarkers(model, LINT, [{
            startLineNumber: line, startColumn: first, endLineNumber: line, endColumn: Math.max(first + 1, model.getLineMaxColumn(line)),
            severity: monaco.MarkerSeverity.Error, source: 'PHP', message: body.message.replace(/^syntax error, /, 'Syntax error: '),
        }]);
    }
    const lintSoon = (delay = 500) => { clearTimeout(lintTimer); const model = editor.getModel(); lintTimer = window.setTimeout(() => void lint(model), delay); };
    editor.onDidChangeModelContent(() => lintSoon());

    window.addEventListener('pymagic:saved', () => void analyse(editor.getModel()));
    editor.onDidChangeModel(() => { void analyse(editor.getModel(), true); lintSoon(0); });
    void analyse(editor.getModel(), true);
    lintSoon(0);
    return { analyse: () => analyse(editor.getModel(), true) };
}

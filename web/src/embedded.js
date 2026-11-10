// HTML, CSS and JavaScript inside templates: Monaco's HTML service on templates (tags, attributes, their documentation), and for the
// CSS and JavaScript blocks a hidden document each (embedded-model.js) that Monaco's CSS and TypeScript services check; their findings
// are copied onto the template. Inside a <script> block the TypeScript service completes; inside <style> or style="" the CSS properties.
import * as monaco from 'monaco-editor';
import { embeddedDocument, inRegion, CSS_PROPERTIES } from './embedded-model.js';

// the templates that get this, and how their expressions are read inside code (Blade's own: {!! !!}, @json, directives)
const TEMPLATES = { 'django-html': 'django', twig: 'django', blade: 'blade' };

export function setupEmbedded() {
    for (const language of Object.keys(TEMPLATES))
        monaco.languages.html.registerHTMLLanguageService(language, {}, {
            completionItems: true, hovers: true, documentHighlights: true, links: true, selectionRanges: true, colors: true,
            documentSymbols: false, foldingRanges: false, diagnostics: false, rename: false, documentFormattingEdits: false, documentRangeFormattingEdits: false,
        });
    // a block's own names are not checked against the page's other scripts (they share a window, not a file)
    monaco.languages.typescript.javascriptDefaults.setDiagnosticsOptions({ noSemanticValidation: true, noSyntaxValidation: false });

    const hidden = new Map();   // template model -> { css, js }
    const owners = new Map();   // hidden model uri -> template model
    let n = 0;

    const doc = (model, text, language) => embeddedDocument(text, language, TEMPLATES[model.getLanguageId()]);

    function sync(model) {
        let h = hidden.get(model);
        const text = model.getValue();
        if (!h) {
            n++;
            h = {
                css: monaco.editor.createModel(doc(model, text, 'css'), 'css', monaco.Uri.parse(`inmemory://embedded/${n}.css`)),
                js: monaco.editor.createModel(doc(model, text, 'javascript'), 'javascript', monaco.Uri.parse(`inmemory://embedded/${n}.js`)),
            };
            hidden.set(model, h);
            owners.set(h.css.uri.toString(), model);
            owners.set(h.js.uri.toString(), model);
            return;
        }
        h.css.setValue(doc(model, text, 'css'));
        h.js.setValue(doc(model, text, 'javascript'));
    }

    function watch(model) {
        if (!(model.getLanguageId() in TEMPLATES) || hidden.has(model)) return;
        sync(model);
        let timer = 0;
        model.onDidChangeContent(() => { clearTimeout(timer); timer = window.setTimeout(() => sync(model), 300); });
        model.onWillDispose(() => {
            const h = hidden.get(model);
            if (!h) return;
            hidden.delete(model);
            owners.delete(h.css.uri.toString());
            owners.delete(h.js.uri.toString());
            h.css.dispose();
            h.js.dispose();
        });
    }
    for (const m of monaco.editor.getModels()) watch(m);
    monaco.editor.onDidCreateModel(watch);
    monaco.editor.onDidChangeModelLanguage((e) => watch(e.model));

    // the hidden documents' findings, on the template at the same places
    monaco.editor.onDidChangeMarkers((uris) => {
        const touched = new Set();
        for (const uri of uris) {
            const owner = owners.get(uri.toString());
            if (owner && !owner.isDisposed()) touched.add(owner);
        }
        for (const owner of touched) {
            const h = hidden.get(owner);
            if (!h) continue;
            const markers = [h.css, h.js].flatMap((m) => monaco.editor.getModelMarkers({ resource: m.uri }).map((x) => ({ ...x, source: m === h.css ? 'css' : 'javascript' })));
            monaco.editor.setModelMarkers(owner, 'embedded', markers.map(({ resource, owner: _o, ...x }) => x));
        }
    });

    const KIND = monaco.languages.CompletionItemKind;
    const tsKind = (k) => ({ function: KIND.Function, method: KIND.Method, property: KIND.Property, var: KIND.Variable, let: KIND.Variable, const: KIND.Constant, class: KIND.Class, keyword: KIND.Keyword, module: KIND.Module, interface: KIND.Interface }[k] ?? KIND.Text);
    monaco.languages.registerCompletionItemProvider(Object.keys(TEMPLATES), {
        triggerCharacters: ['.'],
        async provideCompletionItems(model, position) {
            const h = hidden.get(model);
            if (!h) return { suggestions: [] };
            const offset = model.getOffsetAt(position);
            const text = model.getValue();
            const word = model.getWordUntilPosition(position);
            const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
            if (inRegion(text, offset, 'javascript')) {
                if (h.js.getValue() !== doc(model, text, 'javascript')) sync(model);
                const worker = await (await monaco.languages.typescript.getJavaScriptWorker())(h.js.uri);
                const info = await worker.getCompletionsAtPosition(h.js.uri.toString(), offset);
                return { suggestions: (info?.entries ?? []).slice(0, 300).map((e) => ({ label: e.name, kind: tsKind(e.kind), insertText: e.name, sortText: e.sortText, range })) };
            }
            if (inRegion(text, offset, 'css')) {
                // a property where a declaration starts: after "{", ";" or the start of a style="" value, before its ":"
                const before = text.slice(0, offset);
                const at = Math.max(before.lastIndexOf('{'), before.lastIndexOf(';'), before.lastIndexOf('"'), before.lastIndexOf("'"));
                if (before.slice(at + 1).includes(':')) return { suggestions: [] };
                return { suggestions: CSS_PROPERTIES.map((p) => ({ label: p, kind: KIND.Property, insertText: `${p}: `, range, command: { id: 'editor.action.triggerSuggest', title: '' } })) };
            }
            return { suggestions: [] };
        },
    });
}

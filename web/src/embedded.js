// HTML, CSS and JavaScript inside templates: Monaco's HTML service on templates (tags, attributes, their documentation), and for the
// CSS and JavaScript blocks a hidden document each (embedded-model.js) that Monaco's CSS and TypeScript services check; their findings
// are copied onto the template. Inside a <script> block the TypeScript service completes; inside <style> or style="" the CSS properties.
import * as monaco from 'monaco-editor';
import { embeddedDocument, inRegion, CSS_PROPERTIES, scriptIsTs, componentPrelude, keepFinding } from './embedded-model.js';

// the templates that get this, and how their expressions are read inside code (Blade's own: {!! !!}, @json, directives)
const TEMPLATES = { 'django-html': 'django', twig: 'django', blade: 'blade' };
// components open as HTML: their <script> (TypeScript with lang="ts") and <style> get hidden documents too
const COMPONENT = /\.(vue|svelte)$/i;
const componentOf = (model) => (model.getLanguageId() === 'html' && COMPONENT.exec(model.uri.path)?.[1].toLowerCase()) || null;
const isTemplate = (model) => model.getLanguageId() in TEMPLATES || !!componentOf(model);

export function setupEmbedded() {
    for (const language of Object.keys(TEMPLATES))
        monaco.languages.html.registerHTMLLanguageService(language, {}, {
            completionItems: true, hovers: true, documentHighlights: true, links: true, selectionRanges: true, colors: true,
            documentSymbols: false, foldingRanges: false, diagnostics: false, rename: false, documentFormattingEdits: false, documentRangeFormattingEdits: false,
        });
    // .js files are type-checked as their checkJs or // @ts-check says, as VS Code does (Monaco's JavaScript has it off by default);
    // a template's block is checked for its syntax only (keepFinding)
    monaco.languages.typescript.javascriptDefaults.setDiagnosticsOptions({ noSemanticValidation: false, noSyntaxValidation: false });

    const hidden = new Map();   // template model -> { css, js }
    const owners = new Map();   // hidden model uri -> template model
    let n = 0;

    const doc = (model, text, language) => {
        const kind = componentOf(model);
        const out = embeddedDocument(text, language, kind ? 'component' : TEMPLATES[model.getLanguageId()]);
        return kind && language === 'javascript' ? out + componentPrelude(kind, out) : out;
    };
    /**
     * Where a block's document is: a component's beside it (pymagic:/src/App.svelte.3.ts), so its imports of the project's files resolve as
     * the component's; a template's in memory.
     */
    const scriptUri = (model, script) => {
        const ext = script === 'typescript' ? 'ts' : 'js';
        return componentOf(model) ? monaco.Uri.parse(`${model.uri.toString()}.${n}.${ext}`) : monaco.Uri.parse(`inmemory://embedded/${n}.${ext}`);
    };
    /** The script's language: TypeScript for a component's <script lang="ts">. */
    const scriptOf = (model, text) => (componentOf(model) && scriptIsTs(text) ? 'typescript' : 'javascript');

    function sync(model) {
        let h = hidden.get(model);
        const text = model.getValue();
        const script = scriptOf(model, text);
        // lang="ts" added or taken away: the script's document is made again in its language
        if (h && h.js.getLanguageId() !== script) {
            owners.delete(h.js.uri.toString());
            h.js.dispose();
            n++;
            h.js = monaco.editor.createModel(doc(model, text, 'javascript'), script, scriptUri(model, script));
            owners.set(h.js.uri.toString(), model);
        }
        if (!h) {
            n++;
            h = {
                css: monaco.editor.createModel(doc(model, text, 'css'), 'css', monaco.Uri.parse(`inmemory://embedded/${n}.css`)),
                js: monaco.editor.createModel(doc(model, text, 'javascript'), script, scriptUri(model, script)),
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
        if (!isTemplate(model) || hidden.has(model)) return;
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
            const kind = componentOf(owner);
            const markers = [h.css, h.js].flatMap((m) => monaco.editor.getModelMarkers({ resource: m.uri, owner: m === h.css ? 'css' : m.getLanguageId() })
                .filter((x) => m === h.css || keepFinding(x.code, x.message, kind))
                .map((x) => ({ ...x, source: m === h.css ? 'css' : h.js.getLanguageId() })));
            monaco.editor.setModelMarkers(owner, 'embedded', markers.map(({ resource, owner: _o, ...x }) => x));
        }
    });

    const KIND = monaco.languages.CompletionItemKind;
    const tsKind = (k) => ({ function: KIND.Function, method: KIND.Method, property: KIND.Property, var: KIND.Variable, let: KIND.Variable, const: KIND.Constant, class: KIND.Class, keyword: KIND.Keyword, module: KIND.Module, interface: KIND.Interface }[k] ?? KIND.Text);
    monaco.languages.registerCompletionItemProvider([...Object.keys(TEMPLATES), 'html'], {
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
                const ts = monaco.languages.typescript;
                const worker = await (await (h.js.getLanguageId() === 'typescript' ? ts.getTypeScriptWorker() : ts.getJavaScriptWorker()))(h.js.uri);
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

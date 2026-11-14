// HTML, CSS and JavaScript inside templates: Monaco's HTML service on templates (tags, attributes, their documentation), and for the
// CSS and JavaScript blocks a hidden document each (embedded-model.js) that Monaco's CSS and TypeScript services check; their findings
// are copied onto the template. Inside a <script> block the TypeScript service completes; inside <style> or style="" the CSS properties.
import * as monaco from 'monaco-editor';
import { embeddedDocument, inRegion, CSS_PROPERTIES, scriptIsTs, componentPrelude, keepFinding, withMarkupCode } from './embedded-model.js';

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
        // a component's script, the expressions of its markup at their places, and what both may use without declaring it
        return kind && language === 'javascript' ? withMarkupCode(text, out, kind) + componentPrelude(kind, out) : out;
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
            const text = owner.getValue(), last = owner.getLineCount();
            // (what is after the file's last line is the script's prelude; a finding outside the script is one of a markup expression)
            const inMarkup = (x) => !!kind && !inRegion(text, owner.getOffsetAt({ lineNumber: x.startLineNumber, column: x.startColumn }), 'javascript');
            const markers = [h.css, h.js].flatMap((m) => monaco.editor.getModelMarkers({ resource: m.uri, owner: m === h.css ? 'css' : m.getLanguageId() })
                .filter((x) => m === h.css || (x.startLineNumber <= last && keepFinding(x.code, x.message, kind, inMarkup(x))))
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

    // a component's script and the expressions of its markup: what the TypeScript service says of the name under the cursor, and where
    // it is defined (in the component, another file of the project or a package's types)
    const serviceAt = async (model, position) => {
        const h = hidden.get(model);
        if (!h || !componentOf(model)) return null;
        const text = model.getValue();
        if (h.js.getValue() !== doc(model, text, 'javascript')) sync(model);
        const ts = monaco.languages.typescript;
        const worker = await (await (h.js.getLanguageId() === 'typescript' ? ts.getTypeScriptWorker() : ts.getJavaScriptWorker()))(h.js.uri);
        return { worker, offset: model.getOffsetAt(position), name: h.js.uri.toString(), length: text.length };
    };
    const said = (list) => (list ?? []).map((p) => p.text).join('');
    monaco.languages.registerHoverProvider('html', {
        async provideHover(model, position) {
            const s = await serviceAt(model, position).catch(() => null);
            const info = s && await s.worker.getQuickInfoAtPosition(s.name, s.offset).catch(() => null);
            if (!info || model.isDisposed()) return null;
            const a = model.getPositionAt(info.textSpan.start), b = model.getPositionAt(info.textSpan.start + info.textSpan.length);
            const notes = said(info.documentation);
            return {
                range: new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column),
                contents: [{ value: '```typescript\n' + said(info.displayParts) + '\n```' }, ...(notes ? [{ value: notes }] : [])],
            };
        },
    });
    /** A place of a text as a range: the text of an open file, or of a file the service was given (tsproject.js). */
    const rangeIn = (text, start, length) => {
        const at = (offset) => { const before = text.slice(0, offset).split('\n'); return [before.length, before[before.length - 1].length + 1]; };
        const [l1, c1] = at(start), [l2, c2] = at(start + length);
        return new monaco.Range(l1, c1, l2, c2);
    };
    monaco.languages.registerDefinitionProvider('html', {
        async provideDefinition(model, position) {
            const s = await serviceAt(model, position).catch(() => null);
            const found = s && await s.worker.getDefinitionAtPosition(s.name, s.offset).catch(() => null);
            if (!found || model.isDisposed()) return null;
            const ts = monaco.languages.typescript;
            const out = [];
            for (const d of found) {
                if (d.fileName === s.name) {
                    // in the component itself (what is declared after its end, a macro or a rune, has no place to go to)
                    if (d.textSpan.start >= s.length) continue;
                    const a = model.getPositionAt(d.textSpan.start), b = model.getPositionAt(d.textSpan.start + d.textSpan.length);
                    out.push({ uri: model.uri, range: new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column) });
                    continue;
                }
                const uri = monaco.Uri.parse(d.fileName);
                const text = monaco.editor.getModel(uri)?.getValue() ?? ts.typescriptDefaults.getExtraLibs()[d.fileName]?.content ?? ts.javascriptDefaults.getExtraLibs()[d.fileName]?.content;
                if (text === undefined || /^lib\.[\w.]*d\.ts$/.test(d.fileName)) continue;
                out.push({ uri, range: rangeIn(text, d.textSpan.start, d.textSpan.length) });
            }
            return out;
        },
    });
}

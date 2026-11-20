// HTML, CSS and JavaScript inside templates: Monaco's HTML service on templates (tags, attributes, their documentation), and for the
// CSS and JavaScript blocks a hidden document each (embedded-model.js) that Monaco's CSS and TypeScript services check; their findings
// are copied onto the template. Inside a <script> block the TypeScript service completes; inside <style> or style="" the CSS properties.
import * as monaco from 'monaco-editor';
import { embeddedDocument, inRegion, CSS_PROPERTIES, scriptIsTs, componentPrelude, keepFinding, withMarkupCode, vueScriptDocument, vueMarkupDocument, vueSpots, memberOffset, listedOffset, MEMBERS_AT, vueImports, vueProps, vuePropChecks, PROPS_AT } from './embedded-model.js';

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
        if (!kind || language !== 'javascript') return out;
        // Vue: the script, and what the markup's own document takes from it (a ref is its value in the markup, so that is checked apart)
        if (kind === 'vue') return vueScriptDocument(text, out, scriptIsTs(text));
        // Svelte: the script, the expressions of its markup at their places, and what both may use without declaring it
        return withMarkupCode(text, out, kind) + componentPrelude(kind, out);
    };
    /** A Vue component's markup document: its expressions, with the names of the script's document (named without its extension). */
    const markupDoc = (text, js, props) => vueMarkupDocument(text, js.uri.path.split('/').pop().replace(/\.(ts|js)$/, ''), js.getLanguageId() === 'typescript', props);
    // the components a component imports: their files' text (the editor's when one is open), read again after a while, for their props
    const childTexts = new Map();   // project path -> { text, at }
    function propsFor(model, text) {
        const out = {};
        const dir = decodeURIComponent(model.uri.path).replace(/^\//, '').replace(/[^/]*$/, '');
        for (const from of Object.values(vueImports(text))) {
            if (!/^\.\.?\//.test(from)) continue;
            const parts = [];
            for (const part of (dir + from).split('/')) { if (part === '..') parts.pop(); else if (part && part !== '.') parts.push(part); }
            const path = parts.join('/');
            const open = monaco.editor.getModel(model.uri.with({ path: '/' + path }));
            let childText = open?.getValue();
            if (childText === undefined) {
                const known = childTexts.get(path);
                if (!known || Date.now() - known.at > 10000) {
                    childTexts.set(path, { text: known?.text ?? null, at: Date.now() });
                    fetch('/api/file?path=' + encodeURIComponent(path)).then((r) => (r.ok ? r.text() : null)).then((t) => {
                        const before = childTexts.get(path)?.text;
                        childTexts.set(path, { text: t, at: Date.now() });
                        if (t !== before && !model.isDisposed() && hidden.has(model)) sync(model);
                    }).catch(() => { });
                }
                childText = known?.text ?? null;
            }
            const declared = childText ? vueProps(childText) : null;
            if (declared) out[from] = declared;
        }
        return out;
    }
    const markupUri = (js) => monaco.Uri.parse(js.uri.toString().replace(/\.(ts|js)$/, '.m.$1'));
    function syncMarkup(model, h, text) {
        if (componentOf(model) !== 'vue') return;
        // (the script's document made again in another language: the markup's too, beside it)
        if (h.markup && h.markup.uri.toString() !== markupUri(h.js).toString()) {
            owners.delete(h.markup.uri.toString());
            h.markup.dispose();
            h.markup = null;
        }
        const props = propsFor(model, text);
        h.checks = vuePropChecks(text, props, h.js.getLanguageId() === 'typescript');
        if (!h.markup) {
            h.markup = monaco.editor.createModel(markupDoc(text, h.js, props), h.js.getLanguageId(), markupUri(h.js));
            owners.set(h.markup.uri.toString(), model);
        }
        else h.markup.setValue(markupDoc(text, h.js, props));
    }
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
            syncMarkup(model, h, text);
            return;
        }
        h.css.setValue(doc(model, text, 'css'));
        h.js.setValue(doc(model, text, 'javascript'));
        syncMarkup(model, h, text);
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
            if (h.markup) owners.delete(h.markup.uri.toString());
            h.css.dispose();
            h.js.dispose();
            h.markup?.dispose();
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
            const inMarkup = (x, m) => m === h.markup || (!!kind && !inRegion(text, owner.getOffsetAt({ lineNumber: x.startLineNumber, column: x.startColumn }), 'javascript'));
            const markers = [h.css, h.js, ...(h.markup ? [h.markup] : [])].flatMap((m) => monaco.editor.getModelMarkers({ resource: m.uri, owner: m === h.css ? 'css' : m.getLanguageId() })
                .filter((x) => m === h.css || (x.startLineNumber <= last && keepFinding(x.code, x.message, kind, inMarkup(x, m))))
                .map((x) => ({ ...x, source: m === h.css ? 'css' : h.js.getLanguageId() })));
            // the props given to the components it uses: a value of the wrong type is told at the attribute, a required one left out at the tag
            if (h.markup && h.checks) {
                const at = h.markup.getValue().split('\n').indexOf(PROPS_AT) + 1;
                const range = ([a, z]) => { const s = owner.getPositionAt(a), e = owner.getPositionAt(z); return { startLineNumber: s.lineNumber, startColumn: s.column, endLineNumber: e.lineNumber, endColumn: e.column }; };
                for (const x of monaco.editor.getModelMarkers({ resource: h.markup.uri, owner: h.markup.getLanguageId() })) {
                    const place = at > 0 ? h.checks.places[x.startLineNumber - at - 1] : null;
                    // a value of the wrong type (a prop's, a handler's), a name a slot does not give (2339), an event's argument a statement uses wrongly
                    if (!place || ![2322, 2820, 2741, 2339, 2345, 2554].includes(Number(typeof x.code === 'object' && x.code ? x.code.value : x.code))) continue;
                    markers.push({ ...x, ...range(place), source: h.js.getLanguageId() });
                }
                for (const miss of h.checks.missing) markers.push({ ...range(miss.place), severity: monaco.MarkerSeverity.Warning, message: miss.message, source: 'vue' });
            }
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
            // inside an expression of a component's markup: what the script gives it (a member after a dot; else its names first)
            const spot = markupSpot(model, text, offset);
            if (spot) {
                if (h.js.getValue() !== doc(model, text, 'javascript')) sync(model);
                const from = h.markup ?? h.js;
                const ts = monaco.languages.typescript;
                const worker = await (await (from.getLanguageId() === 'typescript' ? ts.getTypeScriptWorker() : ts.getJavaScriptWorker()))(from.uri);
                const name = from.uri.toString();
                const afterDot = /\.\s*[\w$]*$/.test(text.slice(spot[0], offset));
                const entries = [];
                if (h.markup && !afterDot) {
                    // every name the markup may use: the members of what the markup's document takes from the script
                    const all = h.markup.getValue().lastIndexOf(`.${MEMBERS_AT}`);
                    const own = all < 0 ? null : await worker.getCompletionsAtPosition(name, all + 1).catch(() => null);
                    for (const e of own?.entries ?? []) if (!e.name.startsWith('__pm_')) entries.push({ ...e, sortText: '0' + e.name });
                }
                if (spot[2]) {
                    const info = await worker.getCompletionsAtPosition(name, offset).catch(() => null);
                    for (const e of info?.entries ?? []) if (!e.name.startsWith('__pm_') && !entries.some((x) => x.name === e.name)) entries.push(e);
                }
                return { suggestions: entries.slice(0, 300).map((e) => ({ label: e.name, kind: tsKind(e.kind), insertText: e.name, sortText: e.sortText, range })) };
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
        const offset = model.getOffsetAt(position);
        // a Vue component's markup is asked of its own document
        const from = h.markup && !inRegion(text, offset, 'javascript') ? h.markup : h.js;
        const ts = monaco.languages.typescript;
        const worker = await (await (from.getLanguageId() === 'typescript' ? ts.getTypeScriptWorker() : ts.getJavaScriptWorker()))(from.uri);
        return { worker, offset, name: from.uri.toString(), length: text.length, h, inMarkup: from === h.markup };
    };
    /**
     * Where a name of a Vue component's markup is defined, from the place the markup's document takes it from the script: a member of an
     * options object or a prop where it is written; a <script setup>'s name through the list the script's document gives the markup.
     */
    async function throughScript(s, found) {
        const out = [];
        const scriptName = s.h.js.uri.toString();
        for (const d of found) {
            if (d.fileName !== s.name || d.textSpan.start < s.length) { out.push(d); continue; }
            const markup = s.h.markup.getValue();
            const word = markup.slice(d.textSpan.start, d.textSpan.start + d.textSpan.length);
            const member = memberOffset(markup, word);
            let next = member < 0 ? [] : await s.worker.getDefinitionAtPosition(s.name, member).catch(() => null) ?? [];
            // (a <script setup>'s name is listed after the script's end: asked again there, for where it is declared)
            if (!next.length || next.every((n) => n.fileName === scriptName && n.textSpan.start >= s.length)) {
                const listed = listedOffset(s.h.js.getValue(), word);
                next = listed < 0 ? [] : await s.worker.getDefinitionAtPosition(scriptName, listed).catch(() => null) ?? [];
            }
            out.push(...next.filter((n) => !((n.fileName === scriptName || n.fileName === s.name) && n.textSpan.start >= s.length)));
        }
        return out;
    }
    /** The expression of a component's markup an offset is in: [start, end, written in the hidden document], or null. */
    function markupSpot(model, text, offset) {
        const kind = componentOf(model);
        if (kind === 'vue') {
            const written = vueSpots(text);
            for (const [a, z] of vueSpots(text, true)) if (offset >= a && offset <= z) return [a, z, written.some((w) => w[0] === a)];
            return null;
        }
        if (kind !== 'svelte' || inRegion(text, offset, 'javascript') || inRegion(text, offset, 'css')) return null;
        // Svelte: inside { … } of the markup, not a block's own tag ({#each …}, {:else})
        const open = text.lastIndexOf('{', offset - 1), shut = text.lastIndexOf('}', offset - 1);
        if (open < 0 || shut > open || /^\s*[#:/@]/.test(text.slice(open + 1))) return null;
        const h = hidden.get(model);
        return [open + 1, offset, !!h && h.js.getValue()[open] === ';'];
    }
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
            let found = s && await s.worker.getDefinitionAtPosition(s.name, s.offset).catch(() => null);
            if (!found || model.isDisposed()) return null;
            if (s.inMarkup) found = await throughScript(s, found);
            const ts = monaco.languages.typescript;
            const out = [];
            const own = [s.name, s.h.js.uri.toString()];
            for (const d of found) {
                if (own.includes(d.fileName)) {
                    // in the component itself (what is declared after its end, a macro or a rune, has no place to go to)
                    if (d.textSpan.start >= s.length) continue;
                    const a = model.getPositionAt(d.textSpan.start), b = model.getPositionAt(d.textSpan.start + d.textSpan.length);
                    out.push({ uri: model.uri, range: new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column) });
                    continue;
                }
                if (/__pymagic_components\.d\.ts$/.test(d.fileName)) {
                    // a component this one imports: the file it is
                    const word = model.getWordAtPosition(position)?.word ?? '';
                    const named = word && new RegExp(`\\bimport\\s+${word.replace(/\$/g, '\\$')}\\s+from\\s+['"]([^'"]+)['"]`).exec(model.getValue());
                    const spec = named?.[1] ?? /['"]([^'"]+\.(?:vue|svelte))['"]/.exec(model.getLineContent(position.lineNumber))?.[1];
                    if (spec?.startsWith('.')) out.push({ uri: model.uri.with({ path: new URL(spec, 'file://' + model.uri.path).pathname }), range: new monaco.Range(1, 1, 1, 1) });
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

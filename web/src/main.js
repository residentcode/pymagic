import * as monaco from 'monaco-editor';
import { libraryModel, showLibraryFile } from './libview.js';
import { LIB_SCHEME, libraryPathOf, pathOfLibraryUri } from './libview-model.js';
import { LspClient } from './lsp';
import { setupRun } from './run';
import { setupTests } from './tests';
import { setupInterpreters } from './interpreters';
import { setupPackages } from './packages';
import { setupRefactor } from './refactor';
import { setupSearch } from './search';
import { setupEditorNav } from './editornav';
import { setupCodeVision } from './codevision';
import { setupCodeGen } from './codegen';
import { setupSharedText } from './sharedtext';
import { setupPythonConsole } from './pyconsole';
import { setupDiagrams } from './diagram';
import { setupTemplateLinks } from './templatelinks';
import { setupEmbedded } from './embedded';
import { setupTemplatePreview } from './templatepreview';
import { addModelAdmin, addModelSerializer } from './django-model';
import { setLiveTemplateSource, openSettings } from './settings-ui';
import { setupHierarchy } from './hierarchy';
import { setupRoutes } from './routes';
import { setupDjango } from './django';
import { setupProblems, setupTodo } from './problems';
import { setupTerminal } from './terminal';
import { setupCoverage } from './coverage';
import { setupRequirementsCheck } from './requirements-check';
import { setupNpmCheck } from './npm-check';
import { setupFileChecks } from './filecheck';
import { setupSchemaCheck } from './schemacheck';
import { setupSchemaPicker } from './schema-picker';
import { pluginOn, hiddenElements } from './plugin-model';
import { setupSemantic } from './semantic';
import { setupProfiler } from './profiler';
import { setupDatabase } from './database';
import { setupHttpClient } from './http';
import './ejs-language';
import './gherkin-language';
import './toml-language';
import { languageFor as languageForFile } from './file-language';
import { setupEmmet } from './emmet';
import { setupTagPairs } from './tag-pairs';
import { setupFullLine } from './fullline';
import { setupTailwind } from './tailwind';
import { projectDesktop } from './openproject.js';
import { setupNotebook } from './notebook';
import { buildInfo, buildLabel } from './build-info.js';
import { setupGitSync } from './git-sync';
import { setupGitShelf } from './git-shelf';
import { setupExplorer } from './explorer';
import { setupTabs } from './tabs';
import { setupSplit } from './split';
import { relUnderRoot } from './uripath';
import { setupToolbarFit } from './toolbar-fit';
import { setupDiskWatch } from './diskwatch';
import { setupInspections } from './inspections';
import { setupGit } from './git';
import { toast } from './toast';
import { askChoice } from './ask';
import { setupChrome } from './rail';
import { setupToolbar } from './toolbar';
import { setupMainMenu, refactorMenu } from './mainmenu';
import { setupStructure } from './structure';
import { setupBookmarks } from './bookmarks';
import { setupRecent } from './recent';
import { setupRunConfigurations } from './runconfig';
import { setupHistory } from './history';
import { setupScratches } from './scratches';
import { setupInjections } from './injection';
import { setupSpelling } from './spell';
import { setupGrammar } from './grammar';
import { setupMarkdownPreview } from './mdpreview';
import { setupMarkdownTools } from './mdtools';
import { setupMacros } from './macros';
import { setupServices } from './services';
import { setupDeployment } from './deployment';
import { setupPullRequests } from './pullrequests';
import { setupRunAnything } from './runanything';
import { setupFileActions, fileTypes } from './fileactions';
import { setupAutoSave } from './autosave';
import { themeName, defineTheme, defineLightTheme } from './theme';
import { loadSettings, editorOptions, applySettings, followSettings, themeNow, settings, withoutCallParens, onSettings, change as changeSetting, pluginsAtStart } from './settings';
import { requote } from './quotes-model';
import { defineCommand, setupCommands, applyKeybindings, currentKeyLabel, runById } from './commands.js';
import { setKeyLabels } from './menu';
// each language service runs in its worker; the rest share the editor's
self.MonacoEnvironment = {
    getWorker: (_id, label) => new Worker(
        ['html', 'handlebars', 'razor', 'django-html'].includes(label) ? 'html.worker.js'
            : ['css', 'scss', 'less'].includes(label) ? 'css.worker.js'
                : ['typescript', 'javascript'].includes(label) ? 'ts.worker.js'
                    : label === 'json' ? 'json.worker.js' : 'editor.worker.js'),
};
const toPos = (p) => ({ line: p.lineNumber - 1, character: p.column - 1 });
const toRange = (r) => ({
    startLineNumber: r.start.line + 1, startColumn: r.start.character + 1,
    endLineNumber: r.end.line + 1, endColumn: r.end.character + 1,
});
const markerSeverity = (s) => s === 1 ? monaco.MarkerSeverity.Error : s === 2 ? monaco.MarkerSeverity.Warning
    : s === 3 ? monaco.MarkerSeverity.Info : monaco.MarkerSeverity.Hint;
const K = monaco.languages.CompletionItemKind;
const completionKinds = {
    2: K.Method, 3: K.Function, 5: K.Field, 6: K.Variable, 7: K.Class, 9: K.Module, 10: K.Property,
    14: K.Keyword, 15: K.Snippet, 21: K.Constant, 25: K.TypeParameter,
};
const S = monaco.languages.SymbolKind;
const symbolKinds = {
    2: S.Module, 5: S.Class, 6: S.Method, 7: S.Property, 8: S.Field, 9: S.Constructor, 12: S.Function,
    13: S.Variable, 14: S.Constant, 26: S.TypeParameter,
};
// ---------------------------------------------------------------- workspace state
const lsp = new LspClient();
const $ = (id) => document.getElementById(id);
let rootUri = '';
const models = new Map(); // server uri (normalised) -> model
const serverUri = new WeakMap();
const savedText = new Map(); // relative path -> last saved/loaded text
const relPathOf = new Map(); // normalised uri -> relative path
const loading = new Map();
const normalise = (uri) => decodeURIComponent(uri).toLowerCase();
const uriFor = (rel) => rootUri + rel.split('/').map(encodeURIComponent).join('/');
// a file's path in the project: those the page has loaded, else any address under the project folder (a file the engine reports that is not open)
const relOfUri = (uri) => relPathOf.get(normalise(uri)) ?? relUnderRoot(uri, rootUri);
function ensureModel(rel) {
    const key = normalise(uriFor(rel));
    let p = loading.get(key);
    if (!p) {
        // a type chosen with Override File Type colours the file as that language
        loading.set(key, (p = loadModel(rel, key).then((model) => { const chosen = fileTypes()[rel]; if (model && chosen) applyLanguage(rel, model, chosen); return model; })));
    }
    return p;
}
// Django and Jinja templates: HTML with {{ variables }}, {% tags %} and {# comments #} (Jinja's may span lines). Monaco's own HTML mode needs a language worker this page does not ship.
monaco.languages.register({ id: 'django-html', extensions: ['.html', '.htm', '.jinja', '.jinja2', '.j2'], aliases: ['Django template'] });
monaco.languages.setMonarchTokensProvider('django-html', {
    tokenPostfix: '.html',   // its tokens are named as Monaco's HTML names them (delimiter.html): Emmet tells from them where an element may start
    defaultToken: '',        // plain text has no name, as in Monaco's HTML (Emmet expands only there)
    tokenizer: {
        root: [
            [/\{#/, 'comment', '@djangoComment'],
            [/\{%[-+]?/, { token: 'keyword', next: '@djangoTag' }],
            [/\{\{[-+]?/, { token: 'variable', next: '@djangoVariable' }],
            [/<!--/, 'comment', '@htmlComment'],
            [/(<\/?)([\w:-]+)/, ['delimiter', 'tag']],
            [/\/?>/, 'delimiter'],
            [/([\w:-]+)(\s*=)/, ['attribute.name', 'delimiter']],
            [/"[^"{]*/, { token: 'attribute.value', next: '@dq' }],
            [/'[^'{]*/, { token: 'attribute.value', next: '@sq' }],
        ],
        dq: [[/\{%[-+]?/, { token: 'keyword', next: '@djangoTag' }], [/\{\{[-+]?/, { token: 'variable', next: '@djangoVariable' }], [/[^"{]+/, 'attribute.value'], [/"/, 'attribute.value', '@pop'], [/\{/, 'attribute.value']],
        sq: [[/\{%[-+]?/, { token: 'keyword', next: '@djangoTag' }], [/\{\{[-+]?/, { token: 'variable', next: '@djangoVariable' }], [/[^'{]+/, 'attribute.value'], [/'/, 'attribute.value', '@pop'], [/\{/, 'attribute.value']],
        // Django's tags and Jinja's (macro, call, set, import, raw, ...), with Jinja's whitespace marks {%- -%}
        djangoTag: [[/[-+]?%\}/, { token: 'keyword', next: '@pop' }], [/"[^"]*"|'[^']*'/, 'string'], [/\|\s*\w+/, 'type'], [/\b(?:if|elif|else|endif|for|endfor|empty|block|endblock|extends|include|load|url|with|endwith|csrf_token|comment|endcomment|autoescape|endautoescape|static|trans|endtrans|pluralize|blocktrans|endblocktrans|spaceless|endspaceless|now|firstof|cycle|macro|endmacro|call|endcall|set|endset|import|from|raw|endraw|filter|endfilter|do|break|continue|recursive|is|in|not|and|or|as)\b/, 'keyword'], [/[^%"'|+-]+/, 'keyword'], [/[%|+-]/, 'keyword']],
        djangoVariable: [[/[-+]?\}\}/, { token: 'variable', next: '@pop' }], [/"[^"]*"|'[^']*'/, 'string'], [/\|\s*\w+/, 'type'], [/[^}"'|+-]+/, 'variable'], [/[}|+-]/, 'variable']],
        djangoComment: [[/#\}/, 'comment', '@pop'], [/[^#]+/, 'comment'], [/#/, 'comment']],
        htmlComment: [[/-->/, 'comment', '@pop'], [/[^-]+/, 'comment'], [/-/, 'comment']],
    },
});
/** The language of a file the page shows: Python, templates, and whatever Monaco has a plain highlighter for; the modes that bring a language service (a worker) stay plain text. */
// the template language of each file type (the Django page of the settings): "none" is plain HTML, which the language server does not read as a template
let templateLanguages = {};
fetch('/api/django/config').then((r) => (r.ok ? r.json() : null)).then((c) => { templateLanguages = c?.templateLanguages ?? {}; templateLanguages.__off = c && c.enabled === false; }).catch(() => { /* the defaults */ });
window.addEventListener('pymagic:djangoConfig', (e) => { templateLanguages = e.detail?.templateLanguages ?? {}; templateLanguages.__off = e.detail?.enabled === false; });
function templateLanguageOf(rel) {
    const ext = rel.toLowerCase().match(/\.[^.\/]+$/)?.[0] ?? '';
    const chosen = templateLanguages[ext] ?? (/^\.(jinja2?|j2)$/.test(ext) ? 'jinja' : /^\.html?$/.test(ext) ? 'django' : 'none');
    return chosen === 'django' && templateLanguages.__off ? 'none' : chosen;
}
// the language a file is shown in: Monaco's, and what it leaves out (view engines, components, files known by name or by their #! line)
function languageFor(rel, text = '') {
    return languageForFile(monaco, rel, text);
}
async function loadModel(rel, key) {
    const uri = uriFor(rel);
    const res = await fetch('/api/file?path=' + encodeURIComponent(rel));
    if (!res.ok)
        return null;
    const text = await res.text();
    // search results can lead to files that are not Python: they are shown; only templates are known to the language server
    if (!rel.endsWith('.py')) {
        const lang = languageFor(rel, text);
        const plain = monaco.editor.createModel(text, lang, monaco.Uri.parse('pymagic:///' + rel));
        models.set(key, plain);
        serverUri.set(plain, uri);
        relPathOf.set(key, rel);
        savedText.set(rel, text);
        sharedText.watch(rel, plain, () => plain.getValue() !== savedText.get(rel));
        plain.onDidChangeContent(() => {
            refreshDirty();
            if (served.has(plain))
                lsp.notify('textDocument/didChange', { textDocument: { uri, version: plain.getVersionId() }, contentChanges: [{ text: plain.getValue() }] });
        });
        if (lang === 'django-html' && templateLanguageOf(rel) !== 'none') {
            lsp.notify('textDocument/didOpen', { textDocument: { uri, languageId: 'django-html', version: plain.getVersionId(), text } });
            let templateTimer;
            plain.onDidChangeContent(() => {
                refreshDirty();
                clearTimeout(templateTimer);
                templateTimer = window.setTimeout(() => lsp.notify('textDocument/didChange', {
                    textDocument: { uri, version: plain.getVersionId() },
                    contentChanges: [{ text: plain.getValue() }],
                }), 120);
            });
        }
        else if (rel.toLowerCase().endsWith('.feature') && pluginOn(pluginsAtStart(), 'bdd')) {
            // Gherkin: the language server checks its steps against the project's behave / pytest-bdd step definitions
            lsp.notify('textDocument/didOpen', { textDocument: { uri, languageId: 'gherkin', version: plain.getVersionId(), text } });
            let featureTimer;
            plain.onDidChangeContent(() => {
                clearTimeout(featureTimer);
                featureTimer = window.setTimeout(() => lsp.notify('textDocument/didChange', {
                    textDocument: { uri, version: plain.getVersionId() },
                    contentChanges: [{ text: plain.getValue() }],
                }), 120);
            });
        }
        return plain;
    }
    const model = monaco.editor.createModel(text, 'python', monaco.Uri.parse('pymagic:///' + rel));
    models.set(key, model);
    serverUri.set(model, uri);
    relPathOf.set(key, rel);
    savedText.set(rel, text);
    sharedText.watch(rel, model, () => model.getValue() !== savedText.get(rel));
    runHooks?.onModelCreated(rel, model);
    serveAsPython(model, true);
    let timer;
    model.onDidChangeContent(() => {
        refreshDirty();
        clearTimeout(timer);
        if (!served.has(model))
            return;   // shown as another type (Override File Type): the language server does not have it
        timer = window.setTimeout(() => lsp.notify('textDocument/didChange', {
            textDocument: { uri, version: model.getVersionId() },
            contentChanges: [{ text: model.getValue() }],
        }), 120);
    });
    return model;
}
/** A type for a file (Override File Type): its colours, and whether the language server reads it as Python (templates keep their own reader). */
function applyLanguage(rel, model, id) {
    monaco.editor.setModelLanguage(model, id);
    if (languageFor(rel) !== 'django-html')
        serveAsPython(model, id === 'python');
}
/** Unsaved text shared with PyMagic's other windows on this project (sharedtext.js). */
const sharedText = setupSharedText({
    modelOf: (rel) => models.get(normalise(uriFor(rel))),
    markSaved: (rel, text) => { savedText.set(rel, text); refreshDirty(); },
});
const noteSaved = (rel, text) => { savedText.set(rel, text); sharedText.saved(rel, text); };
/** The models the language server reads as Python: the .py files, and any file whose type was overridden to Python. */
const served = new WeakSet();
function serveAsPython(model, on) {
    const uri = serverUri.get(model);
    if (!uri || on === served.has(model))
        return;
    if (on) {
        served.add(model);
        lsp.notify('textDocument/didOpen', { textDocument: { uri, languageId: 'python', version: model.getVersionId(), text: model.getValue() } });
    }
    else {
        served.delete(model);
        lsp.notify('textDocument/didClose', { textDocument: { uri } });
        monaco.editor.setModelMarkers(model, 'pymagic', []);
    }
}
/** Resolves a location the server returned to a model, loading the file if it is not open yet. */
async function modelForUri(uri) {
    const key = normalise(uri);
    const open = models.get(key);
    if (open)
        return open;
    if (key.startsWith(normalise(rootUri)))
        return ensureModel(decodeURIComponent(uri.slice(rootUri.length)));
    return null; // outside the workspace (typeshed, the standard library): not editable here
}
// ---------------------------------------------------------------- language features
/** Applies LSP text edits to a model as one undo step. */
function applyTextEdits(model, edits) {
    if (edits.length === 0)
        return;
    editor.pushUndoStop();
    model.pushEditOperations([], edits.map((e) => ({ range: toRange(e.range), text: e.newText })), () => null);
    editor.pushUndoStop();
}
/** The formatter the toolbar selects: built-in, or black / ruff through the project's interpreter. */
function formatOptions() {
    let engine = 'builtin';
    try {
        engine = localStorage.getItem('pymagic.formatter') ?? 'builtin';
    }
    catch { /* storage unavailable */ }
    return { tabSize: 4, insertSpaces: true, pymagicEngine: engine };
}
/** Tells the toolbar which formatters exist for the current interpreter. */
async function refreshFormatters() {
    const select = $('fmt-engine');
    try {
        const list = (await lsp.request('pymagic/formatters', {})) ?? [];
        for (const f of list) {
            const option = Array.from(select.options).find((o) => o.value === f.id);
            if (!option)
                continue;
            option.textContent = f.id === 'builtin' ? 'built-in' : f.label;
            option.disabled = !f.available;
        }
        // what the built-in formatter will do for this file, as its project's pyproject.toml asks
        const model = editor.getModel();
        const uri = model && serverUri.get(model);
        if (uri) {
            const cfg = await lsp.request('pymagic/formatConfig', { textDocument: { uri } });
            if (cfg) {
                const quotes = cfg.quotes.toLowerCase() === 'preserve' ? 'quotes as written' : `${cfg.quotes.toLowerCase()} quotes`;
                select.title = `Formatter. The built-in one uses line length ${cfg.lineLength}, ${quotes}`
                    + (cfg.magicTrailingComma ? '' : ', no magic trailing comma') + (cfg.indentWidth !== 4 ? `, indent ${cfg.indentWidth}` : '')
                    + (cfg.source ? ` (from ${cfg.source.split(/[\\/]/).pop()})` : ' (no pyproject.toml settings)');
            }
        }
        // a formatter that is gone (uninstalled, other interpreter) falls back to the built-in one
        const chosen = list.find((f) => f.id === select.value);
        if (chosen && !chosen.available) {
            select.value = 'builtin';
            try {
                localStorage.setItem('pymagic.formatter', 'builtin');
            }
            catch { /* storage unavailable */ }
        }
    }
    catch { /* the server is not ready */ }
}
async function formatCurrent(quiet = false) {
    const model = editor.getModel();
    const uri = model && serverUri.get(model);
    if (!model || !uri || !relPathOf.get(normalise(uri))?.endsWith('.py'))
        return false;
    lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
    try {
        // with a selection only the lines it touches are formatted
        const sel = editor.getSelection();
        const partial = !quiet && sel && !sel.isEmpty();
        const edits = partial
            ? await lsp.request('textDocument/rangeFormatting', {
                textDocument: { uri },
                range: { start: { line: sel.startLineNumber - 1, character: sel.startColumn - 1 }, end: { line: sel.endLineNumber - 1, character: sel.endColumn - 1 } },
                options: formatOptions(),
            })
            : await lsp.request('textDocument/formatting', { textDocument: { uri }, options: formatOptions() });
        applyTextEdits(model, edits ?? []);
        if (!quiet)
            toast(edits?.length ? (partial ? 'Formatted the selection' : 'Formatted') : 'Already formatted');
        return true;
    }
    catch (e) {
        toast(e.message ?? 'The file cannot be formatted.', true);
        return false;
    }
}
/** Reformat Code or Optimize Imports of a file from the project tree, open or not; a file that had no unsaved changes is saved after it. */
async function tidyFile(rel, kind) {
    const model = await ensureModel(rel);
    const uri = model && serverUri.get(model);
    if (!model || !uri)
        return false;
    const wasSaved = model.getValue() === savedText.get(rel);
    lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
    try {
        const before = model.getValue();
        const edits = kind === 'format'
            ? await lsp.request('textDocument/formatting', { textDocument: { uri }, options: formatOptions() })
            : await lsp.request('pymagic/organizeImports', { textDocument: { uri } });
        applyTextEdits(model, edits ?? []);
        if (model.getValue() === before)
            return false;
        if (wasSaved)
            await saveFile(rel);
        return true;
    }
    catch {
        return false;
    }
}
async function organizeImports() {
    const model = editor.getModel();
    const uri = model && serverUri.get(model);
    if (!model || !uri || !relPathOf.get(normalise(uri))?.endsWith('.py'))
        return;
    lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
    try {
        const edits = await lsp.request('pymagic/organizeImports', { textDocument: { uri } });
        applyTextEdits(model, edits ?? []);
        toast(edits?.length ? 'Imports organized' : 'Imports are already in order');
    }
    catch (e) {
        toast(e.message ?? 'The imports cannot be organized.', true);
    }
}
/** Python and Django templates share hover, go to definition, completion, usages and rename. */
const CODE_LANGUAGES = ['python', 'django-html'];
function registerProviders() {
    monaco.languages.registerDocumentFormattingEditProvider('python', {
        async provideDocumentFormattingEdits(model) {
            if (notebook?.isCell(model)) {
                // a cell is formatted as the piece of code it is, not through the notebook's file
                try {
                    const text = await notebook.formatted(model);
                    return text === model.getValue() ? [] : [{ range: model.getFullModelRange(), text }];
                }
                catch (e) {
                    toast(e.message ?? 'The cell cannot be formatted.', true);
                    return [];
                }
            }
            const uri = serverUri.get(model);
            if (!uri)
                return [];
            try {
                const edits = (await lsp.request('textDocument/formatting', { textDocument: { uri }, options: formatOptions() })) ?? [];
                return edits.map((e) => ({ range: toRange(e.range), text: e.newText }));
            }
            catch (e) {
                toast(e.message ?? 'The file cannot be formatted.', true);
                return [];
            }
        },
    });
    monaco.languages.registerDocumentRangeFormattingEditProvider('python', {
        async provideDocumentRangeFormattingEdits(model, range) {
            if (notebook?.isCell(model)) {
                // the selected lines of a cell, the cell read as the piece of code it is
                try {
                    const text = await notebook.formatted(model, { start: { line: range.startLineNumber - 1, character: range.startColumn - 1 }, end: { line: range.endLineNumber - 1, character: range.endColumn - 1 } });
                    return text === model.getValue() ? [] : [{ range: model.getFullModelRange(), text }];
                }
                catch (e) {
                    toast(e.message ?? 'The selection cannot be formatted.', true);
                    return [];
                }
            }
            const uri = serverUri.get(model);
            if (!uri)
                return [];
            try {
                const edits = (await lsp.request('textDocument/rangeFormatting', {
                    textDocument: { uri },
                    range: { start: { line: range.startLineNumber - 1, character: range.startColumn - 1 }, end: { line: range.endLineNumber - 1, character: range.endColumn - 1 } },
                    options: formatOptions(),
                })) ?? [];
                return edits.map((e) => ({ range: toRange(e.range), text: e.newText }));
            }
            catch (e) {
                toast(e.message ?? 'The selection cannot be formatted.', true);
                return [];
            }
        },
    });
    monaco.languages.registerHoverProvider(CODE_LANGUAGES, {
        async provideHover(model, position, token) {
            // stopped in the debugger on a value: its popup (run.js) shows it instead, as PyCharm's does
            if (runHooks?.debugValueAt?.(model, position))
                return null;
            const cell = notebook?.targetOf(model, position); // a notebook cell: the position is in the notebook's file
            const uri = cell ? cell.uri : serverUri.get(model);
            if (!uri)
                return null;
            const h = await lsp.request('textDocument/hover', { textDocument: { uri }, position: cell ? cell.position : toPos(position) }, token);
            if (!h)
                return null;
            return { range: toRange(cell ? cell.fromServer(h.range) : h.range), contents: [{ value: h.contents.value }] };
        },
    });
    // a .feature file's steps go to their step definitions (LanguageService.Bdd.cs)
    monaco.languages.registerDefinitionProvider([...CODE_LANGUAGES, 'gherkin'], {
        async provideDefinition(model, position) {
            const cell = notebook?.targetOf(model, position);
            const uri = cell ? cell.uri : serverUri.get(model);
            if (!uri)
                return null;
            const locs = (await lsp.request('textDocument/definition', { textDocument: { uri }, position: cell ? cell.position : toPos(position) })) ?? [];
            const result = [];
            for (const l of locs) {
                const inCell = notebook?.locate(l.uri, l.range); // defined in a cell of a notebook
                if (inCell) {
                    result.push({ uri: inCell.model.uri, range: toRange(inCell.range) });
                    continue;
                }
                const target = await modelForUri(l.uri);
                if (target) {
                    result.push({ uri: target.uri, range: toRange(l.range) });
                    continue;
                }
                // a library (the standard library, site-packages, typeshed): a read-only model, which peek shows and opening shows in the library window
                const libPath = libraryPathOf(l.uri, rootUri);
                const lib = libPath ? await libraryModel(libPath) : null;
                if (lib)
                    result.push({ uri: lib.uri, range: toRange(l.range) });
            }
            return result;
        },
    });
    const completionProvider = (triggerCharacters) => ({
        triggerCharacters,
        async provideCompletionItems(model, position, _context, token) {
            const cell = notebook?.targetOf(model, position); // targetOf also brings the notebook's file up to date
            const uri = cell ? cell.uri : serverUri.get(model);
            if (!uri)
                return { suggestions: [] };
            const word = model.getWordUntilPosition(position);
            const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
            // make sure the server has the text the cursor is in, not the debounced previous version
            if (!cell)
                lsp.notify('textDocument/didChange', {
                    textDocument: { uri, version: model.getVersionId() },
                    contentChanges: [{ text: model.getValue() }],
                });
            const res = await lsp.request('textDocument/completion', { textDocument: { uri }, position: cell ? cell.position : toPos(position) }, token);
            return {
                suggestions: (res?.items ?? []).map((i) => ({
                    label: i.label,
                    kind: completionKinds[i.kind] ?? K.Text,
                    detail: i.detail,
                    documentation: i.documentation ? { value: i.documentation } : undefined,
                    insertText: i.textEdit && !cell ? i.textEdit.newText : i.isSnippet ? withoutCallParens(i.insertText ?? i.label) : i.insertText ?? i.label,
                    insertTextRules: i.isSnippet ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
                    // an import to add, or the expression a postfix template takes over
                    // an import added at the top of a notebook's file would land in another cell: no edits there
                    additionalTextEdits: cell ? undefined : i.additionalTextEdits?.map((e) => ({ range: toRange(e.range), text: e.newText })),
                    filterText: i.filterText,
                    sortText: i.sortText,
                    // a step of a .feature file replaces all that is typed after its keyword, not just the word
                    range: i.textEdit && !cell ? toRange(i.textEdit.range) : range,
                })),
            };
        },
    });
    monaco.languages.registerCompletionItemProvider('python', completionProvider(['.']));
    // in a template the names after `{%`, `|` and a quote are what is typed next
    monaco.languages.registerCompletionItemProvider('django-html', completionProvider(['.', '|', '"', "'", ' ', '%']));
    // in a .feature file the steps come after a keyword and its space
    monaco.languages.registerCompletionItemProvider('gherkin', completionProvider([' ']));
    monaco.languages.registerReferenceProvider(CODE_LANGUAGES, {
        async provideReferences(model, position, context) {
            const cell = notebook?.targetOf(model, position);
            const uri = cell ? cell.uri : serverUri.get(model);
            if (!uri)
                return [];
            const locs = (await lsp.request('textDocument/references', {
                textDocument: { uri }, position: cell ? cell.position : toPos(position), context: { includeDeclaration: context.includeDeclaration },
            })) ?? [];
            const result = [];
            for (const l of locs) {
                const inCell = notebook?.locate(l.uri, l.range); // a use in a cell of the notebook
                if (inCell) {
                    result.push({ uri: inCell.model.uri, range: toRange(inCell.range) });
                    continue;
                }
                const target = await modelForUri(l.uri);
                if (target)
                    result.push({ uri: target.uri, range: toRange(l.range) });
            }
            return result;
        },
    });
    let renameBase = false; // the answer to "rename the base method?" for the rename being prepared
    monaco.languages.registerRenameProvider(CODE_LANGUAGES, {
        async resolveRenameLocation(model, position) {
            const cell = notebook?.targetOf(model, position);
            const uri = cell ? cell.uri : serverUri.get(model);
            if (!uri)
                return { range: new monaco.Range(1, 1, 1, 1), text: '', rejectReason: 'not a workspace file' };
            try {
                const r = await lsp.request('textDocument/prepareRename', { textDocument: { uri }, position: cell ? cell.position : toPos(position) });
                if (!r)
                    return { range: new monaco.Range(1, 1, 1, 1), text: '', rejectReason: 'Nothing to rename here.' };
                // a method that overrides another: as PyCharm, ask whether the base method goes with it; a library's cannot
                renameBase = false;
                if (r.baseClass || r.libraryBase) {
                    const answer = await askRenameScope(r);
                    if (answer === 'cancel')
                        return { range: new monaco.Range(1, 1, 1, 1), text: '', rejectReason: 'Rename cancelled.' };
                    renameBase = answer === 'base';
                }
                return { range: toRange(cell ? cell.fromServer(r.range) : r.range), text: r.placeholder };
            }
            catch (e) {
                return { range: new monaco.Range(1, 1, 1, 1), text: '', rejectReason: e.message };
            }
        },
        async provideRenameEdits(model, position, newName) {
            const cell = notebook?.targetOf(model, position);
            const uri = cell ? cell.uri : serverUri.get(model);
            if (!uri)
                return { edits: [] };
            try {
                const edit = await lsp.request('textDocument/rename', { textDocument: { uri }, position: cell ? cell.position : toPos(position), newName, renameBase });
                const edits = [];
                for (const [target, list] of Object.entries(edit.changes)) {
                    for (const e of list) {
                        const inCell = notebook?.locate(target, e.range); // the name in a cell of the notebook
                        if (inCell) {
                            edits.push({ resource: inCell.model.uri, versionId: undefined, textEdit: { range: toRange(inCell.range), text: e.newText } });
                            continue;
                        }
                        const m = await modelForUri(target);
                        if (m)
                            edits.push({ resource: m.uri, versionId: undefined, textEdit: { range: toRange(e.range), text: e.newText } });
                    }
                }
                return { edits };
            }
            catch (e) {
                return { edits: [], rejectReason: e.message };
            }
        },
    });
    monaco.languages.registerCodeActionProvider(['python', 'gherkin'], {
        async provideCodeActions(model, range) {
            const from = notebook?.targetOf(model, { lineNumber: range.startLineNumber, column: range.startColumn });
            const to = from && notebook.targetOf(model, { lineNumber: range.endLineNumber, column: range.endColumn });
            const uri = from ? from.uri : serverUri.get(model);
            if (!uri || from && !to)
                return { actions: [], dispose() { } };
            // the server recomputes diagnostics from its own copy, so send it the text the cursor is in
            if (!from)
                lsp.notify('textDocument/didChange', {
                    textDocument: { uri, version: model.getVersionId() },
                    contentChanges: [{ text: model.getValue() }],
                });
            const lspRange = from ? { start: from.position, end: to.position } : {
                start: { line: range.startLineNumber - 1, character: range.startColumn - 1 },
                end: { line: range.endLineNumber - 1, character: range.endColumn - 1 },
            };
            const found = (await lsp.request('textDocument/codeAction', {
                textDocument: { uri }, range: lspRange, context: { diagnostics: [] },
            })) ?? [];
            const actions = [];
            for (const a of found) {
                const edits = [];
                let elsewhere = false;
                for (const [target, list] of Object.entries(a.edit?.changes ?? {})) {
                    if (from) {
                        // in a notebook only what changes this cell is offered: an import added at the top of the file would land in another cell
                        for (const e of list) {
                            const inCell = notebook.locate(target, e.range);
                            if (inCell?.model === model)
                                edits.push({ resource: model.uri, versionId: undefined, textEdit: { range: toRange(inCell.range), text: e.newText } });
                            else
                                elsewhere = true;
                        }
                        continue;
                    }
                    const m = await modelForUri(target);
                    if (!m)
                        continue;
                    for (const e of list)
                        edits.push({ resource: m.uri, versionId: undefined, textEdit: { range: toRange(e.range), text: e.newText } });
                }
                if (elsewhere)
                    continue;
                actions.push({
                    title: a.title, kind: a.kind, isPreferred: a.isPreferred,
                    edit: edits.length ? { edits } : undefined,
                    command: a.command ? { id: a.command.command, title: a.command.title, arguments: a.command.arguments } : undefined,
                });
            }
            return { actions, dispose() { } };
        },
    });
    monaco.languages.registerSignatureHelpProvider(CODE_LANGUAGES, {
        signatureHelpTriggerCharacters: ['(', ','],
        signatureHelpRetriggerCharacters: [')'],
        async provideSignatureHelp(model, position, token) {
            const cell = notebook?.targetOf(model, position); // targetOf also brings the notebook's file up to date
            const uri = cell ? cell.uri : serverUri.get(model);
            if (!uri)
                return null;
            // what is being typed is not at the server yet
            if (!cell)
                lsp.notify('textDocument/didChange', {
                    textDocument: { uri, version: model.getVersionId() },
                    contentChanges: [{ text: model.getValue() }],
                });
            const h = await lsp.request('textDocument/signatureHelp', { textDocument: { uri }, position: cell ? cell.position : toPos(position) }, token);
            if (!h)
                return null;
            return {
                value: {
                    signatures: h.signatures.map((s) => ({ label: s.label, documentation: s.documentation ?? undefined, parameters: s.parameters.map((p) => ({ label: p.label, documentation: p.documentation ?? undefined })) })),
                    activeSignature: h.activeSignature,
                    activeParameter: h.activeParameter,
                },
                dispose() { },
            };
        },
    });
    const convert = (s) => ({
        name: s.name,
        detail: s.detail ?? '',
        kind: symbolKinds[s.kind] ?? S.Variable,
        tags: [],
        range: toRange(s.range),
        selectionRange: toRange(s.selectionRange),
        children: (s.children ?? []).map(convert),
    });
    monaco.languages.registerDocumentSymbolProvider('python', {
        async provideDocumentSymbols(model) {
            const uri = serverUri.get(model);
            if (!uri)
                return [];
            return ((await lsp.request('textDocument/documentSymbol', { textDocument: { uri } })) ?? []).map(convert);
        },
    });
}
lsp.on('textDocument/publishDiagnostics', (p) => {
    // the findings of a notebook's file go to its cells (their lines are `start` too far down)
    if (notebook?.diagnostics(p, (d, start) => ({
        ...toRange({ start: { line: d.range.start.line - start, character: d.range.start.character }, end: { line: d.range.end.line - start, character: d.range.end.character } }),
        severity: markerSeverity(d.severity),
        message: d.message,
        source: d.source,
        tags: markerTags(d.tags),
    }))) {
        updateProblems();
        return;
    }
    const model = models.get(normalise(p.uri));
    if (!model)
        return;
    monaco.editor.setModelMarkers(model, 'pymagic', p.diagnostics.map((d) => ({
        ...toRange(d.range),
        severity: markerSeverity(d.severity),
        message: d.message,
        source: d.source,
        tags: markerTags(d.tags),
    })));
    updateProblems();
});
/** The marker tags of a diagnostic: greyed out (unnecessary) or struck through (deprecated). */
function markerTags(tags) {
    const out = [];
    if (tags?.includes(1))
        out.push(monaco.MarkerTag.Unnecessary);
    if (tags?.includes(2))
        out.push(monaco.MarkerTag.Deprecated);
    return out.length ? out : undefined;
}
// ---------------------------------------------------------------- UI
let editor;
let runHooks = null;
let currentRel = '';
function refreshDirty() {
    for (const li of Array.from($('files').children)) {
        const rel = li.dataset.path;
        const m = models.get(normalise(uriFor(rel)));
        li.classList.toggle('dirty', !!m && m.getValue() !== savedText.get(rel));
        li.classList.toggle('active', rel === currentRel);
    }
    tabs?.refresh();
    split?.refresh();
}
function updateProblems() {
    const all = monaco.editor.getModelMarkers({}).filter((m) => m.severity >= monaco.MarkerSeverity.Warning);
    const errors = all.filter((m) => m.severity === monaco.MarkerSeverity.Error).length;
    $('problems').textContent = `${errors} errors, ${all.length - errors} warnings`;
}
let explorer = null;
const secondWindow = new URLSearchParams(location.search).get('open');   // Open Tab in New Window: the file this window was opened for
let tabs = null;
let split = null;
let recents = null;
let runConfigs = null;
let knownFiles = new Set();   // the files of the project, as the engine listed them last
let notebook = null;
const isDirtyRel = (rel) => { const m = models.get(normalise(uriFor(rel))); return !!m && m.getValue() !== savedText.get(rel); };
/** Writes one open file to disk (a tab being closed with Save). */
async function saveFile(rel) {
    notebook?.flushAll();
    const m = models.get(normalise(uriFor(rel)));
    if (!m)
        return;
    const res = await fetch('/api/file?path=' + encodeURIComponent(rel), { method: 'PUT', body: m.getValue() });
    if (res.ok) {
        noteSaved(rel, m.getValue());
        refreshDirty();
        window.dispatchEvent(new Event('pymagic:saved'));
    }
}
/** Closes the models of a file, or of a folder and what is in it (after it was renamed or deleted). */
async function forgetPath(rel) {
    for (const [key, model] of Array.from(models)) {
        const path = relPathOf.get(key);
        if (!path || (path !== rel && !path.startsWith(rel + '/')))
            continue;
        if (served.has(model) || languageFor(path) === 'django-html')
            lsp.notify('textDocument/didClose', { textDocument: { uri: serverUri.get(model) ?? uriFor(path) } });
        models.delete(key);
        loading.delete(key);
        relPathOf.delete(key);
        savedText.delete(path);
        if (editor.getModel() === model)
            editor.setModel(null);
        for (const side of split?.editors() ?? [])
            if (side.getModel() === model)
                side.setModel(null);
        model.dispose();
    }
    notebook?.close(rel);
    tabs?.drop(rel);
    split?.drop(rel);
    refreshDirty();
}
/**
 * Forgets the editors of Python files that are gone from disk. The language server reads the rest of the project from disk itself (and keeps
 * an index of it), so a file only gets an editor when somebody opens it: a project of thousands of files would not fit in the page otherwise.
 */
async function syncPyModels() {
    const files = await (await fetch('/api/files')).json();
    knownFiles = new Set(files);
    const wanted = new Set(files.map((f) => normalise(uriFor(f))));
    for (const [key] of Array.from(models))
        if (!wanted.has(key) && relPathOf.get(key)?.endsWith('.py'))
            await forgetPath(relPathOf.get(key));
}
function showNothing() {
    currentRel = '';
    notebook?.hide();
    editor.setModel(null);
    refreshDirty();
}
/** The view of an empty editor: the files opened lately and the keys that start the work. */
function updateWelcome() {
    const box = $('welcome');
    if (!box || !editor)
        return;
    const empty = !editor.getModel();
    box.hidden = !empty;
    $('editor').classList.toggle('nb-hidden', empty);
    if (!empty)
        return;
    const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls)
        e.className = cls; if (text)
        e.textContent = text; return e; };
    const files = recents?.list('') ?? [];
    const recent = el('div', 'welcome-recent');
    recent.append(el('h3', '', files.length ? 'Recent files' : 'No file opened yet'));
    for (const rel of files.slice(0, 8)) {
        const b = el('button', 'welcome-file');
        b.append(el('span', 'welcome-name', rel.slice(rel.lastIndexOf('/') + 1)), el('span', 'welcome-path', rel));
        b.onclick = () => void openFile(rel);
        recent.append(b);
    }
    const keys = el('div', 'welcome-keys');
    keys.append(el('h3', '', 'Get going'));
    for (const [key, what] of [['Ctrl+P', 'Search everywhere: files, classes, functions'], ['Ctrl+Shift+A', 'Find any command'], ['Ctrl+Shift+F', 'Find in files'], ['Ctrl+F5', 'Run the file'], ['Ctrl+`', 'Terminal'], ['Ctrl+,', 'Settings']]) {
        const row = el('div', 'welcome-key');
        row.append(el('kbd', '', key), el('span', '', what));
        keys.append(row);
    }
    const head = el('div', 'welcome-head');
    head.append(el('div', 'welcome-title', 'PyMagic'), el('div', 'welcome-sub', 'Open a file from Files on the left, or pick one below.'));
    box.replaceChildren(head, el('div', 'welcome-cols'));
    box.lastChild.append(recent, keys);
}
/** A file with unsaved changes was changed on disk by something else: which version stays. Resolves to 'disk' or 'mine'. */
function askDiskOrMine(rel) {
    return new Promise((resolve) => {
        const shade = document.createElement('div');
        shade.className = 'ask-shade';
        const box = document.createElement('div');
        box.className = 'ask-box';
        const title = document.createElement('div');
        title.className = 'ask-title';
        title.textContent = `${rel} changed on disk`;
        const text = document.createElement('div');
        text.className = 'ask-hint';
        text.textContent = 'Something outside PyMagic changed this file while you have unsaved changes in it. Keep yours (saving writes them over the file), or load the version on disk (Ctrl+Z brings yours back).';
        const row = document.createElement('div');
        row.className = 'ask-buttons';
        const done = (value) => { shade.remove(); resolve(value); };
        for (const [label, value, cls] of [['Keep mine', 'mine', ''], ['Load the version on disk', 'disk', 'danger']]) {
            const b = document.createElement('button');
            b.textContent = label;
            if (cls)
                b.className = cls;
            b.onclick = () => done(value);
            row.append(b);
        }
        box.append(title, text, row);
        shade.append(box);
        shade.onkeydown = (e) => { if (e.key === 'Escape')
            done('mine'); };
        document.body.append(shade);
        row.firstChild.focus();
    });
}
/** Renaming a method that overrides another: the base method with its family, this one only, or nothing. Resolves to 'base', 'this' or 'cancel'. */
function askRenameScope(r) {
    const library = r.libraryBase ? `It overrides a method of ${r.libraryBase}, a library class that is not renamed: after the rename it no longer overrides it.` : '';
    return r.baseClass
        ? askChoice(`Rename ${r.placeholder}`, `${r.placeholder} of ${r.methodClass} overrides the method of ${r.baseClass}. Rename the base method too, with every method that overrides it? ${library}`.trim(),
            [['Rename the base method', 'base'], ['This one only', 'this'], ['Cancel', 'cancel']])
        : askChoice(`Rename ${r.placeholder}`, `${r.placeholder} of ${r.methodClass}: ${library}`, [['Rename anyway', 'this'], ['Cancel', 'cancel']]);
}
async function openAnother() {
    const next = tabs?.mruFirst();
    if (next)
        await openFile(next);
    else
        showNothing();
}
/** `opts.preview`: opened with one click in the project tree, into the preview tab (Settings > Editor tabs). */
async function openFile(rel, reveal, opts = {}) {
    const model = await ensureModel(rel);
    if (!model) {
        toast(`${rel} cannot be shown here (binary or very large).`, true);
        return;
    }
    currentRel = rel;
    editor.setModel(model);
    editor.updateOptions({ readOnly: false });
    if (rel.endsWith('.ipynb'))
        notebook?.show(rel, model);
    else
        notebook?.hide();
    explorer?.reveal(rel);
    tabs?.opened(rel, !!opts.preview);
    recents?.opened(rel);
    if (reveal) {
        editor.setSelection(reveal);
        editor.revealRangeInCenter(reveal);
    }
    editor.focus();
    refreshDirty();
}
async function save() {
    // format on save: a notebook's code cells too, before they are written into its file
    if ($('fmt-save').checked && currentRel.endsWith('.ipynb'))
        await notebook?.formatShown();
    notebook?.flushAll();
    if ($('fmt-save').checked && currentRel.endsWith('.py'))
        await formatCurrent(true);
    const model = editor.getModel();
    const rel = model && relPathOf.get(normalise(serverUri.get(model)));
    if (!model || !rel)
        return;
    const res = await fetch('/api/file?path=' + encodeURIComponent(rel), { method: 'PUT', body: model.getValue() });
    if (res.ok) {
        noteSaved(rel, model.getValue());
        refreshDirty();
        window.dispatchEvent(new Event('pymagic:saved'));
    }
}
/** Reads a file from disk into its open model (after git changed it behind the editor's back). */
async function reloadFromDisk(rel) {
    const model = models.get(normalise(uriFor(rel)));
    const res = await fetch('/api/file?path=' + encodeURIComponent(rel));
    if (!res.ok)
        return;
    const text = await res.text();
    savedText.set(rel, text);
    if (model && model.getValue() !== text)
        model.setValue(text);
    refreshDirty();
}
async function reloadAllFromDisk() {
    for (const key of Array.from(models.keys())) {
        const rel = relPathOf.get(key);
        const model = models.get(key);
        if (rel && model && model.getValue() === savedText.get(rel))
            await reloadFromDisk(rel); // edits that are not saved stay
    }
}
async function main() {
    const chrome = setupChrome();
    const info = await (await fetch('/api/info')).json();
    rootUri = info.rootUri;
    defineTheme(monaco);
    defineLightTheme(monaco);
    await loadSettings();
    // Settings > Plugins: a plugin switched off is not set up, and what it owns is hidden (a change takes effect at the next reload)
    const plugin = (id) => pluginOn(pluginsAtStart(), id);
    for (const id of hiddenElements(pluginsAtStart()))
        document.getElementById(id)?.classList.add('plugin-off');
    // the main editor and the right side of a split are made alike
    // a quote typed over a selected string changes its quotes ("pen" with ' gives 'pen') instead of wrapping it ('"pen"'), in every editor
    monaco.editor.onDidCreateEditor((ed) => ed.onKeyDown((e) => {
        const key = e.browserEvent.key;
        if ((key !== "'" && key !== '"') || e.ctrlKey || e.altKey || e.metaKey)
            return;
        const model = ed.getModel(), selections = ed.getSelections();
        if (!model || !selections?.length || ed.getOption(monaco.editor.EditorOption.readOnly) || selections.some((s) => s.isEmpty()))
            return;
        const texts = selections.map((s) => requote(model.getValueInRange(s), key));
        if (texts.some((t) => t === null))
            return;
        e.preventDefault();
        e.stopPropagation();
        ed.pushUndoStop();
        ed.executeEdits('requote', selections.map((s, i) => ({ range: s, text: texts[i], forceMoveMarkers: true })),
            // the string stays selected (a triple-quoted one over several lines keeps its length: only its quotes change)
            selections.map((s, i) => new monaco.Selection(s.startLineNumber, s.startColumn, s.endLineNumber, s.startLineNumber === s.endLineNumber ? s.startColumn + texts[i].length : s.endColumn)));
        ed.pushUndoStop();
    }));
    const createEditor = (el) => monaco.editor.create(el, {
        theme: themeName(settings().codeColors, themeNow()),
        automaticLayout: true,
        ...editorOptions(settings()),
        padding: { top: 10, bottom: 10 },
        cursorSmoothCaretAnimation: 'on',
        roundedSelection: true,
        quickSuggestions: { other: true, comments: false, strings: false },
        scrollBeyondLastLine: false,
        glyphMargin: true,
        suggestSelection: 'recentlyUsed', // what was picked for a prefix before comes first next time
        wordBasedSuggestions: 'off',
        snippetSuggestions: 'inline',
        suggest: { showWords: false, insertMode: 'replace' },
        'semanticHighlighting.enabled': true,
    });
    editor = createEditor($('editor'));
    setupCommands(editor);
    setKeyLabels(currentKeyLabel);
    editor.onDidChangeModel(() => updateWelcome());
    applySettings(monaco, themeName, editor);
    $('build').textContent = buildLabel();
    notebook = !plugin('notebooks') ? null : setupNotebook({ monaco, area: $('editor-area'), editorEl: $('editor'), save, lsp, formatEngine: () => formatOptions().pymagicEngine, rootUri: () => rootUri, relOf: (model) => relPathOf.get(normalise(serverUri.get(model) ?? '')) });
    window.pymagic = { editor, monaco, openFile, notebook, changeSetting, projectDesktop };
    const common = {
        editor,
        modelFor: ensureModel,
        relOf: (model) => relPathOf.get(normalise(serverUri.get(model) ?? '')),
        openFile,
        saveAll: async () => {
            notebook?.flushAll();
            for (const [key, model] of models) {
                const rel = relPathOf.get(key);
                if (rel && model.getValue() !== savedText.get(rel)) {
                    const res = await fetch('/api/file?path=' + encodeURIComponent(rel), { method: 'PUT', body: model.getValue() });
                    if (res.ok)
                        noteSaved(rel, model.getValue());
                }
            }
            refreshDirty();
            window.dispatchEvent(new Event('pymagic:saved'));
        },
    };
    // Close Project (the project folder's menu in the tree, File, the app's menu): the files are saved first, as PyCharm does; the window then
    // closes, or shows the welcome screen when it is the last one
    const closeProject = async () => {
        const desktop = window.pymagicDesktop;
        if (!desktop?.closeProject) { toast('Close Project works in the PyMagic window; in a browser, close the tab.', true); return; }
        await common.saveAll();
        await desktop.closeProject();
    };
    window.pymagic.closeProject = closeProject;
    runConfigs = setupRunConfigurations({ currentFile: () => currentRel });
    runHooks = setupRun({ ...common, root: info.root, scratches: info.scratches, runConfig: () => runConfigs.current(), runConfigByName: (name) => runConfigs.byName(name), showDiff: (...a) => git.showDiff(...a), askText: (...a) => explorer.askText(...a),
        // a notebook's cells are debugged as files of their own: their breakpoints and the stops in them
        cellFileOf: (model) => notebook?.cellFileOf(model), cellAtFile: (file, line) => notebook?.cellAtFile(file, line), editorOfModel: (model) => notebook?.editorOf(model) });
    notebook?.connectDebugger(runHooks.notebook);
    const tests = setupTests({ ...common, lsp, relOfUri });
    setupInterpreters();
    setupPackages();
    const refactor = setupRefactor({ editor, lsp, modelForUri, uriOf: (model) => serverUri.get(model) });
    const search = setupSearch({
        editor, lsp, openFile, uriFor,
        applyEdits: refactor.applyEdits,
        isEditable: (rel) => rel.endsWith('.py'),
        unsavedTexts: () => {
            const out = {};
            for (const [key, model] of models) {
                const rel = relPathOf.get(key);
                if (rel && model.getValue() !== savedText.get(rel))
                    out[rel] = model.getValue();
            }
            return out;
        },
    });
    setupToolbar({ monaco, editor, save, openFile, currentFile: () => currentRel, refactor, search, runConfigs });
    setupToolbarFit();
    setupAutoSave({ editor, saveAll: common.saveAll });
    // the Django view's "+ admin" and "+ serializer": in admin.py / serializers.py beside the model's file, in the editor to look at and save
    const besideModel = async (model, file, edit) => {
        const rel = relPathOf.get(normalise(serverUri.get(model) ?? ''));
        if (!rel) return;
        const target = (rel.includes('/') ? rel.slice(0, rel.lastIndexOf('/') + 1) : '') + file;
        const exists = await fetch('/api/file?path=' + encodeURIComponent(target)).then((r) => r.ok).catch(() => false);
        if (!exists) await fetch('/api/fs', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ op: 'create', path: target }) });
        const m = await ensureModel(target);
        if (!m) return;
        m.pushEditOperations([], [{ range: m.getFullModelRange(), text: edit(m.getValue()) }], () => null);
        await openFile(target, new monaco.Range(m.getLineCount(), 1, m.getLineCount(), 1));
    };
    const structure = setupStructure({
        editor, lsp, uriOf: (model) => serverUri.get(model), visible: () => chrome.shown() && chrome.view() === 'structure', notebook: () => notebook,
        actions: {
            createAdmin: (model, name, fields) => besideModel(model, 'admin.py', (t) => addModelAdmin(t, name, fields)),
            createSerializer: (model, name, fields) => besideModel(model, 'serializers.py', (t) => addModelSerializer(t, name, fields)),
        },
    });
    window.addEventListener('pymagic:sidebar', (e) => { if (e.detail === 'structure') structure.show(); });
    const bookmarks = setupBookmarks({ monaco, editor, openFile, relOf: (model) => relPathOf.get(normalise(serverUri.get(model) ?? '')), revealInTree: (rel) => explorer?.select(rel) });
    const history = setupHistory({
        monaco, editor, currentFile: () => currentRel, openFile, askText: (...a) => explorer.askText(...a),
        // a label keeps the open files as the editor has them, with what is not saved yet
        unsavedTexts: () => Object.fromEntries([...models].map(([key, model]) => [relPathOf.get(key), model])
            .filter(([rel, model]) => rel && model.getValue() !== savedText.get(rel)).map(([rel, model]) => [rel, model.getValue()])),
    });
    setupScratches({ openFile, askText: (...a) => explorer.askText(...a), forget: forgetPath, currentFile: () => currentRel });
    recents = setupRecent({ exists: (rel) => knownFiles.has(rel) });
    const editorNav = setupEditorNav({
        editor, lsp, openFile, modelFor: ensureModel, currentFile: () => currentRel, saveAll: common.saveAll,
        uriOf: (model) => model && serverUri.get(model), relOf: (model) => model && relPathOf.get(normalise(serverUri.get(model) ?? '')),
        closeTab: (rel) => tabs?.close(rel), reveal: (rel) => explorer?.select(rel),
    });
    setupMainMenu({
        editor, save, saveAll: common.saveAll, reload: reloadFromDisk, openFile, tabs: () => tabs, split: () => split, currentFile: () => currentRel, search, refactor,
        breakpoints: runHooks.breakpoints, debug: runHooks.debug, nav: editorNav.commands, recent: (current) => recents.list(current), chrome, bookmarks, runConfigs, closeProject,
    });
    const git = setupGit({
        editor,
        relOf: (model) => relPathOf.get(normalise(serverUri.get(model) ?? '')),
        currentRel: () => currentRel,
        openFile,
        savedText: (rel) => savedText.get(rel),
        saveAll: common.saveAll,
        reload: reloadFromDisk,
        reloadAll: reloadAllFromDisk,
        enabled: plugin('git'),   // off: the project is treated as one without Git (the diff viewer stays, for tests and comparisons)
    });
    if (plugin('git')) {
        setupGitSync({ git, deps: { saveAll: common.saveAll, reloadAll: reloadAllFromDisk, openFile, monaco } });
        setupGitShelf({ git, deps: { saveAll: common.saveAll, reloadAll: reloadAllFromDisk } });
    }
    const hierarchy = setupHierarchy({
        editor, lsp, openFile, modelForUri,
        uriOf: (model) => serverUri.get(model),
        relOfUri,
        // a mark's target, clicked on a side of the split, opens on that side
        openIn: (ed, rel, sel) => split.openIn(ed, rel, sel),
    });
    setupInspections(lsp);
    const pyConsole = setupPythonConsole({ editor, saveAll: common.saveAll, debugSession: () => runHooks?.consoleSession() ?? Promise.resolve(null), relOf: (model) => model && relPathOf.get(normalise(serverUri.get(model) ?? '')) });
    const diagrams = !plugin('diagrams') ? null : setupDiagrams({ lsp, relOfUri, currentUri: () => { const m = editor.getModel(); return m && m.getLanguageId() === 'python' ? serverUri.get(m) : null; },
        openAt: (rel, line) => openFile(rel, new monaco.Range(line, 1, line, 1)),
        askText: (...a) => explorer.askText(...a),
        // a box's menu: the file opened with the cursor on the name, then an editor command on it (find usages, rename, refactor)
        atSymbol: async (rel, line, name, action) => {
            await openFile(rel, new monaco.Range(line, 1, line, 1));
            const m = editor.getModel();
            const column = m ? m.getLineContent(line).indexOf(name) + 1 : 1;
            editor.setPosition({ lineNumber: line, column: Math.max(1, column) });
            editor.focus();
            if (action) editor.trigger('diagram', action, null);
        } });
    window.addEventListener('pymagic:diagram', (e) => {
        if (!diagrams) return;   // Settings > Plugins: Diagrams off
        if (e.detail !== 'model') { void diagrams.open(e.detail); return; }
        // the model at the cursor: the word there, as a class name
        const m = editor.getModel(), pos = editor.getPosition();
        const word = m && pos ? m.getWordAtPosition(pos)?.word : null;
        if (word) void diagrams.openModel(word);
    });
    setupEmbedded();
    setupTemplatePreview({ editor, lsp, uriOf: (model) => serverUri.get(model) });
    setupTemplateLinks({
        editor, lsp, relOfUri, uriOf: (model) => serverUri.get(model), refreshTree: async () => { await explorer?.refresh(); },
        openFile: (rel, range) => openFile(rel, range),
        // a folder's full path as a path of the project ('' for the project folder itself), or null outside it
        relOfPath: (full) => {
            const root = info.root.replace(/[\\/]+$/, '').replace(/\\/g, '/').toLowerCase();
            const f = full.replace(/\\/g, '/');
            return f.toLowerCase() === root ? '' : f.toLowerCase().startsWith(root + '/') ? f.slice(root.length + 1) : null;
        },
    });
    const codeGen = setupCodeGen();
    codeGen.attach(editor);
    setLiveTemplateSource(() => lsp.request('pymagic/templates', {}));
    window.addEventListener('pymagic:folders', () => lsp.notify('pymagic/foldersChanged', {}));
    setupSemantic({ lsp, uriOf: (model) => model && serverUri.get(model) });
    const codeVision = setupCodeVision({ editor, lsp, uriOf: (model) => model && serverUri.get(model), relOf: (model) => model && relPathOf.get(normalise(serverUri.get(model) ?? '')), savedText: (rel) => savedText.get(rel) });
    setupRoutes({ editor, lsp, openFile, relOfUri });
    if (plugin('django'))
        setupDjango({
            run: runHooks, lsp, relOfUri, modelFor: ensureModel, askText: (...a) => explorer.askText(...a),
            openFile: (rel, line) => openFile(rel, line ? new monaco.Range(line, 1, line, 1) : undefined),
        });
    const problems = setupProblems({ editor, lsp, openFile, relOfUri });
    setupTodo({ editor, lsp, openFile, relOfUri });
    const terminal = setupTerminal();
    // the menus of the project tree, the tabs and the editor (fileactions.js)
    const fileActions = setupFileActions({
        openFile, run: runHooks, tests, history, search, problems, terminal, reload: reloadFromDisk, lsp, uriFor, relOfUri, bookmarks, editor, saveFile, applyEdits: refactor.applyEdits,
        plugin: (name) => plugin(name),
        pythonFiles: () => [...knownFiles], tidyFile,
        showDiff: git.showDiff, refreshGit: git.refresh,
        refreshTree: async () => { await explorer?.refresh(); },
        askText: (...a) => explorer.askText(...a),
        relOfModel: (model) => model ? relPathOf.get(normalise(serverUri.get(model) ?? '')) : undefined,
        textOf: async (rel) => {
            const open = models.get(normalise(uriFor(rel)));
            if (open)
                return open.getValue();
            const res = await fetch('/api/file?path=' + encodeURIComponent(rel));
            return res.ok ? await res.text() : null;
        },
        setLanguage: (rel, id) => { const m = models.get(normalise(uriFor(rel))); if (m) applyLanguage(rel, m, id); },
        defaultLanguage: (rel) => (rel.endsWith('.py') ? 'python' : languageFor(rel)),
    });
    fileActions.attachEditor(editor, monaco, () => refactorMenu(editor, refactor));
    const coverage = plugin('coverage') ? setupCoverage(common) : null;
    setupRequirementsCheck(common);
    setupNpmCheck(common);
    setupFileChecks(common);
    if (plugin('schemas'))
        setupSchemaPicker({ editor, relOf: common.relOf, checker: setupSchemaCheck(common), openSettings });
    if (plugin('emmet'))
        setupEmmet();
    if (plugin('tagpairs'))
        setupTagPairs();
    if (plugin('fullline'))
        setupFullLine();
    if (plugin('tailwind'))
        window.pymagicTailwindReady = setupTailwind({ settings, onSettings }).catch(() => null);
    if (plugin('profiler'))
        setupProfiler({ ...common, askText: (...a) => explorer.askText(...a) });
    if (plugin('httpclient'))
        setupHttpClient({ ...common, root: info.root, askText: (...a) => explorer.askText(...a) });
    const database = plugin('database') ? setupDatabase({ ...common, askText: (...a) => explorer.askText(...a), runScript: (rel, args) => runHooks.runScript(rel, args) }) : null;
    setupInjections({ database });
    if (plugin('markdown')) {
        setupMarkdownPreview({ editor, currentFile: () => currentRel, openFile, askText: (...a) => explorer.askText(...a) });
        setupMarkdownTools({ editor, currentFile: () => currentRel, relOfModel: (model) => relPathOf.get(normalise(serverUri.get(model) ?? '')) });
    }
    if (plugin('prs'))
        setupPullRequests();
    if (plugin('deployment'))
        setupDeployment({ editor, currentFile: () => currentRel, askText: (...a) => explorer.askText(...a), reloadFromDisk, saveAll: common.saveAll });
    setupServices({ providers: [() => runHooks.services(), () => pyConsole.services(), () => terminal.services(), () => notebook?.services((rel) => openFile(rel)) ?? []] });
    setupRunAnything({ runConfigs, runHooks, editor });
    if (plugin('macros'))
        setupMacros({ editor, askText: (...a) => explorer.askText(...a) });
    setupGrammar({ relOf: (model) => relPathOf.get(normalise(serverUri.get(model) ?? '')) });
    setupSpelling({ relOf: (model) => relPathOf.get(normalise(serverUri.get(model) ?? '')) });
    // code actions that need the page (not just a text edit) arrive as commands
    monaco.editor.registerCommand('pymagic.installPackage', (_accessor, name) => {
        window.dispatchEvent(new CustomEvent('pymagic:install', { detail: name }));
    });
    // "ignore this import": the engine keeps it in the project's .pymagic/ignored-imports.json and checks the open files again
    monaco.editor.registerCommand('pymagic.ignoreImport', async (_accessor, module) => {
        try {
            await lsp.request('workspace/executeCommand', { command: 'pymagic.ignoreImport', arguments: [module] });
            toast(`Imports of '${module}' are no longer reported in this project: Settings > Inspections lists them`);
        }
        catch (e) {
            toast(e.message ?? 'The import cannot be ignored.', true);
        }
    });
    // and back, from the list in Settings > Inspections
    window.addEventListener('pymagic:unignore-import', async (e) => {
        const { module, done } = e.detail ?? {};
        try {
            await lsp.request('workspace/executeCommand', { command: 'pymagic.unignoreImport', arguments: [module] });
            toast(`Imports of '${module}' are reported again`);
        }
        catch (err) {
            toast(err.message ?? 'The import cannot be reported again.', true);
        }
        done?.();
    });
    defineCommand({ id: 'pymagic.file.save', label: 'Save', group: 'Edit', keys: ['Ctrl+S'], everywhere: true, run: () => void save() });
    defineCommand({ id: 'pymagic.edit.format', label: 'Format the file or the selection', group: 'Edit', keys: ['Ctrl+Alt+L'], run: () => void formatCurrent() });
    defineCommand({ id: 'pymagic.edit.organizeImports', label: 'Remove unused imports and sort', group: 'Edit', keys: ['Shift+Alt+O'], run: () => void organizeImports() });
    $('b-format').onclick = () => void formatCurrent();
    const engineSelect = $('fmt-engine');
    try {
        engineSelect.value = localStorage.getItem('pymagic.formatter') ?? 'builtin';
    }
    catch { /* storage unavailable */ }
    engineSelect.onchange = () => { try {
        localStorage.setItem('pymagic.formatter', engineSelect.value);
    }
    catch { /* storage unavailable */ } };
    engineSelect.onfocus = () => void refreshFormatters(); // an install in the Packages tab shows up here
    engineSelect.onmousedown = () => void refreshFormatters();
    $('b-imports').onclick = () => void organizeImports();
    try {
        $('fmt-save').checked = localStorage.getItem('pymagic.formatOnSave') === '1';
        $('fmt-save').onchange = () => localStorage.setItem('pymagic.formatOnSave', $('fmt-save').checked ? '1' : '0');
    }
    catch { /* storage unavailable */ }
    editor.onDidChangeCursorPosition((e) => { $('cursor').textContent = `Ln ${e.position.lineNumber}, Col ${e.position.column}`; });
    // go-to-definition into another file switches the editor to it
    monaco.editor.registerEditorOpener({
        openCodeEditor(source, resource, selectionOrPosition) {
            const rel = resource.path.replace(/^\//, '');
            const sel = !selectionOrPosition ? undefined
                : 'startLineNumber' in selectionOrPosition ? selectionOrPosition
                    : new monaco.Range(selectionOrPosition.lineNumber, selectionOrPosition.column, selectionOrPosition.lineNumber, selectionOrPosition.column);
            // a library file opens read only in its window, at the definition
            if (resource.scheme === LIB_SCHEME) {
                void showLibraryFile(pathOfLibraryUri(resource.path), (sel?.startLineNumber ?? 1) - 1, (sel?.startColumn ?? 1) - 1, editor);
                return true;
            }
            // from a side of the split, the definition opens on that side
            if (split?.editors().includes(source))
                void split.openIn(source, rel, sel);
            else
                void openFile(rel, sel);
            return true;
        },
    });
    lsp.onStatus = (ok) => {
        $('conn').textContent = ok ? 'language server connected' : 'language server disconnected';
        $('conn').className = ok ? 'ok' : 'bad';
    };
    const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
    await lsp.connect(`${scheme}://${location.host}/lsp`);
    await lsp.request('initialize', { processId: null, rootUri, capabilities: {} });
    lsp.notify('initialized', {});
    // the quotes Format gives strings when the project does not say (Settings, Editor)
    let quotesSent = null;
    const sendQuotes = () => {
        if (settings().quotes === quotesSent)
            return;
        quotesSent = settings().quotes;
        void lsp.request('pymagic/setFormatDefaults', { quotes: quotesSent });
    };
    sendQuotes();
    onSettings(sendQuotes);
    // how a union argument is checked (Settings, Code vision and hints: Check every member of a union)
    let strictSent = null;
    const sendTypeOptions = () => {
        if (settings().strictUnions === strictSent)
            return;
        strictSent = settings().strictUnions;
        void lsp.request('pymagic/setTypeOptions', { strictUnions: strictSent });
    };
    sendTypeOptions();
    onSettings(sendTypeOptions);
    registerProviders();
    void refreshFormatters();
    const files = await (await fetch('/api/files')).json();
    knownFiles = new Set(files);
    explorer = setupExplorer({
        items: (path, isDir) => fileActions.treeItems(path, isDir),
        closeProject: window.pymagicDesktop?.closeProject ? closeProject : null,
        // the project folder as the tree's first row; New's entries that are other windows' (a scratch file, a data source)
        root: info.root, plugin: (name) => plugin(name),
        newScratch: () => runById('pymagic.scratch.new'),
        addDataSource: database ? (initial) => database.addSqlite(initial) : null,
        findUsages: (path) => fileActions.findUsages(path),
        // Safe Delete: how many places import what is deleted, listed in the Search panel
        usages: async (path) => {
            const files = await fileActions.usagesOf(path);
            const n = (files ?? []).reduce((k, f) => k + f.matches.length, 0);
            if (n) search.showFound(`${n} usage${n === 1 ? '' : 's'} of ${path}: deleting it breaks them`, files);
            return n;
        },
        openFile, refreshDirty, saveAll: common.saveAll, forget: forgetPath, synced: syncPyModels,
        current: () => currentRel, openAnother,
        // the tabs of a moved or renamed file keep their places under the new path
        renamed: (from, to) => { tabs?.rename(from, to); split?.rename(from, to); },
        // a Python module or package that moves: the engine says what its imports become, the preview shows it, and it is saved before the move
        beforeMove: async (from, to) => {
            const name = from.slice(from.lastIndexOf('/') + 1);
            if (name.includes('.') && !/\.pyi?$/.test(name))
                return true;
            let plan;
            try {
                plan = await lsp.request('pymagic/moveModule', { from: uriFor(from), to: uriFor(to) });
            }
            catch {
                return true;   // the engine could not say: the files move without it
            }
            const changes = plan?.changes ?? {};
            if (Object.keys(changes).length === 0) {
                if (plan?.warnings?.length)
                    toast(plan.warnings[0], true);
                return true;
            }
            if (!(await refactor.applyEdits(`Move ${plan.from} to ${plan.to}: the imports that follow it`, changes, { alwaysPreview: true, warnings: plan.warnings ?? [] })))
                return false;
            await common.saveAll();
            return true;
        },
    });
    await explorer.refresh();
    tabs = setupTabs({
        // a window of Open Tab in New Window keeps its tabs apart from the first window's
        ...(secondWindow ? { storageKey: 'pymagic.tabs.window' } : {}),
        openFile, isDirty: isDirtyRel, save: saveFile, revert: reloadFromDisk, empty: showNothing,
        // closing a tab asks about unsaved changes only when the other side of a split does not show the file too
        askOnClose: (rel) => isDirtyRel(rel) && !split?.has(rel),
        isActive: () => split?.focused() !== 'side',
        split: (rel) => split.open(rel, undefined, 'right'),
        splitDown: (rel) => split.open(rel, undefined, 'down'),
        renameFile: (rel) => explorer.rename(rel),
        items: (rel) => fileActions.tabItems(rel),
        detach: (rel) => editorNav.commands.detach(rel),
    });
    split = setupSplit({
        createEditor: (el) => {
            const e = createEditor(el);
            followSettings(e);
            e.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => { const rel = e.getModel() && relPathOf.get(normalise(serverUri.get(e.getModel()) ?? '')); if (rel) void saveFile(rel); });
            // breakpoints, the run and debug keys, the Git change bars, coverage and inheritance marks on this side too
            runHooks.attach(e);
            git.attach(e);
            coverage?.attach(e);
            hierarchy.attach(e);
            codeVision.attach(e);
            editorNav.attach(e);
            codeGen.attach(e);
            pyConsole.attach(e);
            fileActions.attachEditor(e, monaco, () => refactorMenu(e, refactor));
            return e;
        },
        mainEditor: editor,
        renameFile: (rel) => explorer.rename(rel),
        tabItems: (rel) => fileActions.tabItems(rel),
        modelFor: ensureModel,
        openMain: openFile,
        isDirty: isDirtyRel,
        askOnClose: (rel) => isDirtyRel(rel) && !tabs.has(rel),
        save: saveFile,
        revert: reloadFromDisk,
        onCursor: (p) => { $('cursor').textContent = `Ln ${p.lineNumber}, Col ${p.column}`; },
    });
    // only the tabs that were open last time are loaded: the server finds the other files of the project on disk
    if (secondWindow) {
        // Open Tab in New Window: this window shows the one file it was opened for
        tabs.clear();
        if (explorer.has(secondWindow)) await openFile(secondWindow);
    }
    else {
        const restored = tabs.restore((rel) => explorer.has(rel));
        if (restored)
            await openFile(restored);
        else if (files.length)
            await openFile(files[0]);
        split.restore((rel) => explorer.has(rel));
    }
    updateWelcome();
    // files changed outside the page: the tree follows, open files without unsaved changes take the new text, the others ask
    setupDiskWatch({
        openFiles: () => Array.from(models.keys()).map((k) => relPathOf.get(k)).filter(Boolean),
        refreshTree: async () => { await explorer.refresh(); await syncPyModels(); },
        diskText: async (rel) => {
            try {
                const res = await fetch('/api/file?path=' + encodeURIComponent(rel));
                return res.ok ? await res.text() : null;
            }
            catch {
                return null;
            }
        },
        savedText: (rel) => savedText.get(rel),
        currentText: (rel) => models.get(normalise(uriFor(rel)))?.getValue(),
        takeDisk: (rel, text) => {
            savedText.set(rel, text);
            const model = models.get(normalise(uriFor(rel)));
            // an edit, not setValue: Ctrl+Z still brings back what was there
            if (model && model.getValue() !== text)
                model.pushEditOperations([], [{ range: model.getFullModelRange(), text }], () => null);
            refreshDirty();
        },
        ask: askDiskOrMine,
    });
    window.pymagic.split = split;
    window.pymagic.tabs = tabs;
    // the person's keys (Settings > Keymap), now that every command and action is defined, and again whenever they change
    let keysNow = '';
    const keysFollow = (s) => { const k = JSON.stringify(s.keybindings ?? {}); if (k !== keysNow) { keysNow = k; applyKeybindings(s.keybindings); } };
    keysFollow(settings());
    onSettings(keysFollow);
}
main().catch((e) => { $('conn').textContent = String(e.message ?? e); $('conn').className = 'bad'; });

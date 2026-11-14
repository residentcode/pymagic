// Live templates in the editor (Settings > Templates): an abbreviation and the key that expands it (Tab, Enter or Space, the settings' or the
// template's own) writes the template, and the templates of the file's language are offered by completion. Python's own and the Django template
// ones are the language server's to offer (it knows where a statement starts and adds the import): the key asks it for them, and its items pass
// through liveTemplateItems, which leaves out the ones switched off and gives the edited ones their text. Not in a string or a comment.
import * as monaco from 'monaco-editor';
import { settings, onSettings } from './settings';
import { openMenu } from './menu';
import { readSnippets } from './codegen-model.js';
import { allTemplates, cleanConf, matchesAt, offered, servedByServer, serverItems, toSnippet, preview, projectTemplates } from './live-templates-model.js';

const reformatCommand = 'pymagic.liveTemplate.reformat';
let server = null;          // the language server's built-in templates, once it has answered
let serverSource = null;
let project = [];           // the project's own templates (.pymagic/snippets.json)
let templates = allTemplates(null, cleanConf(settings().templates));

let conf = cleanConf(settings().templates);
const rebuild = () => { conf = cleanConf(settings().templates); templates = allTemplates(server, conf, project); };

/** Reads the project's templates again: when the window opens, comes back to the front, and when the Templates page saves them. */
async function readProject() {
    try {
        const res = await fetch('/api/file?path=' + encodeURIComponent('.pymagic/snippets.json'));
        project = projectTemplates(res.ok ? readSnippets(await res.text()) ?? [] : []);
    }
    catch { project = []; }
    rebuild();
}

/** The language server's templates (pymagic/templates), asked once it is there; the page asks again through this. */
export async function serverTemplates() {
    if (!server && serverSource) {
        try {
            const list = await serverSource();
            if (list?.live) { server = list; rebuild(); }
        }
        catch { /* not there yet: asked again the next time */ }
    }
    return server;
}

/** The language server's completion items as the person's templates have them. */
export function liveTemplateItems(items, languageId) {
    return serverItems(items, templates, languageId);
}

/** Whether the place before the cursor is code or text: not a string, a comment or a regular expression, and (HTML) not inside a tag. */
function inCode(model, position) {
    try {
        const tokens = model.tokenization.getLineTokens(position.lineNumber);
        const type = tokens.getStandardTokenType(tokens.findTokenIndexAtOffset(Math.max(0, position.column - 2)));
        // (YAML's plain words are strings to its colouring: only its comments are left out)
        if (type !== 0 && !(type === 2 && model.getLanguageId() === 'yaml')) return false;
    }
    catch { /* no tokens to tell from */ }
    return true;
}
const inTag = (before) => before.lastIndexOf('<') > before.lastIndexOf('>');
const HTML_LIKE = new Set(['html', 'django-html', 'ejs', 'handlebars', 'twig', 'razor', 'blade', 'liquid', 'xml']);

export function setupLiveTemplates(editor, { source, suggestions }) {
    serverSource = source;
    onSettings(rebuild);
    readProject();
    window.addEventListener('focus', readProject);
    window.addEventListener('pymagic:snippets', readProject);
    // the language server starts after the page: asked until it answers
    const ask = (left) => serverTemplates().then((list) => { if (!list && left > 0) window.setTimeout(() => ask(left - 1), 2000); });
    ask(60);

    // every language by its name: a provider for "*" is only asked when the language's own offer nothing
    monaco.languages.registerCompletionItemProvider(monaco.languages.getLanguages().map((l) => l.id), {
        provideCompletionItems(model, position) {
            const word = model.getWordUntilPosition(position);
            if (!word.word) return { suggestions: [] };
            if (!server) serverTemplates();
            const languageId = model.getLanguageId();
            const before = model.getLineContent(position.lineNumber).slice(0, word.startColumn - 1);
            if (!inCode(model, position) || (HTML_LIKE.has(languageId) && inTag(before))) return { suggestions: [] };
            const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
            return {
                suggestions: offered(templates, languageId, before).filter((t) => /^\w/.test(t.abbr)).map((t) => ({
                    label: t.abbr, kind: monaco.languages.CompletionItemKind.Snippet, detail: t.description || t.group,
                    documentation: { value: '```\n' + preview(t.body, t.vars) + '\n```' },
                    insertText: toSnippet(t.body, t.vars), insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
                    sortText: '3' + t.abbr, range,
                    // (a template to reformat is tidied once it is filled in)
                    command: t.reformat ? { id: reformatCommand, title: '', arguments: [position.lineNumber, toSnippet(t.body, t.vars).split('\n').length] } : undefined,
                })),
            };
        },
    });

    const K = monaco.KeyCode;
    const plain = (key) => {
        if (key === 'tab') editor.trigger('keyboard', 'tab', {});
        else editor.trigger('keyboard', 'type', { text: key === 'enter' ? '\n' : ' ' });
    };
    const inSnippet = () => !!editor.getContribution('snippetController2')?.isInSnippet?.();

    /** "Reformat according to style": the lines a template wrote are formatted once its places are filled in (the language's own formatter). */
    function reformatLater(firstLine, lineCount) {
        const model = editor.getModel();
        if (!model) return;
        const last = Math.min(model.getLineCount(), firstLine + lineCount - 1);
        // the lines are followed as they move and grow while the places are typed
        const marks = editor.createDecorationsCollection([{ range: new monaco.Range(firstLine, 1, last, model.getLineMaxColumn(last)), options: { stickiness: monaco.editor.TrackedRangeStickiness.AlwaysGrowsWhenTypingAtEdges } }]);
        const run = async () => {
            const range = marks.getRange(0);
            marks.clear();
            if (!range || editor.getModel() !== model) return;
            const cursor = editor.getPosition();
            editor.setSelection(new monaco.Range(range.startLineNumber, 1, range.endLineNumber, model.getLineMaxColumn(range.endLineNumber)));
            try { await editor.getAction('editor.action.formatSelection')?.run(); }
            catch { /* no formatter for this language */ }
            if (cursor && editor.getModel() === model) editor.setPosition(model.validatePosition(cursor));
        };
        if (!inSnippet()) { run(); return; }
        const watch = editor.onDidChangeCursorSelection(() => {
            if (inSnippet()) return;
            watch.dispose();
            window.setTimeout(run, 0);
        });
    }
    monaco.editor.registerCommand(reformatCommand, (_accessor, firstLine, lineCount) => reformatLater(firstLine, lineCount));

    const insert = (snippet, length, edits, reformat) => {
        const line = editor.getPosition()?.lineNumber ?? 1;
        // the lines an import adds above move the template down
        const above = (edits ?? []).filter((e) => e.range.endLineNumber <= line).reduce((n, e) => n + e.text.split('\n').length - 1 - (e.range.endLineNumber - e.range.startLineNumber), 0);
        editor.pushUndoStop();
        if (edits?.length) editor.executeEdits('live-template', edits.map((e) => ({ range: e.range, text: e.text })));
        editor.getContribution('snippetController2')?.insert(snippet, { overwriteBefore: length, overwriteAfter: 0 });
        editor.pushUndoStop();
        if (reformat) reformatLater(line + above, snippet.split('\n').length);
    };

    /** Writes one template over its abbreviation; a language server's one through its completion (which may say it is not one for this place). */
    function expand(t, key, model, position) {
        editor.getContribution('editor.contrib.suggestController')?.cancelSuggestWidget?.();
        if (!servedByServer(t)) { insert(toSnippet(t.body, t.vars), t.abbr.length, null, t.reformat); return; }
        if (!suggestions) { plain(key); return; }
        const version = model.getVersionId();
        const sort = t.server === 'project' ? '3' : '4';
        Promise.resolve(suggestions(model, position)).catch(() => []).then((items) => {
            if (editor.getModel() !== model || model.getVersionId() !== version) return;
            const item = (items ?? []).find((s) => s.label === t.abbr && s.kind === monaco.languages.CompletionItemKind.Snippet && (s.sortText ?? '').startsWith(sort));
            if (!item) { plain(key); return; }
            insert(item.insertText, t.abbr.length, item.additionalTextEdits, t.reformat);
        });
    }

    editor.onKeyDown((e) => {
        const key = e.keyCode === K.Tab ? 'tab' : e.keyCode === K.Enter ? 'enter' : e.keyCode === K.Space ? 'space' : null;
        if (!key || e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) return;
        const model = editor.getModel(), sel = editor.getSelection();
        if (!model || !sel?.isEmpty() || editor.getSelections().length > 1 || editor.getOption(monaco.editor.EditorOption.readOnly)) return;
        // Tab in a template being filled in goes to its next place; Tab and Enter with the completion list open take its item
        if (inSnippet()) return;
        if (key !== 'space' && editor._contextKeyService?.getContextKeyValue('suggestWidgetVisible')) return;
        const position = sel.getPosition(), languageId = model.getLanguageId();
        const before = model.getLineContent(position.lineNumber).slice(0, position.column - 1);
        const found = matchesAt(templates, conf, languageId, before, key);
        if (!found.length || !inCode(model, position)) return;
        if (HTML_LIKE.has(languageId) && inTag(before.slice(0, before.length - found[0].abbr.length))) return;
        e.preventDefault();
        e.stopPropagation();
        if (found.length === 1) { expand(found[0], key, model, position); return; }
        // templates sharing the abbreviation: which one is asked, at the cursor
        const box = editor.getDomNode()?.getBoundingClientRect(), at = editor.getScrolledVisiblePosition(position);
        openMenu(box && at ? { x: box.left + at.left, y: box.top + at.top + at.height } : { x: 200, y: 200 },
            found.map((t) => ({ label: `${t.abbr}  ${t.description || preview(t.body, t.vars).split('\n')[0]}  (${t.group})`, run: () => { editor.focus(); expand(t, key, model, position); } })));
    });
}


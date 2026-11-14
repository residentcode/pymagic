// Live templates in the editor (Settings > Templates): an abbreviation and the key that expands it (Tab, Enter or Space, the settings' or the
// template's own) writes the template, and the templates of the file's language are offered by completion. Python's own and the Django template
// ones are the language server's to offer (it knows where a statement starts and adds the import): the key asks it for them, and its items pass
// through liveTemplateItems, which leaves out the ones switched off and gives the edited ones their text. Not in a string or a comment.
import * as monaco from 'monaco-editor';
import { settings, onSettings } from './settings';
import { allTemplates, cleanConf, matchAt, offered, servedByServer, serverItems, toSnippet, preview, applicable } from './live-templates-model.js';

let server = null;          // the language server's built-in templates, once it has answered
let serverSource = null;
let templates = allTemplates(null, cleanConf(settings().templates));

let conf = cleanConf(settings().templates);
const rebuild = () => { conf = cleanConf(settings().templates); templates = allTemplates(server, conf); };

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
                    documentation: { value: '```\n' + preview(t.body) + '\n```' },
                    insertText: toSnippet(t.body), insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
                    sortText: '3' + t.abbr, range,
                })),
            };
        },
    });

    const K = monaco.KeyCode;
    const plain = (key) => {
        if (key === 'tab') editor.trigger('keyboard', 'tab', {});
        else editor.trigger('keyboard', 'type', { text: key === 'enter' ? '\n' : ' ' });
    };
    const insert = (snippet, length, edits) => {
        editor.pushUndoStop();
        if (edits?.length) editor.executeEdits('live-template', edits.map((e) => ({ range: e.range, text: e.text })));
        editor.getContribution('snippetController2')?.insert(snippet, { overwriteBefore: length, overwriteAfter: 0 });
        editor.pushUndoStop();
    };
    editor.onKeyDown((e) => {
        const key = e.keyCode === K.Tab ? 'tab' : e.keyCode === K.Enter ? 'enter' : e.keyCode === K.Space ? 'space' : null;
        if (!key || e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) return;
        const model = editor.getModel(), sel = editor.getSelection();
        if (!model || !sel?.isEmpty() || editor.getSelections().length > 1 || editor.getOption(monaco.editor.EditorOption.readOnly)) return;
        // Tab in a template being filled in goes to its next place; Tab and Enter with the completion list open take its item
        const keys = editor._contextKeyService;
        if (editor.getContribution('snippetController2')?.isInSnippet?.()) return;
        if (key !== 'space' && keys?.getContextKeyValue('suggestWidgetVisible')) return;
        const position = sel.getPosition(), languageId = model.getLanguageId();
        const before = model.getLineContent(position.lineNumber).slice(0, position.column - 1);
        const t = matchAt(templates, conf, languageId, before, key);
        if (!t || !inCode(model, position)) return;
        const head = before.slice(0, before.length - t.abbr.length);
        if (HTML_LIKE.has(languageId) && applicable(t, languageId) && inTag(head)) return;
        if (!servedByServer(t)) {
            e.preventDefault();
            e.stopPropagation();
            editor.getContribution('editor.contrib.suggestController')?.cancelSuggestWidget?.();
            insert(toSnippet(t.body), t.abbr.length);
            return;
        }
        // the language server's own: it says whether the template is one for this place, and brings the import
        if (!suggestions) return;
        e.preventDefault();
        e.stopPropagation();
        const version = model.getVersionId();
        Promise.resolve(suggestions(model, position)).catch(() => []).then((items) => {
            if (editor.getModel() !== model || model.getVersionId() !== version) return;
            const item = (items ?? []).find((s) => s.label === t.abbr && s.kind === monaco.languages.CompletionItemKind.Snippet && /^[34]/.test(s.sortText ?? ''));
            if (!item) { plain(key); return; }
            editor.getContribution('editor.contrib.suggestController')?.cancelSuggestWidget?.();
            insert(item.insertText, t.abbr.length, item.additionalTextEdits);
        });
    });
}

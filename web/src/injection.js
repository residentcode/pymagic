// Language injections in Python and JavaScript (and TypeScript) strings (injection-model.js finds them): SQL, JSON, HTML and CSS are
// coloured by their own tokenizers, regular expressions by their parts, a string built from parts as the one text it makes, SQL is completed
// from the Database panel's tables, and *Edit Injected Fragment* opens the text of a string in a window of its own, in its language, and
// writes it back with the escapes a string needs.
import * as monaco from 'monaco-editor';
import { findInjections, fragmentText, sourceRanges, maskHoles, unescapeBody, escapeBody, regexTokens, NAMES } from './injection-model';
import { statementAt, sqlCompletions } from './db-model';
import { toast } from './toast';

const MAX_DECORATIONS = 4000;
const TOKENIZED = ['sql', 'json', 'html', 'css'];

/** Which strings a model's language has: Python's, JavaScript's (TypeScript's are the same), or none. */
const kindOf = (model) => {
    const id = model.getLanguageId();
    return id === 'python' ? 'python' : id === 'javascript' || id === 'typescript' ? 'javascript' : null;
};

/** The class of a token of a Monaco language, or null when it needs none. */
function classOf(type) {
    if (/comment/.test(type)) return 'com';
    if (/string\.key|attribute\.name/.test(type)) return 'attr';
    if (/string/.test(type)) return 'str';
    if (/number/.test(type)) return 'num';
    if (/keyword|predefined|tag|metatag/.test(type)) return 'kw';
    if (/operator/.test(type)) return 'op';
    return null;
}

export function setupInjections({ database }) {
    const marks = new Map();       // model -> decoration ids
    const timers = new Map();
    // the tokenizers of the basic languages load when a model of the language first exists
    const keep = TOKENIZED.map((l) => monaco.editor.createModel('', l));
    void keep;

    function tokensOf(text, language) {
        try { return monaco.editor.tokenize(text, language); } catch { return null; }
    }

    function decorate(model) {
        const kind = model.isDisposed() ? null : kindOf(model);
        if (!kind) return;
        const text = model.getValue();
        const found = text.includes('"') || text.includes("'") || text.includes('`') ? findInjections(text, kind) : [];
        const decorations = [];
        let retry = false;
        const range = (a, b) => { const s = model.getPositionAt(a), e = model.getPositionAt(b); return new monaco.Range(s.lineNumber, s.column, e.lineNumber, e.column); };
        // a token of the fragment's text, put on the parts of the source it stands on
        const mark = (pieces, a, b, cls) => { for (const [s, e] of sourceRanges(pieces, a, b)) if (decorations.length < MAX_DECORATIONS) decorations.push({ range: range(s, e), options: { inlineClassName: cls } }); };
        const done = new Set();    // the strings built from parts are coloured once, as a whole
        for (const inj of found) {
            decorations.push({ range: range(inj.start, inj.end), options: { inlineClassName: 'inj-frag inj-' + inj.language } });
            if (inj.chain && done.has(inj.chain)) continue;
            if (inj.chain) done.add(inj.chain);
            const { text: masked, pieces } = fragmentText(text, inj);
            if (inj.language === 'regex') {
                for (const t of regexTokens(masked)) mark(pieces, t.start, t.end, 'inj-re-' + t.kind);
                continue;
            }
            const lines = tokensOf(masked, inj.language);
            if (!lines || (masked.trim() && lines.every((l) => l.length === 0))) { retry = true; continue; }   // the tokenizer is still loading
            let lineOffset = 0;
            masked.split('\n').forEach((line, n) => {
                const tokens = lines[n] ?? [];
                tokens.forEach((t, k) => {
                    const cls = classOf(t.type);
                    const end = k + 1 < tokens.length ? tokens[k + 1].offset : line.length;
                    if (cls && end > t.offset) mark(pieces, lineOffset + t.offset, lineOffset + end, 'inj-' + cls);
                });
                lineOffset += line.length + 1;
            });
        }
        marks.set(model, model.deltaDecorations(marks.get(model) ?? [], decorations));
        if (retry) schedule(model, 400);
    }

    function schedule(model, ms = 300) {
        clearTimeout(timers.get(model));
        timers.set(model, setTimeout(() => decorate(model), ms));
    }

    function watch(model) {
        if (!kindOf(model)) return;
        schedule(model, 50);
        model.onDidChangeContent(() => schedule(model));
        model.onWillDispose(() => { marks.delete(model); clearTimeout(timers.get(model)); timers.delete(model); });
    }
    monaco.editor.onDidCreateModel((model) => setTimeout(() => watch(model), 0));
    for (const model of monaco.editor.getModels()) watch(model);

    /** The injection the offset is inside of, with the text of its body, or null. */
    function injectionAt(model, offset) {
        const kind = kindOf(model);
        return kind ? findInjections(model.getValue(), kind).find((i) => offset >= i.start && offset <= i.end) ?? null : null;
    }

    // SQL in a string is completed from the tables of the Database panel's chosen source
    const sqlCompletion = {
        triggerCharacters: ['.', ' '],
        async provideCompletionItems(model, position) {
            const offset = model.getOffsetAt(position);
            const inj = injectionAt(model, offset);
            if (!inj || inj.language !== 'sql') return { suggestions: [] };
            const objects = database ? await database.objects() : [];
            const body = model.getValue().slice(inj.start, inj.end);
            const at = offset - inj.start;
            const statement = statementAt(maskHoles(body, inj.fstring, inj.template).text, at);
            const word = model.getWordUntilPosition(position);
            const wordRange = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
            const K = monaco.languages.CompletionItemKind;
            const kinds = { table: K.Class, view: K.Interface, column: K.Field, keyword: K.Keyword };
            return { suggestions: sqlCompletions(body.slice(statement.start, at), statement.text, objects).map((c, i) => ({ label: c.label, kind: kinds[c.kind], detail: c.detail, insertText: c.label, range: wordRange, sortText: String(i).padStart(4, '0') })) };
        },
    };
    for (const language of ['python', 'javascript', 'typescript']) monaco.languages.registerCompletionItemProvider(language, sqlCompletion);

    // ------------------------------------------------------------ the fragment in a window of its own

    function editFragment(editor) {
        const model = editor.getModel();
        const position = editor.getPosition();
        if (!model || !position) return;
        const inj = injectionAt(model, model.getOffsetAt(position));
        if (!inj) { alertBox('The cursor is not in a string that holds SQL, JSON, HTML, CSS or a regular expression.'); return; }
        if (inj.fstring) { alertBox('This is an f-string: its {expressions} cannot be edited apart.'); return; }
        if (inj.chain) { alertBox('This string is built from parts: edit the parts where they are.'); return; }
        const body = model.getValue().slice(inj.start, inj.end);
        if (inj.template && maskHoles(body, false, true).holes.length) { alertBox('This template has ${values}: they cannot be edited apart.'); return; }
        const text = unescapeBody(body, inj.raw, inj.template);
        if (text === null) { alertBox('This string has an escape (such as \\x41) that cannot be shown as it is.'); return; }

        const shade = document.createElement('div');
        shade.className = 'settings-shade';
        const box = document.createElement('div');
        box.className = 'hist-diff inj-window';
        const head = document.createElement('div');
        head.className = 'hist-diff-head';
        const title = document.createElement('span');
        title.className = 'hist-diff-title';
        title.textContent = `${NAMES[inj.language]} fragment`;
        const ok = document.createElement('button');
        ok.className = 'primary';
        ok.textContent = 'OK';
        ok.title = 'Write the text back into the string (Ctrl+Enter)';
        const cancel = document.createElement('button');
        cancel.textContent = 'Cancel';
        head.append(title, ok, cancel);
        const body2 = document.createElement('div');
        body2.className = 'hist-diff-body';
        box.append(head, body2);
        shade.append(box);
        document.body.append(shade);
        const piece = monaco.editor.createModel(text, inj.language === 'regex' ? 'plaintext' : inj.language);
        const inner = monaco.editor.create(body2, { model: piece, automaticLayout: true, minimap: { enabled: false }, scrollBeyondLastLine: false, wordWrap: 'on', lineNumbers: inj.language === 'regex' ? 'off' : 'on' });
        const done = () => { document.removeEventListener('keydown', onKey, true); inner.dispose(); piece.dispose(); shade.remove(); editor.focus(); };
        const accept = () => {
            const escaped = escapeBody(piece.getValue(), inj);
            if (escaped === null) { alertBox('That text cannot be put in this kind of string (a raw string cannot hold its own quote).'); return; }
            // the string may have moved or changed while the window was open: it is looked for again
            const again = findInjections(model.getValue(), kindOf(model)).find((i) => i.start === inj.start && i.language === inj.language);
            if (!again) { alertBox('The string changed meanwhile.'); done(); return; }
            const s = model.getPositionAt(again.start), e = model.getPositionAt(again.end);
            editor.pushUndoStop();
            editor.executeEdits('injection', [{ range: new monaco.Range(s.lineNumber, s.column, e.lineNumber, e.column), text: escaped }]);
            editor.pushUndoStop();
            done();
        };
        const onKey = (e) => {
            if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); done(); }
            else if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); e.stopPropagation(); accept(); }
        };
        document.addEventListener('keydown', onKey, true);
        ok.onclick = accept;
        cancel.onclick = done;
        shade.onmousedown = (e) => { if (e.target === shade) done(); };
        inner.focus();
    }

    const alertBox = (message) => toast(message, true);

    const add = (ed) => ed.addAction({ id: 'pymagic.injection.edit', label: 'Edit Injected Fragment', contextMenuGroupId: 'navigation', contextMenuOrder: 9.9, run: () => editFragment(ed) });
    monaco.editor.onDidCreateEditor(add);
    monaco.editor.getEditors().forEach(add);
    return { refresh: () => monaco.editor.getModels().forEach((m) => schedule(m, 10)), injectionAt };
}

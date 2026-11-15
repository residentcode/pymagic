// The quick fixes of every language, as a list that can be asked without an editor (the MCP server's get_quick_fixes and apply_quick_fix):
// Monaco gives no way to ask its code action providers, so each one is noted as it is registered (PyMagic's own, and the ones Monaco's
// TypeScript service registers when its first file opens).
import * as monaco from 'monaco-editor';

const providers = [];
const original = monaco.languages.registerCodeActionProvider;
monaco.languages.registerCodeActionProvider = (selector, provider, metadata) => {
    const entry = { selector, provider };
    providers.push(entry);
    const d = original.call(monaco.languages, selector, provider, metadata);
    return { dispose() { const i = providers.indexOf(entry); if (i >= 0) providers.splice(i, 1); d.dispose(); } };
};

const matches = (selector, model) => {
    if (Array.isArray(selector)) return selector.some((s) => matches(s, model));
    if (typeof selector === 'string') return selector === '*' || selector === model.getLanguageId();
    if (!selector) return false;
    if (selector.language && selector.language !== '*' && selector.language !== model.getLanguageId()) return false;
    if (selector.scheme && selector.scheme !== model.uri.scheme) return false;
    return true;
};

/**
 * The quick fixes the providers of a model's language give for a line: [{ title, edits: [{ resource, range, text }] }], or, for one that only
 * runs a command of the editor (Save to the dictionary, Ignore the rule), [{ title, command: { id, args } }]. Refactorings are left out.
 */
export async function codeActionsOf(model, line) {
    const markers = monaco.editor.getModelMarkers({ resource: model.uri }).filter((m) => m.startLineNumber <= line && m.endLineNumber >= line);
    const token = new monaco.CancellationTokenSource().token;
    const out = [];
    // asked as the editor asks: at each finding's own place with that finding (a service reads the word there), then for the line as a whole
    const asks = [...markers.map((m) => ({ range: new monaco.Range(m.startLineNumber, m.startColumn, m.endLineNumber, m.endColumn), markers: [m] })),
        { range: new monaco.Range(line, 1, line, model.getLineMaxColumn(line)), markers: [] }];
    for (const { selector, provider } of [...providers]) for (const ask of asks) {
        if (!matches(selector, model)) continue;
        let list = null;
        try { list = await provider.provideCodeActions(model, ask.range, { markers: ask.markers, trigger: 1 }, token); } catch { list = null; }
        for (let action of list?.actions ?? []) {
            if (!action.edit && provider.resolveCodeAction) { try { action = (await provider.resolveCodeAction(action, token)) ?? action; } catch { /* as it is */ } }
            const edits = (action.edit?.edits ?? []).filter((e) => e.resource && e.textEdit).map((e) => ({ resource: e.resource, range: e.textEdit.range, text: e.textEdit.text }));
            if (action.disabled || out.some((x) => x.title === action.title)) continue;
            if (edits.length) out.push({ title: action.title, edits });
            else if (action.command?.id && String(action.kind ?? 'quickfix').startsWith('quickfix')) out.push({ title: action.title, command: { id: action.command.id, args: action.command.arguments ?? [] } });
        }
        list?.dispose?.();
    }
    return out;
}

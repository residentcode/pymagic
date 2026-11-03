// Full line completion (a plugin of Settings > Plugins, as JetBrains' Full Line Code Completion is): when typing pauses at the end of a line's
// code, the engine's bundled model writes the rest of the line, shown in grey after the caret; Tab takes it, Ctrl+Right takes its next word, and
// typing on goes past it. Nothing leaves the computer (FullLine.cs). A request typing has made stale is dropped, and the engine stops writing it.
import * as monaco from 'monaco-editor';
import { completesLanguage, engineLanguage, FULL_LINE_DELAY } from './fullline-model.js';

const none = { items: [] };

export function setupFullLine() {
    let pending = null;   // the request being answered: one at a time
    const provider = {
        async provideInlineCompletions(model, position, context, token) {
            if (!completesLanguage(model.getLanguageId())) return none;
            await new Promise((done) => setTimeout(done, FULL_LINE_DELAY));
            if (token.isCancellationRequested) return none;
            pending?.abort();
            const mine = (pending = new AbortController());
            token.onCancellationRequested(() => mine.abort());
            const body = JSON.stringify({ text: model.getValue(), offset: model.getOffsetAt(position), language: engineLanguage(model.getLanguageId()) });
            const res = await fetch('/api/fullline', { method: 'POST', body, signal: mine.signal }).catch(() => null);
            const answer = res?.ok ? await res.json().catch(() => null) : null;
            if (!answer?.text || token.isCancellationRequested) return none;
            const at = monaco.Range.fromPositions(position);
            return { items: [{ insertText: answer.text, range: at }] };
        },
        freeInlineCompletions() {},
    };
    const off = monaco.languages.registerInlineCompletionsProvider('*', provider);
    return () => off.dispose();
}

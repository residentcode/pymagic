// Typos: the words of comments, strings and names in Python files, and the prose of Markdown and text files, asked of Windows' own spell checker
// (through the engine; nothing is downloaded) and marked as hints; Alt+Enter on one offers the suggestions and *Save to the project's dictionary*
// (`.pymagic/dictionary.txt`, which stays with the project). spell-model.js decides which words are asked about.
import * as monaco from 'monaco-editor';
import { pythonWords, textWords, spellKind, likeOriginal } from './spell-model';
import { settings, onSettings } from './settings';
import { toast } from './toast';

const OWNER = 'pymagic-spell';
const MAX_CHARS = 250_000;

export function setupSpelling({ relOf }) {
    const verdict = new Map();          // word -> true when it is a typo
    const timers = new Map();
    let available = null;               // does the machine have a spell checker for the language (null: not asked yet)

    async function ask(words) {
        const unknown = [...new Set(words)].filter((w) => !verdict.has(w));
        if (!unknown.length) return true;
        for (let i = 0; i < unknown.length; i += 1500) {
            const chunk = unknown.slice(i, i + 1500);
            let answer;
            try { answer = await fetch('/api/spell', { method: 'POST', body: JSON.stringify({ words: chunk }) }).then((r) => (r.ok ? r.json() : null)); } catch { answer = null; }
            if (!answer) return false;
            const bad = new Set(answer.bad);
            for (const w of chunk) verdict.set(w, bad.has(w));
        }
        return true;
    }

    async function check(model) {
        if (model.isDisposed()) return;
        const rel = relOf(model);
        const kind = rel && spellKind(rel);
        if (!kind || !settings().spelling || model.getValueLength() > MAX_CHARS) { monaco.editor.setModelMarkers(model, OWNER, []); return; }
        if (available === false) return;
        const text = model.getValue();
        const found = kind === 'python' ? pythonWords(text) : textWords(text);
        if (!await ask(found.map((w) => w.word))) return;
        if (model.isDisposed() || model.getValue() !== text) return;
        monaco.editor.setModelMarkers(model, OWNER, found.filter((w) => verdict.get(w.word)).map((w) => {
            const s = model.getPositionAt(w.start), e = model.getPositionAt(w.end);
            return { startLineNumber: s.lineNumber, startColumn: s.column, endLineNumber: e.lineNumber, endColumn: e.column, severity: monaco.MarkerSeverity.Info, message: `Typo: In word '${w.word}'`, code: 'typo', source: 'Spelling' };
        }));
    }

    function schedule(model, ms = 800) {
        clearTimeout(timers.get(model));
        timers.set(model, setTimeout(() => void check(model), ms));
    }

    function watch(model) {
        const rel = relOf(model);
        if (!rel || !spellKind(rel)) return;
        schedule(model, 200);
        model.onDidChangeContent(() => schedule(model));
        model.onDidDispose(() => { clearTimeout(timers.get(model)); timers.delete(model); });
    }

    async function start() {
        try { available = (await fetch('/api/spell').then((r) => r.json())).available; } catch { available = false; }
        if (!available) return;
        monaco.editor.onDidCreateModel((model) => setTimeout(() => watch(model), 0));
        for (const model of monaco.editor.getModels()) watch(model);
    }
    void start();
    const again = () => { if (available) for (const m of monaco.editor.getModels()) schedule(m, 50); };
    onSettings(again);

    // the quick fixes of a typo: what Windows suggests, and the project's dictionary
    async function provideCodeActions(model, range, context) {
        const marks = context.markers.filter((m) => m.owner === OWNER || m.source === 'Spelling');
        if (!marks.length) return { actions: [], dispose() { } };
        const mark = marks[0];
        const where = new monaco.Range(mark.startLineNumber, mark.startColumn, mark.endLineNumber, mark.endColumn);
        const word = model.getValueInRange(where);
        const suggestions = await fetch('/api/spell/suggest?word=' + encodeURIComponent(word)).then((r) => (r.ok ? r.json() : [])).catch(() => []);
        const actions = suggestions.slice(0, 5).map((s) => ({
            title: `Typo: replace '${word}' with '${likeOriginal(word, s)}'`, kind: 'quickfix', isPreferred: false,
            edit: { edits: [{ resource: model.uri, versionId: undefined, textEdit: { range: where, text: likeOriginal(word, s) } }] },
        }));
        actions.push({ title: `Save '${word}' to the project's dictionary`, kind: 'quickfix', command: { id: 'pymagic.spell.save', title: 'Save to the dictionary', arguments: [word] } });
        return { actions, dispose() { } };
    }
    for (const language of ['python', 'markdown', 'plaintext', 'restructuredtext']) monaco.languages.registerCodeActionProvider(language, { provideCodeActions });
    monaco.editor.registerCommand('pymagic.spell.save', async (_accessor, word) => {
        const res = await fetch('/api/spell/dictionary', { method: 'POST', body: JSON.stringify({ word }) }).catch(() => null);
        if (!res?.ok) { toast('That word could not be saved.', true); return; }
        verdict.delete(word);
        toast(`'${word}' is in the project's dictionary (.pymagic/dictionary.txt).`);
        again();
    });
    return { refresh: again };
}

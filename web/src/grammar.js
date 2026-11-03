// Grammar: the sentences of comments, docstrings and strings in Python files, of Markdown and text files, and of the commit message, checked by
// LanguageTool, which runs as a small server on this machine (the engine's Grammar). It is downloaded once, when the user says yes. Problems
// are info markers (`Grammar: …`); Alt+Enter offers what LanguageTool suggests and *Ignore this rule in the project*
// (.pymagic/grammar-ignore.txt). Spelling is Windows' (spell.js), so LanguageTool's own word list is left out.
import * as monaco from 'monaco-editor';
import { proseSegments, spellKind } from './spell-model';
import { settings, onSettings } from './settings';
import { sizeText } from './deploy-model.js';
import { toast } from './toast';

const OWNER = 'pymagic-grammar';
const MAX_SEGMENTS = 40;
const $ = (id) => document.getElementById(id);

export function setupGrammar({ relOf }) {
    const cache = new Map();          // language + text -> matches
    const found = new Map();          // model -> [{line, column, replacements, rule, message}]
    const timers = new Map();
    let status = null;
    let installing = false;
    let ignoredAtCheck = '';

    const lang = () => settings().grammarLanguage || 'en-US';
    const enabled = () => !!settings().grammar;

    async function refreshStatus() {
        try { status = await fetch('/api/grammar').then((r) => r.json()); } catch { status = null; }
        return status;
    }

    async function ask(text) {
        const key = lang() + '|' + (status?.ignored ?? []).join(',') + '|' + text;
        if (cache.has(key)) return cache.get(key);
        let answer;
        try { answer = await fetch('/api/grammar/check', { method: 'POST', body: JSON.stringify({ text, lang: lang() }) }).then((r) => r.json()); } catch { answer = { error: 'The engine did not answer.' }; }
        if (answer.error) { if (!ask.told) { ask.told = true; toast(answer.error, true); } return null; }
        cache.set(key, answer.matches);
        if (cache.size > 800) cache.delete(cache.keys().next().value);
        return answer.matches;
    }

    async function check(model) {
        if (model.isDisposed()) return;
        const rel = relOf(model);
        const kind = rel && spellKind(rel);
        if (!kind || !enabled() || !status?.installed) { monaco.editor.setModelMarkers(model, OWNER, []); found.delete(model); return; }
        const text = model.getValue();
        const marks = [], mine = [];
        for (const seg of proseSegments(text, kind).slice(0, MAX_SEGMENTS)) {
            const matches = await ask(seg.text);
            if (matches === null) return;
            if (model.isDisposed() || model.getValue() !== text) return;
            for (const m of matches) {
                if (m.length <= 0 || seg.map[m.offset] === undefined) continue;
                const start = seg.map[m.offset], end = (seg.map[Math.min(seg.map.length - 1, m.offset + m.length - 1)] ?? start) + 1;
                const a = model.getPositionAt(start), b = model.getPositionAt(end);
                marks.push({ startLineNumber: a.lineNumber, startColumn: a.column, endLineNumber: b.lineNumber, endColumn: b.column, severity: monaco.MarkerSeverity.Info, message: `Grammar: ${m.message}`, code: m.ruleId, source: 'Grammar' });
                mine.push({ line: a.lineNumber, column: a.column, replacements: m.replacements, rule: m.ruleId, message: m.message });
            }
        }
        found.set(model, mine);
        monaco.editor.setModelMarkers(model, OWNER, marks);
    }

    function schedule(model, ms = 2500) {
        clearTimeout(timers.get(model));
        timers.set(model, setTimeout(() => void check(model), ms));
    }

    function watch(model) {
        const rel = relOf(model);
        if (!rel || !spellKind(rel)) return;
        schedule(model, 600);
        model.onDidChangeContent(() => schedule(model));
        model.onWillDispose(() => { clearTimeout(timers.get(model)); timers.delete(model); found.delete(model); });
    }
    monaco.editor.onDidCreateModel((model) => setTimeout(() => watch(model), 0));
    for (const model of monaco.editor.getModels()) watch(model);

    // ---- installing, the first time it is wanted

    async function install() {
        if (installing) return;
        installing = true;
        await fetch('/api/grammar/install', { method: 'POST' }).catch(() => null);
        for (;;) {
            await new Promise((ok) => setTimeout(ok, 1000));
            await refreshStatus();
            const i = status?.installing;
            if (!i) break;
            if (i.phase === 'downloading') toast(`Downloading LanguageTool… ${i.total ? Math.round((100 * i.bytes) / i.total) + ' % of ' + sizeText(i.total) : sizeText(i.bytes)}`);
            else if (i.phase === 'unpacking') toast('Unpacking LanguageTool…');
            else if (i.phase === 'failed') { toast(`LanguageTool could not be installed: ${i.error}`, true); break; }
            else if (i.phase === 'done') { toast('LanguageTool is installed. The first check starts it (a few seconds).'); break; }
            else break;
        }
        installing = false;
        again();
    }

    async function wanted() {
        await refreshStatus();
        if (!enabled() || !status) return;
        if (status.installed) { again(); return; }
        if (!status.java) { toast('Grammar check needs Java (11 or newer) for LanguageTool, which was not found.', true); return; }
        const ok = window.confirm(`Check grammar with LanguageTool?\n\nIt is a Java program that runs on this computer only. PyMagic would download it once from languagetool.org (about 250 MB) and keep it in:\n${status.folder}\n\nJava was found: ${status.java}`);
        if (ok) await install();
    }

    function again() { for (const m of monaco.editor.getModels()) schedule(m, 50); void checkMessage(); }
    let wasOn = enabled();
    onSettings(() => {
        if (enabled() && !wasOn) void wanted(); else again();
        wasOn = enabled();
    });
    void refreshStatus().then(() => { if (enabled() && !status?.installed) void wanted(); else again(); });

    // ---- the quick fixes

    async function provideCodeActions(model, range, context) {
        const marks = context.markers.filter((m) => m.source === 'Grammar');
        if (!marks.length) return { actions: [], dispose() { } };
        const mark = marks[0];
        const where = new monaco.Range(mark.startLineNumber, mark.startColumn, mark.endLineNumber, mark.endColumn);
        // by where the marker stands (the text may have changed since it was made, which moves offsets but not a marker)
        const mine = (found.get(model) ?? []).find((f) => f.line === mark.startLineNumber && f.column === mark.startColumn);
        const actions = (mine?.replacements ?? []).map((r) => ({
            title: `Grammar: replace with '${r}'`, kind: 'quickfix',
            edit: { edits: [{ resource: model.uri, versionId: undefined, textEdit: { range: where, text: r } }] },
        }));
        if (mine?.rule) actions.push({ title: `Ignore the rule ${mine.rule} in this project`, kind: 'quickfix', command: { id: 'pymagic.grammar.ignore', title: 'Ignore the rule', arguments: [mine.rule] } });
        return { actions, dispose() { } };
    }
    for (const language of ['python', 'markdown', 'plaintext', 'restructuredtext']) monaco.languages.registerCodeActionProvider(language, { provideCodeActions });
    monaco.editor.registerCommand('pymagic.grammar.ignore', async (_accessor, rule) => {
        const res = await fetch('/api/grammar/ignore', { method: 'POST', body: JSON.stringify({ rule }) }).catch(() => null);
        if (!res?.ok) { toast('The rule could not be switched off.', true); return; }
        toast(`${rule} is switched off for this project (.pymagic/grammar-ignore.txt).`);
        await refreshStatus();
        again();
    });

    // ---- the commit message: the problems listed under it, each fixed with a click

    let messageTimer = 0;
    async function checkMessage() {
        const box = $('git-grammar'), input = $('git-message');
        if (!box || !input) return;
        box.replaceChildren();
        const text = input.value;
        if (!enabled() || !status?.installed || text.trim().length < 12) { box.hidden = true; return; }
        const matches = await ask(text);
        if (!matches || input.value !== text) return;
        box.hidden = matches.length === 0;
        for (const m of matches.slice(0, 6)) {
            const row = document.createElement('div');
            row.className = 'git-grammar-row';
            row.append(Object.assign(document.createElement('span'), { textContent: `${text.slice(m.offset, m.offset + m.length) || '…'}: ${m.message}` }));
            for (const r of m.replacements.slice(0, 2)) {
                const b = Object.assign(document.createElement('button'), { className: 'mini', textContent: r || '(remove)' });
                b.onclick = () => { input.value = text.slice(0, m.offset) + r + text.slice(m.offset + m.length); input.dispatchEvent(new Event('input')); };
                row.append(b);
            }
            box.append(row);
        }
    }
    $('git-message')?.addEventListener('input', () => { clearTimeout(messageTimer); messageTimer = window.setTimeout(() => void checkMessage(), 1500); });

    return { refresh: again, status: () => status, checkNow: () => Promise.all(monaco.editor.getModels().map((m) => check(m))) };
}

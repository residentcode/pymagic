// Macros: the typing and the editing keys of the editor recorded, played back (once, or many times), kept by name in the project
// (.pymagic/macros.json) and given a key. macro-model.js says what a key press means; here are the recording, the playing and the dialog.
// Besides the keys of the table a macro carries the clipboard keys (played with a clipboard of its own, which starts as the system's), the clicks
// and drags of the mouse in the text, the commands the editor runs for any other key (F2, F12, Shift+F6...), for its own menu and the command
// palette, and the commands of the main menu and of Find action, by their place in the menu.
import * as monaco from 'monaco-editor';
import { allowedCombo, comboOf, compress, copiedText, describe, isTableCommand, pastedParts, recordedCommand, stepOf, tidy } from './macro-model';
import { runById } from './commands.js';
import { runMenuPath } from './mainmenu.js';
import { toast } from './toast';

const $ = (id) => document.getElementById(id);

export function setupMacros({ editor, askText }) {
    let saved = [];
    let last = null;                 // the macro recorded last, not kept yet: {steps}
    let recording = null;            // the steps so far, or null
    let restore = null;              // the editors' options before recording turned suggestions off
    let dialog = null;

    const indicator = document.createElement('span');
    indicator.id = 'macro-state';
    indicator.hidden = true;
    $('runstate').after(indicator);

    async function load() {
        try { saved = tidy(await fetch('/api/macros').then((r) => (r.ok ? r.json() : []))); } catch { saved = []; }
    }
    async function store() {
        await fetch('/api/macros', { method: 'PUT', body: JSON.stringify(saved) }).catch(() => toast('The macros could not be saved.', true));
    }
    const active = () => monaco.editor.getEditors().find((e) => e.hasTextFocus()) ?? editor;

    // ------------------------------------------------------------ recording

    /** While a macro is recorded, what a key does must not depend on a menu that will not be there when it is played. */
    function quiet(on) {
        const editors = monaco.editor.getEditors();
        if (on) {
            restore = editors.map((e) => [e, { quickSuggestions: e.getOption(monaco.editor.EditorOption.quickSuggestions), acceptSuggestionOnEnter: e.getOption(monaco.editor.EditorOption.acceptSuggestionOnEnter), suggestOnTriggerCharacters: e.getOption(monaco.editor.EditorOption.suggestOnTriggerCharacters), tabCompletion: e.getOption(monaco.editor.EditorOption.tabCompletion) }]);
            for (const e of editors) e.updateOptions({ quickSuggestions: false, acceptSuggestionOnEnter: 'off', suggestOnTriggerCharacters: false, tabCompletion: 'off' });
        } else {
            for (const [e, o] of restore ?? []) e.updateOptions(o);
            restore = null;
        }
    }

    let skipped = 0;
    let awaiting = false;        // a key of the text that is not in the table: the command the editor runs for it is recorded
    let picking = false;         // a key or a click outside the text (a menu of the editor, the command palette): the command it runs is recorded
    let inMenu = false;          // a command of the main menu runs: it is recorded as such, not the commands it runs
    let watches = [];            // the listeners of the recording: the command service, the mouse of each editor

    // what the person does is handled in this turn of the page: a command the editor runs in it is theirs
    const soon = (fn) => setTimeout(fn, 0);
    /** The text of an editor (its own input), not a box in it such as Find or Rename. */
    const inText = (t) => !!t?.closest?.('.monaco-editor') && !t.closest('.inj-window') && (t.classList?.contains('inputarea') || t.classList?.contains('native-edit-context'));

    function onKey(e) {
        if (!recording || e.defaultPrevented || ['Shift', 'Control', 'Alt', 'Meta'].includes(e.key)) return;
        if (!inText(e.target)) {
            picking = true;
            soon(() => { picking = false; });
            return;
        }
        const step = stepOf(e);
        if (step) { recording.push(step); return; }
        awaiting = true;
        soon(() => { if (awaiting) skipped++; awaiting = false; });
    }
    document.addEventListener('keydown', onKey, true);
    for (const type of ['mousedown', 'mouseup', 'click']) {
        document.addEventListener(type, (e) => {
            if (!recording || e.target?.closest?.('.monaco-editor .view-lines, .monaco-editor .margin')) return;
            picking = true;
            soon(() => { picking = false; });
        }, true);
    }
    window.addEventListener('pymagic:menu-ran', (e) => {
        if (!recording || !e.detail?.path) return;
        recording.push({ k: 'menu', path: e.detail.path });
        inMenu = true;
        soon(() => { inMenu = false; });
    });

    /** The command the editor ran for a key, for a menu of its own or the command palette, with its name for the list. */
    function onCommand({ commandId }) {
        if (!recording || inMenu || !(awaiting || picking)) return;
        const id = recordedCommand(commandId);
        if (!id) return;
        awaiting = false;
        const ed = active();
        const label = ed.getAction?.(commandId)?.label ?? ed.getSupportedActions?.().find((a) => a.id === id || a.id.endsWith(':' + id))?.label;
        recording.push(label ? { k: 'cmd', id, label } : { k: 'cmd', id });
    }

    /** A click in the text: where, how many (a double click selects the word), with Shift (extends) or Alt (adds a cursor); a drag selects. */
    function onMouseDown(m) {
        if (!recording || !m.event.leftButton || !m.target.position) return;
        const T = monaco.editor.MouseTargetType;
        if (m.target.type !== T.CONTENT_TEXT && m.target.type !== T.CONTENT_EMPTY) return;
        if (m.event.ctrlKey || m.event.metaKey) { skipped++; return; }
        const { lineNumber: line, column } = m.target.position;
        const n = Math.min(3, Math.max(1, m.event.detail || 1));
        const lastStep = recording[recording.length - 1];
        // the second press of a double click: the click before it becomes the double click
        if (n > 1 && lastStep?.k === 'click' && lastStep.line === line && !lastStep.to) { lastStep.n = n; delete lastStep.mode; return; }
        recording.push({ k: 'click', line, column, n: 1, ...(m.event.shiftKey ? { mode: 'extend' } : m.event.altKey ? { mode: 'add' } : {}) });
    }
    function onMouseUp(ed) {
        const lastStep = recording?.[recording.length - 1];
        if (lastStep?.k !== 'click' || lastStep.n !== 1 || lastStep.mode) return;
        const sel = ed.getSelection();
        if (!sel || sel.isEmpty() || sel.selectionStartLineNumber !== lastStep.line || sel.selectionStartColumn !== lastStep.column) return;
        lastStep.to = [sel.positionLineNumber, sel.positionColumn];
    }

    function watch(on) {
        for (const w of watches) w.dispose();
        watches = [];
        if (!on) return;
        // the editors share one command service: every command run for a key, a menu of the editor or the palette passes it
        const commands = editor._commandService;
        if (commands?.onDidExecuteCommand) watches.push(commands.onDidExecuteCommand(onCommand));
        for (const ed of monaco.editor.getEditors()) watches.push(ed.onMouseDown(onMouseDown), ed.onMouseUp(() => onMouseUp(ed)));
    }

    function start() {
        if (recording) return;
        recording = [];
        skipped = 0;
        quiet(true);
        watch(true);
        indicator.hidden = false;
        indicator.textContent = '● Recording a macro';
        indicator.title = 'Typing and editing keys are being recorded (Ctrl+Alt+Shift+R stops)';
        active().focus();
    }

    function stop() {
        if (!recording) return null;
        const steps = compress(recording);
        recording = null;
        quiet(false);
        watch(false);
        indicator.hidden = true;
        if (!steps.length) { toast('Nothing was recorded.'); return null; }
        last = { steps };
        toast(`Macro recorded: ${describe(steps, 60)}${skipped ? ` (${skipped} keys it cannot carry were left out)` : ''}`);
        return last;
    }

    // ------------------------------------------------------------ playing

    /** The clipboard keys, with the macro's own clipboard ({text, whole}: whole lines when copied from no selection, pasted above the line). */
    function clipboard(ed, op, clip) {
        const model = ed.getModel();
        const sels = ed.getSelections() ?? [];
        if (!model || !sels.length) return clip;
        if (op === 'paste') {
            if (!clip?.text) return clip;
            if (clip.whole && sels.every((s) => s.isEmpty())) {
                ed.executeEdits('macro', [...new Set(sels.map((s) => s.positionLineNumber))].map((line) => ({ range: new monaco.Range(line, 1, line, 1), text: clip.text })));
            } else {
                const parts = pastedParts(clip.text, sels.length);
                ed.executeEdits('macro', sels.map((s, i) => ({ range: s, text: parts[i], forceMoveMarkers: true })),
                    (inverse) => inverse.map((e) => new monaco.Selection(e.range.endLineNumber, e.range.endColumn, e.range.endLineNumber, e.range.endColumn)));
            }
            return clip;
        }
        const copied = copiedText(sels.map((s) => model.getValueInRange(s)), sels.map((s) => model.getLineContent(s.positionLineNumber)));
        if (op === 'cut') {
            if (copied.whole) ed.trigger('macro', 'editor.action.deleteLines', null);
            else ed.executeEdits('macro', sels.filter((s) => !s.isEmpty()).map((s) => ({ range: s, text: '' })));
        }
        return copied;
    }

    /** A click of the macro: the cursor put, the selection extended, a cursor added, the word or the line selected, or the text dragged over. */
    function click(ed, s) {
        const model = ed.getModel();
        if (!model) return;
        const at = model.validatePosition({ lineNumber: s.line, column: s.column });
        const sel = (a, b) => new monaco.Selection(a.lineNumber, a.column, b.lineNumber, b.column);
        if (s.to) ed.setSelection(sel(at, model.validatePosition({ lineNumber: s.to[0], column: s.to[1] })));
        else if (s.n === 2) {
            const word = model.getWordAtPosition(at);
            ed.setSelection(word ? new monaco.Selection(at.lineNumber, word.startColumn, at.lineNumber, word.endColumn) : sel(at, at));
        } else if (s.n === 3) {
            const lastLine = at.lineNumber === model.getLineCount();
            ed.setSelection(new monaco.Selection(at.lineNumber, 1, lastLine ? at.lineNumber : at.lineNumber + 1, lastLine ? model.getLineMaxColumn(at.lineNumber) : 1));
        } else if (s.mode === 'extend') {
            const now = ed.getSelection();
            ed.setSelection(new monaco.Selection(now.selectionStartLineNumber, now.selectionStartColumn, at.lineNumber, at.column));
        } else if (s.mode === 'add') ed.setSelections([...(ed.getSelections() ?? []), sel(at, at)]);
        else ed.setPosition(at);
        ed.revealPositionInCenterIfOutsideViewport(ed.getPosition());
    }

    async function play(steps, times = 1) {
        const ed = active();
        ed.focus();
        // the clipboard of the macro starts as the system's, and the system's has what the macro left in it at the end
        let clip = null;
        const usesClipboard = steps.some((s) => s.k === 'clip');
        if (steps.some((s) => s.k === 'clip' && s.op === 'paste')) {
            try { clip = { text: await navigator.clipboard.readText(), whole: false }; } catch { clip = { text: '', whole: false }; }
        }
        const start = clip?.text;
        ed.pushUndoStop();
        let missing = '';
        for (let n = 0; n < times; n++) {
            for (const s of steps) {
                try {
                    if (s.k === 'text') ed.trigger('macro', 'type', { text: s.text });
                    else if (s.k === 'clip') clip = clipboard(ed, s.op, clip);
                    else if (s.k === 'click') click(ed, s);
                    else if (s.k === 'menu') { if (!(await runMenuPath(s.path))) missing ||= s.path; }
                    else if (isTableCommand(s.id)) ed.trigger('macro', s.id, null);
                    else await runById(s.id, ed);
                } catch (err) { console.warn('A step of the macro failed', s, err); }
            }
        }
        ed.pushUndoStop();
        if (usesClipboard && clip?.text && clip.text !== start) navigator.clipboard?.writeText(clip.text).catch(() => { /* the system's clipboard keeps what it had */ });
        if (missing) toast(`The main menu has no command '${missing}' now: the macro went on without it.`, true);
    }

    async function playLast() {
        if (!last) { toast('No macro has been recorded yet.', true); return; }
        play(last.steps);
    }

    async function save(macro, suggested = '') {
        const name = await askText('Save the macro', suggested, 'A name; the macro is kept in the project (.pymagic/macros.json) and can be given a key');
        if (!name?.trim()) return false;
        const n = name.trim().slice(0, 60);
        saved = tidy([...saved.filter((m) => m.name !== n), { name: n, key: saved.find((m) => m.name === n)?.key ?? '', steps: macro.steps }]);
        await store();
        toast(`Macro '${n}' is saved.`);
        return true;
    }

    // ------------------------------------------------------------ the keys of saved macros, and the commands

    window.addEventListener('keydown', (e) => {
        if (recording || dialog || e.defaultPrevented) return;
        const combo = comboOf(e);
        if (!combo || !allowedCombo(combo)) return;
        const m = saved.find((x) => x.key === combo);
        if (!m) return;
        e.preventDefault();
        e.stopPropagation();
        play(m.steps);
    }, true);

    // ------------------------------------------------------------ the dialog

    function manage() {
        if (dialog) return;
        const shade = document.createElement('div');
        shade.className = 'settings-shade';
        const box = document.createElement('div');
        box.className = 'hist-diff macro-box';
        const head = document.createElement('div');
        head.className = 'hist-diff-head';
        const title = document.createElement('span');
        title.className = 'hist-diff-title';
        title.textContent = 'Macros';
        const record = document.createElement('button');
        record.textContent = '● Record';
        record.title = 'Record typing and editing keys (Ctrl+Alt+Shift+R starts and stops)';
        const close = document.createElement('button');
        close.textContent = 'Close';
        head.append(title, record, close);
        const list = document.createElement('div');
        list.className = 'hist-diff-body macro-list';
        box.append(head, list);
        shade.append(box);
        document.body.append(shade);
        dialog = shade;
        const done = () => { document.removeEventListener('keydown', onEsc, true); shade.remove(); dialog = null; editor.focus(); };
        const onEsc = (e) => { if (e.key === 'Escape' && !capturing) { e.preventDefault(); e.stopPropagation(); done(); } };
        document.addEventListener('keydown', onEsc, true);
        close.onclick = done;
        shade.onmousedown = (e) => { if (e.target === shade) done(); };
        record.onclick = () => { done(); start(); };
        let capturing = false;

        const row = (name, steps, key, saved_) => {
            const r = document.createElement('div');
            r.className = 'macro-row';
            const info = document.createElement('div');
            info.className = 'macro-info';
            const n = document.createElement('div');
            n.className = 'macro-name';
            n.textContent = name;
            const d = document.createElement('div');
            d.className = 'macro-steps';
            d.textContent = describe(steps);
            info.append(n, d);
            const play1 = document.createElement('button');
            play1.textContent = 'Play';
            play1.onclick = () => { done(); setTimeout(() => play(steps), 50); };
            const many = document.createElement('button');
            many.textContent = 'Play N times…';
            many.onclick = async () => {
                const answer = await askText('Play the macro', '10', 'How many times');
                const times = parseInt(answer ?? '', 10);
                if (!(times >= 1 && times <= 5000)) return;
                done();
                setTimeout(() => play(steps, times), 50);
            };
            r.append(info, play1, many);
            if (saved_) {
                const keyButton = document.createElement('button');
                keyButton.className = 'macro-key';
                keyButton.textContent = key || 'Set key…';
                keyButton.title = 'Press the keys to give the macro (with Ctrl or Alt, or a function key); Escape keeps it as it is';
                keyButton.onclick = () => {
                    capturing = true;
                    keyButton.textContent = 'Press the keys…';
                    const onCombo = async (e) => {
                        const combo = comboOf(e);
                        if (!combo) return;
                        e.preventDefault();
                        e.stopPropagation();
                        document.removeEventListener('keydown', onCombo, true);
                        capturing = false;
                        if (e.key === 'Escape') { render(); return; }
                        if (e.key === 'Backspace' || e.key === 'Delete') { saved_.key = ''; }
                        else if (!allowedCombo(combo)) { toast(`${combo} would take a typed character: use Ctrl or Alt with it, or a function key.`, true); render(); return; }
                        else {
                            const other = saved.find((m) => m.key === combo && m !== saved_);
                            if (other) other.key = '';
                            saved_.key = combo;
                        }
                        await store();
                        render();
                    };
                    document.addEventListener('keydown', onCombo, true);
                };
                const remove = document.createElement('button');
                remove.textContent = 'Delete';
                remove.onclick = async () => { saved = saved.filter((m) => m !== saved_); await store(); render(); };
                r.append(keyButton, remove);
            } else {
                const keep = document.createElement('button');
                keep.className = 'primary';
                keep.textContent = 'Save…';
                keep.onclick = async () => { if (await save(last)) render(); };
                r.append(keep);
            }
            return r;
        };
        function render() {
            list.replaceChildren();
            if (last && !saved.some((m) => describe(m.steps, 10000) === describe(last.steps, 10000))) list.append(row('The last recording', last.steps, '', null));
            for (const m of saved) list.append(row(m.name, m.steps, m.key, m));
            if (!list.children.length) {
                const none = document.createElement('div');
                none.className = 'side-empty';
                none.textContent = 'No macros yet. ● Record, type or edit in the editor, then stop with Ctrl+Alt+Shift+R and save it under a name.';
                list.append(none);
            }
        }
        render();
    }

    // ------------------------------------------------------------ commands

    const toggleRecording = () => { if (recording) { const m = stop(); if (m) void save(m); } else start(); };
    const act = (id, label, run, keys) => editor.addAction({ id, label, keybindings: keys, run });
    act('pymagic.macro.toggle', 'Macros: Start or Stop Recording', toggleRecording, [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Alt | monaco.KeyMod.Shift | monaco.KeyCode.KeyR]);
    act('pymagic.macro.playLast', 'Macros: Play the Last Recording', () => void playLast(), [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Alt | monaco.KeyMod.Shift | monaco.KeyCode.KeyP]);
    act('pymagic.macro.manage', 'Macros: Manage…', manage);
    window.addEventListener('pymagic:macros', manage);
    void load();
    return { manage, start, stop, play, isRecording: () => !!recording, saved: () => saved, last: () => last };
}

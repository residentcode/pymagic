// Macros: the typing and the editing keys of the editor recorded, played back (once, or many times), kept by name in the project
// (.pymagic/macros.json) and given a key. macro-model.js says what a key press means; here are the recording, the playing and the dialog.
import * as monaco from 'monaco-editor';
import { allowedCombo, comboOf, compress, describe, stepOf, tidy } from './macro-model';
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

    function onKey(e) {
        if (!recording || e.defaultPrevented) return;
        const inEditor = e.target?.closest?.('.monaco-editor') && !e.target.closest('.inj-window');
        if (!inEditor) return;
        const step = stepOf(e);
        if (step) recording.push(step);
        else if (!['Shift', 'Control', 'Alt', 'Meta'].includes(e.key)) skipped++;
    }
    let skipped = 0;
    document.addEventListener('keydown', onKey, true);

    function start() {
        if (recording) return;
        recording = [];
        skipped = 0;
        quiet(true);
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
        indicator.hidden = true;
        if (!steps.length) { toast('Nothing was recorded.'); return null; }
        last = { steps };
        toast(`Macro recorded: ${describe(steps, 60)}${skipped ? ` (${skipped} keys it cannot carry were left out)` : ''}`);
        return last;
    }

    // ------------------------------------------------------------ playing

    function play(steps, times = 1) {
        const ed = active();
        ed.focus();
        ed.pushUndoStop();
        for (let n = 0; n < times; n++) {
            for (const s of steps) {
                if (s.k === 'text') ed.trigger('macro', 'type', { text: s.text });
                else ed.trigger('macro', s.id, null);
            }
        }
        ed.pushUndoStop();
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

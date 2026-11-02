// Refactorings: the server offers them as code actions that carry the command "pymagic.refactor"; the page asks for the
// name where one is needed, sends the request and applies the returned edits.
import { putLabel } from './labels';
import * as monaco from 'monaco-editor';
import { editSignature, fetchSignature } from './signature';
import { pickTarget } from './movepicker';
import { previewAlways, previewChanges } from './preview';
import { askChoice } from './ask';
import { defineCommand } from './commands.js';
const toRange = (r) => ({
    startLineNumber: r.start.line + 1, startColumn: r.start.character + 1,
    endLineNumber: r.end.line + 1, endColumn: r.end.character + 1,
});
export function setupRefactor(deps) {
    const { editor, lsp } = deps;
    let toastTimer;
    function toast(message, bad = false) {
        let el = document.getElementById('toast');
        if (!el) {
            el = document.createElement('div');
            el.id = 'toast';
            document.body.appendChild(el);
        }
        el.textContent = message;
        el.className = bad ? 'bad' : '';
        el.style.display = 'block';
        clearTimeout(toastTimer);
        toastTimer = window.setTimeout(() => { el.style.display = 'none'; }, bad ? 7000 : 2500);
    }
    /** A small box over the editor asking for a name; Enter accepts, Escape cancels. */
    function promptName(title, value) {
        return new Promise((resolve) => {
            const box = document.createElement('div');
            box.id = 'namebox';
            const label = document.createElement('label');
            label.textContent = title;
            const input = document.createElement('input');
            input.value = value;
            input.spellcheck = false;
            input.autocomplete = 'off';
            box.append(label, input);
            document.body.appendChild(box);
            let done = false;
            const finish = (result) => {
                if (done)
                    return;
                done = true;
                box.remove();
                editor.focus();
                resolve(result);
            };
            input.onkeydown = (e) => {
                if (e.key === 'Enter') {
                    e.preventDefault();
                    finish(input.value.trim() || null);
                }
                else if (e.key === 'Escape') {
                    e.preventDefault();
                    finish(null);
                }
            };
            input.onblur = () => window.setTimeout(() => finish(null), 120);
            input.focus();
            input.select();
        });
    }
    /** A box asking for a name (when value is not null) and which members of a class to take; Enter or OK accepts, Escape cancels. */
    function chooseMembers(title, nameTitle, value, members, chosen) {
        return new Promise((resolve) => {
            const box = document.createElement('div');
            box.id = 'membersbox';
            const head = document.createElement('div');
            head.className = 'mb-title';
            head.textContent = title;
            box.append(head);
            let input = null;
            if (value !== null) {
                const label = document.createElement('label');
                label.textContent = nameTitle;
                input = document.createElement('input');
                input.value = value;
                input.spellcheck = false;
                label.append(input);
                box.append(label);
            }
            const list = document.createElement('div');
            list.className = 'mb-list';
            const checks = members.map((m) => {
                const row = document.createElement('label');
                const c = document.createElement('input');
                c.type = 'checkbox';
                c.checked = chosen.includes(m);
                row.append(c, document.createTextNode(' ' + m));
                list.append(row);
                return [m, c];
            });
            const buttons = document.createElement('div');
            buttons.className = 'mb-buttons';
            const ok = document.createElement('button');
            ok.className = 'primary';
            ok.textContent = 'OK';
            const cancel = document.createElement('button');
            cancel.textContent = 'Cancel';
            buttons.append(cancel, ok);
            box.append(list, buttons);
            document.body.appendChild(box);
            const finish = (result) => {
                box.remove();
                document.removeEventListener('keydown', onKey, true);
                editor.focus();
                resolve(result);
            };
            const accept = () => {
                const picked = checks.filter(([, c]) => c.checked).map(([m]) => m);
                if (picked.length === 0) {
                    toast('Choose at least one member.', true);
                    return;
                }
                finish({ name: input ? input.value.trim() : null, members: picked });
            };
            const onKey = (e) => {
                if (e.key === 'Escape') {
                    e.preventDefault();
                    e.stopPropagation();
                    finish(null);
                }
                else if (e.key === 'Enter') {
                    e.preventDefault();
                    e.stopPropagation();
                    accept();
                }
            };
            document.addEventListener('keydown', onKey, true);
            ok.onclick = accept;
            cancel.onclick = () => finish(null);
            (input ?? checks[0]?.[1])?.focus();
            input?.select();
        });
    }
    const titles = {
        extractVariable: 'Name of the new variable',
        extractConstant: 'Name of the new constant',
        extractMethod: 'Name of the new function',
        introduceParameter: 'Name of the new parameter',
        introduceField: 'Name of the new attribute',
        extractSuperclass: 'Name of the superclass',
        extractDelegate: 'Name of the new class',
        invertBoolean: 'Name of the inverted variable',
        moveStaticMember: 'Move it to the class',
    };
    const displayName = (uri) => decodeURIComponent(uri.replace(/^file:\/\/\/?/, '')).split('/').slice(-2).join('/');
    /**
     * Applies the edits as one undo step. A refactoring that touches several files, one that was marked as always
     * previewed, and the ones that change code elsewhere (`alwaysPreview`) are shown first; false means cancelled.
     */
    async function applyEdits(title, changes, opts = {}) {
        const targets = [];
        for (const [target, list] of Object.entries(changes)) {
            const m = await deps.modelForUri(target);
            if (!m)
                continue;
            targets.push({ model: m, uri: target, edits: list.map((e) => ({ range: toRange(e.range), text: e.newText })) });
        }
        const warnings = opts.warnings ?? [];
        if (opts.alwaysPreview || previewAlways() || targets.length > 1 || warnings.length > 0) {
            const files = targets.map((t) => {
                const oldText = t.model.getValue();
                const ordered = [...t.edits].sort((a, b) => t.model.getOffsetAt({ lineNumber: b.range.startLineNumber, column: b.range.startColumn })
                    - t.model.getOffsetAt({ lineNumber: a.range.startLineNumber, column: a.range.startColumn }));
                let text = oldText;
                for (const e of ordered) {
                    const r = e.range;
                    text = text.slice(0, t.model.getOffsetAt({ lineNumber: r.startLineNumber, column: r.startColumn })) + (e.text ?? '')
                        + text.slice(t.model.getOffsetAt({ lineNumber: r.endLineNumber, column: r.endColumn }));
                }
                return { name: displayName(t.uri), oldText, newText: text, target: t };
            }).filter((f) => f.oldText !== f.newText);
            const answer = files.length > 0 ? await previewChanges(title, files, warnings) : true;
            if (!answer)
                return false;
            if (answer !== true) {
                // only some files or changes were chosen: each file gets the text its chosen changes make, the others are left alone
                const chosen = [];
                files.forEach((f, i) => {
                    const text = answer.texts[i];
                    if (text !== null)
                        chosen.push({ ...f.target, edits: text === f.newText ? f.target.edits : [{ range: f.target.model.getFullModelRange(), text }] });
                });
                if (chosen.length === 0)
                    return false;
                targets.splice(0, targets.length, ...chosen);
            }
        }
        editor.pushUndoStop();
        for (const t of targets)
            t.model.pushEditOperations([], t.edits, () => null);
        editor.pushUndoStop();
        return true;
    }
    async function changeSignature(uri, position) {
        const model = editor.getModel();
        if (model && deps.uriOf(model) === uri) {
            lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
        }
        let info;
        try {
            info = await fetchSignature(lsp, uri, position);
            // a method that overrides one of the project's: as PyCharm, offer to change the base method (and with it every override) instead
            if (info.base) {
                const answer = await askChoice('Change signature', `${info.name} overrides the method of ${info.base.class}. Change the base method instead, with every method that overrides it?`,
                    [['Change the base method', 'base'], ['This one only', 'this'], ['Cancel', 'cancel']]);
                if (answer === 'cancel') {
                    editor.focus();
                    return;
                }
                if (answer === 'base') {
                    uri = info.base.uri;
                    position = info.base.position;
                    info = await fetchSignature(lsp, uri, position);
                }
            }
        }
        catch (e) {
            toast(e.message ?? 'The signature cannot be changed.', true);
            return;
        }
        const edited = await editSignature(info);
        if (!edited) {
            editor.focus();
            return;
        }
        try {
            const result = await lsp.request('pymagic/changeSignature', { textDocument: { uri }, position, newName: edited.newName, parameters: edited.parameters });
            await applyEdits('Change signature', result.changes, { warnings: result.warnings ?? [], alwaysPreview: true });
        }
        catch (e) {
            toast(e.message ?? 'The signature cannot be changed.', true);
        }
        editor.focus();
    }
    async function moveSymbol(uri, position) {
        const model = editor.getModel();
        if (model && deps.uriOf(model) === uri) {
            lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
        }
        try {
            const targets = await lsp.request('pymagic/moveTargets', { textDocument: { uri } });
            const chosen = await pickTarget(targets);
            if (!chosen) {
                editor.focus();
                return;
            }
            const edit = await lsp.request('pymagic/move', { textDocument: { uri }, position, target: chosen.uri });
            if (await applyEdits(`Move to ${chosen.module}`, edit.changes, { alwaysPreview: true }))
                toast(`Moved to ${chosen.module}`);
        }
        catch (e) {
            toast(e.message ?? 'The definition cannot be moved.', true);
        }
        editor.focus();
    }
    const titleOf = {
        extractVariable: 'Extract variable', extractConstant: 'Extract constant', extractMethod: 'Extract method', inlineVariable: 'Inline variable',
        inlineMethod: 'Inline method', inlineMethodAll: 'Inline method and remove it', safeDelete: 'Safe delete',
        introduceParameter: 'Introduce parameter', introduceField: 'Introduce attribute', extractSuperclass: 'Extract superclass', extractDelegate: 'Extract delegate',
        pullMembersUp: 'Pull members up', pushMembersDown: 'Push members down', encapsulateField: 'Encapsulate field', invertBoolean: 'Invert boolean',
        makeTopLevel: 'Make function top-level', moveStaticMember: 'Move static member',
    };
    // the ones that change other code or other files are always shown first
    const previewed = new Set(['inlineMethodAll', 'safeDelete', 'extractSuperclass', 'extractDelegate', 'pullMembersUp', 'pushMembersDown', 'invertBoolean', 'makeTopLevel', 'moveStaticMember']);
    async function run(kind, uri, range, defaultName, all, members = [], chosen = []) {
        if (kind === 'moveSymbol') {
            await moveSymbol(uri, range.start);
            return;
        }
        if (kind === 'changeSignature') {
            await changeSignature(uri, range.start);
            return;
        }
        let name, picked;
        if (members.length > 0) {
            const answer = await chooseMembers(titleOf[kind] ?? 'Refactor', titles[kind] ?? 'Name', defaultName || null, members, chosen);
            if (answer === null)
                return;
            name = answer.name ?? undefined;
            picked = answer.members;
        }
        else if (defaultName) {
            const answer = await promptName(titles[kind] ?? 'Name', defaultName);
            if (answer === null)
                return;
            name = answer;
        }
        // the server must see exactly the text the selection refers to
        const model = editor.getModel();
        if (model && deps.uriOf(model) === uri) {
            lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
        }
        let edit;
        try {
            edit = await lsp.request('pymagic/refactor', { textDocument: { uri }, range, kind, name, all, members: picked });
        }
        catch (e) {
            toast(e.message ?? 'The refactoring could not be done.', true);
            return;
        }
        // the project as it is on disk is kept first, to go back to from Local History
        await putLabel('Before ' + (titleOf[kind] ?? 'refactoring').toLowerCase());
        await applyEdits(titleOf[kind] ?? 'Refactor', edit.changes, { alwaysPreview: previewed.has(kind) });
    }
    monaco.editor.registerCommand('pymagic.refactor', (_accessor, kind, uri, range, defaultName, all, members, chosen) => {
        void run(kind, uri, range, defaultName, all, members ?? [], chosen ?? []);
    });
    /** Runs the refactoring of this kind for the current selection (or cursor), through the same menu the lightbulb uses. */
    async function trigger(kind, ed = editor) {
        const model = ed.getModel(), sel = ed.getSelection();
        const uri = model && deps.uriOf(model);
        if (!model || !sel || !uri)
            return;
        lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
        const range = {
            start: { line: sel.startLineNumber - 1, character: sel.startColumn - 1 },
            end: { line: sel.endLineNumber - 1, character: sel.endColumn - 1 },
        };
        const actions = (await lsp.request('textDocument/codeAction', { textDocument: { uri }, range, context: { diagnostics: [] } })) ?? [];
        const match = actions.find((a) => a.command?.command === 'pymagic.refactor' && a.command.arguments[0] === kind && !a.command.arguments[4]);
        if (!match) {
            toast(kind === 'inlineVariable' ? 'Put the cursor on a variable that can be inlined.' : 'This refactoring is not available for the current selection.', true);
            return;
        }
        const [k, u, r, name, all, members, chosen] = match.command.arguments;
        await run(k, u, r, name, all, members ?? [], chosen ?? []);
    }
    for (const [kind, label, key] of [['extractVariable', 'Extract variable', 'Ctrl+Alt+V'], ['extractConstant', 'Extract constant', 'Ctrl+Alt+C'], ['extractMethod', 'Extract method', 'Ctrl+Alt+M'],
        ['inlineVariable', 'Inline variable', 'Ctrl+Alt+N'], ['changeSignature', 'Change signature', 'Ctrl+F6'], ['inlineMethod', 'Inline method', 'Ctrl+Alt+P'], ['moveSymbol', 'Move to another file', 'F6']])
        defineCommand({ id: 'pymagic.refactor.' + kind, label, group: 'Refactor', keys: [key], run: () => void trigger(kind) });
    return { trigger, applyEdits };
}

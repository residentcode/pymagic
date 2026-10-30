// Code generation in the editor: Surround With (Ctrl+Alt+T), Unwrap / Remove (Ctrl+Shift+Delete), Emmet abbreviations in HTML
// (Tab after `ul>li*3`), and the text a new file starts with (its file template).
import * as monaco from 'monaco-editor';
import { openMenu } from './menu';
import { toast } from './toast';
import { SURROUND_LINES, SURROUND_EXPRESSION, surroundLines, surroundExpression, unwrapOptions, abbreviationBefore, expandAbbreviation, newFileText, readFileTemplates } from './codegen-model.js';

const FILE_TEMPLATES = '.pymagic/fileTemplates.json';

/** The project's file templates (the defaults when it has none). */
export async function fileTemplates() {
    try {
        const res = await fetch('/api/file?path=' + encodeURIComponent(FILE_TEMPLATES));
        return readFileTemplates(res.ok ? await res.text() : '');
    }
    catch {
        return readFileTemplates('');
    }
}

/** What a new file of this name starts with. */
export async function templateText(fileName, projectName = '') {
    const now = new Date();
    const pad = (n) => String(n).padStart(2, '0');
    return newFileText(fileName, await fileTemplates(), {
        DATE: `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`, TIME: `${pad(now.getHours())}:${pad(now.getMinutes())}`,
        YEAR: now.getFullYear(), USER: '', PROJECT_NAME: projectName,
    });
}

export function setupCodeGen() {
    const K = monaco.KeyCode, M = monaco.KeyMod;
    const insert = (ed, range, snippet) => {
        ed.focus();
        ed.pushUndoStop();
        ed.setSelection(range);
        ed.getContribution('snippetController2')?.insert(snippet, { overwriteBefore: 0, overwriteAfter: 0 });
        ed.pushUndoStop();
    };
    const atCursor = (ed, items) => {
        const pos = ed.getPosition(), box = ed.getDomNode()?.getBoundingClientRect();
        const p = pos && ed.getScrolledVisiblePosition(pos);
        openMenu(box && p ? { x: box.left + p.left, y: box.top + p.top + p.height } : { x: 200, y: 200 }, items);
    };
    const unit = (model) => (model.getOptions().insertSpaces ? ' '.repeat(model.getOptions().indentSize) : '\t');

    function surround(ed) {
        const model = ed.getModel(), sel = ed.getSelection();
        if (!model || !sel)
            return;
        // a part of one line is an expression; whole lines (or the cursor's line) are statements
        const partial = !sel.isEmpty() && sel.startLineNumber === sel.endLineNumber
            && (sel.startColumn > model.getLineFirstNonWhitespaceColumn(sel.startLineNumber) || sel.endColumn < model.getLineLastNonWhitespaceColumn(sel.startLineNumber));
        if (partial) {
            const text = model.getValueInRange(sel);
            atCursor(ed, SURROUND_EXPRESSION.map(([id, label]) => ({ label, run: () => insert(ed, sel, surroundExpression(text, id)) })));
            return;
        }
        const last = sel.endColumn === 1 && sel.endLineNumber > sel.startLineNumber ? sel.endLineNumber - 1 : sel.endLineNumber;
        const range = new monaco.Range(sel.startLineNumber, 1, last, model.getLineMaxColumn(last));
        const lines = model.getValueInRange(range).split(/\r?\n/);
        if (model.getLanguageId() !== 'python') {
            toast('Surround With works in Python files.', true);
            return;
        }
        atCursor(ed, SURROUND_LINES.map(([id, label]) => ({
            label,
            run: () => {
                const snippet = surroundLines(lines, id, unit(model));
                if (snippet)
                    insert(ed, range, snippet);
                else
                    toast('There is nothing to surround here.', true);
            },
        })));
    }

    function unwrap(ed) {
        const model = ed.getModel(), pos = ed.getPosition();
        if (!model || !pos || model.getLanguageId() !== 'python')
            return;
        const options = unwrapOptions((l) => (l >= 1 && l <= model.getLineCount() ? model.getLineContent(l) : ''), pos.lineNumber, model.getLineCount());
        if (options.length === 0) {
            toast('There is no statement around the cursor to unwrap.', true);
            return;
        }
        const decorations = ed.createDecorationsCollection();
        const preview = (o) => decorations.set([{ range: new monaco.Range(o.start, 1, o.end, 1), options: { isWholeLine: true, className: 'unwrap-preview' } }]);
        const apply = (o) => {
            decorations.clear();
            ed.focus();
            ed.pushUndoStop();
            ed.executeEdits('unwrap', [{ range: new monaco.Range(o.start, 1, o.end, model.getLineMaxColumn(o.end)), text: o.lines.join(model.getEOL()), forceMoveMarkers: true }]);
            ed.pushUndoStop();
            ed.setPosition({ lineNumber: o.start, column: model.getLineFirstNonWhitespaceColumn(o.start) || 1 });
        };
        preview(options[0]);
        atCursor(ed, options.map((o) => ({ label: o.label, run: () => apply(o), onActive: () => preview(o) })));
        // the preview goes when the menu closes without a choice
        const stop = () => { decorations.clear(); document.removeEventListener('mousedown', stop, true); document.removeEventListener('keydown', onEsc, true); };
        const onEsc = (e) => { if (e.key === 'Escape') stop(); };
        window.setTimeout(() => { document.addEventListener('mousedown', stop, true); document.addEventListener('keydown', onEsc, true); }, 0);
    }

    /** Tab after an abbreviation in an HTML file or template expands it; elsewhere Tab does what it always does. */
    function emmet(ed) {
        const model = ed.getModel(), sel = ed.getSelection();
        if (model && sel && sel.isEmpty()) {
            const before = model.getLineContent(sel.startLineNumber).slice(0, sel.startColumn - 1);
            const abbr = abbreviationBefore(before);
            // not inside a {% %} or {{ }} of a template, nor inside a tag
            const open = before.lastIndexOf('<'), close = before.lastIndexOf('>');
            const inTag = open > close;
            const inTemplateCode = /\{[{%#][^}]*$/.test(before);
            const snippet = !inTag && !inTemplateCode && abbr ? expandAbbreviation(abbr, unit(model)) : null;
            if (snippet) {
                insert(ed, new monaco.Range(sel.startLineNumber, sel.startColumn - abbr.length, sel.startLineNumber, sel.startColumn), snippet);
                return;
            }
        }
        ed.trigger('keyboard', 'tab', null);
    }

    function attach(ed) {
        ed.addAction({ id: 'pymagic.surroundWith', label: 'Surround With…', contextMenuGroupId: '1_modification', contextMenuOrder: 0.7, keybindings: [M.CtrlCmd | M.Alt | K.KeyT], run: () => surround(ed) });
        ed.addAction({ id: 'pymagic.unwrap', label: 'Unwrap / Remove…', contextMenuGroupId: '1_modification', contextMenuOrder: 0.8, keybindings: [M.CtrlCmd | M.Shift | K.Delete], precondition: 'editorLangId == python', run: () => unwrap(ed) });
        ed.addCommand(K.Tab, () => emmet(ed), 'editorLangId =~ /^(html|django-html)$/ && editorTextFocus && !suggestWidgetVisible && !inSnippetMode && !editorHasSelection && !editorTabMovesFocus');
    }
    return { attach, surround, unwrap };
}

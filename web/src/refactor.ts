// Refactorings: the server offers them as code actions that carry the command "pymagic.refactor"; the page asks for the
// name where one is needed, sends the request and applies the returned edits.
import * as monaco from 'monaco-editor';
import type { LspClient } from './lsp';
import { editSignature, fetchSignature, type SigInfo } from './signature';
import { pickTarget } from './movepicker';
import { previewAlways, previewChanges, type FileChange } from './preview';

export interface RefactorDeps {
  editor: monaco.editor.IStandaloneCodeEditor;
  lsp: LspClient;
  modelForUri(uri: string): Promise<monaco.editor.ITextModel | null>;
  uriOf(model: monaco.editor.ITextModel): string | undefined;
}

interface LspPosition { line: number; character: number }
interface LspRange { start: LspPosition; end: LspPosition }

const toRange = (r: LspRange): monaco.IRange => ({
  startLineNumber: r.start.line + 1, startColumn: r.start.character + 1,
  endLineNumber: r.end.line + 1, endColumn: r.end.character + 1,
});

export function setupRefactor(deps: RefactorDeps) {
  const { editor, lsp } = deps;
  let toastTimer: number | undefined;

  function toast(message: string, bad = false) {
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
    toastTimer = window.setTimeout(() => { el!.style.display = 'none'; }, bad ? 7000 : 2500);
  }

  /** A small box over the editor asking for a name; Enter accepts, Escape cancels. */
  function promptName(title: string, value: string): Promise<string | null> {
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
      const finish = (result: string | null) => {
        if (done) return;
        done = true;
        box.remove();
        editor.focus();
        resolve(result);
      };
      input.onkeydown = (e) => {
        if (e.key === 'Enter') { e.preventDefault(); finish(input.value.trim() || null); }
        else if (e.key === 'Escape') { e.preventDefault(); finish(null); }
      };
      input.onblur = () => window.setTimeout(() => finish(null), 120);
      input.focus();
      input.select();
    });
  }

  const titles: Record<string, string> = {
    extractVariable: 'Name of the new variable',
    extractConstant: 'Name of the new constant',
    extractMethod: 'Name of the new function',
  };

  type Changes = Record<string, { range: LspRange; newText: string }[]>;

  const displayName = (uri: string) => decodeURIComponent(uri.replace(/^file:\/\/\/?/, '')).split('/').slice(-2).join('/');

  /**
   * Applies the edits as one undo step. A refactoring that touches several files, one that was marked as always
   * previewed, and the ones that change code elsewhere (`alwaysPreview`) are shown first; false means cancelled.
   */
  async function applyEdits(title: string, changes: Changes, opts: { warnings?: string[]; alwaysPreview?: boolean } = {}): Promise<boolean> {
    const targets: { model: monaco.editor.ITextModel; uri: string; edits: monaco.editor.IIdentifiedSingleEditOperation[] }[] = [];
    for (const [target, list] of Object.entries(changes)) {
      const m = await deps.modelForUri(target);
      if (!m) continue;
      targets.push({ model: m, uri: target, edits: list.map((e) => ({ range: toRange(e.range), text: e.newText })) });
    }
    const warnings = opts.warnings ?? [];
    if (opts.alwaysPreview || previewAlways() || targets.length > 1 || warnings.length > 0) {
      const files: FileChange[] = targets.map((t) => {
        const oldText = t.model.getValue();
        const ordered = [...t.edits].sort((a, b) => t.model.getOffsetAt({ lineNumber: (b.range as monaco.IRange).startLineNumber, column: (b.range as monaco.IRange).startColumn })
          - t.model.getOffsetAt({ lineNumber: (a.range as monaco.IRange).startLineNumber, column: (a.range as monaco.IRange).startColumn }));
        let text = oldText;
        for (const e of ordered) {
          const r = e.range as monaco.IRange;
          text = text.slice(0, t.model.getOffsetAt({ lineNumber: r.startLineNumber, column: r.startColumn })) + (e.text ?? '')
            + text.slice(t.model.getOffsetAt({ lineNumber: r.endLineNumber, column: r.endColumn }));
        }
        return { name: displayName(t.uri), oldText, newText: text };
      }).filter((f) => f.oldText !== f.newText);
      if (files.length > 0 && !(await previewChanges(title, files, warnings))) return false;
    }
    editor.pushUndoStop();
    for (const t of targets) t.model.pushEditOperations([], t.edits, () => null);
    editor.pushUndoStop();
    return true;
  }

  async function changeSignature(uri: string, position: LspPosition) {
    const model = editor.getModel();
    if (model && deps.uriOf(model) === uri) {
      lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
    }
    let info: SigInfo;
    try { info = await fetchSignature(lsp, uri, position); }
    catch (e: any) { toast(e.message ?? 'The signature cannot be changed.', true); return; }
    const edited = await editSignature(info);
    if (!edited) { editor.focus(); return; }
    try {
      const result: { changes: Record<string, { range: LspRange; newText: string }[]>; warnings: string[] } =
        await lsp.request('pymagic/changeSignature', { textDocument: { uri }, position, newName: edited.newName, parameters: edited.parameters });
      await applyEdits('Change signature', result.changes, { warnings: result.warnings ?? [], alwaysPreview: true });
    } catch (e: any) {
      toast(e.message ?? 'The signature cannot be changed.', true);
    }
    editor.focus();
  }

  async function moveSymbol(uri: string, position: LspPosition) {
    const model = editor.getModel();
    if (model && deps.uriOf(model) === uri) {
      lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
    }
    try {
      const targets: { uri: string; module: string }[] = await lsp.request('pymagic/moveTargets', { textDocument: { uri } });
      const chosen = await pickTarget(targets);
      if (!chosen) { editor.focus(); return; }
      const edit: { changes: Record<string, { range: LspRange; newText: string }[]> } =
        await lsp.request('pymagic/move', { textDocument: { uri }, position, target: chosen.uri });
      if (await applyEdits(`Move to ${chosen.module}`, edit.changes, { alwaysPreview: true })) toast(`Moved to ${chosen.module}`);
    } catch (e: any) {
      toast(e.message ?? 'The definition cannot be moved.', true);
    }
    editor.focus();
  }

  const titleOf: Record<string, string> = {
    extractVariable: 'Extract variable', extractConstant: 'Extract constant', extractMethod: 'Extract method', inlineVariable: 'Inline variable',
    inlineMethod: 'Inline method', inlineMethodAll: 'Inline method and remove it', safeDelete: 'Safe delete',
  };

  async function run(kind: string, uri: string, range: LspRange, defaultName: string, all: boolean) {
    if (kind === 'moveSymbol') { await moveSymbol(uri, range.start); return; }
    if (kind === 'changeSignature') { await changeSignature(uri, range.start); return; }
    let name: string | undefined;
    if (defaultName) {
      const answer = await promptName(titles[kind] ?? 'Name', defaultName);
      if (answer === null) return;
      name = answer;
    }

    // the server must see exactly the text the selection refers to
    const model = editor.getModel();
    if (model && deps.uriOf(model) === uri) {
      lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
    }

    let edit: { changes: Record<string, { range: LspRange; newText: string }[]> };
    try {
      edit = await lsp.request('pymagic/refactor', { textDocument: { uri }, range, kind, name, all });
    } catch (e: any) {
      toast(e.message ?? 'The refactoring could not be done.', true);
      return;
    }

    await applyEdits(titleOf[kind] ?? 'Refactor', edit.changes, { alwaysPreview: kind === 'inlineMethodAll' || kind === 'safeDelete' });
  }

  monaco.editor.registerCommand('pymagic.refactor', (_accessor, kind: string, uri: string, range: LspRange, defaultName: string, all: boolean) => {
    void run(kind, uri, range, defaultName, all);
  });

  /** Runs the refactoring of this kind for the current selection (or cursor), through the same menu the lightbulb uses. */
  async function trigger(kind: string) {
    const model = editor.getModel(), sel = editor.getSelection();
    const uri = model && deps.uriOf(model);
    if (!model || !sel || !uri) return;
    lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
    const range: LspRange = {
      start: { line: sel.startLineNumber - 1, character: sel.startColumn - 1 },
      end: { line: sel.endLineNumber - 1, character: sel.endColumn - 1 },
    };
    const actions: any[] = (await lsp.request('textDocument/codeAction', { textDocument: { uri }, range, context: { diagnostics: [] } })) ?? [];
    const match = actions.find((a) => a.command?.command === 'pymagic.refactor' && a.command.arguments[0] === kind && !a.command.arguments[4]);
    if (!match) { toast(kind === 'inlineVariable' ? 'Put the cursor on a variable that can be inlined.' : 'This refactoring is not available for the current selection.', true); return; }
    const [k, u, r, name, all] = match.command.arguments;
    await run(k, u, r, name, all);
  }

  const M = monaco.KeyMod, K = monaco.KeyCode;
  editor.addCommand(M.CtrlCmd | M.Alt | K.KeyV, () => void trigger('extractVariable'));
  editor.addCommand(M.CtrlCmd | M.Alt | K.KeyC, () => void trigger('extractConstant'));
  editor.addCommand(M.CtrlCmd | M.Alt | K.KeyM, () => void trigger('extractMethod'));
  editor.addCommand(M.CtrlCmd | M.Alt | K.KeyN, () => void trigger('inlineVariable'));
  editor.addCommand(M.CtrlCmd | K.F6, () => void trigger('changeSignature'));
  editor.addCommand(M.CtrlCmd | M.Alt | K.KeyP, () => void trigger('inlineMethod'));
  editor.addCommand(K.F6, () => void trigger('moveSymbol'));

  return { trigger };
}

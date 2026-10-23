import * as monaco from 'monaco-editor';
import { LspClient } from './lsp';
import { setupRun } from './run';

(self as any).MonacoEnvironment = { getWorker: () => new Worker('editor.worker.js') };

// ---------------------------------------------------------------- LSP <-> Monaco conversions

interface LspPosition { line: number; character: number }
interface LspRange { start: LspPosition; end: LspPosition }

const toPos = (p: monaco.Position): LspPosition => ({ line: p.lineNumber - 1, character: p.column - 1 });
const toRange = (r: LspRange): monaco.IRange => ({
  startLineNumber: r.start.line + 1, startColumn: r.start.character + 1,
  endLineNumber: r.end.line + 1, endColumn: r.end.character + 1,
});

const markerSeverity = (s: number | undefined) =>
  s === 1 ? monaco.MarkerSeverity.Error : s === 2 ? monaco.MarkerSeverity.Warning
  : s === 3 ? monaco.MarkerSeverity.Info : monaco.MarkerSeverity.Hint;

const K = monaco.languages.CompletionItemKind;
const completionKinds: Record<number, monaco.languages.CompletionItemKind> = {
  2: K.Method, 3: K.Function, 5: K.Field, 6: K.Variable, 7: K.Class, 9: K.Module, 10: K.Property,
  14: K.Keyword, 21: K.Constant, 25: K.TypeParameter,
};

const S = monaco.languages.SymbolKind;
const symbolKinds: Record<number, monaco.languages.SymbolKind> = {
  2: S.Module, 5: S.Class, 6: S.Method, 7: S.Property, 8: S.Field, 9: S.Constructor, 12: S.Function,
  13: S.Variable, 14: S.Constant, 26: S.TypeParameter,
};

// ---------------------------------------------------------------- workspace state

const lsp = new LspClient();
const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

let rootUri = '';
const models = new Map<string, monaco.editor.ITextModel>();   // server uri (normalised) -> model
const serverUri = new WeakMap<monaco.editor.ITextModel, string>();
const savedText = new Map<string, string>();                   // relative path -> last saved/loaded text
const relPathOf = new Map<string, string>();                   // normalised uri -> relative path
const loading = new Map<string, Promise<monaco.editor.ITextModel | null>>();

const normalise = (uri: string) => decodeURIComponent(uri).toLowerCase();
const uriFor = (rel: string) => rootUri + rel.split('/').map(encodeURIComponent).join('/');

function ensureModel(rel: string): Promise<monaco.editor.ITextModel | null> {
  const key = normalise(uriFor(rel));
  let p = loading.get(key);
  if (!p) loading.set(key, (p = loadModel(rel, key)));
  return p;
}

async function loadModel(rel: string, key: string): Promise<monaco.editor.ITextModel | null> {
  const uri = uriFor(rel);
  const res = await fetch('/api/file?path=' + encodeURIComponent(rel));
  if (!res.ok) return null;
  const text = await res.text();

  const model = monaco.editor.createModel(text, 'python', monaco.Uri.parse('pymagic:///' + rel));
  models.set(key, model);
  serverUri.set(model, uri);
  relPathOf.set(key, rel);
  savedText.set(rel, text);
  runHooks?.onModelCreated(rel, model);

  lsp.notify('textDocument/didOpen', {
    textDocument: { uri, languageId: 'python', version: model.getVersionId(), text },
  });
  let timer: number | undefined;
  model.onDidChangeContent(() => {
    refreshDirty();
    clearTimeout(timer);
    timer = window.setTimeout(() => lsp.notify('textDocument/didChange', {
      textDocument: { uri, version: model.getVersionId() },
      contentChanges: [{ text: model.getValue() }],
    }), 120);
  });
  return model;
}

/** Resolves a location the server returned to a model, loading the file if it is not open yet. */
async function modelForUri(uri: string): Promise<monaco.editor.ITextModel | null> {
  const key = normalise(uri);
  const open = models.get(key);
  if (open) return open;
  if (key.startsWith(normalise(rootUri))) return ensureModel(decodeURIComponent(uri.slice(rootUri.length)));
  return null;   // outside the workspace (typeshed, the standard library): not editable here
}

// ---------------------------------------------------------------- language features

function registerProviders() {
  monaco.languages.registerHoverProvider('python', {
    async provideHover(model, position) {
      const uri = serverUri.get(model);
      if (!uri) return null;
      const h = await lsp.request('textDocument/hover', { textDocument: { uri }, position: toPos(position) });
      if (!h) return null;
      return { range: toRange(h.range), contents: [{ value: h.contents.value }] };
    },
  });

  monaco.languages.registerDefinitionProvider('python', {
    async provideDefinition(model, position) {
      const uri = serverUri.get(model);
      if (!uri) return null;
      const locs: { uri: string; range: LspRange }[] =
        (await lsp.request('textDocument/definition', { textDocument: { uri }, position: toPos(position) })) ?? [];
      const result: monaco.languages.Location[] = [];
      for (const l of locs) {
        const target = await modelForUri(l.uri);
        if (target) result.push({ uri: target.uri, range: toRange(l.range) });
      }
      return result;
    },
  });

  monaco.languages.registerCompletionItemProvider('python', {
    triggerCharacters: ['.'],
    async provideCompletionItems(model, position) {
      const uri = serverUri.get(model);
      if (!uri) return { suggestions: [] };
      const word = model.getWordUntilPosition(position);
      const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
      // make sure the server has the text the cursor is in, not the debounced previous version
      lsp.notify('textDocument/didChange', {
        textDocument: { uri, version: model.getVersionId() },
        contentChanges: [{ text: model.getValue() }],
      });
      const res = await lsp.request('textDocument/completion', { textDocument: { uri }, position: toPos(position) });
      return {
        suggestions: (res?.items ?? []).map((i: any) => ({
          label: i.label,
          kind: completionKinds[i.kind] ?? K.Text,
          detail: i.detail,
          insertText: i.label,
          sortText: i.sortText,
          range,
        })),
      };
    },
  });

  monaco.languages.registerReferenceProvider('python', {
    async provideReferences(model, position, context) {
      const uri = serverUri.get(model);
      if (!uri) return [];
      const locs: { uri: string; range: LspRange }[] = (await lsp.request('textDocument/references', {
        textDocument: { uri }, position: toPos(position), context: { includeDeclaration: context.includeDeclaration },
      })) ?? [];
      const result: monaco.languages.Location[] = [];
      for (const l of locs) {
        const target = await modelForUri(l.uri);
        if (target) result.push({ uri: target.uri, range: toRange(l.range) });
      }
      return result;
    },
  });

  monaco.languages.registerRenameProvider('python', {
    async resolveRenameLocation(model, position) {
      const uri = serverUri.get(model);
      if (!uri) return { range: new monaco.Range(1, 1, 1, 1), text: '', rejectReason: 'not a workspace file' };
      try {
        const r = await lsp.request('textDocument/prepareRename', { textDocument: { uri }, position: toPos(position) });
        if (!r) return { range: new monaco.Range(1, 1, 1, 1), text: '', rejectReason: 'Nothing to rename here.' };
        return { range: toRange(r.range), text: r.placeholder };
      } catch (e: any) {
        return { range: new monaco.Range(1, 1, 1, 1), text: '', rejectReason: e.message };
      }
    },
    async provideRenameEdits(model, position, newName) {
      const uri = serverUri.get(model);
      if (!uri) return { edits: [] };
      try {
        const edit = await lsp.request('textDocument/rename', { textDocument: { uri }, position: toPos(position), newName });
        const edits: monaco.languages.IWorkspaceTextEdit[] = [];
        for (const [target, list] of Object.entries<any[]>(edit.changes)) {
          const m = await modelForUri(target);
          if (!m) continue;
          for (const e of list) edits.push({ resource: m.uri, versionId: undefined, textEdit: { range: toRange(e.range), text: e.newText } });
        }
        return { edits };
      } catch (e: any) {
        return { edits: [], rejectReason: e.message };
      }
    },
  });

  monaco.languages.registerCodeActionProvider('python', {
    async provideCodeActions(model, range) {
      const uri = serverUri.get(model);
      if (!uri) return { actions: [], dispose() {} };
      // the server recomputes diagnostics from its own copy, so send it the text the cursor is in
      lsp.notify('textDocument/didChange', {
        textDocument: { uri, version: model.getVersionId() },
        contentChanges: [{ text: model.getValue() }],
      });
      const lspRange = {
        start: { line: range.startLineNumber - 1, character: range.startColumn - 1 },
        end: { line: range.endLineNumber - 1, character: range.endColumn - 1 },
      };
      const found: any[] = (await lsp.request('textDocument/codeAction', {
        textDocument: { uri }, range: lspRange, context: { diagnostics: [] },
      })) ?? [];
      const actions: monaco.languages.CodeAction[] = [];
      for (const a of found) {
        const edits: monaco.languages.IWorkspaceTextEdit[] = [];
        for (const [target, list] of Object.entries<any[]>(a.edit.changes)) {
          const m = await modelForUri(target);
          if (!m) continue;
          for (const e of list) edits.push({ resource: m.uri, versionId: undefined, textEdit: { range: toRange(e.range), text: e.newText } });
        }
        actions.push({ title: a.title, kind: a.kind, isPreferred: a.isPreferred, edit: { edits } });
      }
      return { actions, dispose() {} };
    },
  });

  const convert = (s: any): monaco.languages.DocumentSymbol => ({
    name: s.name,
    detail: s.detail ?? '',
    kind: symbolKinds[s.kind] ?? S.Variable,
    tags: [],
    range: toRange(s.range),
    selectionRange: toRange(s.selectionRange),
    children: (s.children ?? []).map(convert),
  });
  monaco.languages.registerDocumentSymbolProvider('python', {
    async provideDocumentSymbols(model) {
      const uri = serverUri.get(model);
      if (!uri) return [];
      return ((await lsp.request('textDocument/documentSymbol', { textDocument: { uri } })) ?? []).map(convert);
    },
  });
}

lsp.on('textDocument/publishDiagnostics', (p) => {
  const model = models.get(normalise(p.uri));
  if (!model) return;
  monaco.editor.setModelMarkers(model, 'pymagic', p.diagnostics.map((d: any) => ({
    ...toRange(d.range),
    severity: markerSeverity(d.severity),
    message: d.message,
    source: d.source,
    tags: d.tags?.includes(1) ? [monaco.MarkerTag.Unnecessary] : undefined,
  })));
  updateProblems();
});

// ---------------------------------------------------------------- UI

let editor: monaco.editor.IStandaloneCodeEditor;
let runHooks: { onModelCreated(rel: string, model: monaco.editor.ITextModel): void } | null = null;
let currentRel = '';

function refreshDirty() {
  for (const li of Array.from($('files').children)) {
    const rel = (li as HTMLElement).dataset.path!;
    const m = models.get(normalise(uriFor(rel)));
    li.classList.toggle('dirty', !!m && m.getValue() !== savedText.get(rel));
    li.classList.toggle('active', rel === currentRel);
  }
}

function updateProblems() {
  const all = monaco.editor.getModelMarkers({}).filter((m) => m.severity >= monaco.MarkerSeverity.Warning);
  const errors = all.filter((m) => m.severity === monaco.MarkerSeverity.Error).length;
  $('problems').textContent = `${errors} errors, ${all.length - errors} warnings`;
}

async function openFile(rel: string, reveal?: monaco.IRange) {
  const model = await ensureModel(rel);
  if (!model) return;
  currentRel = rel;
  editor.setModel(model);
  if (reveal) {
    editor.setSelection(reveal);
    editor.revealRangeInCenter(reveal);
  }
  editor.focus();
  refreshDirty();
}

async function save() {
  const model = editor.getModel();
  const rel = model && relPathOf.get(normalise(serverUri.get(model)!));
  if (!model || !rel) return;
  const res = await fetch('/api/file?path=' + encodeURIComponent(rel), { method: 'PUT', body: model.getValue() });
  if (res.ok) { savedText.set(rel, model.getValue()); refreshDirty(); }
}

async function main() {
  const info = await (await fetch('/api/info')).json();
  rootUri = info.rootUri;

  editor = monaco.editor.create($('editor'), {
    theme: 'vs-dark',
    automaticLayout: true,
    fontSize: 14,
    minimap: { enabled: false },
    quickSuggestions: { other: true, comments: false, strings: false },
    scrollBeyondLastLine: false,
    glyphMargin: true,
  });
  (window as any).pymagic = { editor, monaco, openFile };
  runHooks = setupRun({
    editor,
    root: info.root,
    modelFor: ensureModel,
    relOf: (model) => relPathOf.get(normalise(serverUri.get(model) ?? '')),
    openFile,
    saveAll: async () => {
      for (const [key, model] of models) {
        const rel = relPathOf.get(key);
        if (rel && model.getValue() !== savedText.get(rel)) {
          const res = await fetch('/api/file?path=' + encodeURIComponent(rel), { method: 'PUT', body: model.getValue() });
          if (res.ok) savedText.set(rel, model.getValue());
        }
      }
      refreshDirty();
    },
  });
  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => void save());
  editor.onDidChangeCursorPosition((e) => { $('cursor').textContent = `Ln ${e.position.lineNumber}, Col ${e.position.column}`; });

  // go-to-definition into another file switches the editor to it
  monaco.editor.registerEditorOpener({
    openCodeEditor(_source, resource, selectionOrPosition) {
      const rel = resource.path.replace(/^\//, '');
      const sel = !selectionOrPosition ? undefined
        : 'startLineNumber' in selectionOrPosition ? selectionOrPosition
        : new monaco.Range(selectionOrPosition.lineNumber, selectionOrPosition.column, selectionOrPosition.lineNumber, selectionOrPosition.column);
      void openFile(rel, sel);
      return true;
    },
  });

  lsp.onStatus = (ok) => {
    $('conn').textContent = ok ? 'language server connected' : 'language server disconnected';
    $('conn').className = ok ? 'ok' : 'bad';
  };
  const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
  await lsp.connect(`${scheme}://${location.host}/lsp`);
  await lsp.request('initialize', { processId: null, rootUri, capabilities: {} });
  lsp.notify('initialized', {});
  registerProviders();

  const files: string[] = await (await fetch('/api/files')).json();
  const list = $('files');
  for (const rel of files) {
    const li = document.createElement('li');
    li.textContent = rel;
    li.title = rel;
    li.dataset.path = rel;
    li.onclick = () => void openFile(rel);
    list.appendChild(li);
  }
  // open everything so cross-file diagnostics and navigation see the whole project
  await Promise.all(files.map((f) => ensureModel(f)));
  if (files.length) await openFile(files[0]);
  else $('editor').textContent = 'No .py files in the workspace folder.';
}

main().catch((e) => { $('conn').textContent = String(e.message ?? e); $('conn').className = 'bad'; });

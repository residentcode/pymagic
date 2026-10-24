import * as monaco from 'monaco-editor';
import { LspClient } from './lsp';
import { setupRun } from './run';
import { setupTests } from './tests';
import { setupInterpreters } from './interpreters';
import { setupPackages } from './packages';
import { setupRefactor } from './refactor';
import { setupSearch } from './search';
import { setupHierarchy } from './hierarchy';
import { toast } from './toast';

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
  14: K.Keyword, 15: K.Snippet, 21: K.Constant, 25: K.TypeParameter,
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

  // search results can lead to files that are not Python: they are shown, but the language server never sees them
  if (!rel.endsWith('.py')) {
    const lang = monaco.languages.getLanguages().find((l) => l.extensions?.some((x) => rel.toLowerCase().endsWith(x)))?.id ?? 'plaintext';
    const plain = monaco.editor.createModel(text, lang, monaco.Uri.parse('pymagic:///' + rel));
    models.set(key, plain);
    serverUri.set(plain, uri);
    relPathOf.set(key, rel);
    savedText.set(rel, text);
    return plain;
  }
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

/** Applies LSP text edits to a model as one undo step. */
function applyTextEdits(model: monaco.editor.ITextModel, edits: { range: LspRange; newText: string }[]) {
  if (edits.length === 0) return;
  editor.pushUndoStop();
  model.pushEditOperations([], edits.map((e) => ({ range: toRange(e.range), text: e.newText })), () => null);
  editor.pushUndoStop();
}

/** The formatter the toolbar selects: built-in, or black / ruff through the project's interpreter. */
function formatOptions() {
  let engine = 'builtin';
  try { engine = localStorage.getItem('pymagic.formatter') ?? 'builtin'; } catch { /* storage unavailable */ }
  return { tabSize: 4, insertSpaces: true, pymagicEngine: engine };
}

/** Tells the toolbar which formatters exist for the current interpreter. */
async function refreshFormatters() {
  const select = $<HTMLSelectElement>('fmt-engine');
  try {
    const list: { id: string; label: string; available: boolean }[] = (await lsp.request('pymagic/formatters', {})) ?? [];
    for (const f of list) {
      const option = Array.from(select.options).find((o) => o.value === f.id);
      if (!option) continue;
      option.textContent = f.id === 'builtin' ? 'built-in' : f.label;
      option.disabled = !f.available;
    }
    // what the built-in formatter will do for this file, as its project's pyproject.toml asks
    const model = editor.getModel();
    const uri = model && serverUri.get(model);
    if (uri) {
      const cfg: { lineLength: number; quotes: string; magicTrailingComma: boolean; indentWidth: number; source: string | null } | null = await lsp.request('pymagic/formatConfig', { textDocument: { uri } });
      if (cfg) {
        const quotes = cfg.quotes.toLowerCase() === 'preserve' ? 'quotes as written' : `${cfg.quotes.toLowerCase()} quotes`;
        select.title = `Formatter. The built-in one uses line length ${cfg.lineLength}, ${quotes}`
          + (cfg.magicTrailingComma ? '' : ', no magic trailing comma') + (cfg.indentWidth !== 4 ? `, indent ${cfg.indentWidth}` : '')
          + (cfg.source ? ` (from ${cfg.source.split(/[\\/]/).pop()})` : ' (no pyproject.toml settings)');
      }
    }
    // a formatter that is gone (uninstalled, other interpreter) falls back to the built-in one
    const chosen = list.find((f) => f.id === select.value);
    if (chosen && !chosen.available) { select.value = 'builtin'; try { localStorage.setItem('pymagic.formatter', 'builtin'); } catch { /* storage unavailable */ } }
  } catch { /* the server is not ready */ }
}

async function formatCurrent(quiet = false): Promise<boolean> {
  const model = editor.getModel();
  const uri = model && serverUri.get(model);
  if (!model || !uri || !relPathOf.get(normalise(uri))?.endsWith('.py')) return false;
  lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
  try {
    // with a selection only the lines it touches are formatted
    const sel = editor.getSelection();
    const partial = !quiet && sel && !sel.isEmpty();
    const edits = partial
      ? await lsp.request('textDocument/rangeFormatting', {
          textDocument: { uri },
          range: { start: { line: sel!.startLineNumber - 1, character: sel!.startColumn - 1 }, end: { line: sel!.endLineNumber - 1, character: sel!.endColumn - 1 } },
          options: formatOptions(),
        })
      : await lsp.request('textDocument/formatting', { textDocument: { uri }, options: formatOptions() });
    applyTextEdits(model, edits ?? []);
    if (!quiet) toast(edits?.length ? (partial ? 'Formatted the selection' : 'Formatted') : 'Already formatted');
    return true;
  } catch (e: any) {
    toast(e.message ?? 'The file cannot be formatted.', true);
    return false;
  }
}

async function organizeImports() {
  const model = editor.getModel();
  const uri = model && serverUri.get(model);
  if (!model || !uri || !relPathOf.get(normalise(uri))?.endsWith('.py')) return;
  lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
  try {
    const edits = await lsp.request('pymagic/organizeImports', { textDocument: { uri } });
    applyTextEdits(model, edits ?? []);
    toast(edits?.length ? 'Imports organized' : 'Imports are already in order');
  } catch (e: any) {
    toast(e.message ?? 'The imports cannot be organized.', true);
  }
}

function registerProviders() {
  monaco.languages.registerDocumentFormattingEditProvider('python', {
    async provideDocumentFormattingEdits(model) {
      const uri = serverUri.get(model);
      if (!uri) return [];
      try {
        const edits: { range: LspRange; newText: string }[] = (await lsp.request('textDocument/formatting', { textDocument: { uri }, options: formatOptions() })) ?? [];
        return edits.map((e) => ({ range: toRange(e.range), text: e.newText }));
      } catch (e: any) {
        toast(e.message ?? 'The file cannot be formatted.', true);
        return [];
      }
    },
  });

  monaco.languages.registerDocumentRangeFormattingEditProvider('python', {
    async provideDocumentRangeFormattingEdits(model, range) {
      const uri = serverUri.get(model);
      if (!uri) return [];
      try {
        const edits: { range: LspRange; newText: string }[] = (await lsp.request('textDocument/rangeFormatting', {
          textDocument: { uri },
          range: { start: { line: range.startLineNumber - 1, character: range.startColumn - 1 }, end: { line: range.endLineNumber - 1, character: range.endColumn - 1 } },
          options: formatOptions(),
        })) ?? [];
        return edits.map((e) => ({ range: toRange(e.range), text: e.newText }));
      } catch (e: any) {
        toast(e.message ?? 'The selection cannot be formatted.', true);
        return [];
      }
    },
  });

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
          documentation: i.documentation ? { value: i.documentation } : undefined,
          insertText: i.insertText ?? i.label,
          insertTextRules: i.isSnippet ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
          // an import to add, or the expression a postfix template takes over
          additionalTextEdits: i.additionalTextEdits?.map((e: { range: LspRange; newText: string }) => ({ range: toRange(e.range), text: e.newText })),
          filterText: i.filterText,
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
        for (const [target, list] of Object.entries<any[]>(a.edit?.changes ?? {})) {
          const m = await modelForUri(target);
          if (!m) continue;
          for (const e of list) edits.push({ resource: m.uri, versionId: undefined, textEdit: { range: toRange(e.range), text: e.newText } });
        }
        actions.push({
          title: a.title, kind: a.kind, isPreferred: a.isPreferred,
          edit: edits.length ? { edits } : undefined,
          command: a.command ? { id: a.command.command, title: a.command.title, arguments: a.command.arguments } : undefined,
        });
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
  editor.updateOptions({ readOnly: !rel.endsWith('.py') });
  if (reveal) {
    editor.setSelection(reveal);
    editor.revealRangeInCenter(reveal);
  }
  editor.focus();
  refreshDirty();
}

async function save() {
  if ($<HTMLInputElement>('fmt-save').checked) await formatCurrent(true);
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
    suggestSelection: 'recentlyUsed',   // what was picked for a prefix before comes first next time
    wordBasedSuggestions: 'off',
    snippetSuggestions: 'inline',
    suggest: { showWords: false, insertMode: 'replace' },
  });
  (window as any).pymagic = { editor, monaco, openFile };
  const common = {
    editor,
    modelFor: ensureModel,
    relOf: (model: monaco.editor.ITextModel) => relPathOf.get(normalise(serverUri.get(model) ?? '')),
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
  };
  runHooks = setupRun({ ...common, root: info.root });
  setupTests(common);
  setupInterpreters();
  setupPackages();
  const refactor = setupRefactor({ editor, lsp, modelForUri, uriOf: (model: monaco.editor.ITextModel) => serverUri.get(model) });
  setupSearch({
    editor, lsp, openFile, uriFor,
    applyEdits: refactor.applyEdits,
    isEditable: (rel) => rel.endsWith('.py'),
    unsavedTexts: () => {
      const out: Record<string, string> = {};
      for (const [key, model] of models) {
        const rel = relPathOf.get(key);
        if (rel && model.getValue() !== savedText.get(rel)) out[rel] = model.getValue();
      }
      return out;
    },
  });
  setupHierarchy({
    editor, lsp, openFile, modelForUri,
    uriOf: (model) => serverUri.get(model),
    relOfUri: (uri) => relPathOf.get(normalise(uri)),
  });
  // code actions that need the page (not just a text edit) arrive as commands
  monaco.editor.registerCommand('pymagic.installPackage', (_accessor, name: string) => {
    window.dispatchEvent(new CustomEvent('pymagic:install', { detail: name }));
  });
  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => void save());
  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Alt | monaco.KeyCode.KeyL, () => void formatCurrent());
  editor.addCommand(monaco.KeyMod.Shift | monaco.KeyMod.Alt | monaco.KeyCode.KeyO, () => void organizeImports());
  $('b-format').onclick = () => void formatCurrent();
  const engineSelect = $<HTMLSelectElement>('fmt-engine');
  try { engineSelect.value = localStorage.getItem('pymagic.formatter') ?? 'builtin'; } catch { /* storage unavailable */ }
  engineSelect.onchange = () => { try { localStorage.setItem('pymagic.formatter', engineSelect.value); } catch { /* storage unavailable */ } };
  engineSelect.onfocus = () => void refreshFormatters();     // an install in the Packages tab shows up here
  engineSelect.onmousedown = () => void refreshFormatters();
  $('b-imports').onclick = () => void organizeImports();
  try {
    $<HTMLInputElement>('fmt-save').checked = localStorage.getItem('pymagic.formatOnSave') === '1';
    $<HTMLInputElement>('fmt-save').onchange = () => localStorage.setItem('pymagic.formatOnSave', $<HTMLInputElement>('fmt-save').checked ? '1' : '0');
  } catch { /* storage unavailable */ }
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
  void refreshFormatters();

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

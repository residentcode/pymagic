// Navigation by structure: the Hierarchy tab (call hierarchy Ctrl+Alt+H, type hierarchy Ctrl+H), go to implementations
// (Ctrl+F12 and the context menu), and the gutter marks for methods that override or are overridden and classes with subclasses.
import * as monaco from 'monaco-editor';
import type { LspClient } from './lsp';
import { showTab } from './panel';
import { toast } from './toast';

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

interface LspPosition { line: number; character: number }
interface LspRange { start: LspPosition; end: LspPosition }
interface LspLocation { uri: string; range: LspRange }
interface Item { name: string; kind: number; detail?: string; uri: string; range: LspRange; selectionRange: LspRange }
interface Edge { item: Item; ranges: LspRange[] }
interface Marker { line: number; kind: 'overrides' | 'implements' | 'overridden' | 'implemented' | 'subclassed'; label: string; targets: LspLocation[] }

type Mode = 'callers' | 'callees' | 'subtypes' | 'supertypes';

export interface HierarchyDeps {
  editor: monaco.editor.IStandaloneCodeEditor;
  lsp: LspClient;
  uriOf(model: monaco.editor.ITextModel): string | undefined;
  relOfUri(uri: string): string | undefined;
  modelForUri(uri: string): Promise<monaco.editor.ITextModel | null>;
  openFile(rel: string, reveal?: monaco.IRange): Promise<void>;
}

const toRange = (r: LspRange): monaco.IRange => ({
  startLineNumber: r.start.line + 1, startColumn: r.start.character + 1, endLineNumber: r.end.line + 1, endColumn: r.end.character + 1,
});

const KIND_MARK: Record<number, string> = { 2: 'M', 5: 'C', 6: 'm', 12: 'ƒ' };
const MODE_LABELS: Record<Mode, string> = { callers: 'Callers', callees: 'Callees', subtypes: 'Subtypes', supertypes: 'Supertypes' };

export function setupHierarchy(deps: HierarchyDeps) {
  const { editor, lsp } = deps;
  const tree = $('hier-tree');
  const title = $('hier-title');
  const modeBar = $('hier-modes');

  let root: Item | null = null;
  let family: 'call' | 'type' = 'call';
  let mode: Mode = 'callers';
  let selectedRow: HTMLElement | null = null;

  // ---------------------------------------------------------------- navigation

  async function goTo(uri: string, range: LspRange) {
    const rel = deps.relOfUri(uri);
    if (!rel) { toast('That place is outside the project.', true); return; }
    await deps.openFile(rel, toRange(range));
  }

  function flush(model: monaco.editor.ITextModel, uri: string) {
    lsp.notify('textDocument/didChange', { textDocument: { uri, version: model.getVersionId() }, contentChanges: [{ text: model.getValue() }] });
  }

  // ---------------------------------------------------------------- the tree

  async function children(item: Item): Promise<Edge[]> {
    switch (mode) {
      case 'callers': return ((await lsp.request('callHierarchy/incomingCalls', { item })) ?? []).map((c: any) => ({ item: c.from, ranges: c.fromRanges }));
      case 'callees': return ((await lsp.request('callHierarchy/outgoingCalls', { item })) ?? []).map((c: any) => ({ item: c.to, ranges: c.fromRanges }));
      case 'subtypes': return ((await lsp.request('typeHierarchy/subtypes', { item })) ?? []).map((i: Item) => ({ item: i, ranges: [] }));
      case 'supertypes': return ((await lsp.request('typeHierarchy/supertypes', { item })) ?? []).map((i: Item) => ({ item: i, ranges: [] }));
    }
  }

  function emptyText(): string {
    return { callers: 'Nothing calls this.', callees: 'This calls nothing that can be followed.', subtypes: 'No subclasses.', supertypes: 'No base classes in the project.' }[mode];
  }

  function row(edge: Edge, depth: number, parent: Item | null): HTMLElement {
    const holder = document.createElement('div');
    const line = document.createElement('div');
    line.className = 'hn';
    line.style.paddingLeft = `${8 + depth * 16}px`;
    const caret = document.createElement('span'); caret.className = 'caret'; caret.textContent = '▸';
    const kind = document.createElement('span'); kind.className = 'kind'; kind.textContent = KIND_MARK[edge.item.kind] ?? '·';
    const name = document.createElement('span'); name.textContent = edge.item.name;
    line.append(caret, kind, name);
    if (edge.item.detail) { const d = document.createElement('span'); d.className = 'detail'; d.textContent = edge.item.detail; line.append(d); }
    if (edge.ranges.length > 1) { const c = document.createElement('span'); c.className = 'count'; c.textContent = `×${edge.ranges.length}`; line.append(c); }
    const rel = deps.relOfUri(edge.item.uri);
    if (rel) { const f = document.createElement('span'); f.className = 'detail'; f.textContent = rel; line.append(f); }
    const kids = document.createElement('div');
    kids.style.display = 'none';
    holder.append(line, kids);

    let loaded = false, open = false;
    async function toggle() {
      open = !open;
      caret.textContent = open ? '▾' : '▸';
      kids.style.display = open ? '' : 'none';
      if (open && !loaded) {
        loaded = true;
        kids.replaceChildren(textRow('Loading…', depth + 1));
        try {
          const list = await children(edge.item);
          kids.replaceChildren(...(list.length ? list.map((e) => row(e, depth + 1, edge.item)) : [textRow(emptyText(), depth + 1)]));
        } catch (e: any) { kids.replaceChildren(textRow(String(e.message ?? e), depth + 1)); }
      }
    }
    caret.onclick = (ev) => { ev.stopPropagation(); void toggle(); };
    line.ondblclick = () => void toggle();
    line.onclick = () => {
      selectedRow?.classList.remove('sel');
      line.classList.add('sel');
      selectedRow = line;
      // a caller is shown where it calls; everything else where it is defined
      if (mode === 'callers' && edge.ranges.length > 0) void goTo(edge.item.uri, edge.ranges[0]);
      else void goTo(edge.item.uri, edge.item.selectionRange);
    };
    (holder as any).toggle = toggle;
    return holder;
  }

  function textRow(text: string, depth: number): HTMLElement {
    const d = document.createElement('div');
    d.className = 'hn-empty';
    d.style.paddingLeft = `${8 + depth * 16 + 24}px`;
    d.textContent = text;
    return d;
  }

  function renderModes() {
    const modes: Mode[] = family === 'call' ? ['callers', 'callees'] : ['subtypes', 'supertypes'];
    modeBar.replaceChildren(...modes.map((m) => {
      const b = document.createElement('button');
      b.textContent = MODE_LABELS[m];
      b.className = m === mode ? 'active' : '';
      b.onclick = () => { mode = m; show(); };
      return b;
    }));
  }

  function show() {
    if (!root) return;
    title.textContent = `${family === 'call' ? 'Call' : 'Type'} hierarchy of ${root.name}`;
    renderModes();
    tree.replaceChildren();
    selectedRow = null;
    const top = row({ item: root, ranges: [] }, 0, null);
    tree.append(top);
    void (top as any).toggle();
    showTab('hier');
  }

  async function open(kind: 'call' | 'type') {
    const model = editor.getModel(), pos = editor.getPosition();
    const uri = model && deps.uriOf(model);
    if (!model || !pos || !uri) return;
    flush(model, uri);
    const items: Item[] = (await lsp.request(kind === 'call' ? 'textDocument/prepareCallHierarchy' : 'textDocument/prepareTypeHierarchy', {
      textDocument: { uri }, position: { line: pos.lineNumber - 1, character: pos.column - 1 },
    })) ?? [];
    if (items.length === 0) { toast(kind === 'call' ? 'Put the cursor on a function, method or class.' : 'Put the cursor on a class.', true); return; }
    family = kind;
    mode = kind === 'call' ? 'callers' : 'subtypes';
    root = items[0];
    show();
  }

  $('t-hier').onclick = () => showTab('hier');
  editor.addAction({
    id: 'pymagic.callHierarchy', label: 'Call Hierarchy', contextMenuGroupId: 'navigation', contextMenuOrder: 1.6,
    keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Alt | monaco.KeyCode.KeyH],
    run: () => void open('call'),
  });
  editor.addAction({
    id: 'pymagic.typeHierarchy', label: 'Type Hierarchy', contextMenuGroupId: 'navigation', contextMenuOrder: 1.7,
    keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyH],
    run: () => void open('type'),
  });

  // ---------------------------------------------------------------- go to implementations

  monaco.languages.registerImplementationProvider('python', {
    async provideImplementation(model, position) {
      const uri = deps.uriOf(model);
      if (!uri) return null;
      flush(model, uri);
      const locs: LspLocation[] = (await lsp.request('textDocument/implementation', {
        textDocument: { uri }, position: { line: position.lineNumber - 1, character: position.column - 1 },
      })) ?? [];
      const result: monaco.languages.Location[] = [];
      for (const l of locs) {
        const target = await deps.modelForUri(l.uri);
        if (target) result.push({ uri: target.uri, range: toRange(l.range) });
      }
      return result;
    },
  });

  // ---------------------------------------------------------------- gutter marks

  const Lane = monaco.editor.GlyphMarginLane.Right;   // the left lane belongs to breakpoints
  const decorations = editor.createDecorationsCollection();
  let byLine = new Map<number, Marker[]>();
  let timer: number | undefined;
  let menu: HTMLElement | null = null;

  const isUp = (m: Marker) => m.kind === 'overrides' || m.kind === 'implements';

  async function refreshMarks() {
    const model = editor.getModel();
    const uri = model && deps.uriOf(model);
    if (!model || !uri || !model.uri.path.endsWith('.py')) { decorations.clear(); byLine = new Map(); return; }
    let list: Marker[];
    try { list = (await lsp.request('pymagic/inheritance', { textDocument: { uri } })) ?? []; } catch { return; }
    if (editor.getModel() !== model) return;
    byLine = new Map();
    for (const m of list) byLine.set(m.line, [...(byLine.get(m.line) ?? []), m]);
    decorations.set([...byLine].map(([line, marks]) => ({
      range: new monaco.Range(line + 1, 1, line + 1, 1),
      options: {
        glyphMargin: { position: Lane },
        glyphMarginClassName: 'inh-glyph ' + (marks.some(isUp) && marks.some((m) => !isUp(m)) ? 'inh-both' : marks.some(isUp) ? 'inh-up' : 'inh-down'),
        glyphMarginHoverMessage: { value: marks.map((m) => m.label).join('  \n') },
      },
    })));
  }

  function schedule(delay: number) {
    clearTimeout(timer);
    timer = window.setTimeout(() => void refreshMarks(), delay);
  }
  editor.onDidChangeModel(() => schedule(400));
  editor.onDidChangeModelContent(() => schedule(1200));   // after the edit has reached the server, and the typing has paused
  window.addEventListener('pymagic:connected', () => schedule(200));

  function closeMenu() { menu?.remove(); menu = null; }

  function pick(entries: { label: string; loc: LspLocation }[], x: number, y: number) {
    closeMenu();
    if (entries.length === 1) { void goTo(entries[0].loc.uri, entries[0].loc.range); return; }
    menu = document.createElement('div');
    menu.className = 'inh-menu';
    for (const e of entries) {
      const item = document.createElement('div');
      const rel = deps.relOfUri(e.loc.uri) ?? e.loc.uri;
      item.textContent = e.label;
      const where = document.createElement('span'); where.className = 'where'; where.textContent = `${rel}:${e.loc.range.start.line + 1}`;
      item.append(where);
      item.onclick = () => { closeMenu(); void goTo(e.loc.uri, e.loc.range); };
      menu.append(item);
    }
    menu.style.left = `${Math.min(x, window.innerWidth - 320)}px`;
    menu.style.top = `${Math.min(y, window.innerHeight - 200)}px`;
    document.body.append(menu);
  }
  // any press outside the menu closes it; this runs before the editor sees the same press, so a press that opens the menu cannot close it
  document.addEventListener('mousedown', (ev) => { if (menu && !menu.contains(ev.target as Node)) closeMenu(); }, true);
  document.addEventListener('keydown', (ev) => { if (ev.key === 'Escape') closeMenu(); }, true);

  editor.onMouseDown((e) => {
    if (e.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN || !e.target.position) return;
    if ((e.target.detail as any)?.glyphMarginLane !== Lane) return;
    const marks = byLine.get(e.target.position.lineNumber - 1);
    if (!marks) return;
    pick(marks.flatMap((m) => m.targets.map((loc) => ({ label: m.targets.length > 1 ? `${m.label} · ${m.kind}` : m.label, loc }))), e.event.browserEvent.clientX, e.event.browserEvent.clientY);
  });

  return { refreshMarks: () => schedule(0) };
}

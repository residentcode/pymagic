// Find in files (the Search tab of the bottom panel, Ctrl+Shift+F), replace in files, and go to symbol in the workspace.
import * as monaco from 'monaco-editor';
import type { LspClient } from './lsp';
import { showTab } from './panel';

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

interface SearchMatch { line: number; column: number; length: number; text: string; replacement: string | null }
interface SearchFile { path: string; matches: SearchMatch[] }
interface SearchResult { files: SearchFile[]; total: number; truncated: boolean; filesSearched: number }
interface WorkspaceSymbol { name: string; kind: number; location: { uri: string; range: { start: { line: number; character: number }; end: { line: number; character: number } } }; containerName: string | null }

export interface SearchDeps {
  editor: monaco.editor.IStandaloneCodeEditor;
  lsp: LspClient;
  /** Texts of the editors with unsaved changes, by relative path. */
  unsavedTexts(): Record<string, string>;
  openFile(rel: string, reveal?: monaco.IRange): Promise<void>;
  /** Replacements are applied by the refactoring machinery, so they get its preview and one undo step. */
  applyEdits(title: string, changes: Record<string, { range: any; newText: string }[]>, opts: { alwaysPreview: boolean }): Promise<boolean>;
  uriFor(rel: string): string;
  isEditable(rel: string): boolean;
}

const KINDS: Record<number, string> = { 2: 'module', 5: 'class', 6: 'method', 7: 'property', 8: 'field', 9: 'constructor', 12: 'function', 13: 'variable', 14: 'constant' };

export function setupSearch(deps: SearchDeps) {
  const { editor, lsp } = deps;
  let running = 0;
  let last: SearchResult | null = null;

  const query = $<HTMLInputElement>('fs-query');
  const replace = $<HTMLInputElement>('fs-replace');
  const include = $<HTMLInputElement>('fs-include');
  const exclude = $<HTMLInputElement>('fs-exclude');
  const caseBox = $<HTMLInputElement>('fs-case');
  const wordBox = $<HTMLInputElement>('fs-word');
  const regexBox = $<HTMLInputElement>('fs-regex');
  const summary = $('fs-summary');
  const results = $('fs-results');
  const replaceAll = $<HTMLButtonElement>('fs-replace-all');

  const options = () => ({
    query: query.value, regex: regexBox.checked, caseSensitive: caseBox.checked, wholeWord: wordBox.checked,
    include: include.value, exclude: exclude.value, unsaved: deps.unsavedTexts(),
  });

  async function search() {
    if (!query.value) { summary.textContent = ''; results.replaceChildren(); last = null; replaceAll.disabled = true; return; }
    const ticket = ++running;
    summary.textContent = 'Searching…';
    let res: Response;
    try {
      res = await fetch('/api/search', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ ...options(), replace: replace.value || undefined }) });
    } catch (e: any) { summary.textContent = String(e.message ?? e); return; }
    if (ticket !== running) return;   // a newer search is under way
    if (!res.ok) {
      const body = await res.json().catch(() => ({}));
      summary.textContent = body.error ?? 'The search failed.';
      summary.className = 'bad'; results.replaceChildren(); last = null; replaceAll.disabled = true;
      return;
    }
    summary.className = '';
    last = await res.json();
    render();
  }

  function render() {
    results.replaceChildren();
    if (!last) return;
    const r = last;
    summary.textContent = r.total === 0
      ? `No results in ${r.filesSearched} files`
      : `${r.total}${r.truncated ? '+' : ''} result${r.total === 1 ? '' : 's'} in ${r.files.length} file${r.files.length === 1 ? '' : 's'}${r.truncated ? ' (showing the first ones; narrow the search)' : ''}`;
    replaceAll.disabled = r.total === 0 || !replace.value || r.files.every((f) => !deps.isEditable(f.path));

    for (const file of r.files) {
      const group = document.createElement('div');
      group.className = 'fs-file';
      const head = document.createElement('div');
      head.className = 'fs-head';
      const arrow = document.createElement('span');
      arrow.textContent = '▾ ';
      const name = document.createElement('span');
      name.textContent = file.path;
      const count = document.createElement('span');
      count.className = 'fs-count';
      count.textContent = String(file.matches.length);
      head.append(arrow, name, count);
      const body = document.createElement('div');
      head.onclick = () => { const hidden = body.style.display === 'none'; body.style.display = hidden ? '' : 'none'; arrow.textContent = hidden ? '▾ ' : '▸ '; };
      for (const m of file.matches) {
        const row = document.createElement('div');
        row.className = 'fs-match';
        const num = document.createElement('span');
        num.className = 'fs-line';
        num.textContent = String(m.line + 1);
        const text = document.createElement('span');
        const lead = m.text.slice(0, m.column).trimStart();
        const hit = document.createElement('mark');
        hit.textContent = m.text.slice(m.column, m.column + m.length);
        text.append(lead, hit);
        if (m.replacement !== null) {
          const to = document.createElement('ins');
          to.textContent = m.replacement;
          text.appendChild(to);
        }
        text.append(m.text.slice(m.column + m.length));
        row.append(num, text);
        row.onclick = () => void deps.openFile(file.path, new monaco.Range(m.line + 1, m.column + 1, m.line + 1, m.column + 1 + m.length));
        body.appendChild(row);
      }
      group.append(head, body);
      results.appendChild(group);
    }
  }

  async function replaceEverywhere() {
    if (!last || !replace.value) return;
    // search again so that the matches carry exactly the replacement that is typed now
    await search();
    if (!last) return;
    const changes: Record<string, { range: any; newText: string }[]> = {};
    let skipped = 0;
    for (const file of last.files) {
      if (!deps.isEditable(file.path)) { skipped += file.matches.length; continue; }
      changes[deps.uriFor(file.path)] = file.matches.map((m) => ({
        range: { start: { line: m.line, character: m.column }, end: { line: m.line, character: m.column + m.length } },
        newText: m.replacement ?? '',
      }));
    }
    if (Object.keys(changes).length === 0) return;
    const done = await deps.applyEdits(`Replace "${query.value}" with "${replace.value}"`, changes, { alwaysPreview: true });
    if (done) {
      summary.textContent = `Replaced${skipped ? ` (${skipped} matches in files that cannot be edited here were left alone)` : ''}. The files are changed in the editor; save them with Ctrl+S.`;
      results.replaceChildren(); last = null; replaceAll.disabled = true;
    }
  }

  let timer: number | undefined;
  const later = () => { clearTimeout(timer); timer = window.setTimeout(() => void search(), 300); };
  query.oninput = later;
  replace.oninput = () => { if (last) later(); };
  include.onchange = exclude.onchange = () => void search();
  for (const box of [caseBox, wordBox, regexBox]) box.onchange = () => void search();
  for (const input of [query, replace, include, exclude]) {
    input.onkeydown = (e) => {
      if (e.key === 'Enter') { e.preventDefault(); clearTimeout(timer); void search(); }
      else if (e.key === 'Escape') editor.focus();
    };
  }
  $('fs-go').onclick = () => void search();
  replaceAll.onclick = () => void replaceEverywhere();
  $('t-search').onclick = () => { showTab('search'); query.focus(); };

  function open(prefill?: string) {
    showTab('search');
    const sel = editor.getModel() && editor.getSelection() && !editor.getSelection()!.isEmpty()
      ? editor.getModel()!.getValueInRange(editor.getSelection()!) : '';
    const text = prefill ?? (sel && !sel.includes('\n') ? sel : '');
    if (text) { query.value = text; void search(); }
    query.focus();
    query.select();
  }
  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyF, () => open());
  document.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && e.shiftKey && !e.altKey && e.key.toLowerCase() === 'f') { e.preventDefault(); open(); }
    else if ((e.ctrlKey || e.metaKey) && e.altKey && !e.shiftKey && e.key.toLowerCase() === 'o') { e.preventDefault(); void symbolPicker(); }
  });
  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Alt | monaco.KeyCode.KeyO, () => void symbolPicker());

  // ---------------------------------------------------------------- go to symbol in the workspace

  function symbolPicker(): Promise<void> {
    return new Promise((resolve) => {
      const overlay = document.createElement('div');
      overlay.id = 'sigdialog';
      const box = document.createElement('div');
      box.className = 'box';
      const title = document.createElement('h2');
      title.textContent = 'Go to symbol';
      const input = document.createElement('input');
      input.placeholder = 'function, class, method or variable name';
      input.spellcheck = false;
      const list = document.createElement('div');
      list.className = 'picklist';
      box.append(title, input, list);
      overlay.appendChild(box);

      let shown: WorkspaceSymbol[] = [];
      let index = 0;
      let ticket = 0;
      const finish = (target?: WorkspaceSymbol) => {
        overlay.remove();
        if (target) {
          const rel = decodeURIComponent(target.location.uri).split('/').slice(-1)[0];
          const r = target.location.range;
          void deps.openFile(relOf(target.location.uri) ?? rel, new monaco.Range(r.start.line + 1, r.start.character + 1, r.end.line + 1, r.end.character + 1));
        } else editor.focus();
        resolve();
      };
      function draw() {
        list.replaceChildren();
        if (shown.length === 0) {
          const none = document.createElement('div');
          none.className = 'dim';
          none.textContent = 'No symbol matches.';
          list.appendChild(none);
        }
        shown.forEach((s, i) => {
          const row = document.createElement('div');
          row.className = i === index ? 'pick sel' : 'pick';
          const name = document.createElement('span');
          name.textContent = s.name;
          const detail = document.createElement('span');
          detail.className = 'pick-detail';
          detail.textContent = `${KINDS[s.kind] ?? ''}  ${s.containerName ?? ''}`;
          row.append(name, detail);
          row.onclick = () => finish(s);
          list.appendChild(row);
        });
        list.querySelector('.sel')?.scrollIntoView({ block: 'nearest' });
      }
      async function refresh() {
        const mine = ++ticket;
        const found: WorkspaceSymbol[] = (await lsp.request('workspace/symbol', { query: input.value })) ?? [];
        if (mine !== ticket) return;
        shown = found.slice(0, 100); index = 0; draw();
      }
      input.oninput = () => void refresh();
      overlay.onkeydown = (e) => {
        if (e.key === 'Escape') { e.preventDefault(); finish(); }
        else if (e.key === 'ArrowDown') { e.preventDefault(); index = Math.min(index + 1, shown.length - 1); draw(); }
        else if (e.key === 'ArrowUp') { e.preventDefault(); index = Math.max(index - 1, 0); draw(); }
        else if (e.key === 'Enter') { e.preventDefault(); if (shown[index]) finish(shown[index]); }
      };
      overlay.onmousedown = (e) => { if (e.target === overlay) finish(); };
      document.body.appendChild(overlay);
      input.focus();
      void refresh();
    });
  }

  function relOf(uri: string): string | undefined {
    const root = deps.uriFor('');
    return uri.toLowerCase().startsWith(root.toLowerCase()) ? decodeURIComponent(uri.slice(root.length)) : undefined;
  }

  return { open, symbolPicker };
}

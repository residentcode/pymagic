// Test explorer: discovered tests as a tree, run all / selected / failed / at the cursor, results with tracebacks.
import * as monaco from 'monaco-editor';
import { RunClient } from './runclient';
import { showTab } from './panel';

export interface TestDeps {
  editor: monaco.editor.IStandaloneCodeEditor;
  modelFor(rel: string): Promise<monaco.editor.ITextModel | null>;
  relOf(model: monaco.editor.ITextModel): string | undefined;
  openFile(rel: string, reveal?: monaco.IRange): Promise<void>;
  saveAll(): Promise<void>;
}

type Status = 'idle' | 'queued' | 'running' | 'passed' | 'failed' | 'error' | 'skipped' | 'xfailed' | 'xpassed';

interface Result { outcome: Status; duration: number; message: string; traceback: string; output: string; file: string | null; line: number | null }

interface TNode {
  id: string; name: string; kind: string; file: string; line: number; endLine: number;
  children: TNode[]; parent?: TNode; status: Status; result?: Result; open: boolean;
  row?: HTMLElement; icon?: HTMLElement; list?: HTMLElement;
}

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

const ICONS: Record<Status, [string, string]> = {
  idle: ['○', 'var(--dim)'], queued: ['◔', 'var(--dim)'], running: ['◐', 'var(--amber)'],
  passed: ['✔', 'var(--green)'], failed: ['✖', 'var(--red)'], error: ['⚠', 'var(--red)'],
  skipped: ['⊘', 'var(--dim)'], xfailed: ['◇', 'var(--dim)'], xpassed: ['◆', 'var(--amber)'],
};

export function setupTests(deps: TestDeps) {
  const { editor } = deps;
  const byId = new Map<string, TNode>();
  let roots: TNode[] = [];
  let selected: TNode | null = null;
  let client: RunClient | null = null;
  let started = 0;
  let markerFiles = new Set<string>();

  const tree = $('tests-tree');
  const detail = $('test-detail');
  const consoleOut = $('tests-console');

  // ------------------------------------------------------------ tree model

  function build(raw: any, parent?: TNode): TNode {
    const old = byId.get(raw.id);
    const node: TNode = {
      id: raw.id, name: raw.name, kind: raw.kind, file: raw.file, line: raw.line, endLine: raw.endLine,
      children: [], parent, status: old?.status ?? 'idle', result: old?.result, open: old?.open ?? raw.kind === 'file',
    };
    byId.set(node.id, node);
    node.children = (raw.children ?? []).map((c: any) => build(c, node));
    return node;
  }

  async function load() {
    const raw = await (await fetch('/api/tests')).json();
    const known = new Set<string>();
    const mark = (r: any) => { known.add(r.id); (r.children ?? []).forEach(mark); };
    raw.forEach(mark);
    for (const id of [...byId.keys()]) if (!known.has(id) && !id.includes('[')) byId.delete(id);   // keep parametrized cases of kept parents
    roots = raw.map((r: any) => build(r));
    render();
    summarize();
  }

  function leaves(n: TNode): TNode[] {
    return n.children.length === 0 ? [n] : n.children.flatMap(leaves);
  }

  function aggregate(n: TNode): Status {
    if (n.children.length === 0) return n.status;
    const kids = n.children.map(aggregate);
    for (const s of ['running', 'failed', 'error'] as Status[]) if (kids.includes(s)) return s;
    if (kids.includes('queued')) return 'queued';
    if (kids.every((s) => s === 'idle')) return 'idle';
    if (kids.some((s) => s === 'idle')) return 'idle';
    return kids.includes('passed') ? 'passed' : kids[0];
  }

  function paint(n: TNode) {
    for (let x: TNode | undefined = n; x; x = x.parent) {
      if (!x.icon) continue;
      const [glyph, color] = ICONS[aggregate(x)];
      x.icon.textContent = glyph;
      x.icon.style.color = color;
    }
  }

  // ------------------------------------------------------------ rendering

  function render() {
    tree.textContent = '';
    if (roots.length === 0) {
      tree.innerHTML = '<div class="empty">No tests found. Test files are named test*.py or *_test.py.</div>';
      return;
    }
    const ul = document.createElement('ul');
    for (const r of roots) ul.appendChild(renderNode(r));
    tree.appendChild(ul);
  }

  function renderNode(n: TNode): HTMLLIElement {
    const li = document.createElement('li');
    const row = document.createElement('div');
    row.className = 'trow' + (selected === n ? ' sel' : '');
    const caret = document.createElement('span');
    caret.className = 'caret';
    caret.textContent = n.children.length ? (n.open ? '▾' : '▸') : '';
    const icon = document.createElement('span');
    icon.className = 'ticon';
    const label = document.createElement('span');
    label.className = 'tname';
    label.textContent = n.name;
    const play = document.createElement('span');
    play.className = 'tplay';
    play.textContent = '▶';
    play.title = 'Run';
    row.append(caret, icon, label, play);
    li.appendChild(row);
    n.row = row; n.icon = icon;

    const [glyph, color] = ICONS[aggregate(n)];
    icon.textContent = glyph; icon.style.color = color;

    caret.onclick = (e) => { e.stopPropagation(); n.open = !n.open; render(); };
    row.onclick = () => select(n);
    row.ondblclick = () => void goTo(n);
    play.onclick = (e) => { e.stopPropagation(); void run([n.id]); };

    if (n.children.length && n.open) {
      const ul = document.createElement('ul');
      for (const c of n.children) ul.appendChild(renderNode(c));
      li.appendChild(ul);
      n.list = ul;
    }
    return li;
  }

  function select(n: TNode) {
    selected?.row?.classList.remove('sel');
    selected = n;
    n.row?.classList.add('sel');
    showDetail(n);
  }

  async function goTo(n: TNode, line = n.line) {
    await deps.openFile(n.file, new monaco.Range(line, 1, line, 1));
  }

  function showDetail(n: TNode) {
    detail.textContent = '';
    const title = $('detail-title');
    title.textContent = n.id;
    const add = (cls: string, text: string, tag = 'div') => {
      const el = document.createElement(tag);
      el.className = cls;
      el.textContent = text;
      detail.appendChild(el);
      return el;
    };

    const r = n.result;
    if (!r) {
      const kids = leaves(n);
      if (n.children.length) {
        const count = (s: Status) => kids.filter((k) => k.status === s).length;
        add('dim', `${kids.length} tests: ${count('passed')} passed, ${count('failed') + count('error')} failed, ${count('skipped')} skipped, ${count('idle') + count('queued')} not run`);
      } else add('dim', n.status === 'queued' ? 'Waiting to run…' : n.status === 'running' ? 'Running…' : 'Not run yet.');
      return;
    }
    add(`outcome ${r.outcome}`, `${r.outcome.toUpperCase()}   ${(r.duration * 1000).toFixed(1)} ms`);
    if (r.message) add('msg', r.message);
    if (r.file && r.line) {
      const link = add('link', `Go to ${r.file}:${r.line}`);
      link.onclick = () => void deps.openFile(r.file!, new monaco.Range(r.line!, 1, r.line!, 1));
    }
    if (r.traceback) add('tb', r.traceback, 'pre');
    if (r.output) { add('dim', 'Captured output'); add('tb', r.output, 'pre'); }
  }

  // ------------------------------------------------------------ results

  function ensureNode(id: string, file?: string, line?: number): TNode {
    let node = byId.get(id);
    if (node) return node;
    // a case the static scan could not know about (parametrized tests): hang it under its function
    const base = id.includes('[') ? id.slice(0, id.indexOf('[')) : id;
    let parent = byId.get(base);
    if (!parent || base === id) {
      const parts = id.split('::');
      parent = byId.get(parts[0]);
      if (!parent) {
        parent = { id: parts[0], name: parts[0], kind: 'file', file: parts[0], line: 1, endLine: 1, children: [], status: 'idle', open: true };
        byId.set(parent.id, parent);
        roots.push(parent);
      }
      for (let i = 1; i < parts.length - 1; i++) {
        const cid = parts.slice(0, i + 1).join('::');
        let c = byId.get(cid);
        if (!c) {
          c = { id: cid, name: parts[i], kind: 'class', file: parts[0], line: 1, endLine: 1, children: [], parent, status: 'idle', open: true };
          byId.set(cid, c);
          parent.children.push(c);
        }
        parent = c;
      }
    }
    node = {
      id, name: id.includes('[') ? id.slice(id.indexOf('[')) : id.split('::').pop()!, kind: 'case',
      file: file ?? parent.file, line: line || parent.line, endLine: parent.endLine, children: [], parent, status: 'idle', open: false,
    };
    byId.set(id, node);
    if (parent.children.length === 0 && parent.kind !== 'file') { /* the function becomes a group of cases */ }
    parent.children.push(node);
    parent.open = true;
    render();
    return node;
  }

  function setStatus(n: TNode, s: Status) {
    n.status = s;
    paint(n);
  }

  async function updateMarkers() {
    const perFile = new Map<string, monaco.editor.IMarkerData[]>();
    for (const n of byId.values()) {
      const r = n.result;
      if (!r || (r.outcome !== 'failed' && r.outcome !== 'error') || !r.file || !r.line) continue;
      const list = perFile.get(r.file) ?? [];
      list.push({
        severity: monaco.MarkerSeverity.Error, startLineNumber: r.line, startColumn: 1, endLineNumber: r.line, endColumn: 1000,
        message: `${n.name}: ${r.message}`, source: 'tests',
      });
      perFile.set(r.file, list);
    }
    for (const file of new Set([...markerFiles, ...perFile.keys()])) {
      const model = await deps.modelFor(file);
      if (model) monaco.editor.setModelMarkers(model, 'pymagic-tests', perFile.get(file) ?? []);
    }
    markerFiles = new Set(perFile.keys());
  }

  function summarize() {
    const all = [...byId.values()].filter((n) => n.children.length === 0);
    const count = (...s: Status[]) => all.filter((n) => s.includes(n.status)).length;
    const parts: string[] = [];
    const passed = count('passed', 'xfailed'), failed = count('failed', 'error', 'xpassed'), skipped = count('skipped');
    if (passed) parts.push(`${passed} passed`);
    if (failed) parts.push(`${failed} failed`);
    if (skipped) parts.push(`${skipped} skipped`);
    const running = count('running', 'queued');
    if (running) parts.push(`${running} running`);
    const total = all.reduce((t, n) => t + (n.result?.duration ?? 0), 0);
    $('tests-summary').textContent = parts.length ? parts.join(' · ') + (total ? ` · ${total.toFixed(2)}s` : '') : `${all.length} tests`;
    $('tests-summary').className = failed ? 'bad' : passed ? 'good' : '';
    ($('tests-rerun') as HTMLButtonElement).disabled = client !== null || failed === 0;
  }

  function appendConsole(text: string, cls = '') {
    const span = document.createElement('span');
    if (cls) span.className = cls;
    span.textContent = text.split('\r\n').join('\n');
    consoleOut.appendChild(span);
    consoleOut.scrollTop = consoleOut.scrollHeight;
  }

  function handle(m: any) {
    switch (m.type) {
      case 'output':
        appendConsole(m.text, m.stream === 'stderr' ? 'err' : '');
        break;
      case 'error':
        appendConsole(m.message + '\n', 'err');
        finish();
        break;
      case 'collected':
        for (const t of m.tests) setStatus(ensureNode(t.id, t.file, t.line), 'queued');
        summarize();
        break;
      case 'testStarted': {
        const n = ensureNode(m.id);
        n.result = undefined;
        setStatus(n, 'running');
        summarize();
        break;
      }
      case 'testResult': {
        const n = ensureNode(m.id);
        n.result = { outcome: m.outcome, duration: m.duration, message: m.message, traceback: m.traceback, output: m.output, file: m.file, line: m.line };
        setStatus(n, m.outcome);
        if (selected === n) showDetail(n);
        void updateMarkers();
        summarize();
        break;
      }
      case 'collectError': {
        const n = ensureNode(String(m.file).split('::')[0]);
        n.result = { outcome: 'error', duration: 0, message: 'Collection failed', traceback: m.message, output: '', file: null, line: null };
        setStatus(n, 'error');
        appendConsole(`Collection error in ${m.file}\n`, 'err');
        showDetail(n);
        break;
      }
      case 'exited':
        appendConsole(`\nTest process finished with exit code ${m.code}\n`, 'info');
        finish();
        break;
    }
  }

  function finish() {
    client?.close();
    client = null;
    for (const n of byId.values()) if (n.status === 'queued' || n.status === 'running') setStatus(n, 'idle');
    setButtons();
    summarize();
    if (selected) showDetail(selected);
  }

  function setButtons() {
    for (const id of ['tests-all', 'tests-sel', 'tests-here']) ($(id) as HTMLButtonElement).disabled = client !== null;
    ($('tests-rerun') as HTMLButtonElement).disabled = client !== null;
  }

  // ------------------------------------------------------------ running

  async function run(ids: string[]) {
    if (client) return;
    showTab('tests');
    await deps.saveAll();
    await load();
    consoleOut.textContent = '';

    const wanted = ids.length ? ids : roots.map((r) => r.id);
    for (const id of wanted) {
      const n = byId.get(id);
      if (!n) continue;
      for (const leaf of leaves(n)) { leaf.result = undefined; setStatus(leaf, 'queued'); }
    }
    started = performance.now();
    const framework = ($('tests-fw') as HTMLSelectElement).value;
    client = new RunClient({ type: 'startTests', ids, framework });
    client.onMessage = handle;
    client.onClose = () => { if (client) { appendConsole('\nConnection to the host lost.\n', 'err'); finish(); } };
    setButtons();
    summarize();
  }

  /** The test (or class, or file) around the cursor. */
  function idAtCursor(): string | null {
    const model = editor.getModel(), pos = editor.getPosition();
    const rel = model && deps.relOf(model);
    if (!rel || !pos) return null;
    let best: TNode | null = null;
    for (const n of byId.values()) {
      if (n.file !== rel || n.kind === 'case' || pos.lineNumber < n.line || pos.lineNumber > n.endLine) continue;
      if (!best || (n.endLine - n.line) <= (best.endLine - best.line)) best = n;
    }
    return best?.id ?? null;
  }

  function runAtCursor() {
    const id = idAtCursor();
    if (id) void run([id]);
    else { showTab('tests'); appendConsole('The cursor is not inside a test.\n', 'info'); }
  }

  $('tests-all').onclick = () => void run([]);
  $('tests-sel').onclick = () => { if (selected) void run([selected.id]); };
  $('tests-here').onclick = runAtCursor;
  $('tests-rerun').onclick = () => {
    const failed = [...byId.values()].filter((n) => n.children.length === 0 && (n.status === 'failed' || n.status === 'error')).map((n) => n.id);
    if (failed.length) void run(failed);
  };
  $('tests-refresh').onclick = () => void load();
  $('t-tests').onclick = () => { showTab('tests'); if (roots.length === 0) void load(); };
  $('b-tests').onclick = () => { showTab('tests'); void load(); };
  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.F10, runAtCursor);

  void load().catch(() => {});
  setButtons();
}

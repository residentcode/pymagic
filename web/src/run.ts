// Run and debug: toolbar, output panel, breakpoints in the gutter, stepping, frames, variables and evaluate.
import * as monaco from 'monaco-editor';
import { RunClient } from './runclient';
import { showTab } from './panel';

export interface RunDeps {
  editor: monaco.editor.IStandaloneCodeEditor;
  root: string;                                                         // absolute workspace folder
  modelFor(rel: string): Promise<monaco.editor.ITextModel | null>;
  relOf(model: monaco.editor.ITextModel): string | undefined;
  openFile(rel: string, reveal?: monaco.IRange): Promise<void>;
  saveAll(): Promise<void>;
}

interface Frame { id: number; name: string; file: string; line: number }
interface VarItem { name: string; value: string; type: string; ref: number }

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const STORAGE_KEY = 'pymagic.breakpoints';
const norm = (p: string) => p.replace(/\\/g, '/').toLowerCase();

export function setupRun(deps: RunDeps) {
  const { editor } = deps;
  const rootNorm = norm(deps.root).replace(/\/$/, '');

  // ------------------------------------------------------------ breakpoints

  const bpOptions: monaco.editor.IModelDecorationOptions = {
    glyphMarginClassName: 'bp-glyph',
    glyphMarginHoverMessage: { value: 'Breakpoint' },
    stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
  };

  const stored = (): Record<string, number[]> => {
    try { return JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}'); } catch { return {}; }
  };

  const breakpointLines = (model: monaco.editor.ITextModel): number[] =>
    model.getAllDecorations().filter((d) => d.options.glyphMarginClassName === 'bp-glyph')
      .map((d) => d.range.startLineNumber).sort((a, b) => a - b)
      .filter((l, i, all) => i === 0 || all[i - 1] !== l);

  function persist() {
    const all: Record<string, number[]> = {};
    for (const m of monaco.editor.getModels()) {
      const rel = deps.relOf(m);
      const lines = rel ? breakpointLines(m) : [];
      if (rel && lines.length) all[rel] = lines;
    }
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(all)); } catch { /* storage unavailable */ }
  }

  function sendBreakpoints(model: monaco.editor.ITextModel) {
    const rel = deps.relOf(model);
    if (rel && run) run.send({ type: 'setBreakpoints', file: rel, lines: breakpointLines(model) });
  }

  function toggleBreakpoint(model: monaco.editor.ITextModel, line: number) {
    const existing = model.getLineDecorations(line).filter((d) => d.options.glyphMarginClassName === 'bp-glyph');
    if (existing.length) model.deltaDecorations(existing.map((d) => d.id), []);
    else model.deltaDecorations([], [{ range: new monaco.Range(line, 1, line, 1), options: bpOptions }]);
    persist();
    sendBreakpoints(model);
  }

  editor.onMouseDown((e) => {
    if (e.target.type === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN && e.target.position) {
      const model = editor.getModel();
      if (model) toggleBreakpoint(model, e.target.position.lineNumber);
    }
  });
  editor.addCommand(monaco.KeyCode.F9, () => {
    const model = editor.getModel(), pos = editor.getPosition();
    if (model && pos) toggleBreakpoint(model, pos.lineNumber);
  });

  /** Called for every model the app creates, so saved breakpoints come back. */
  function onModelCreated(rel: string, model: monaco.editor.ITextModel) {
    const lines = stored()[rel] ?? [];
    model.deltaDecorations([], lines.filter((l) => l <= model.getLineCount())
      .map((l) => ({ range: new monaco.Range(l, 1, l, 1), options: bpOptions })));
    model.onDidChangeDecorations(() => { /* line shifts are picked up when persisting */ });
    model.onDidChangeContent(() => { persist(); });
  }

  let run: RunClient | null = null;
  let debugging = false;
  let paused = false;
  let frames: Frame[] = [];
  let selectedFrame = 0;
  let currentLine: { model: monaco.editor.ITextModel; ids: string[] } | null = null;

  // ------------------------------------------------------------ UI state

  const panel = $('panel');
  const output = $<HTMLPreElement>('output');

  $('t-output').onclick = () => showTab('output');
  $('t-debug').onclick = () => showTab('debug');
  $('t-close').onclick = () => panel.classList.remove('open');

  function append(text: string, cls = '') {
    const span = document.createElement('span');
    if (cls) span.className = cls;
    span.textContent = text.split('\r\n').join('\n');
    const stick = output.scrollTop + output.clientHeight >= output.scrollHeight - 20;
    output.appendChild(span);
    if (stick) output.scrollTop = output.scrollHeight;
  }

  function setButtons() {
    const active = run !== null;
    ($('b-run') as HTMLButtonElement).disabled = active;
    ($('b-debug') as HTMLButtonElement).disabled = active && !paused;
    ($('b-debug') as HTMLButtonElement).textContent = paused ? '▶ Resume' : '🐞 Debug';
    ($('b-stop') as HTMLButtonElement).disabled = !active;
    for (const id of ['b-over', 'b-into', 'b-out']) ($(id) as HTMLButtonElement).disabled = !paused;
    $('runstate').textContent = !active ? '' : paused ? 'paused' : debugging ? 'debugging…' : 'running…';
  }

  function relOfAbsolute(file: string): string | null {
    const n = norm(file);
    return n.startsWith(rootNorm + '/') ? file.replace(/\\/g, '/').slice(rootNorm.length + 1) : null;
  }

  function clearCurrentLine() {
    if (currentLine) { currentLine.model.deltaDecorations(currentLine.ids, []); currentLine = null; }
  }

  async function showFrame(index: number) {
    selectedFrame = index;
    const f = frames[index];
    if (!f) return;
    document.querySelectorAll('#frames div').forEach((d, i) => d.classList.toggle('sel', i === index));
    clearCurrentLine();
    const rel = relOfAbsolute(f.file);
    if (rel) {
      await deps.openFile(rel, new monaco.Range(f.line, 1, f.line, 1));
      const model = await deps.modelFor(rel);
      if (model) {
        const ids = model.deltaDecorations([], [{
          range: new monaco.Range(f.line, 1, f.line, 1),
          options: { isWholeLine: true, className: 'debug-line', glyphMarginClassName: 'debug-arrow' },
        }]);
        currentLine = { model, ids };
        editor.revealLineInCenterIfOutsideViewport(f.line);
      }
    }
    await loadVariables();
  }

  // ------------------------------------------------------------ variables

  async function loadVariables() {
    const holder = $('vars');
    holder.textContent = '';
    if (!run || !paused) return;
    const scopes = (await run.ask({ type: 'scopes', frameId: selectedFrame })).scopes as { name: string; ref: number }[];
    const list = document.createElement('ul');
    holder.appendChild(list);
    for (const [i, s] of scopes.entries()) {
      const li = document.createElement('li');
      const head = document.createElement('div');
      head.innerHTML = `<span class="caret"></span><span class="scope"></span>`;
      (head.querySelector('.scope') as HTMLElement).textContent = s.name;
      li.appendChild(head);
      list.appendChild(li);
      attachChildren(head, li, s.ref, i === 0);
    }
  }

  /** Makes a node expandable: children are fetched from the debugger the first time it opens. */
  function attachChildren(head: HTMLElement, li: HTMLElement, ref: number, openNow: boolean) {
    const caret = head.querySelector('.caret') as HTMLElement;
    let child: HTMLUListElement | null = null;
    let open = false;
    const toggle = async () => {
      if (!run) return;
      open = !open;
      caret.textContent = open ? '▾' : '▸';
      if (!open) { child?.remove(); child = null; return; }
      const items = (await run.ask({ type: 'variables', ref })).items as VarItem[];
      if (!open) return;
      child = document.createElement('ul');
      for (const v of items) child.appendChild(variableNode(v));
      if (!items.length) child.innerHTML = '<li><div class="type">(empty)</div></li>';
      li.appendChild(child);
    };
    caret.textContent = '▸';
    caret.onclick = toggle;
    if (openNow) void toggle();
  }

  function variableNode(v: VarItem): HTMLLIElement {
    const li = document.createElement('li');
    const head = document.createElement('div');
    head.innerHTML = '<span class="caret"></span><span class="name"></span> = <span class="val"></span> <span class="type"></span>';
    (head.querySelector('.name') as HTMLElement).textContent = v.name;
    (head.querySelector('.val') as HTMLElement).textContent = v.value;
    (head.querySelector('.type') as HTMLElement).textContent = v.type;
    li.appendChild(head);
    if (v.ref > 0) attachChildren(head, li, v.ref, false);
    return li;
  }

  // ------------------------------------------------------------ frames and messages

  function renderFrames() {
    const holder = $('frames');
    holder.textContent = '';
    frames.forEach((f, i) => {
      const row = document.createElement('div');
      const rel = relOfAbsolute(f.file) ?? f.file.split(/[\\/]/).pop() ?? f.file;
      row.innerHTML = '<span class="fn"></span> <span class="loc"></span>';
      (row.querySelector('.fn') as HTMLElement).textContent = f.name;
      (row.querySelector('.loc') as HTMLElement).textContent = `${rel}:${f.line}`;
      row.onclick = () => void showFrame(i);
      holder.appendChild(row);
    });
  }

  function handle(m: any) {
    switch (m.type) {
      case 'started':
        append(`${debugging ? 'Debugging' : 'Running'} (pid ${m.pid})\n`, 'info');
        break;
      case 'output':
        append(m.text, m.stream === 'stderr' ? 'err' : '');
        break;
      case 'error':
        append(m.message + '\n', 'err');
        finish();
        break;
      case 'stopped':
        paused = true;
        frames = m.frames;
        if (m.reason === 'exception') append(`Paused on exception: ${m.message}\n`, 'err');
        renderFrames();
        showTab('debug');
        setButtons();
        void showFrame(0);
        break;
      case 'running':
        paused = false;
        clearCurrentLine();
        $('vars').textContent = '';
        setButtons();
        break;
      case 'exited':
        append(`\nProcess finished with exit code ${m.code}\n`, 'info');
        finish();
        break;
    }
  }

  function finish() {
    run?.close();
    run = null;
    paused = false;
    frames = [];
    clearCurrentLine();
    $('frames').textContent = '';
    $('vars').textContent = '';
    setButtons();
  }

  // ------------------------------------------------------------ commands

  async function start(debug: boolean) {
    if (run) return;
    const model = editor.getModel();
    const rel = model && deps.relOf(model);
    if (!rel) return;
    await deps.saveAll();

    const breakpoints: Record<string, number[]> = {};
    for (const m of monaco.editor.getModels()) {
      const r = deps.relOf(m);
      const lines = r ? breakpointLines(m) : [];
      if (r && lines.length) breakpoints[r] = lines;
    }

    output.textContent = '';
    debugging = debug;
    paused = false;
    showTab('output');
    run = new RunClient({ type: 'start', file: rel, debug, args: [], breakpoints });
    run.onMessage = handle;
    run.onClose = () => { if (run) { append('\nConnection to the host lost.\n', 'err'); finish(); } };
    setButtons();
  }

  const resume = (type: string) => { if (run && paused) run.send({ type }); };

  $('b-run').onclick = () => void start(false);
  $('b-debug').onclick = () => (paused ? resume('continue') : void start(true));
  $('b-stop').onclick = () => run?.send({ type: 'stop' });
  $('b-over').onclick = () => resume('next');
  $('b-into').onclick = () => resume('stepIn');
  $('b-out').onclick = () => resume('stepOut');

  const K = monaco.KeyCode, M = monaco.KeyMod;
  editor.addCommand(K.F5, () => (paused ? resume('continue') : void start(true)));
  editor.addCommand(M.CtrlCmd | K.F5, () => void start(false));
  editor.addCommand(M.Shift | K.F5, () => run?.send({ type: 'stop' }));
  editor.addCommand(K.F10, () => resume('next'));
  editor.addCommand(K.F11, () => resume('stepIn'));
  editor.addCommand(M.Shift | K.F11, () => resume('stepOut'));

  // program input
  const stdin = $<HTMLInputElement>('stdin');
  stdin.onkeydown = (e) => {
    if (e.key !== 'Enter' || !run) return;
    append(stdin.value + '\n', 'info');
    run.send({ type: 'stdin', text: stdin.value + '\n' });
    stdin.value = '';
  };

  // evaluate while paused
  const watchIn = $<HTMLInputElement>('watch-in');
  const log = $('watch-log');
  watchIn.onkeydown = async (e) => {
    if (e.key !== 'Enter' || !watchIn.value.trim()) return;
    const expression = watchIn.value;
    watchIn.value = '';
    const q = document.createElement('div');
    q.className = 'q';
    q.textContent = '> ' + expression;
    log.appendChild(q);
    const a = document.createElement('div');
    if (!run || !paused) { a.className = 'e'; a.textContent = 'The program is not paused.'; }
    else {
      const r = await run.ask({ type: 'evaluate', frameId: selectedFrame, expression });
      if (r.error) { a.className = 'e'; a.textContent = r.error; }
      else a.textContent = r.result + (r.valueType ? `   (${r.valueType})` : '');
      if (!r.error) void loadVariables();   // an assignment may have changed what is shown
    }
    log.appendChild(a);
    log.scrollTop = log.scrollHeight;
  };

  // which interpreter will be used
  fetch('/api/interpreter').then((r) => r.text()).then((t) => { $('interp').textContent = t || 'no Python found'; }).catch(() => {});
  setButtons();

  return { onModelCreated };
}

// Run and debug: toolbar, output panel, breakpoints in the gutter, stepping, frames, variables and evaluate.
import * as monaco from 'monaco-editor';
import { RunClient } from './runclient';
import { showTab } from './panel';
import { editBreakpoint, isPlain, type BpMeta } from './bpdialog';

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

  type Bp = { line: number } & BpMeta;
  const metaOf = new Map<string, BpMeta>();                 // model uri + decoration id -> settings of a breakpoint
  const keyOf = (model: monaco.editor.ITextModel, id: string) => model.uri.toString() + '#' + id;
  const isBp = (d: monaco.editor.IModelDecoration) => !!d.options.glyphMarginClassName?.startsWith('bp-glyph');

  function optionsFor(meta?: BpMeta): monaco.editor.IModelDecorationOptions {
    const kind = meta?.logMessage ? ' bp-log' : meta?.condition || meta?.hitCondition ? ' bp-cond' : '';
    const text = [meta?.condition && `when ${meta.condition}`, meta?.hitCondition && `hit count ${meta.hitCondition}`, meta?.logMessage && `logs "${meta.logMessage}"`]
      .filter(Boolean).join(', ');
    return {
      glyphMarginClassName: 'bp-glyph' + kind,
      glyphMarginHoverMessage: { value: text ? `Breakpoint: ${text}` : 'Breakpoint' },
      stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
    };
  }

  const stored = (): Record<string, (number | Bp)[]> => {
    try { return JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}'); } catch { return {}; }
  };

  const breakpointsOf = (model: monaco.editor.ITextModel): Bp[] => {
    const byLine = new Map<number, Bp>();
    for (const d of model.getAllDecorations().filter(isBp)) {
      const line = d.range.startLineNumber;
      if (!byLine.has(line)) byLine.set(line, { line, ...(metaOf.get(keyOf(model, d.id)) ?? {}) });
    }
    return [...byLine.values()].sort((a, b) => a.line - b.line);
  };
  /** What the debugger is sent: a plain number, or an object when the breakpoint has settings. */
  const wire = (bps: Bp[]): (number | Bp)[] => bps.map((b) => (isPlain(b) ? b.line : b));

  function persist() {
    const all: Record<string, (number | Bp)[]> = {};
    for (const m of monaco.editor.getModels()) {
      const rel = deps.relOf(m);
      const bps = rel ? breakpointsOf(m) : [];
      if (rel && bps.length) all[rel] = wire(bps);
    }
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(all)); } catch { /* storage unavailable */ }
  }

  function sendBreakpoints(model: monaco.editor.ITextModel) {
    const rel = deps.relOf(model);
    if (rel && run) run.send({ type: 'setBreakpoints', file: rel, lines: wire(breakpointsOf(model)) });
  }

  function placeBreakpoint(model: monaco.editor.ITextModel, line: number, meta?: BpMeta) {
    const [id] = model.deltaDecorations([], [{ range: new monaco.Range(line, 1, line, 1), options: optionsFor(meta) }]);
    if (meta && !isPlain(meta)) metaOf.set(keyOf(model, id), meta);
  }

  function removeBreakpoint(model: monaco.editor.ITextModel, line: number) {
    const existing = model.getLineDecorations(line).filter(isBp);
    for (const d of existing) metaOf.delete(keyOf(model, d.id));
    model.deltaDecorations(existing.map((d) => d.id), []);
  }

  function toggleBreakpoint(model: monaco.editor.ITextModel, line: number) {
    if (model.getLineDecorations(line).some(isBp)) removeBreakpoint(model, line);
    else placeBreakpoint(model, line);
    persist();
    sendBreakpoints(model);
  }

  async function editBreakpointAt(model: monaco.editor.ITextModel, line: number) {
    const current = breakpointsOf(model).find((b) => b.line === line);
    const result = await editBreakpoint(line, current, !!current);
    if (result === null) { editor.focus(); return; }
    removeBreakpoint(model, line);
    if (result !== 'remove') placeBreakpoint(model, line, result);
    persist();
    sendBreakpoints(model);
    editor.focus();
  }

  editor.onMouseDown((e) => {
    if (e.target.type === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN && e.target.position) {
      const model = editor.getModel();
      if (!model) return;
      if (e.event.rightButton) void editBreakpointAt(model, e.target.position.lineNumber);
      else toggleBreakpoint(model, e.target.position.lineNumber);
    }
  });
  editor.addCommand(monaco.KeyCode.F9, () => {
    const model = editor.getModel(), pos = editor.getPosition();
    if (model && pos) toggleBreakpoint(model, pos.lineNumber);
  });
  editor.addCommand(monaco.KeyMod.Shift | monaco.KeyCode.F9, () => {
    const model = editor.getModel(), pos = editor.getPosition();
    if (model && pos) void editBreakpointAt(model, pos.lineNumber);
  });

  /** Called for every model the app creates, so saved breakpoints come back. */
  function onModelCreated(rel: string, model: monaco.editor.ITextModel) {
    for (const saved of stored()[rel] ?? []) {
      const bp: Bp = typeof saved === 'number' ? { line: saved } : saved;
      if (bp.line <= model.getLineCount()) placeBreakpoint(model, bp.line, bp);
    }
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
    void refreshWatches();
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
      attachChildren(head, li, s.ref, i === 0, s.ref);
    }
  }

  /** Makes a node expandable: children are fetched from the debugger the first time it opens. */
  function attachChildren(head: HTMLElement, li: HTMLElement, ref: number, openNow: boolean, scopeRef?: number) {
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
      for (const v of items) child.appendChild(variableNode(v, scopeRef));
      if (!items.length) child.innerHTML = '<li><div class="type">(empty)</div></li>';
      li.appendChild(child);
    };
    caret.textContent = '▸';
    caret.onclick = toggle;
    if (openNow) void toggle();
  }

  function variableNode(v: VarItem, scopeRef?: number): HTMLLIElement {
    const li = document.createElement('li');
    const head = document.createElement('div');
    head.innerHTML = '<span class="caret"></span><span class="name"></span> = <span class="val"></span> <span class="type"></span>';
    (head.querySelector('.name') as HTMLElement).textContent = v.name;
    (head.querySelector('.val') as HTMLElement).textContent = v.value;
    (head.querySelector('.type') as HTMLElement).textContent = v.type;
    li.appendChild(head);
    if (v.ref > 0) attachChildren(head, li, v.ref, false);
    if (scopeRef !== undefined) {
      const val = head.querySelector('.val') as HTMLElement;
      val.title = 'Double-click to change the value';
      val.ondblclick = () => editVariable(val, scopeRef, v);
    }
    return li;
  }

  /** Replaces the shown value by an input; Enter assigns the expression typed there to the variable. */
  function editVariable(valueEl: HTMLElement, scopeRef: number, v: VarItem) {
    const input = document.createElement('input');
    input.className = 'edit';
    input.value = v.value;
    input.spellcheck = false;
    valueEl.replaceWith(input);
    input.focus();
    input.select();
    let done = false;
    const finish = async (apply: boolean) => {
      if (done) return;
      done = true;
      if (apply && run && paused) {
        const r = await run.ask({ type: 'setVariable', ref: scopeRef, name: v.name, expression: input.value });
        if (r.error) logLine(`${v.name} = ${input.value}`, r.error);
      }
      await loadVariables();
      void refreshWatches();
    };
    input.onkeydown = (e) => {
      if (e.key === 'Enter') { e.preventDefault(); void finish(true); }
      else if (e.key === 'Escape') { e.preventDefault(); void finish(false); }
    };
    input.onblur = () => void finish(false);
  }

  function logLine(question: string, answer: string, isError = true) {
    const q = document.createElement('div');
    q.className = 'q';
    q.textContent = '> ' + question;
    const a = document.createElement('div');
    if (isError) a.className = 'e';
    a.textContent = answer;
    $('watch-log').append(q, a);
    $('watch-log').scrollTop = $('watch-log').scrollHeight;
  }

  // ------------------------------------------------------------ watches

  const WATCH_KEY = 'pymagic.watches';
  let watches: string[] = [];
  try { watches = JSON.parse(localStorage.getItem(WATCH_KEY) ?? '[]'); } catch { /* storage unavailable */ }
  const saveWatches = () => { try { localStorage.setItem(WATCH_KEY, JSON.stringify(watches)); } catch { /* storage unavailable */ } };

  /** Evaluates every watch in the selected frame (expressions only: a watch never assigns). */
  async function refreshWatches() {
    const holder = $('watches');
    holder.textContent = '';
    for (const [index, expression] of watches.entries()) {
      const row = document.createElement('div');
      row.className = 'w';
      const name = document.createElement('span');
      name.className = 'wx';
      name.textContent = expression;
      const value = document.createElement('span');
      value.className = 'wv';
      const del = document.createElement('span');
      del.className = 'wd';
      del.textContent = '✕';
      del.title = 'Remove the watch';
      del.onclick = () => { watches.splice(index, 1); saveWatches(); void refreshWatches(); };
      row.append(name, value, del);
      holder.appendChild(row);
      if (!run || !paused) { value.className = 'wv wn'; value.textContent = 'not paused'; continue; }
      const r = await run.ask({ type: 'evaluate', frameId: selectedFrame, expression, evalOnly: true });
      if (r.error) { value.className = 'wv we'; value.textContent = r.error; }
      else { value.textContent = '= ' + r.result; value.title = r.valueType; }
    }
  }

  const watchAdd = $<HTMLInputElement>('watch-add');
  watchAdd.onkeydown = (e) => {
    if (e.key !== 'Enter' || !watchAdd.value.trim()) return;
    watches.push(watchAdd.value.trim());
    watchAdd.value = '';
    saveWatches();
    void refreshWatches();
  };
  void refreshWatches();

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
        void refreshWatches();
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

    const breakpoints: Record<string, (number | Bp)[]> = {};
    for (const m of monaco.editor.getModels()) {
      const r = deps.relOf(m);
      const bps = r ? breakpointsOf(m) : [];
      if (r && bps.length) breakpoints[r] = wire(bps);
    }

    output.textContent = '';
    debugging = debug;
    paused = false;
    showTab('output');
    run = new RunClient({ type: 'start', file: rel, debug, args: [], breakpoints, exceptionBreak: raisedBox.checked ? 'raised' : 'uncaught' });
    run.onMessage = handle;
    run.onClose = () => { if (run) { append('\nConnection to the host lost.\n', 'err'); finish(); } };
    setButtons();
  }

  const raisedBox = $<HTMLInputElement>('exc-raised');
  try { raisedBox.checked = localStorage.getItem('pymagic.breakOnRaised') === '1'; } catch { /* storage unavailable */ }
  raisedBox.onchange = () => {
    try { localStorage.setItem('pymagic.breakOnRaised', raisedBox.checked ? '1' : '0'); } catch { /* storage unavailable */ }
    run?.send({ type: 'setExceptionBreak', mode: raisedBox.checked ? 'raised' : 'uncaught' });
  };

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
  setButtons();

  return { onModelCreated };
}

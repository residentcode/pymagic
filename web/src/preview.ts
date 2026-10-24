// A preview of what a refactoring is about to change: a line diff per file, with Apply and Cancel.

export interface FileChange { name: string; oldText: string; newText: string }

export type DiffLine = { kind: ' ' | '-' | '+'; text: string };
export interface Hunk { oldStart: number; newStart: number; lines: DiffLine[] }

/** Line diff: common head and tail are cut off, the middle is matched with a longest common subsequence. */
export function diffLines(oldText: string, newText: string): DiffLine[] {
  const a = oldText.split('\n'), b = newText.split('\n');
  let head = 0;
  while (head < a.length && head < b.length && a[head] === b[head]) head++;
  let tail = 0;
  while (tail < a.length - head && tail < b.length - head && a[a.length - 1 - tail] === b[b.length - 1 - tail]) tail++;
  const ma = a.slice(head, a.length - tail), mb = b.slice(head, b.length - tail);

  const out: DiffLine[] = [];
  for (let i = 0; i < head; i++) out.push({ kind: ' ', text: a[i] });
  if (ma.length * mb.length > 4_000_000) {
    for (const t of ma) out.push({ kind: '-', text: t });
    for (const t of mb) out.push({ kind: '+', text: t });
  } else {
    const n = ma.length, m = mb.length;
    const lcs = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1));
    for (let i = n - 1; i >= 0; i--)
      for (let j = m - 1; j >= 0; j--)
        lcs[i][j] = ma[i] === mb[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
    let i = 0, j = 0;
    while (i < n && j < m) {
      if (ma[i] === mb[j]) { out.push({ kind: ' ', text: ma[i] }); i++; j++; }
      else if (lcs[i + 1][j] >= lcs[i][j + 1]) out.push({ kind: '-', text: ma[i++] });
      else out.push({ kind: '+', text: mb[j++] });
    }
    while (i < n) out.push({ kind: '-', text: ma[i++] });
    while (j < m) out.push({ kind: '+', text: mb[j++] });
  }
  for (let i = a.length - tail; i < a.length; i++) out.push({ kind: ' ', text: a[i] });
  return out;
}

/** The changed lines with a few lines of context around them. */
export function hunksOf(lines: DiffLine[], context = 2): Hunk[] {
  const hunks: Hunk[] = [];
  let oldNo = 1, newNo = 1;
  const numbered = lines.map((l) => {
    const entry = { l, oldNo, newNo };
    if (l.kind !== '+') oldNo++;
    if (l.kind !== '-') newNo++;
    return entry;
  });
  const changed = numbered.map((e, i) => (e.l.kind === ' ' ? -1 : i)).filter((i) => i >= 0);
  let k = 0;
  while (k < changed.length) {
    let from = Math.max(changed[k] - context, 0);
    let to = changed[k];
    while (k + 1 < changed.length && changed[k + 1] - to <= context * 2 + 1) to = changed[++k];
    to = Math.min(to + context, numbered.length - 1);
    k++;
    const slice = numbered.slice(from, to + 1);
    hunks.push({ oldStart: slice[0].oldNo, newStart: slice[0].newNo, lines: slice.map((e) => e.l) });
  }
  return hunks;
}

const store = {
  get always(): boolean { try { return localStorage.getItem('pm.previewAlways') === '1'; } catch { return false; } },
  set always(v: boolean) { try { localStorage.setItem('pm.previewAlways', v ? '1' : '0'); } catch { /* not available */ } },
};
export const previewAlways = () => store.always;

/** Shows the changes and resolves true when the user applies them. */
export function previewChanges(title: string, changes: FileChange[], warnings: string[] = []): Promise<boolean> {
  return new Promise((resolve) => {
    const overlay = document.createElement('div');
    overlay.id = 'sigdialog';
    overlay.className = 'preview';
    const box = document.createElement('div');
    box.className = 'box';
    overlay.appendChild(box);

    const h = document.createElement('h2');
    const added = { n: 0 }, removed = { n: 0 };
    const sections: HTMLElement[] = [];
    for (const change of changes) {
      const lines = diffLines(change.oldText, change.newText);
      for (const l of lines) { if (l.kind === '+') added.n++; else if (l.kind === '-') removed.n++; }
      const section = document.createElement('div');
      section.className = 'pfile';
      const name = document.createElement('div');
      name.className = 'pname';
      const fileAdded = lines.filter((l) => l.kind === '+').length, fileRemoved = lines.filter((l) => l.kind === '-').length;
      name.textContent = `${change.name}   +${fileAdded} −${fileRemoved}`;
      section.appendChild(name);
      const pre = document.createElement('pre');
      for (const hunk of hunksOf(lines)) {
        const head = document.createElement('div');
        head.className = 'hunk';
        head.textContent = `@@ line ${hunk.oldStart} @@`;
        pre.appendChild(head);
        for (const l of hunk.lines) {
          const row = document.createElement('div');
          row.className = l.kind === '+' ? 'add' : l.kind === '-' ? 'del' : 'ctx';
          row.textContent = (l.kind === ' ' ? '  ' : l.kind + ' ') + l.text;
          pre.appendChild(row);
        }
      }
      section.appendChild(pre);
      sections.push(section);
    }
    h.textContent = `${title} — ${changes.length} file${changes.length === 1 ? '' : 's'}, +${added.n} −${removed.n}`;
    box.appendChild(h);

    if (warnings.length) {
      const w = document.createElement('div');
      w.className = 'warnings';
      for (const text of warnings) { const p = document.createElement('div'); p.textContent = '⚠ ' + text; w.appendChild(p); }
      box.appendChild(w);
    }
    const body = document.createElement('div');
    body.className = 'pbody';
    sections.forEach((s) => body.appendChild(s));
    box.appendChild(body);

    const buttons = document.createElement('div');
    buttons.className = 'buttons';
    const always = document.createElement('label');
    const check = document.createElement('input');
    check.type = 'checkbox';
    check.checked = store.always;
    always.append(check, ' Always preview');
    const spacer = document.createElement('span');
    spacer.style.flex = '1';
    const cancel = document.createElement('button');
    cancel.textContent = 'Cancel';
    const apply = document.createElement('button');
    apply.textContent = 'Apply';
    apply.className = 'primary';
    buttons.append(always, spacer, cancel, apply);
    box.appendChild(buttons);

    const finish = (ok: boolean) => { store.always = check.checked; overlay.remove(); resolve(ok); };
    cancel.onclick = () => finish(false);
    apply.onclick = () => finish(true);
    overlay.onkeydown = (e) => {
      if (e.key === 'Escape') { e.preventDefault(); finish(false); }
      else if (e.key === 'Enter' && !(e.target instanceof HTMLInputElement)) { e.preventDefault(); finish(true); }
    };
    overlay.onmousedown = (e) => { if (e.target === overlay) finish(false); };
    document.body.appendChild(overlay);
    apply.focus();
  });
}

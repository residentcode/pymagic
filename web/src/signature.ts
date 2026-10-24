// The "Change signature" dialog: an editable table of the parameters of the function under the cursor.
import type { LspClient } from './lsp';

export interface SigParam {
  origin: number | null;
  name: string;
  category: string;
  annotation: string | null;
  default: string | null;
  callValue: string | null;
}
export interface SigInfo {
  name: string; kind: string; implicitFirst: string | null; parameters: SigParam[];
  calls: number; files: number; otherReferences: number;
}
export interface SigResult { newName: string; parameters: SigParam[] }

const CATEGORIES: [string, string][] = [
  ['positionalOnly', 'positional only'], ['normal', 'normal'], ['keywordOnly', 'keyword only'],
  ['varArgs', '*args'], ['kwArgs', '**kwargs'],
];

/** Shows the dialog; resolves with the edited signature, or null when cancelled. */
export function editSignature(info: SigInfo): Promise<SigResult | null> {
  return new Promise((resolve) => {
    const rows: SigParam[] = info.parameters.map((p) => ({ ...p }));
    const overlay = document.createElement('div');
    overlay.id = 'sigdialog';
    const box = document.createElement('div');
    box.className = 'box';
    overlay.appendChild(box);

    const title = document.createElement('h2');
    title.textContent = 'Change signature';
    const nameRow = document.createElement('div');
    nameRow.className = 'namerow';
    const nameLabel = document.createElement('label');
    nameLabel.textContent = info.kind === 'constructor' ? 'Class' : 'Name';
    const nameInput = document.createElement('input');
    nameInput.value = info.name;
    nameInput.spellcheck = false;
    nameInput.disabled = info.kind === 'constructor';
    nameRow.append(nameLabel, nameInput);
    if (info.implicitFirst) {
      const hint = document.createElement('span');
      hint.className = 'dim';
      hint.textContent = `(${info.implicitFirst} is kept)`;
      nameRow.appendChild(hint);
    }

    const table = document.createElement('table');
    const summary = document.createElement('div');
    summary.className = 'dim';
    summary.textContent = `${info.calls} call${info.calls === 1 ? '' : 's'} in ${info.files} file${info.files === 1 ? '' : 's'}` +
      (info.otherReferences ? `, ${info.otherReferences} other reference${info.otherReferences === 1 ? '' : 's'} (not rewritten)` : '');

    const buttons = document.createElement('div');
    buttons.className = 'buttons';
    const add = document.createElement('button');
    add.textContent = 'Add parameter';
    const spacer = document.createElement('span');
    spacer.style.flex = '1';
    const cancel = document.createElement('button');
    cancel.textContent = 'Cancel';
    const ok = document.createElement('button');
    ok.textContent = 'Refactor';
    ok.className = 'primary';
    buttons.append(add, spacer, cancel, ok);
    box.append(title, nameRow, table, summary, buttons);

    function input(value: string | null, placeholder: string, set: (v: string) => void, width = 110): HTMLInputElement {
      const el = document.createElement('input');
      el.value = value ?? '';
      el.placeholder = placeholder;
      el.spellcheck = false;
      el.style.width = width + 'px';
      el.oninput = () => set(el.value);
      return el;
    }
    function button(text: string, title: string, on: () => void, disabled = false): HTMLButtonElement {
      const b = document.createElement('button');
      b.textContent = text; b.title = title; b.disabled = disabled; b.onclick = on;
      return b;
    }

    function render() {
      table.replaceChildren();
      const head = table.insertRow();
      for (const h of ['Name', 'Type', 'Default', 'Value in existing calls', '']) {
        const th = document.createElement('th'); th.textContent = h; head.appendChild(th);
      }
      rows.forEach((row, i) => {
        const tr = table.insertRow();
        const cell = (el: HTMLElement) => { tr.insertCell().appendChild(el); };
        cell(input(row.name, 'name', (v) => (row.name = v)));
        const sel = document.createElement('select');
        for (const [value, label] of CATEGORIES) {
          const o = document.createElement('option'); o.value = value; o.textContent = label; sel.appendChild(o);
        }
        sel.value = row.category;
        sel.onchange = () => (row.category = sel.value);
        cell(sel);
        cell(input(row.default, 'no default', (v) => (row.default = v.trim() ? v : null), 90));
        const isNew = row.origin === null;
        const cv = input(row.callValue, isNew ? 'required' : 'unchanged', (v) => (row.callValue = v.trim() ? v : null), 130);
        cv.disabled = !isNew;
        cell(cv);
        const actions = document.createElement('span');
        actions.append(
          button('↑', 'Move up', () => { [rows[i - 1], rows[i]] = [rows[i], rows[i - 1]]; render(); }, i === 0),
          button('↓', 'Move down', () => { [rows[i + 1], rows[i]] = [rows[i], rows[i + 1]]; render(); }, i === rows.length - 1),
          button('✕', 'Remove', () => { rows.splice(i, 1); render(); }),
        );
        cell(actions);
      });
    }

    const finish = (result: SigResult | null) => { overlay.remove(); resolve(result); };
    add.onclick = () => {
      rows.push({ origin: null, name: '', category: 'normal', annotation: null, default: null, callValue: null });
      render();
      (table.querySelector('tr:last-child input') as HTMLInputElement | null)?.focus();
    };
    cancel.onclick = () => finish(null);
    ok.onclick = () => finish({ newName: nameInput.value.trim() || info.name, parameters: rows });
    overlay.onkeydown = (e) => {
      if (e.key === 'Escape') { e.preventDefault(); finish(null); }
      else if (e.key === 'Enter' && e.target instanceof HTMLInputElement) { e.preventDefault(); ok.click(); }
    };
    document.body.appendChild(overlay);
    render();
    nameInput.focus();
  });
}

export async function fetchSignature(lsp: LspClient, uri: string, position: { line: number; character: number }): Promise<SigInfo> {
  return lsp.request('pymagic/signature', { textDocument: { uri }, position });
}

// The Inspections tab: every inspection the editor can report, how severe it is for this project, and a switch for each.
// The choices are kept in .pymagic/inspections.json in the project folder, so they travel with it.
import type { LspClient } from './lsp';
import { showTab } from './panel';
import { toast } from './toast';

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

interface Inspection { code: string; group: string; title: string; description: string; default: string; level: string }

const LEVELS: [string, string][] = [['error', 'Error'], ['warning', 'Warning'], ['weak', 'Weak warning'], ['hint', 'Hint (greyed)'], ['off', 'Off']];

export function setupInspections(lsp: LspClient) {
  const list = $('insp-list');
  const summary = $('insp-summary');
  const filter = $<HTMLInputElement>('insp-filter');
  let items: Inspection[] = [];

  function paint() {
    const needle = filter.value.trim().toLowerCase();
    const shown = items.filter((i) => !needle || `${i.group} ${i.title} ${i.code} ${i.description}`.toLowerCase().includes(needle));
    const changed = items.filter((i) => i.level !== i.default).length;
    summary.textContent = `${items.length} inspections, ${changed} changed from the default`;
    $<HTMLButtonElement>('insp-reset').disabled = changed === 0;
    const rows: HTMLElement[] = [];
    let group = '';
    for (const i of shown) {
      if (i.group !== group) {
        group = i.group;
        const head = document.createElement('div');
        head.className = 'insp-group';
        head.textContent = group;
        rows.push(head);
      }
      const row = document.createElement('div');
      row.className = 'insp-row' + (i.level !== i.default ? ' changed' : '');
      const title = document.createElement('span'); title.className = 'insp-title'; title.textContent = i.title; title.title = i.description;
      const code = document.createElement('span'); code.className = 'detail'; code.textContent = i.code;
      const select = document.createElement('select');
      for (const [value, label] of LEVELS) {
        const o = document.createElement('option');
        o.value = value;
        o.textContent = value === i.default ? `${label} (default)` : label;
        select.append(o);
      }
      select.value = i.level;
      select.onchange = async () => {
        try {
          await lsp.request('pymagic/setInspection', { code: i.code, level: select.value === i.default ? null : select.value });
          i.level = select.value;
          paint();
        } catch (e: any) { toast(e.message ?? 'The profile cannot be saved.', true); select.value = i.level; }
      };
      row.append(title, code, select);
      rows.push(row);
    }
    list.replaceChildren(...rows);
  }

  async function refresh() {
    try { items = ((await lsp.request('pymagic/inspections', {})) as Inspection[] | null) ?? []; paint(); }
    catch (e: any) { summary.textContent = String(e.message ?? e); }
  }

  $('t-insp').onclick = () => { showTab('insp'); void refresh(); };
  $('insp-reset').onclick = async () => {
    try { await lsp.request('pymagic/resetInspections', {}); await refresh(); toast('Every inspection is back to its default'); }
    catch (e: any) { toast(e.message ?? 'The profile cannot be saved.', true); }
  };
  filter.oninput = paint;
}

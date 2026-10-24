// Interpreter picker: lists what the host found, switches the project's interpreter, creates a .venv.
const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

interface Interp { path: string; label: string; kind: string; source: string; version: string; prefix: string; current: boolean }

export function setupInterpreters() {
  const select = $<HTMLSelectElement>('interp');
  const note = $('interp-note');
  const venvButton = $<HTMLButtonElement>('b-venv');
  let flash: number | undefined;

  function say(text: string, bad = false) {
    note.textContent = text;
    note.className = bad ? 'bad' : '';
    clearTimeout(flash);
    if (text) flash = window.setTimeout(() => { note.textContent = ''; }, 6000);
  }

  async function refresh(retries = 8) {
    select.disabled = true;
    select.textContent = '';
    const loading = document.createElement('option');
    loading.textContent = 'Detecting Python…';
    select.appendChild(loading);

    let list: Interp[] = [];
    try { list = await (await fetch('/api/interpreters')).json(); } catch { /* host not reachable */ }
    select.textContent = '';

    if (list.length === 0) {
      const none = document.createElement('option');
      none.textContent = 'No Python found';
      select.appendChild(none);
      venvButton.disabled = true;
      return;
    }
    const hasCurrent = list.some((i) => i.current);
    if (!hasCurrent) {
      const pick = document.createElement('option');
      pick.textContent = 'Choose an interpreter…';
      pick.value = '';
      pick.selected = true;
      select.appendChild(pick);
      // the host picks one in the background right after it starts
      if (retries > 0) window.setTimeout(() => void refresh(retries - 1), 1500);
    }
    for (const i of list) {
      const option = document.createElement('option');
      option.value = i.path;
      option.textContent = i.label;
      option.title = i.path;
      option.selected = i.current;
      select.appendChild(option);
    }
    select.disabled = false;
    venvButton.disabled = false;
    select.title = list.find((i) => i.current)?.path ?? 'Python interpreter';
  }

  async function choose(path: string) {
    say('Switching…');
    select.disabled = true;
    const res = await fetch('/api/interpreter', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path }) });
    const body = await res.json();
    if (!res.ok) { say(body.error ?? 'Could not switch the interpreter', true); await refresh(0); return; }
    say(`Using ${body.label}`);
    window.dispatchEvent(new Event('pymagic:interpreter'));
    await refresh(0);
  }

  select.onchange = () => { if (select.value) void choose(select.value); };

  venvButton.onclick = async () => {
    venvButton.disabled = true;
    say('Creating .venv…');
    try {
      const res = await fetch('/api/venv', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
      const body = await res.json();
      if (!res.ok) { say(body.error ?? 'Could not create the environment', true); return; }
      await choose(body.path);
    } finally { venvButton.disabled = false; }
  };

  void refresh();
}

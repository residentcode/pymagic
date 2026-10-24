// A small chooser for the file a definition is moved to: type to filter, arrows and Enter to choose, Escape to cancel.
export interface MoveTarget { uri: string; module: string }

export function pickTarget(targets: MoveTarget[]): Promise<MoveTarget | null> {
  return new Promise((resolve) => {
    const overlay = document.createElement('div');
    overlay.id = 'sigdialog';
    const box = document.createElement('div');
    box.className = 'box';
    const title = document.createElement('h2');
    title.textContent = 'Move to…';
    const filter = document.createElement('input');
    filter.placeholder = 'module name';
    filter.spellcheck = false;
    const list = document.createElement('div');
    list.className = 'picklist';
    box.append(title, filter, list);
    overlay.appendChild(box);

    let shown: MoveTarget[] = [];
    let index = 0;
    const finish = (r: MoveTarget | null) => { overlay.remove(); resolve(r); };
    function render() {
      const q = filter.value.trim().toLowerCase();
      shown = targets.filter((t) => t.module.toLowerCase().includes(q));
      index = Math.min(index, Math.max(shown.length - 1, 0));
      list.replaceChildren();
      if (shown.length === 0) {
        const none = document.createElement('div');
        none.className = 'dim';
        none.textContent = targets.length === 0 ? 'There is no other file in the project.' : 'No file matches.';
        list.appendChild(none);
      }
      shown.forEach((t, i) => {
        const row = document.createElement('div');
        row.textContent = t.module;
        row.className = i === index ? 'pick sel' : 'pick';
        row.onclick = () => finish(t);
        list.appendChild(row);
      });
    }
    filter.oninput = () => { index = 0; render(); };
    overlay.onkeydown = (e) => {
      if (e.key === 'Escape') { e.preventDefault(); finish(null); }
      else if (e.key === 'ArrowDown') { e.preventDefault(); index = Math.min(index + 1, shown.length - 1); render(); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); index = Math.max(index - 1, 0); render(); }
      else if (e.key === 'Enter') { e.preventDefault(); if (shown[index]) finish(shown[index]); }
    };
    overlay.onmousedown = (e) => { if (e.target === overlay) finish(null); };
    document.body.appendChild(overlay);
    render();
    filter.focus();
  });
}

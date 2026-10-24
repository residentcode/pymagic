// The dialog behind Shift+F9 and a right click on a breakpoint: a condition, a hit count, a message to log instead of stopping.

export interface BpMeta { condition?: string; hitCondition?: string; logMessage?: string }

export const isPlain = (m: BpMeta | undefined): boolean => !m || (!m.condition && !m.hitCondition && !m.logMessage);

/** Resolves with the new settings, 'remove' for the Remove button, or null when cancelled. */
export function editBreakpoint(line: number, meta: BpMeta | undefined, exists: boolean): Promise<BpMeta | 'remove' | null> {
  return new Promise((resolve) => {
    const overlay = document.createElement('div');
    overlay.id = 'sigdialog';
    const box = document.createElement('div');
    box.className = 'box';
    overlay.appendChild(box);
    const title = document.createElement('h2');
    title.textContent = `Breakpoint at line ${line}`;
    box.appendChild(title);

    const field = (label: string, value: string | undefined, placeholder: string, hint: string) => {
      const row = document.createElement('div');
      row.className = 'namerow';
      const l = document.createElement('label');
      l.textContent = label;
      l.style.width = '90px';
      const input = document.createElement('input');
      input.value = value ?? '';
      input.placeholder = placeholder;
      input.spellcheck = false;
      input.style.flex = '1';
      input.style.minWidth = '280px';
      input.title = hint;
      row.append(l, input);
      box.appendChild(row);
      return input;
    };
    const condition = field('Condition', meta?.condition, 'e.g. i == 3 and name != ""', 'Stops only when this Python expression is true.');
    const hits = field('Hit count', meta?.hitCondition, '3   >=3   %2', 'Stops on the 3rd hit, from the 3rd on, or on every 2nd hit.');
    const log = field('Log message', meta?.logMessage, 'i={i} total={total}', 'Prints the message instead of stopping; {expression} is evaluated in the frame.');
    const note = document.createElement('div');
    note.className = 'dim';
    note.textContent = 'With a log message the program does not stop here.';
    box.appendChild(note);

    const buttons = document.createElement('div');
    buttons.className = 'buttons';
    const remove = document.createElement('button');
    remove.textContent = 'Remove';
    remove.disabled = !exists;
    const spacer = document.createElement('span');
    spacer.style.flex = '1';
    const cancel = document.createElement('button');
    cancel.textContent = 'Cancel';
    const ok = document.createElement('button');
    ok.textContent = 'OK';
    ok.className = 'primary';
    buttons.append(remove, spacer, cancel, ok);
    box.appendChild(buttons);

    const finish = (r: BpMeta | 'remove' | null) => { overlay.remove(); resolve(r); };
    remove.onclick = () => finish('remove');
    cancel.onclick = () => finish(null);
    ok.onclick = () => finish({
      condition: condition.value.trim() || undefined,
      hitCondition: hits.value.trim() || undefined,
      logMessage: log.value.trim() || undefined,
    });
    overlay.onkeydown = (e) => {
      if (e.key === 'Escape') { e.preventDefault(); finish(null); }
      else if (e.key === 'Enter' && e.target instanceof HTMLInputElement) { e.preventDefault(); ok.click(); }
    };
    overlay.onmousedown = (e) => { if (e.target === overlay) finish(null); };
    document.body.appendChild(overlay);
    condition.focus();
  });
}

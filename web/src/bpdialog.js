// The dialog behind Shift+F9 and a right click on a breakpoint: a condition, a hit count, a message to log instead of
// stopping, a description, and another breakpoint it waits for.
export const isPlain = (m) => !m || (!m.condition && !m.hitCondition && !m.logMessage && !m.description && !m.after && !m.suspend);
/** Resolves with the new settings, 'remove' for the Remove button, or null when cancelled. `others` are the project's
 * other breakpoints ({file, line}) this one can wait for. */
export function editBreakpoint(line, meta, exists, others = [], functionName = null) {
    return new Promise((resolve) => {
        const overlay = document.createElement('div');
        overlay.id = 'sigdialog';
        const box = document.createElement('div');
        box.className = 'box';
        overlay.appendChild(box);
        const title = document.createElement('h2');
        title.textContent = functionName ? `Breakpoint on calls of ${functionName}() (line ${line})` : `Breakpoint at line ${line}`;
        box.appendChild(title);
        const field = (label, value, placeholder, hint) => {
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
        const description = field('Description', meta?.description, 'what this breakpoint is for', 'Shown when the mouse is over the breakpoint.');
        const afterRow = document.createElement('div');
        afterRow.className = 'namerow';
        const afterLabel = document.createElement('label');
        afterLabel.textContent = 'Disabled until';
        afterLabel.style.width = '90px';
        const after = document.createElement('select');
        after.style.flex = '1';
        after.title = 'Does nothing until the chosen breakpoint has been hit in this run.';
        after.append(new Option('(always enabled)', ''));
        for (const o of others)
            after.append(new Option(`${o.file}:${o.line} is hit`, `${o.file}:${o.line}`));
        after.value = meta?.after ? `${meta.after.file}:${meta.after.line}` : '';
        afterRow.append(afterLabel, after);
        box.appendChild(afterRow);
        const suspendRow = document.createElement('div');
        suspendRow.className = 'namerow';
        const suspendLabel = document.createElement('label');
        suspendLabel.textContent = 'Suspend';
        suspendLabel.style.width = '90px';
        const suspend = document.createElement('select');
        suspend.style.flex = '1';
        suspend.title = 'All threads: the others wait at their next line while this one is stopped. This thread: they go on.';
        suspend.append(new Option('all threads', ''), new Option('this thread only', 'thread'));
        suspend.value = meta?.suspend ?? '';
        suspendRow.append(suspendLabel, suspend);
        box.appendChild(suspendRow);
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
        const finish = (r) => { overlay.remove(); resolve(r); };
        remove.onclick = () => finish('remove');
        cancel.onclick = () => finish(null);
        ok.onclick = () => finish({
            condition: condition.value.trim() || undefined,
            hitCondition: hits.value.trim() || undefined,
            logMessage: log.value.trim() || undefined,
            description: description.value.trim() || undefined,
            suspend: suspend.value || undefined,
            after: after.value ? { file: after.value.replace(/:\d+$/, ''), line: Number(after.value.match(/:(\d+)$/)[1]) } : undefined,
        });
        overlay.onkeydown = (e) => {
            if (e.key === 'Escape') {
                e.preventDefault();
                finish(null);
            }
            else if (e.key === 'Enter' && e.target instanceof HTMLInputElement) {
                e.preventDefault();
                ok.click();
            }
        };
        overlay.onmousedown = (e) => { if (e.target === overlay)
            finish(null); };
        document.body.appendChild(overlay);
        condition.focus();
    });
}

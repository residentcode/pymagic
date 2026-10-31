// A preview of what a refactoring is about to change: a line diff per file, with Apply and Cancel.
/** Line diff: common head and tail are cut off, the middle is matched with a longest common subsequence. */
export function diffLines(oldText, newText) {
    const a = oldText.split('\n'), b = newText.split('\n');
    let head = 0;
    while (head < a.length && head < b.length && a[head] === b[head])
        head++;
    let tail = 0;
    while (tail < a.length - head && tail < b.length - head && a[a.length - 1 - tail] === b[b.length - 1 - tail])
        tail++;
    const ma = a.slice(head, a.length - tail), mb = b.slice(head, b.length - tail);
    const out = [];
    for (let i = 0; i < head; i++)
        out.push({ kind: ' ', text: a[i] });
    if (ma.length * mb.length > 4_000_000) {
        for (const t of ma)
            out.push({ kind: '-', text: t });
        for (const t of mb)
            out.push({ kind: '+', text: t });
    }
    else {
        const n = ma.length, m = mb.length;
        const lcs = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1));
        for (let i = n - 1; i >= 0; i--)
            for (let j = m - 1; j >= 0; j--)
                lcs[i][j] = ma[i] === mb[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
        let i = 0, j = 0;
        while (i < n && j < m) {
            if (ma[i] === mb[j]) {
                out.push({ kind: ' ', text: ma[i] });
                i++;
                j++;
            }
            else if (lcs[i + 1][j] >= lcs[i][j + 1])
                out.push({ kind: '-', text: ma[i++] });
            else
                out.push({ kind: '+', text: mb[j++] });
        }
        while (i < n)
            out.push({ kind: '-', text: ma[i++] });
        while (j < m)
            out.push({ kind: '+', text: mb[j++] });
    }
    for (let i = a.length - tail; i < a.length; i++)
        out.push({ kind: ' ', text: a[i] });
    return out;
}
/** The changed lines with a few lines of context around them. */
export function hunksOf(lines, context = 2) {
    const hunks = [];
    let oldNo = 1, newNo = 1;
    const numbered = lines.map((l) => {
        const entry = { l, oldNo, newNo };
        if (l.kind !== '+')
            oldNo++;
        if (l.kind !== '-')
            newNo++;
        return entry;
    });
    const changed = numbered.map((e, i) => (e.l.kind === ' ' ? -1 : i)).filter((i) => i >= 0);
    let k = 0;
    while (k < changed.length) {
        let from = Math.max(changed[k] - context, 0);
        let to = changed[k];
        while (k + 1 < changed.length && changed[k + 1] - to <= context * 2 + 1)
            to = changed[++k];
        to = Math.min(to + context, numbered.length - 1);
        k++;
        const slice = numbered.slice(from, to + 1);
        hunks.push({ oldStart: slice[0].oldNo, newStart: slice[0].newNo, lines: slice.map((e) => e.l), from });
    }
    return hunks;
}
/** The changes of a line diff: each run of removed and added lines that are next to each other, by the indexes of its lines. */
export function blocksOf(lines) {
    const blocks = [];
    let current = null;
    lines.forEach((l, i) => {
        if (l.kind === ' ') {
            current = null;
            return;
        }
        if (!current) {
            current = [];
            blocks.push(current);
        }
        current.push(i);
    });
    return blocks;
}

/** The new text with only the chosen changes (by their index in blocksOf) made: the others keep their old lines. */
export function textWithChosen(lines, chosen) {
    const block = new Map();
    blocksOf(lines).forEach((b, n) => b.forEach((i) => block.set(i, n)));
    const out = [];
    lines.forEach((l, i) => {
        const take = !block.has(i) || chosen.has(block.get(i));
        if (l.kind === ' ' || (l.kind === '+' && take) || (l.kind === '-' && !take))
            out.push(l.text);
    });
    return out.join('\n');
}

/**
 * The words that changed between a removed line and the added line that replaces it: the part between their common start and their common
 * end, widened to whole words; null when the lines have nothing in common.
 */
export function changedSpan(oldLine, newLine) {
    let head = 0;
    while (head < oldLine.length && head < newLine.length && oldLine[head] === newLine[head])
        head++;
    let tail = 0;
    while (tail < oldLine.length - head && tail < newLine.length - head && oldLine[oldLine.length - 1 - tail] === newLine[newLine.length - 1 - tail])
        tail++;
    if (head === 0 && tail === 0)
        return null;
    const word = /[\w]/;
    while (head > 0 && word.test(oldLine[head - 1]) && word.test(newLine[head - 1] ?? ''))
        head--;
    while (tail > 0 && word.test(oldLine[oldLine.length - tail] ?? '') && word.test(newLine[newLine.length - tail] ?? ''))
        tail--;
    return { old: [head, oldLine.length - tail], new: [head, newLine.length - tail] };
}

/** For each line of a change, the span of it that differs from its partner (the n-th removed line with the n-th added one of the block). */
export function wordSpans(lines) {
    const spans = new Map();
    for (const b of blocksOf(lines)) {
        const removed = b.filter((i) => lines[i].kind === '-'), added = b.filter((i) => lines[i].kind === '+');
        for (let k = 0; k < Math.min(removed.length, added.length); k++) {
            const s = changedSpan(lines[removed[k]].text, lines[added[k]].text);
            if (!s)
                continue;
            spans.set(removed[k], s.old);
            spans.set(added[k], s.new);
        }
    }
    return spans;
}

const store = {
    get always() { try {
        return localStorage.getItem('pm.previewAlways') === '1';
    }
    catch {
        return false;
    } },
    set always(v) { try {
        localStorage.setItem('pm.previewAlways', v ? '1' : '0');
    }
    catch { /* not available */ } },
};
export const previewAlways = () => store.always;
/**
 * Shows the changes and resolves true when the user applies them all, false when cancelled, and { texts } when only some are chosen:
 * for each file (by its index in changes) the text it gets, null for a file left as it is.
 */
export function previewChanges(title, changes, warnings = []) {
    return new Promise((resolve) => {
        const overlay = document.createElement('div');
        overlay.id = 'sigdialog';
        overlay.className = 'preview';
        const box = document.createElement('div');
        box.className = 'box';
        overlay.appendChild(box);
        const h = document.createElement('h2');
        const added = { n: 0 }, removed = { n: 0 };
        const sections = [];
        // what is chosen: for each file the changes (blocks) that are to be made, all of them at first
        const files = [];
        for (const change of changes) {
            const lines = diffLines(change.oldText, change.newText);
            for (const l of lines) {
                if (l.kind === '+')
                    added.n++;
                else if (l.kind === '-')
                    removed.n++;
            }
            const blocks = blocksOf(lines);
            const blockOf = new Map();
            blocks.forEach((b, n) => b.forEach((i) => blockOf.set(i, n)));
            const file = { lines, blocks, chosen: new Set(blocks.map((_, n) => n)), boxes: [] };
            files.push(file);
            const spans = wordSpans(lines);
            const section = document.createElement('div');
            section.className = 'pfile';
            const name = document.createElement('label');
            name.className = 'pname';
            const fileBox = document.createElement('input');
            fileBox.type = 'checkbox';
            fileBox.checked = true;
            fileBox.title = 'Change this file';
            const fileAdded = lines.filter((l) => l.kind === '+').length, fileRemoved = lines.filter((l) => l.kind === '-').length;
            name.append(fileBox, ` ${change.name}   +${fileAdded} −${fileRemoved}`);
            section.appendChild(name);
            const pre = document.createElement('pre');
            const sync = () => {
                fileBox.checked = file.chosen.size > 0;
                fileBox.indeterminate = file.chosen.size > 0 && file.chosen.size < blocks.length;
                for (const [n, box] of file.boxes) box.checked = file.chosen.has(n);
                pre.classList.toggle('skipped', file.chosen.size === 0);
            };
            fileBox.onchange = () => {
                file.chosen = fileBox.checked ? new Set(blocks.map((_, n) => n)) : new Set();
                sync();
            };
            for (const hunk of hunksOf(lines)) {
                const head = document.createElement('div');
                head.className = 'hunk';
                head.textContent = `@@ line ${hunk.oldStart} @@`;
                pre.appendChild(head);
                hunk.lines.forEach((l, k) => {
                    const at = hunk.from + k;
                    const row = document.createElement('div');
                    row.className = l.kind === '+' ? 'add' : l.kind === '-' ? 'del' : 'ctx';
                    // the first line of each change carries the box that chooses it
                    const n = blockOf.get(at);
                    if (n !== undefined && blocks[n][0] === at && blocks.length > 1) {
                        const box = document.createElement('input');
                        box.type = 'checkbox';
                        box.checked = true;
                        box.title = 'Make this change';
                        box.className = 'pchoose';
                        box.onchange = () => {
                            if (box.checked)
                                file.chosen.add(n);
                            else
                                file.chosen.delete(n);
                            sync();
                        };
                        file.boxes.push([n, box]);
                        row.appendChild(box);
                    }
                    const span = spans.get(at);
                    const mark = (l.kind === ' ' ? '  ' : l.kind + ' ');
                    if (span) {
                        const word = document.createElement('span');
                        word.className = 'word';
                        word.textContent = l.text.slice(span[0], span[1]);
                        row.append(mark + l.text.slice(0, span[0]), word, l.text.slice(span[1]));
                    }
                    else
                        row.append(mark + l.text);
                    pre.appendChild(row);
                });
            }
            section.appendChild(pre);
            sections.push(section);
        }
        /** What Apply resolves with: true for everything, otherwise each file's text with only its chosen changes. */
        const outcome = () => {
            if (files.every((f) => f.chosen.size === f.blocks.length))
                return true;
            return { texts: files.map((f, i) => (f.chosen.size === 0 ? null : f.chosen.size === f.blocks.length ? changes[i].newText : textWithChosen(f.lines, f.chosen))) };
        };
        h.textContent = `${title} — ${changes.length} file${changes.length === 1 ? '' : 's'}, +${added.n} −${removed.n}`;
        box.appendChild(h);
        if (warnings.length) {
            const w = document.createElement('div');
            w.className = 'warnings';
            for (const text of warnings) {
                const p = document.createElement('div');
                p.textContent = '⚠ ' + text;
                w.appendChild(p);
            }
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
        const finish = (ok) => { store.always = check.checked; overlay.remove(); resolve(ok); };
        cancel.onclick = () => finish(false);
        apply.onclick = () => finish(outcome());
        overlay.onkeydown = (e) => {
            if (e.key === 'Escape') {
                e.preventDefault();
                finish(false);
            }
            else if (e.key === 'Enter' && !(e.target instanceof HTMLInputElement)) {
                e.preventDefault();
                finish(outcome());
            }
        };
        overlay.onmousedown = (e) => { if (e.target === overlay)
            finish(false); };
        document.body.appendChild(overlay);
        apply.focus();
    });
}

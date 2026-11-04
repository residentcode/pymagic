// Files changed on disk by something else than the page (a script in the terminal, another editor, git on the command line): Explorer shows
// what was added or removed, an open file without unsaved changes takes the new text, and one with unsaved changes asks which to keep.
// The host tells each change at /api/watch as `kind|path`; they are taken in batches, so a tool that writes many files causes one refresh.

/** Splits `created|pkg/a.py` into its kind and path. */
export function parseEvent(text) {
    const bar = text.indexOf('|');
    return bar < 0 ? { kind: 'changed', path: text } : { kind: text.slice(0, bar), path: text.slice(bar + 1) };
}

/** What a batch of changes asks for: whether the tree must be read again (something was added or removed), and which open files to look at. */
export function planBatch(events, openFiles) {
    const all = events.some((e) => e.kind === 'all');
    const open = new Set(openFiles);
    const files = all ? [...open] : [...new Set(events.map((e) => e.path))].filter((p) => open.has(p));
    // a file whose content changed is already in the tree: only additions and removals change it
    const tree = all || events.some((e) => e.kind !== 'changed');
    return { tree, files };
}

/**
 * deps: openFiles() (paths with an editor), refreshTree() (Explorer and the engine's list), diskText(rel) (null when gone), savedText(rel),
 * currentText(rel), takeDisk(rel, text) (the editor shows the disk's text, unsaved changes are dropped), ask(rel) -> 'disk' | 'mine'.
 */
export function setupDiskWatch(deps) {
    if (typeof EventSource === 'undefined') return { stop() { } };
    const source = new EventSource('/api/watch');
    let batch = [];
    let timer = null;
    const declined = new Map();   // rel -> the disk text the user chose to keep their own over: not asked again for the same text
    let busy = Promise.resolve();

    async function handle(events) {
        const { tree, files } = planBatch(events, deps.openFiles());
        if (tree) await deps.refreshTree();
        for (const rel of files) {
            const disk = await deps.diskText(rel);
            if (disk === null) continue;                  // removed: the tree refresh took care of it
            const saved = deps.savedText(rel), mine = deps.currentText(rel);
            if (disk === saved) continue;                  // our own save, or nothing new
            if (mine === saved || mine === disk) { deps.takeDisk(rel, disk); continue; }   // nothing of the user's would be lost
            if (declined.get(rel) === disk) continue;
            if ((await deps.ask(rel)) === 'disk') deps.takeDisk(rel, disk);
            else declined.set(rel, disk);
        }
    }

    source.onmessage = (e) => {
        let event;
        try { event = parseEvent(JSON.parse(e.data)); } catch { return; }
        // not a file: the host took an environment just made in the project as the interpreter (its label)
        if (event.kind === 'interpreter' || event.kind === 'environment') { window.dispatchEvent(new CustomEvent('pymagic:interpreter-taken', { detail: { kind: event.kind, text: event.path } })); return; }
        batch.push(event);
        clearTimeout(timer);
        timer = setTimeout(() => {
            const events = batch;
            batch = [];
            busy = busy.then(() => handle(events)).catch(() => { });
        }, 300);
    };
    return { stop() { source.close(); } };
}

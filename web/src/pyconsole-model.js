// The pure parts of the Python console: the names of several consoles, and the queue of commands that wait while one runs.

/** The name for a new console: "Python console", then "Python console (2)", the lowest number not used among `names`. */
export function consoleName(names, base = 'Python console') {
    if (!names.includes(base)) return base;
    for (let n = 2; ; n++)
        if (!names.includes(`${base} (${n})`)) return `${base} (${n})`;
}

/** The first line of a command, shortened, as the queue shows it. */
export function queueLabel(code, max = 40) {
    const first = code.split('\n').find((l) => l.trim()) ?? '';
    const text = first.trim();
    const more = code.trim().includes('\n') ? ' …' : '';
    return (text.length > max ? text.slice(0, max - 1) + '…' : text) + more;
}

/**
 * Commands for one console, run one at a time in the order they were given. `run(item)` does one and returns a promise; items still
 * waiting can be taken out, and `clear` drops them all (their `onCancel` is called). `onChange` is told when the line-up changes.
 */
export class CommandQueue {
    constructor(run, onChange = () => { }) {
        this.run = run;
        this.onChange = onChange;
        this.waiting = [];
        this.current = null;
        this.stopped = false;
    }

    get busy() { return this.current !== null; }

    /** Adds a command ({ code, ... }); the promise is what `run` answers for it, or null when it was taken out. */
    add(item) {
        return new Promise((resolve) => {
            this.waiting.push({ ...item, resolve });
            this.onChange();
            void this.pump();
        });
    }

    async pump() {
        if (this.current || this.pumping) return;
        this.pumping = true;
        try {
            while (this.waiting.length) {
                this.current = this.waiting.shift();
                this.onChange();
                let answer = null;
                try { answer = await this.run(this.current); }
                catch { answer = null; }     // a console that went away: the commands behind it still get their turn (and fail the same way)
                this.current.resolve(answer ?? null);
                this.current = null;
            }
        }
        finally { this.pumping = false; this.onChange(); }
    }

    /** Takes the waiting command at `index` out; true when there was one. */
    remove(index) {
        if (index < 0 || index >= this.waiting.length) return false;
        const [gone] = this.waiting.splice(index, 1);
        gone.resolve(null);
        this.onChange();
        return true;
    }

    /** Drops every command that is waiting (the one running is not touched). */
    clear() {
        const gone = this.waiting.splice(0);
        for (const g of gone) g.resolve(null);
        if (gone.length) this.onChange();
    }
}

/** The console's history as the list of Ctrl+Alt+E shows it: the newest first, each command once, only the ones that have `filter` in them (any case). */
/** How many commands a console's history keeps (each once, the newest last). */
export const HISTORY_MAX = 200;

/** The history with this command last (once; an empty one is not kept), at most HISTORY_MAX long. */
export function withCommand(history, code, max = HISTORY_MAX) {
    if (!code.trim()) return history;
    return [...history.filter((h) => h !== code), code].slice(-max);
}

/** The kind of a console whose history it shares: the Python console's and the Django console's are kept apart, as PyCharm keeps them. */
export function historyKind(console) {
    return console.django ? 'django' : 'python';
}

/** The kept histories (.pymagic/console-history.json) as they are read back: lists of strings by kind, anything else left out. */
export function keptHistories(data) {
    const out = { python: [], django: [] };
    if (!data || typeof data !== 'object') return out;
    for (const kind of Object.keys(out))
        if (Array.isArray(data[kind])) out[kind] = data[kind].filter((h) => typeof h === 'string' && h.trim()).slice(-HISTORY_MAX);
    return out;
}

export function historyMatches(history, filter = '') {
    const want = filter.trim().toLowerCase();
    const seen = new Set();
    const out = [];
    for (let i = history.length - 1; i >= 0; i--) {
        const h = history[i];
        if (!h.trim() || seen.has(h)) continue;
        seen.add(h);
        if (!want || h.toLowerCase().includes(want)) out.push(h);
    }
    return out;
}

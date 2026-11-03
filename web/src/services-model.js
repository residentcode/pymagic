// The Services window without the page: how long something has been running, in words. web/test/services.test.mjs checks it.

/** "12 s", "3 min", "2 h 5 min". */
export function elapsed(ms) {
    const s = Math.max(0, Math.round(ms / 1000));
    if (s < 60) return `${s} s`;
    const m = Math.floor(s / 60);
    if (m < 60) return `${m} min`;
    return `${Math.floor(m / 60)} h ${m % 60} min`;
}

/** A service's own output with more text added: the colour codes of a terminal taken out, and only the last `limit` characters kept. */
export function withOutput(kept, more, limit = 200000) {
    return ((kept ?? '') + String(more ?? '').replace(/\x1b\[[0-9;?]*[A-Za-z]/g, '')).slice(-limit);
}

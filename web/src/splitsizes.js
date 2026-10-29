// The shares of the editors of a split (the first one and each side). No DOM and no imports, so web/test/splitsizes.test.mjs can run it under Node.

/** `n` shares from what was kept: missing ones are the average of the others (1 when there are none), wrong ones are 1, extra ones dropped. */
export function splitWeights(kept, n) {
    const good = (Array.isArray(kept) ? kept : []).slice(0, n).map((w) => (Number.isFinite(w) && w > 0 ? w : 1));
    const fill = good.length ? good.reduce((a, b) => a + b, 0) / good.length : 1;
    while (good.length < n) good.push(fill);
    return good;
}

/** The divider between group `i` and group `i + 1` moved: the first takes `share` of the two (at least 15% each), the others keep theirs. */
export function resizePair(weights, i, share) {
    if (i < 0 || i + 1 >= weights.length || !Number.isFinite(share)) return weights;
    const out = [...weights];
    const pair = out[i] + out[i + 1];
    const s = Math.min(0.85, Math.max(0.15, share));
    out[i] = pair * s;
    out[i + 1] = pair * (1 - s);
    return out;
}

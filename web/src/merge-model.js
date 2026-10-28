// The merge editor without the page: finding the conflicts a merge left in a file, and settling one of them. The text is the whole state,
// so a conflict settled here and one settled by hand in the result are the same thing to the rest of the code.

const OPEN = /^<{7}(?: (.*))?$/;
const BASE = /^\|{7}(?: (.*))?$/;
const MIDDLE = /^={7}$/;
const CLOSE = /^>{7}(?: (.*))?$/;

/** The lines of a text with their line endings kept, and the offset each one starts at. */
function linesOf(text) {
    const lines = [];
    let at = 0;
    while (at < text.length) {
        let end = text.indexOf('\n', at);
        end = end < 0 ? text.length : end + 1;
        lines.push({ start: at, end, body: text.slice(at, end).replace(/\r?\n$/, '') });
        at = end;
    }
    return lines;
}

/**
 * The conflicts in `text`, in order: `{ start, end, ours, theirs, base, oursLabel, theirsLabel }`. `start` and `end` cover the whole block, markers
 * included; `ours`, `theirs` and `base` (null unless the file was written in diff3 style) are the lines between the markers with their endings.
 * A block that is never closed, or has no middle line, is not a conflict.
 */
export function findConflicts(text) {
    const lines = linesOf(text);
    const found = [];
    for (let i = 0; i < lines.length; i++) {
        const open = OPEN.exec(lines[i].body);
        if (!open) continue;
        let base = null, middle = -1, baseAt = -1, close = -1;
        for (let j = i + 1; j < lines.length; j++) {
            const b = lines[j].body;
            if (OPEN.test(b)) break; // another one begins: this one was never closed
            if (middle < 0 && baseAt < 0 && BASE.test(b)) baseAt = j;
            else if (middle < 0 && MIDDLE.test(b)) middle = j;
            else if (middle >= 0 && CLOSE.test(b)) { close = j; break; }
        }
        if (close < 0) continue;
        const slice = (from, to) => text.slice(lines[from].start, lines[to].start);
        const oursEnd = baseAt >= 0 ? baseAt : middle;
        if (baseAt >= 0) base = slice(baseAt + 1, middle);
        found.push({
            start: lines[i].start,
            end: lines[close].end,
            ours: slice(i + 1, oursEnd),
            theirs: slice(middle + 1, close),
            base,
            oursLabel: open[1] ?? '',
            theirsLabel: CLOSE.exec(lines[close].body)[1] ?? '',
        });
        i = close;
    }
    return found;
}

/** What a conflict becomes for each way of settling it: 'ours', 'theirs', both in either order, or the common ancestor when the file has one. */
export function chosen(conflict, how) {
    switch (how) {
        case 'ours': return conflict.ours;
        case 'theirs': return conflict.theirs;
        case 'both': return conflict.ours + conflict.theirs;
        case 'bothReversed': return conflict.theirs + conflict.ours;
        case 'base': return conflict.base ?? null;
        default: return null;
    }
}

/** `text` with conflict number `index` settled; the text itself when there is no such conflict or no such way. */
export function settle(text, index, how) {
    const c = findConflicts(text)[index];
    const replacement = c ? chosen(c, how) : null;
    return replacement === null ? text : text.slice(0, c.start) + replacement + text.slice(c.end);
}

/** `text` with every conflict settled the same way (those that cannot be are left as they are). */
export function settleAll(text, how) {
    let out = text;
    // from the end, so the offsets of the ones before it stay true
    for (const c of findConflicts(text).reverse()) {
        const replacement = chosen(c, how);
        if (replacement !== null) out = out.slice(0, c.start) + replacement + out.slice(c.end);
    }
    return out;
}

/** The first line (from 1) of each conflict of `text`, for moving between them. */
export function conflictLines(text) {
    const starts = findConflicts(text).map((c) => c.start);
    let line = 1, at = 0;
    return starts.map((s) => {
        for (; at < s; at++) if (text.charCodeAt(at) === 10) line++;
        return line;
    });
}

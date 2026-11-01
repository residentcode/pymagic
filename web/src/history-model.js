// Local history without the page: how a version's time is told.

const pad = (n) => String(n).padStart(2, '0');

/** "just now", "3 minutes ago", "2 hours ago", "yesterday", "5 days ago": how long before `now` the version was made. */
export function ago(now, then) {
    const s = Math.max(0, Math.round((now - then) / 1000));
    if (s < 45) return 'just now';
    const m = Math.round(s / 60);
    if (m < 60) return m === 1 ? 'a minute ago' : `${m} minutes ago`;
    const h = Math.round(m / 60);
    if (h < 24) return h === 1 ? 'an hour ago' : `${h} hours ago`;
    const d = Math.round(h / 24);
    return d === 1 ? 'yesterday' : `${d} days ago`;
}

/** The time of day, and the day when it is not today: "14:32:07", "yesterday 09:15:00", "3 Mar 18:02:44". */
export function clock(now, then) {
    const a = new Date(now), b = new Date(then);
    const time = `${pad(b.getHours())}:${pad(b.getMinutes())}:${pad(b.getSeconds())}`;
    const same = (x, y) => x.getFullYear() === y.getFullYear() && x.getMonth() === y.getMonth() && x.getDate() === y.getDate();
    if (same(a, b)) return time;
    const yesterday = new Date(a.getFullYear(), a.getMonth(), a.getDate() - 1);
    if (same(yesterday, b)) return `yesterday ${time}`;
    const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    return `${b.getDate()} ${months[b.getMonth()]}${a.getFullYear() === b.getFullYear() ? '' : ' ' + b.getFullYear()} ${time}`;
}

/** "812 bytes", "3.4 KB". */
export function size(bytes) {
    return bytes < 1024 ? `${bytes} bytes` : `${(bytes / 1024).toFixed(1)} KB`;
}

/**
 * The lines of two texts that match, as [index in a, index in b] pairs in order: the common start and end first, the longest common
 * run of lines between them after (not tried when both middles are very long: the start and end alone are then all that is known).
 */
export function matchLines(a, b) {
    let head = 0;
    while (head < a.length && head < b.length && a[head] === b[head]) head++;
    let tail = 0;
    while (tail < a.length - head && tail < b.length - head && a[a.length - 1 - tail] === b[b.length - 1 - tail]) tail++;
    const pairs = [];
    for (let i = 0; i < head; i++) pairs.push([i, i]);
    const n = a.length - head - tail, m = b.length - head - tail;
    if (n > 0 && m > 0 && n * m <= 16_000_000) {
        // table[i][j]: the length of the common run of a[head+i..] and b[head+j..]
        const table = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1));
        for (let i = n - 1; i >= 0; i--)
            for (let j = m - 1; j >= 0; j--)
                table[i][j] = a[head + i] === b[head + j] ? table[i + 1][j + 1] + 1 : Math.max(table[i + 1][j], table[i][j + 1]);
        let i = 0, j = 0;
        while (i < n && j < m) {
            if (a[head + i] === b[head + j]) { pairs.push([head + i, head + j]); i++; j++; }
            else if (table[i + 1][j] >= table[i][j + 1]) i++;
            else j++;
        }
    }
    for (let k = tail; k > 0; k--) pairs.push([a.length - k, b.length - k]);
    return pairs;
}

/**
 * The lines of an older text that stand where lines `start` to `end` (0-based, inclusive) of the current one are: a changed line takes in
 * what lies between the lines on both sides of it that did not change. {from, to} (to is not included).
 */
export function oldRange(pairs, start, end, oldLength) {
    const map = new Map(pairs);
    let from = 0, to = oldLength;
    if (map.has(start)) from = map.get(start);
    else for (let i = start - 1; i >= 0; i--) if (map.has(i)) { from = map.get(i) + 1; break; }
    if (map.has(end)) to = map.get(end) + 1;
    else for (let i = end + 1; ; i++) { if (map.has(i)) { to = map.get(i); break; } if (i > end + pairs.length + oldLength + 2) break; }
    return { from, to: Math.max(from, to) };
}

/**
 * The versions in which the chosen lines (0-based, inclusive) of the current text were different from the next newer one: the first
 * is compared with the lines as they are now. `versions` are {id, time, text}, newest first. Gives {id, time, text} of just the lines.
 */
export function selectionHistory(currentText, start, end, versions) {
    const current = currentText.split('\n');
    let previous = current.slice(start, end + 1).join('\n');
    const found = [];
    for (const v of versions) {
        const lines = v.text.split('\n');
        const { from, to } = oldRange(matchLines(current, lines), start, end, lines.length);
        const text = lines.slice(from, to).join('\n');
        if (text !== previous) found.push({ id: v.id, time: v.time, text, previous });
        previous = text;
    }
    return found;
}

/** Versions and labels in one list, newest first: [{type: 'version', ...}, {type: 'label', ...}] (a label is put after the versions it knows). */
export function interleave(versions, labels) {
    return [...versions.map((v) => ({ type: 'version', ...v })), ...labels.map((l) => ({ type: 'label', ...l }))]
        .sort((x, y) => (x.id < y.id ? 1 : x.id > y.id ? -1 : x.type === 'label' ? -1 : 1));
}

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

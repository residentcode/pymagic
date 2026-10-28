// The files opened lately in this project, newest first (File > Recent files). They are kept in the project, in .pymagic/recent.json, so that they come
// back with it whatever the address of the page.
const FILE = '.pymagic/recent.json';
const MAX = 20;

export function setupRecent({ exists }) {
    let list = [];
    let timer = 0;
    const save = () => {
        window.clearTimeout(timer);
        timer = window.setTimeout(() => {
            fetch('/api/file?path=' + encodeURIComponent(FILE), { method: 'PUT', body: JSON.stringify(list) }).catch(() => { /* not remembered this time */ });
        }, 800);
    };
    fetch('/api/file?path=' + encodeURIComponent(FILE)).then((r) => (r.ok ? r.json() : [])).then((stored) => {
        if (Array.isArray(stored)) list = [...new Set([...list, ...stored.filter((f) => typeof f === 'string')])].slice(0, MAX);
    }).catch(() => { /* nothing remembered */ });
    return {
        opened(rel) {
            if (!rel || rel.startsWith('.pymagic/')) return;
            list = [rel, ...list.filter((f) => f !== rel)].slice(0, MAX);
            save();
        },
        /** The recent files that still exist, the open one left out. */
        list: (current) => list.filter((f) => f !== current && exists(f)),
    };
}

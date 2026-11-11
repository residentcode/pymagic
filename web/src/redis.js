// Redis in the Database panel (/api/redis, RedisClient.cs): a server's keys in the tree (SCAN with a pattern, each with its type), a key's
// value in the grid whatever its type (text, a hash's fields, a list's, set's or sorted set's items, a stream's last entries) with its TTL,
// changes to it (a value, a field, an item, the TTL, its name, removing it), and Redis commands typed in the console.
import { toast } from './toast';
import { openMenu } from './menu';

async function ask(request) {
    const res = await fetch('/api/redis', { method: 'POST', body: JSON.stringify(request) });
    if (!res.ok)
        return { error: (await res.text()) || `HTTP ${res.status}` };
    return res.json();
}

/** A reply of Redis as redis-cli shows it: "text", (integer) 3, (nil), numbered lines for an array. */
export function replyText(reply, indent = '') {
    if (reply === null || reply === undefined)
        return '(nil)';
    if (typeof reply === 'number')
        return `(integer) ${reply}`;
    if (typeof reply === 'string')
        return JSON.stringify(reply);
    if (Array.isArray(reply)) {
        if (!reply.length)
            return '(empty array)';
        const width = String(reply.length).length;
        return reply.map((r, i) => {
            const head = `${String(i + 1).padStart(width)}) `;
            return (i ? indent : '') + head + replyText(r, indent + ' '.repeat(head.length));
        }).join('\n');
    }
    return JSON.stringify(reply);
}

/** Whether a console line holds the connection (the server sends to it from then on): it is run on a connection of its own. */
export const holdsConnection = (line) => /^\s*(p|s)?subscribe\s|^\s*monitor\s*$/i.test(line);
/** How many lines a held command's view keeps (the latest). */
const LIVE_LINES = 2000;

/** The lines of the console to run: the selection's, the cursor's, or all of them; empty lines and # comments left out. */
export function commandLines(text) {
    return text.split(/\r?\n/).map((l) => l.trim()).filter((l) => l && !l.startsWith('#'));
}

export function setupRedis({ grid, status, node, paintTree }) {
    const keys = new Map();      // path -> { pattern, list: [{key, type}], cursor, total }
    const loading = new Set();

    async function loadKeys(path, pattern) {
        const was = keys.get(path);
        const p = pattern ?? was?.pattern ?? '*';
        loading.add(path);
        const r = await ask({ op: 'keys', path, pattern: p });
        loading.delete(path);
        if (r.error) {
            toast(r.error);
            keys.set(path, { pattern: p, list: [], cursor: '0', total: 0, error: r.error });
        }
        else
            keys.set(path, { pattern: p, list: r.keys, cursor: r.cursor, total: r.total });
        paintTree();
    }

    /** The keys of a Redis server under its node in the tree: a pattern to filter them, then each key by its type. */
    function paintKeys(path, depth) {
        const state = keys.get(path);
        if (!state) {
            if (!loading.has(path))
                void loadKeys(path);
            node('…', 'db-empty', 'reading the keys', depth, null);
            return;
        }
        const filter = node('', 'db-redis-filter', '', depth, null);
        const box = document.createElement('input');
        box.className = 'db-redis-pattern';
        box.placeholder = 'Pattern: user:*  (Enter)';
        box.value = state.pattern === '*' ? '' : state.pattern;
        box.title = 'The keys that match this pattern (Redis\'s MATCH: * any text, ? one character, [ab] one of them)';
        box.onkeydown = (e) => { e.stopPropagation(); if (e.key === 'Enter') void loadKeys(path, box.value.trim() || '*'); };
        box.onclick = (e) => e.stopPropagation();
        filter.replaceChildren(box);
        const more = state.cursor !== '0' ? ` · the first ${state.list.length}` : '';
        if (!state.list.length)
            node(state.error ? 'could not read the keys' : 'no key', 'db-empty', state.error ?? `matches ${state.pattern} of ${state.total ?? 0}`, depth, null);
        for (const k of state.list) {
            const row = node(k.key, 'db-redis-key db-redis-' + k.type, k.type, depth, null, () => void showKey(path, k.key));
            row.title = `A ${k.type}: double-click to see its value; right-click to rename or delete it`;
            row.oncontextmenu = (e) => {
                e.preventDefault();
                openMenu({ x: e.clientX, y: e.clientY }, [
                    { label: 'Show its value', run: () => void showKey(path, k.key) },
                    { label: 'Rename…', run: () => void rename(path, k.key) },
                    '-',
                    { label: 'Delete', run: () => void remove(path, k.key) },
                ]);
            };
        }
        if (more)
            node(`${state.list.length} of ${state.total ?? '?'} keys`, 'db-empty', 'narrow the pattern to see others', depth, null);
    }

    async function change(path, c, again = true) {
        const r = await ask({ op: 'change', path, change: c });
        if (r.error) {
            toast(r.error);
            return false;
        }
        if (again)
            await showKey(path, c.op === 'rename' ? c.to : c.key);
        return true;
    }
    async function rename(path, key) {
        const to = prompt(`Rename ${key} to:`, key);
        if (!to || to === key)
            return;
        if (await change(path, { op: 'rename', key, to }))
            await loadKeys(path);
    }
    async function remove(path, key) {
        if (!confirm(`Delete the key ${key}? Redis keeps no copy of it.`))
            return;
        if (await change(path, { op: 'delete', key }, false)) {
            grid.textContent = '';
            status.textContent = `${key} deleted.`;
            await loadKeys(path);
        }
    }

    /** A key's value in the grid: its type, size and TTL above, and what it holds, changeable where Redis lets it be. */
    async function showKey(path, key) {
        const r = await ask({ op: 'get', path, key });
        status.className = r.error ? 'err' : '';
        if (r.error) {
            status.textContent = r.error;
            return;
        }
        grid.textContent = '';
        const head = document.createElement('div');
        head.className = 'db-redis-head';
        const ttl = r.ttl === -1 ? 'no expiry' : r.ttl === -2 ? 'gone' : `expires in ${r.ttl} s`;
        head.textContent = `${r.key} · ${r.type}${r.type === 'string' ? '' : ` · ${r.size} item${r.size === 1 ? '' : 's'}`} · ${ttl}`;
        const ttlButton = document.createElement('button');
        ttlButton.textContent = 'TTL…';
        ttlButton.title = 'Seconds until Redis removes the key; empty for never';
        ttlButton.onclick = async () => {
            const v = prompt(`Seconds until ${key} expires (empty: never):`, r.ttl > 0 ? String(r.ttl) : '');
            if (v !== null)
                await change(path, { op: 'ttl', key, ttl: v.trim() || '-1' });
        };
        const del = document.createElement('button');
        del.textContent = 'Delete';
        del.onclick = () => void remove(path, key);
        head.append(' ', ttlButton, del);
        grid.append(head);
        status.textContent = '';
        if (r.type === 'none') {
            status.textContent = `There is no key ${key} (any more).`;
            return;
        }
        if (r.type === 'string') {
            const text = document.createElement('textarea');
            text.className = 'db-redis-text';
            text.spellcheck = false;
            text.value = r.value ?? '';
            const save = document.createElement('button');
            save.textContent = 'Save';
            save.className = 'primary';
            save.onclick = () => void change(path, { op: 'set', key, value: text.value });
            grid.append(text, save);
            return;
        }
        // the other types as a table: a row per field or item, each changeable, one added at the end
        const table = document.createElement('table');
        table.className = 'db-table db-redis-table';
        const columns = { hash: ['field', 'value'], list: ['index', 'value'], set: ['member'], zset: ['member', 'score'], stream: ['id', 'fields'] }[r.type] ?? ['value'];
        const thead = table.createTHead().insertRow();
        for (const c of [...columns, ''])
            thead.insertCell().textContent = c;
        const body = table.createTBody();
        const rows = r.type === 'hash' ? Object.entries(r.value ?? {}).map(([f, v]) => [f, v])
            : r.type === 'list' ? (r.value ?? []).map((v, i) => [String(i), v])
                : r.type === 'set' ? (r.value ?? []).map((v) => [v])
                    : r.type === 'zset' ? (r.value ?? []).map((m) => [m.member, m.score])
                        : r.type === 'stream' ? (r.value ?? []).map(([id, fields]) => [id, JSON.stringify(Object.fromEntries((fields ?? []).reduce((a, x, i, all) => (i % 2 ? a : [...a, [x, all[i + 1]]]), [])))])
                            : [];
        for (const values of rows) {
            const tr = body.insertRow();
            values.forEach((v, i) => {
                const td = tr.insertCell();
                td.textContent = v ?? '';
                // a hash's value and a list's item are changed in place (a double-click); a member is removed and added again
                if ((r.type === 'hash' || r.type === 'list') && i === 1) {
                    td.title = 'Double-click to change it';
                    td.ondblclick = async () => {
                        const nv = prompt(r.type === 'hash' ? `The value of ${values[0]}:` : `Item ${values[0]}:`, v ?? '');
                        if (nv !== null)
                            await change(path, r.type === 'hash' ? { op: 'hset', key, field: values[0], value: nv } : { op: 'lset', key, index: values[0], value: nv });
                    };
                }
            });
            const x = tr.insertCell();
            if (r.type !== 'stream') {
                const rm = document.createElement('button');
                rm.className = 'mini';
                rm.textContent = '✕';
                rm.title = 'Remove it from the key';
                rm.onclick = () => void change(path, r.type === 'hash' ? { op: 'hdel', key, field: values[0] } : { op: { list: 'lrem', set: 'srem', zset: 'zrem' }[r.type], key, value: r.type === 'list' ? values[1] : values[0] });
                x.append(rm);
            }
        }
        grid.append(table);
        if (r.type !== 'stream') {
            const add = document.createElement('button');
            add.textContent = { hash: 'Add a field…', list: 'Add an item at the end…', set: 'Add a member…', zset: 'Add a member…' }[r.type];
            add.onclick = async () => {
                if (r.type === 'hash') {
                    const field = prompt('Field:');
                    if (!field)
                        return;
                    const value = prompt(`The value of ${field}:`, '');
                    if (value !== null)
                        await change(path, { op: 'hset', key, field, value });
                }
                else if (r.type === 'zset') {
                    const value = prompt('Member:');
                    const score = value ? prompt(`The score of ${value}:`, '0') : null;
                    if (value && score !== null)
                        await change(path, { op: 'zadd', key, value, score });
                }
                else {
                    const value = prompt(r.type === 'list' ? 'Item:' : 'Member:');
                    if (value !== null && value !== '')
                        await change(path, { op: r.type === 'list' ? 'rpush' : 'sadd', key, value });
                }
            };
            grid.append(add);
        }
        if (r.size > rows.length)
            status.textContent = `The first ${rows.length} of ${r.size}.`;
    }

    // a command that holds the connection (SUBSCRIBE, MONITOR): its own connection (/api/redis/live), each message as it comes, until Stop
    let live = null;
    const stopLive = () => { if (live?.readyState === WebSocket.OPEN) live.send(JSON.stringify({ type: 'stop' })); };
    function listen(path, line, pre, out) {
        stopLive();
        const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
        const ws = new WebSocket(`${scheme}://${location.host}/api/redis/live`);
        live = ws;
        const word = line.split(/\s+/)[0].toUpperCase();
        const paint = () => {
            pre.textContent = out.join('\n');
            pre.scrollTop = pre.scrollHeight;
        };
        const stop = Object.assign(document.createElement('button'), { textContent: 'Stop', title: `End ${word} (its connection is closed)` });
        stop.onclick = stopLive;
        status.className = '';
        status.replaceChildren(`${word}: connecting… `, stop);
        let messages = 0;
        ws.onopen = () => ws.send(JSON.stringify({ path, line }));
        ws.onmessage = (e) => {
            const m = JSON.parse(e.data);
            if (m.type === 'open')
                status.replaceChildren(`${word}: listening (messages show as they come) `, stop);
            else if (m.type === 'message') {
                messages++;
                out.push(m.text);
                // the latest lines kept: a busy MONITOR would grow without end
                if (out.length > LIVE_LINES)
                    out.splice(1, out.length - LIVE_LINES);
                paint();
                status.replaceChildren(`${word}: listening, ${messages} message${messages === 1 ? '' : 's'} `, stop);
            }
            else if (m.type === 'closed') {
                if (m.error)
                    out.push(`(error) ${m.error}`);
                paint();
                status.className = m.error ? 'err' : '';
                status.textContent = `${word} ended${m.error ? '' : ' (Stop)'}: ${messages} message${messages === 1 ? '' : 's'}`;
            }
        };
        ws.onclose = () => {
            if (live === ws)
                live = null;
        };
    }

    /** Commands typed in the console, one per line, each reply shown as redis-cli shows it; one that holds the connection is listened to. */
    async function run(path, text) {
        const lines = commandLines(text);
        if (!lines.length) {
            toast('No command: GET key, HGETALL key, SCAN 0 MATCH user:*, SUBSCRIBE channel, MONITOR, …');
            return;
        }
        stopLive();
        const out = [];
        let failed = false;
        status.className = '';
        status.textContent = 'Running…';
        const pre = document.createElement('pre');
        pre.className = 'db-redis-replies';
        for (const [i, line] of lines.entries()) {
            if (holdsConnection(line)) {
                out.push(`> ${line}`);
                if (i < lines.length - 1)
                    out.push(`(the ${lines.length - 1 - i} line${lines.length - 1 - i === 1 ? '' : 's'} after it not run: ${line.split(/\s+/)[0].toUpperCase()} holds its connection)`);
                grid.textContent = '';
                pre.textContent = out.join('\n');
                grid.append(pre);
                listen(path, line, pre, out);
                return;
            }
            const r = await ask({ op: 'command', path, line });
            out.push(`> ${line}`, r.error ? `(error) ${r.error}` : replyText(r.reply));
            if (r.error)
                failed = true;
        }
        grid.textContent = '';
        pre.textContent = out.join('\n');
        grid.append(pre);
        status.className = failed ? 'err' : '';
        status.textContent = `${lines.length} command${lines.length === 1 ? '' : 's'}${failed ? ', one failed' : ''}`;
        keys.delete(path);   // a command may have changed the keys
        paintTree();
    }

    return { paintKeys, showKey, run, stop: stopLive, forget: (path) => keys.delete(path) };
}

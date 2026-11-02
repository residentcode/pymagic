// Scratch files: files of any language kept outside the project (one folder of the user's, shared by every project), listed under the
// project's files, made with Ctrl+Alt+Shift+Insert, opened as `.scratches/name`. A Python scratch runs with the project's interpreter and
// resolves the project's modules like a file of the project (the server answers where `.scratches/` is).
import { ago } from './history-model.js';
import { openMenu } from './menu';
import { toast } from './toast';
import { defineCommand } from './commands.js';

const $ = (id) => document.getElementById(id);
export const PREFIX = '.scratches/';

export function setupScratches({ openFile, askText, forget, currentFile }) {
    let list = [];

    async function load() {
        list = await fetch('/api/scratches').then((r) => (r.ok ? r.json() : [])).catch(() => []);
        draw();
    }

    function draw() {
        const box = $('scratch-list');
        box.replaceChildren();
        if (!list.length) {
            const none = document.createElement('div');
            none.className = 'side-empty';
            none.textContent = 'No scratch files yet. + makes one: a file of any language, kept outside the project.';
            box.append(none);
            return;
        }
        const now = Date.now();
        for (const f of list) {
            const row = document.createElement('div');
            row.className = 'scratch-row' + (currentFile() === PREFIX + f.name ? ' current' : '');
            const name = document.createElement('span');
            name.className = 'scratch-name';
            name.textContent = f.name;
            const when = document.createElement('span');
            when.className = 'scratch-when';
            when.textContent = ago(now, Date.parse(f.modified));
            row.append(name, when);
            row.title = 'Kept outside the project · right-click: rename, delete';
            row.onclick = () => void openFile(PREFIX + f.name);
            row.oncontextmenu = (e) => { e.preventDefault(); menu(e, f); };
            box.append(row);
        }
    }

    function menu(e, f) {
        openMenu({ x: e.clientX, y: e.clientY }, [
            { label: 'Rename…', run: () => void rename(f) },
            { label: 'Delete', run: () => void remove(f) },
        ]);
    }

    async function remove(f) {
        if (!window.confirm(`Delete the scratch file ${f.name}?`)) return;
        const res = await fetch('/api/scratches?name=' + encodeURIComponent(f.name), { method: 'DELETE' });
        if (res.ok) { await forget(PREFIX + f.name); toast(`${f.name} is deleted.`); } else toast('It could not be deleted.', true);
        await load();
    }

    async function rename(f) {
        const to = await askText('Rename the scratch file', f.name, 'Keep the extension: it says the language');
        if (!to || to === f.name) return;
        const res = await fetch('/api/scratches/rename', { method: 'POST', body: JSON.stringify({ from: f.name, to }) });
        if (res.ok) await forget(PREFIX + f.name); else toast(await res.text(), true);
        await load();
    }

    /** A new scratch file: the name's extension is its language. */
    async function create(suggested = 'scratch.py', text) {
        const name = await askText('New scratch file', suggested, 'The extension is the language: .py .js .json .md .sql .html .http .yml …');
        if (!name?.trim()) return;
        const res = await fetch('/api/scratches', { method: 'POST', body: JSON.stringify({ name: name.trim(), text }) }).catch(() => null);
        if (!res?.ok) { toast(res ? await res.text() : 'The scratch file could not be made.', true); return; }
        const made = (await res.json()).name;
        await load();
        await openFile(PREFIX + made);
    }

    $('scratch-new').onclick = () => void create();
    defineCommand({ id: 'pymagic.scratch.new', label: 'New scratch file', group: 'Window', keys: ['Ctrl+Shift+Alt+Insert'], everywhere: true, run: () => void create() });
    window.addEventListener('pymagic:saved', () => void load());
    void load();
    return { load, create, isScratch: (rel) => rel.startsWith(PREFIX) };
}

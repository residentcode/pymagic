// The project tree in the sidebar: folders that open and close, every text file, and new / rename / delete.
import { toast } from './toast';
import { openMenu } from './menu';
import { copyName, join, moveProblem, movedPath } from './explorer-model';

const FILE_TYPE = 'application/x-pymagic-file';   // a row of the tree being dragged; the editor and the tab bars take nothing from it

const $ = (id) => document.getElementById(id);

// file kind -> the symbol of the icon sprite in index.html (and the class that colours it)
const ICONS = {
    py: ['py', ''], pyi: ['py', ''], html: ['web', 'web'], htm: ['web', 'web'], jinja: ['web', 'web'], j2: ['web', 'web'], css: ['web', 'web'], js: ['code', 'web'],
    json: ['data', 'data'], md: ['doc', 'doc'], rst: ['doc', 'doc'], txt: ['doc', 'doc'], pdf: ['doc', 'doc'],
    toml: ['cog', 'cog'], ini: ['cog', 'cog'], cfg: ['cog', 'cog'], yaml: ['cog', 'cog'], yml: ['cog', 'cog'], env: ['cog', 'cog'], http: ['web', 'web'], sql: ['data', 'data'],
    sh: ['shell', 'cog'], ps1: ['shell', 'cog'], bat: ['shell', 'cog'], cmd: ['shell', 'cog'],
    png: ['file', 'doc'], jpg: ['file', 'doc'], jpeg: ['file', 'doc'], gif: ['file', 'doc'], svg: ['file', 'doc'], ico: ['file', 'doc'],
    zip: ['cube', 'data'], whl: ['cube', 'data'], gz: ['cube', 'data'], exe: ['cube', 'cog'], dll: ['cube', 'cog'], pyd: ['cube', 'cog'], so: ['cube', 'cog'],
};

function sprite(name, cls = '') {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', `i ${cls}`.trim());
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', `#i-${name}`);
    svg.append(use);
    return svg;
}
const BINARY = /\.(png|jpe?g|gif|ico|bmp|webp|zip|whl|gz|tar|7z|rar|pdf|exe|dll|pyd|so|dylib|pyc|pyo|class|jar|woff2?|ttf|otf|eot|mp[34]|wav|avi|mov|sqlite3?|db|bin|dat|pkl|npy|npz|h5|pt|pth|onnx)$/i;

function iconOf(name) {
    const ext = name.includes('.') ? name.split('.').pop().toLowerCase() : '';
    if (name === 'requirements.txt' || name === 'Dockerfile' || name === '.gitignore') return ['cog', 'cog'];
    return ICONS[ext] ?? ['file', 'doc'];
}

function baseName(path) { return path.slice(path.lastIndexOf('/') + 1); }
function parentOf(path) { const i = path.lastIndexOf('/'); return i < 0 ? '' : path.slice(0, i); }

/** A small modal with one text box. Resolves to the text, or null when it is cancelled. */
function askText(title, initial = '', hint = '') {
    return new Promise((resolve) => {
        const shade = document.createElement('div');
        shade.className = 'ask-shade';
        const box = document.createElement('div');
        box.className = 'ask-box';
        const h = document.createElement('div');
        h.className = 'ask-title';
        h.textContent = title;
        const input = document.createElement('input');
        input.value = initial;
        input.spellcheck = false;
        const note = document.createElement('div');
        note.className = 'ask-hint';
        note.textContent = hint;
        const row = document.createElement('div');
        row.className = 'ask-buttons';
        const ok = document.createElement('button');
        ok.textContent = 'OK';
        const cancel = document.createElement('button');
        cancel.textContent = 'Cancel';
        row.append(ok, cancel);
        box.append(h, input, note, row);
        shade.append(box);
        const done = (value) => { shade.remove(); resolve(value); };
        ok.onclick = () => done(input.value.trim());
        cancel.onclick = () => done(null);
        shade.onmousedown = (e) => { if (e.target === shade) done(null); };
        input.onkeydown = (e) => {
            if (e.key === 'Enter') done(input.value.trim());
            else if (e.key === 'Escape') done(null);
            e.stopPropagation();
        };
        document.body.append(shade);
        input.focus();
        const dot = initial.lastIndexOf('.');
        input.setSelectionRange(0, dot > 0 ? dot : initial.length);
    });
}

function askYes(title, message, action = 'Delete') {
    return new Promise((resolve) => {
        const shade = document.createElement('div');
        shade.className = 'ask-shade';
        const box = document.createElement('div');
        box.className = 'ask-box';
        const h = document.createElement('div');
        h.className = 'ask-title';
        h.textContent = title;
        const text = document.createElement('div');
        text.className = 'ask-hint';
        text.textContent = message;
        const row = document.createElement('div');
        row.className = 'ask-buttons';
        const yes = document.createElement('button');
        yes.textContent = action;
        yes.className = 'danger';
        const no = document.createElement('button');
        no.textContent = 'Cancel';
        row.append(yes, no);
        box.append(h, text, row);
        shade.append(box);
        const done = (value) => { shade.remove(); resolve(value); };
        yes.onclick = () => done(true);
        no.onclick = () => done(false);
        shade.onmousedown = (e) => { if (e.target === shade) done(false); };
        shade.onkeydown = (e) => { if (e.key === 'Escape') done(false); };
        document.body.append(shade);
        no.focus();
    });
}

export function setupExplorer(deps) {
    const list = $('files');
    let dirs = [];
    let files = [];
    let open = new Set();
    let selected = '';
    let clip = null;          // { path, cut }: what Ctrl+X / Ctrl+C took, for Ctrl+V
    let dragging = null;      // the row being dragged onto a folder
    try { open = new Set(JSON.parse(localStorage.getItem('pymagic.explorer.open') ?? '[]')); } catch { /* storage unavailable */ }
    list.tabIndex = 0;

    function remember() { try { localStorage.setItem('pymagic.explorer.open', JSON.stringify([...open])); } catch { /* storage unavailable */ } }

    function children(dir) {
        const subdirs = dirs.filter((d) => parentOf(d) === dir).sort((a, b) => baseName(a).toLowerCase().localeCompare(baseName(b).toLowerCase()));
        const names = files.filter((f) => parentOf(f.path) === dir).sort((a, b) => baseName(a.path).toLowerCase().localeCompare(baseName(b.path).toLowerCase()));
        return { subdirs, names };
    }

    function row(path, name, isDir, depth, isOpen) {
        const li = document.createElement('li');
        li.dataset.path = path;
        li.dataset.kind = isDir ? 'dir' : 'file';
        li.style.paddingLeft = `${8 + depth * 14}px`;
        li.title = path;
        const twisty = document.createElement('span');
        twisty.className = 'twisty';
        if (isDir) twisty.append(sprite(isOpen ? 'chev-down' : 'chev-right'));
        const icon = document.createElement('span');
        icon.className = 'ficon';
        if (isDir) icon.append(sprite(isOpen ? 'folder-open' : 'folder', 'folder'));
        else icon.append(sprite(...iconOf(name)));
        const label = document.createElement('span');
        label.className = 'fname';
        label.textContent = name;
        li.append(twisty, icon, label);
        if (path === selected) li.classList.add('sel');
        if (clip?.cut && clip.path === path) li.classList.add('cut');
        li.draggable = true;
        li.ondragstart = (e) => {
            dragging = path;
            e.dataTransfer.effectAllowed = 'move';
            e.dataTransfer.setData(FILE_TYPE, path);
        };
        li.ondragend = () => { dragging = null; markTarget(null); };
        li.onclick = () => {
            selected = path;
            if (isDir) {
                if (open.has(path)) open.delete(path); else open.add(path);
                remember();
                paint();
            } else {
                paint();
                deps.openFile(path);
            }
        };
        li.oncontextmenu = (e) => { e.preventDefault(); e.stopPropagation(); selected = path; paint(); menu(e.clientX, e.clientY, path, isDir); };
        return li;
    }

    function paint() {
        const rows = [];
        const walk = (dir, depth) => {
            const { subdirs, names } = children(dir);
            for (const d of subdirs) {
                const isOpen = open.has(d);
                rows.push(row(d, baseName(d), true, depth, isOpen));
                if (isOpen) walk(d, depth + 1);
            }
            for (const f of names) rows.push(row(f.path, baseName(f.path), false, depth, false));
        };
        walk('', 0);
        list.replaceChildren(...rows);
        window.dispatchEvent(new Event('pymagic:files'));
        deps.refreshDirty();
    }

    async function refresh() {
        try {
            const res = await fetch('/api/tree');
            if (!res.ok) return;
            const tree = await res.json();
            dirs = tree.dirs;
            files = tree.files;
            // folders that hold files the user works with are open the first time
            if (!localStorage.getItem('pymagic.explorer.open')) {
                for (const d of dirs) if (!d.includes('/')) open.add(d);
                remember();
            }
            paint();
        } catch (e) { toast(String(e.message ?? e), true); }
    }

    function reveal(path) {
        let p = parentOf(path);
        let changed = false;
        while (p) { if (!open.has(p)) { open.add(p); changed = true; } p = parentOf(p); }
        if (changed) { remember(); paint(); }
    }

    // ------------------------------------------------------------ operations

    async function post(body) {
        const res = await fetch('/api/fs', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
        if (res.ok) return true;
        let message = `${res.status}`;
        try { message = (await res.json()).error ?? message; } catch { /* no body */ }
        toast(message, true);
        return false;
    }

    const INVALID = /[<>:"|?*\u0000-\u001f]|^\.\.?$|(^|\/)\.\.(\/|$)/;

    async function create(dir, isDir) {
        const name = await askText(isDir ? 'New folder' : 'New file', '', dir ? `in ${dir}` : 'in the project folder');
        if (!name) return;
        if (INVALID.test(name) || name.includes('\\') || name.startsWith('/')) { toast('That is not a usable name.', true); return; }
        const path = dir ? `${dir}/${name}` : name;
        if (!(await post({ op: 'create', path, dir: isDir }))) return;
        if (dir) open.add(dir);
        if (isDir) open.add(path);
        remember();
        selected = path;
        await refresh();
        reveal(path);
        await deps.synced();
        if (!isDir) await deps.openFile(path);
    }

    async function rename(path, isDir) {
        const name = await askText('Rename', baseName(path), isDir ? 'a folder' : 'a file');
        if (!name || name === baseName(path)) return;
        if (INVALID.test(name) || name.includes('/') || name.includes('\\')) { toast('That is not a usable name.', true); return; }
        await deps.saveAll();
        const target = parentOf(path) ? `${parentOf(path)}/${name}` : name;
        if (!(await deps.beforeMove(path, target))) return;
        if (!(await post({ op: 'rename', from: path, to: target }))) return;
        await afterMove(path, target, isDir);
    }

    /** What follows a rename or a move: open folders, the selection and the file in the editor go along to the new place. */
    async function afterMove(path, target, isDir) {
        if (isDir) {
            const moved = [...open].filter((d) => d === path || d.startsWith(path + '/')).map((d) => movedPath(d, path, target));
            for (const d of [...open]) if (d === path || d.startsWith(path + '/')) open.delete(d);
            for (const d of moved) open.add(d);
        }
        if (parentOf(target)) open.add(parentOf(target));
        remember();
        if (clip) clip = { ...clip, path: movedPath(clip.path, path, target) };
        selected = target;
        const current = deps.current();
        deps.renamed?.(path, target);
        await deps.forget(path);
        await refresh();
        reveal(target);
        await deps.synced();
        if (current && movedPath(current, path, target) !== current) await deps.openFile(movedPath(current, path, target));
    }

    const exists = (path) => dirs.includes(path) || files.some((f) => f.path === path);

    /** Moves a file or folder into the folder `dir` ('' is the project folder), keeping its name. True when it moved. */
    async function move(path, dir) {
        const problem = moveProblem(path, dir);
        if (problem) { toast(`${baseName(path)} cannot be moved there: ${problem}.`, true); return false; }
        const target = join(dir, baseName(path));
        if (exists(target)) { toast(`${dir || 'The project folder'} already has a ${baseName(path)}.`, true); return false; }
        const isDir = dirs.includes(path);
        await deps.saveAll();
        // the imports of a Python module or package follow it (shown first; Cancel leaves everything as it was)
        if (!(await deps.beforeMove(path, target))) return false;
        if (!(await post({ op: 'rename', from: path, to: target }))) return false;
        await afterMove(path, target, isDir);
        toast(`Moved ${baseName(path)} to ${dir || 'the project folder'}`);
        return true;
    }

    // ------------------------------------------------------------ cut, copy and paste

    /** The folder a paste or a drop goes into: a folder itself, the folder of a file, or the project folder. */
    const folderOf = (path) => (!path ? '' : dirs.includes(path) ? path : parentOf(path));

    function take(path, cut) {
        if (!path) return;
        clip = { path, cut };
        paint();
        toast(`${cut ? 'Cut' : 'Copied'} ${baseName(path)}: paste it into a folder with Ctrl+V`);
    }

    async function paste(dir) {
        if (!clip) return;
        const { path, cut } = clip;
        if (!exists(path)) { clip = null; paint(); toast(`${path} is not there any more.`, true); return; }
        if (cut) {
            if (await move(path, dir)) { clip = null; paint(); }
            return;
        }
        if (dirs.includes(path) && (dir === path || dir.startsWith(path + '/'))) { toast('A folder cannot be copied into itself.', true); return; }
        const { subdirs, names } = children(dir);
        const name = copyName(baseName(path), [...subdirs.map(baseName), ...names.map((f) => baseName(f.path))]);
        const target = join(dir, name);
        await deps.saveAll();
        if (!(await post({ op: 'copy', from: path, to: target }))) return;
        if (dir) open.add(dir);
        remember();
        selected = target;
        await refresh();
        reveal(target);
        await deps.synced();
        toast(`Copied ${baseName(path)} to ${target}`);
    }

    // ------------------------------------------------------------ dragging rows onto folders

    let opener = null;   // a closed folder opens when a row is held over it
    function markTarget(dir, li = null) {
        for (const el of list.querySelectorAll('.drop-target')) el.classList.remove('drop-target');
        list.classList.toggle('drop-root', dir === '');
        if (li && dir) li.classList.add('drop-target');
    }
    list.addEventListener('dragover', (e) => {
        if (!dragging || !e.dataTransfer.types.includes(FILE_TYPE)) return;
        const li = e.target.closest?.('li');
        const dir = li ? folderOf(li.dataset.path) : '';
        if (moveProblem(dragging, dir)) { markTarget(null); return; }
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        const holder = li && li.dataset.kind === 'dir' ? li : list.querySelector(`li[data-kind="dir"][data-path="${CSS.escape(dir)}"]`);
        markTarget(dir, holder);
        if (li?.dataset.kind === 'dir' && !open.has(dir)) {
            if (opener?.dir !== dir) { clearTimeout(opener?.timer); opener = { dir, timer: setTimeout(() => { open.add(dir); remember(); paint(); }, 700) }; }
        }
    });
    list.addEventListener('dragleave', (e) => { if (!list.contains(e.relatedTarget)) { markTarget(null); clearTimeout(opener?.timer); opener = null; } });
    list.addEventListener('drop', (e) => {
        if (!dragging || !e.dataTransfer.types.includes(FILE_TYPE)) return;
        e.preventDefault();
        const li = e.target.closest?.('li');
        const path = dragging;
        dragging = null;
        markTarget(null);
        clearTimeout(opener?.timer);
        opener = null;
        void move(path, li ? folderOf(li.dataset.path) : '');
    });

    async function remove(path, isDir) {
        const what = isDir ? `the folder "${path}" and everything in it` : `the file "${path}"`;
        if (!(await askYes('Delete', `Delete ${what}? This cannot be undone.`))) return;
        await deps.saveAll();
        if (!(await post({ op: 'delete', path }))) return;
        await deps.forget(path);
        selected = '';
        await refresh();
        await deps.synced();
        const current = deps.current();
        if (current === path || current.startsWith(path + '/')) await deps.openAnother();
    }

    // ------------------------------------------------------------ the menu

    function menu(x, y, path, isDir) {
        const dir = path === '' ? '' : isDir ? path : parentOf(path);
        const items = [
            { label: 'New file…', run: () => create(dir, false) },
            { label: 'New folder…', run: () => create(dir, true) },
        ];
        if (path) items.push('-', { label: 'Cut', key: 'Ctrl+X', run: () => take(path, true) }, { label: 'Copy', key: 'Ctrl+C', run: () => take(path, false) });
        if (clip) items.push({ label: `Paste ${baseName(clip.path)}`, key: 'Ctrl+V', run: () => paste(dir) });
        if (path) {
            items.push('-', { label: 'Rename…', key: 'F2', run: () => rename(path, isDir) }, { label: 'Delete…', key: 'Del', run: () => remove(path, isDir) },
                { label: 'Copy path', run: () => navigator.clipboard?.writeText(path) });
        }
        // find, inspect, format, tests, history, Open In, .gitignore: the same as the tabs' (fileactions.js)
        if (deps.items) items.push('-', ...deps.items(path, isDir));
        items.push('-', { label: 'Refresh', run: () => refresh() });
        openMenu({ x, y }, items);
    }
    $('sidebar').oncontextmenu = (e) => { e.preventDefault(); menu(e.clientX, e.clientY, '', true); };

    list.onkeydown = (e) => {
        const isDir = dirs.includes(selected);
        const ctrl = e.ctrlKey && !e.altKey && !e.shiftKey;
        if (ctrl && (e.key === 'v' || e.key === 'V')) { e.preventDefault(); void paste(folderOf(selected)); return; }
        if (e.key === 'Escape' && clip) { clip = null; paint(); return; }
        if (!selected) return;
        if (e.key === 'F2') { e.preventDefault(); void rename(selected, isDir); }
        else if (e.key === 'Delete') { e.preventDefault(); void remove(selected, isDir); }
        else if (ctrl && (e.key === 'x' || e.key === 'X')) { e.preventDefault(); take(selected, true); }
        else if (ctrl && (e.key === 'c' || e.key === 'C')) { e.preventDefault(); take(selected, false); }
    };

    $('ex-new-file').onclick = () => void create(dirs.includes(selected) ? selected : parentOf(selected), false);
    $('ex-new-folder').onclick = () => void create(dirs.includes(selected) ? selected : parentOf(selected), true);
    $('ex-refresh').onclick = () => void refresh();
    $('ex-collapse').onclick = () => { open.clear(); remember(); paint(); };
    window.addEventListener('focus', () => void refresh());

    return {
        refresh, reveal, has: (path) => files.some((f) => f.path === path), select: (path) => { selected = path; reveal(path); paint(); },
        // Rename File… of a tab
        rename: (path) => rename(path, dirs.includes(path)),
        askText,
    };
}

export { BINARY };

// The project tree in the sidebar: folders that open and close, every text file, and new / rename / delete.
import { toast } from './toast';
import { openMenu } from './menu';
import { copyName, join, moveProblem, movedPath, packageTarget, moduleTarget } from './explorer-model';
import { templateText } from './codegen';
import { NEW_KINDS, IGNORE_FILES, newFileName, newFileText } from './newfile-model';

const FILE_TYPE = 'application/x-pymagic-file';   // a row of the tree being dragged; the editor and the tab bars take nothing from it

const $ = (id) => document.getElementById(id);

// file kind -> the symbol of the icon sprite in index.html (and the class that colours it)
const ICONS = {
    py: ['python', ''], pyi: ['python', ''], pyw: ['python', ''], html: ['html', ''], htm: ['html', ''], jinja: ['web', 'web'], j2: ['web', 'web'], css: ['css', 'folder'], js: ['js', ''],
    json: ['data', 'data'], md: ['doc', 'doc'], rst: ['doc', 'doc'], txt: ['doc', 'doc'], pdf: ['doc', 'doc'],
    toml: ['cog', 'cog'], ini: ['cog', 'cog'], cfg: ['cog', 'cog'], yaml: ['cog', 'cog'], yml: ['cog', 'cog'], env: ['cog', 'cog'], http: ['web', 'web'], sql: ['database', 'data'],
    ipynb: ['jupyter', ''], sqlite: ['database', 'data'], sqlite3: ['database', 'data'], db: ['database', 'data'],
    sh: ['shell', 'cog'], ps1: ['shell', 'cog'], bat: ['shell', 'cog'], cmd: ['shell', 'cog'],
    png: ['file', 'doc'], jpg: ['file', 'doc'], jpeg: ['file', 'doc'], gif: ['file', 'doc'], svg: ['file', 'doc'], ico: ['file', 'doc'],
    // JavaScript and TypeScript, the view engines and components of the other kinds of New Project
    ts: ['ts', ''], tsx: ['ts', ''], jsx: ['js', ''], mjs: ['js', ''], cjs: ['js', ''], scss: ['css', 'folder'], less: ['css', 'folder'],
    ejs: ['web', 'web'], eta: ['web', 'web'], hjs: ['web', 'web'], hbs: ['web', 'web'], handlebars: ['web', 'web'], mustache: ['web', 'web'], pug: ['web', 'web'], jade: ['web', 'web'],
    twig: ['web', 'web'], vash: ['web', 'web'], njk: ['web', 'web'], liquid: ['web', 'web'], vue: ['web', 'web'], svelte: ['web', 'web'], astro: ['web', 'web'],
    xml: ['data', 'data'], webmanifest: ['data', 'data'], gradle: ['cog', 'cog'], properties: ['cog', 'cog'], lock: ['cog', 'cog'],
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
    // files known by their whole name: Docker's, the ignore files (PyCharm's .i*), the other settings
    if (name === 'Dockerfile' || /^(docker-)?compose(\.[^.]+)?\.ya?ml$/i.test(name)) return ['docker', 'folder'];
    if (/^\.[a-z]+ignore$/.test(name)) return ['dotignore', 'doc'];
    if (['requirements.txt', 'Gemfile', 'Podfile', '.editorconfig', '.gitattributes', '.npmrc', '.prettierrc'].includes(name)) return ['cog', 'cog'];
    return ICONS[ext] ?? ['file', 'doc'];
}

const MARK_LABELS = { sources: 'sources root', tests: 'test sources root', excluded: 'excluded', templates: 'template folder', resources: 'resources root' };

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
    let marks = { sources: [], tests: [], excluded: [], templates: [], resources: [], autoTemplates: [], environments: [], libraries: [] };
    // folders whose content is read when they are opened (a Python environment: .venv, .conda, and every folder in one)
    let lazy = new Set();   // Mark Directory As (.pymagic/folders.json), and TEMPLATES['DIRS']
    let coverage = window.pymagicCoverageTree ?? null;   // {path: {lines, hit, branches, taken, percent}} from the Coverage panel, or null
    try { open = new Set(JSON.parse(localStorage.getItem('pymagic.explorer.open') ?? '[]')); } catch { /* storage unavailable */ }
    // the project folder itself is the tree's first row, as in PyCharm: its name, its path dimmed, and everything else inside it
    const rootPath = String(deps.root ?? '').replace(/[\\/]+$/, '');
    const rootName = rootPath.split(/[\\/]/).pop() || 'Project';
    let rootOpen = true;
    try { rootOpen = localStorage.getItem('pymagic.explorer.rootClosed') !== '1'; } catch { /* storage unavailable */ }
    window.addEventListener('pymagic:coverageTree', (e) => { coverage = e.detail; paint(); });
    list.tabIndex = 0;

    function remember() { try { localStorage.setItem('pymagic.explorer.open', JSON.stringify([...open])); } catch { /* storage unavailable */ } }

    function children(dir) {
        const subdirs = dirs.filter((d) => parentOf(d) === dir).sort((a, b) => baseName(a).toLowerCase().localeCompare(baseName(b).toLowerCase()));
        const names = files.filter((f) => parentOf(f.path) === dir).sort((a, b) => baseName(a.path).toLowerCase().localeCompare(baseName(b.path).toLowerCase()));
        return { subdirs, names };
    }

    // an environment of the project (.venv, .conda) is excluded as PyCharm shows it: it is the interpreter's, not the project's code
    const isEnvironment = (path) => (marks.environments ?? []).includes(path);
    // node_modules too: the project's npm packages, shown and excluded
    const isLibrary = (path) => (marks.libraries ?? []).includes(path);
    const markOf = (path) => (marks.sources.includes(path) ? 'sources' : marks.tests.includes(path) ? 'tests' : marks.excluded.includes(path) || isEnvironment(path) || isLibrary(path) ? 'excluded'
        : (marks.templates ?? []).includes(path) || (marks.autoTemplates ?? []).includes(path) ? 'templates' : (marks.resources ?? []).includes(path) ? 'resources' : null);

    /** Mark Directory As: the folder becomes a sources root, test sources or excluded (or loses its mark); the page starts again so the analysis follows. */
    async function markAs(path, kind) {
        await deps.saveAll();
        if (!(await post({ op: 'mark', path, kind }))) return;
        toast(kind === 'none' ? `${path} is not marked any more` : `${path} is marked as ${MARK_LABELS[kind]}`);
        // the language server takes the new roots at once (main.js tells it); the tree takes the new colours
        window.dispatchEvent(new CustomEvent('pymagic:folders'));
        await refresh();
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
        // coverage of the shown runs: the share of the lines that ran, for a file and for everything in a folder
        const cov = (coverage ?? window.pymagicCoverageTree)?.[path];
        if (cov) {
            const pct = document.createElement('span');
            pct.className = `fcov ${cov.percent >= 80 ? 'good' : cov.percent < 50 ? 'poor' : ''}`;
            pct.textContent = `${cov.percent.toFixed(0)}%`;
            pct.title = `${cov.hit} of ${cov.lines} lines ran${cov.branches ? `, ${cov.taken} of ${cov.branches} branch ways taken` : ''}`;
            li.append(pct);
        }
        if (path === selected) li.classList.add('sel');
        // a marked folder has its colour: sources blue, test sources green, excluded orange (and what is inside an excluded folder is dim)
        const mark = isDir ? markOf(path) : null;
        if (mark) { li.classList.add(`mark-${mark}`); li.title = `${path} (${isEnvironment(path) ? "the project's Python environment, excluded from the analysis" : isLibrary(path) ? "the project's npm packages, excluded from the analysis and the search" : MARK_LABELS[mark]}${mark === 'templates' && !(marks.templates ?? []).includes(path) ? ", from TEMPLATES['DIRS'] of the settings" : ''})`; }
        if ([...marks.excluded, ...(marks.environments ?? []), ...(marks.libraries ?? [])].some((e) => path.startsWith(e + '/'))) li.classList.add('in-excluded');
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
                if (open.has(path) && lazy.has(path)) void loadDir(path);
                else paint();
            } else {
                paint();
                deps.openFile(path, undefined, { preview: true });
            }
        };
        // a double click keeps the file's tab (out of the preview tab)
        if (!isDir) li.ondblclick = () => deps.openFile(path);
        li.oncontextmenu = (e) => { e.preventDefault(); e.stopPropagation(); selected = path; paint(); menu(e.clientX, e.clientY, path, isDir); };
        return li;
    }

    /** The project folder's row: a click opens or closes it, its menu is the project's (New, Paste, Find in files, ...). */
    function rootRow() {
        const li = document.createElement('li');
        li.dataset.path = '';
        li.dataset.kind = 'root';
        li.className = 'root';
        li.style.paddingLeft = '8px';
        li.title = rootPath;
        const twisty = document.createElement('span');
        twisty.className = 'twisty';
        twisty.append(sprite(rootOpen ? 'chev-down' : 'chev-right'));
        const icon = document.createElement('span');
        icon.className = 'ficon';
        icon.append(sprite(rootOpen ? 'folder-open' : 'folder', 'folder'));
        const label = document.createElement('span');
        label.className = 'fname';
        label.textContent = rootName;
        const where = document.createElement('span');
        where.className = 'fpath';
        where.textContent = rootPath;
        li.append(twisty, icon, label, where);
        li.onclick = () => {
            rootOpen = !rootOpen;
            try { localStorage.setItem('pymagic.explorer.rootClosed', rootOpen ? '0' : '1'); } catch { /* storage unavailable */ }
            paint();
        };
        li.oncontextmenu = (e) => { e.preventDefault(); e.stopPropagation(); menu(e.clientX, e.clientY, '', true); };
        return li;
    }

    function paint() {
        const rows = [rootRow()];
        const walk = (dir, depth) => {
            const { subdirs, names } = children(dir);
            for (const d of subdirs) {
                const isOpen = open.has(d);
                rows.push(row(d, baseName(d), true, depth, isOpen));
                if (isOpen) walk(d, depth + 1);
            }
            for (const f of names) rows.push(row(f.path, baseName(f.path), false, depth, false));
        };
        if (rootOpen) walk('', 1);
        list.replaceChildren(...rows);
        window.dispatchEvent(new Event('pymagic:files'));
        deps.refreshDirty();
    }

    /** Reads a lazy folder's own folders and files (each folder in it is lazy too), then shows them. */
    async function loadDir(path, quiet = false) {
        try {
            const res = await fetch('/api/tree/dir?path=' + encodeURIComponent(path));
            if (!res.ok) { paint(); return; }
            const got = await res.json();
            lazy.delete(path);
            const known = new Set(dirs);
            for (const d of got.dirs ?? []) if (!known.has(d)) { dirs.push(d); lazy.add(d); }
            const have = new Set(files.map((f) => f.path));
            for (const f of got.files ?? []) if (!have.has(f.path)) files.push(f);
        } catch (e) { toast(String(e.message ?? e), true); }
        if (!quiet) paint();
    }

    async function refresh() {
        try {
            const res = await fetch('/api/tree');
            if (!res.ok) return;
            const tree = await res.json();
            dirs = tree.dirs;
            files = tree.files;
            marks = tree.marks ?? marks;
            lazy = new Set(tree.lazy ?? []);
            // folders that hold files the user works with are open the first time (an environment stays closed)
            if (!localStorage.getItem('pymagic.explorer.open')) {
                for (const d of dirs) if (!d.includes('/') && !lazy.has(d)) open.add(d);
                remember();
            }
            // what was open in an environment is read again (a level at a time, as it was opened)
            for (let round = 0; round < 20; round++) {
                const pending = [...lazy].filter((d) => open.has(d));
                if (!pending.length) break;
                for (const d of pending) await loadDir(d, true);
            }
            paint();
        } catch (e) { toast(String(e.message ?? e), true); }
    }

    function reveal(path) {
        let p = parentOf(path);
        let changed = !rootOpen;
        rootOpen = true;
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
        const content = isDir ? '' : await templateText(name.slice(name.lastIndexOf('/') + 1));
        if (!(await post({ op: 'create', path, dir: isDir, content }))) return;
        if (dir) open.add(dir);
        if (isDir) open.add(path);
        remember();
        selected = path;
        await refresh();
        reveal(path);
        await deps.synced();
        if (!isDir) await deps.openFile(path);
    }

    /** New > (a kind): its name asked with the kind's start, its extension added, the project's file template or the kind's text inside. */
    async function createKind(dir, kind) {
        if (kind.dir) return create(dir, true);
        if (kind.scratch) return deps.newScratch?.();
        if (kind.dataSource) return deps.addDataSource?.(dir ? `${dir}/db.sqlite3` : 'db.sqlite3');
        const what = kind.package ? 'New Python package' : `New ${kind.label.replace(/…$/, '')}`;
        const typed = await askText(what, kind.initial ?? '', dir ? `in ${dir}` : 'in the project folder');
        if (!typed) return;
        const name = newFileName(kind, typed);
        if (INVALID.test(name) || name.startsWith('/')) { toast('That is not a usable name.', true); return; }
        const path = dir ? `${dir}/${name}` : name;
        // a package: the folder and its __init__.py (with the project's template for it, if any)
        const file = kind.package ? `${path}/__init__.py` : path;
        const content = (await templateText(file.slice(file.lastIndexOf('/') + 1))) || newFileText(kind, file);
        if (!(await post({ op: 'create', path: file, dir: false, content }))) return;
        for (let p = parentOf(file); p; p = parentOf(p)) open.add(p);
        remember();
        selected = file;
        await refresh();
        reveal(file);
        await deps.synced();
        await deps.openFile(file);
    }

    /** New > .ignore File > (one): made in the folder with its usual start, or opened when the folder has it already. */
    async function createIgnore(dir, name, text) {
        const path = dir ? `${dir}/${name}` : name;
        if (!files.some((f) => f.path === path)) {
            if (!(await post({ op: 'create', path, dir: false, content: text }))) return;
            await refresh();
            await deps.synced();
        }
        selected = path;
        reveal(path);
        await deps.openFile(path);
    }

    function newItems(dir) {
        return NEW_KINDS.filter((k) => k === '-' || !k.plugin || deps.plugin?.(k.plugin) !== false).map((k) => k === '-' ? '-'
            : k.ignore ? { label: k.label, icon: k.icon, submenu: IGNORE_FILES.map(([name, owner, text]) => ({ label: `${name} (${owner})`, icon: ['dotignore', 'doc'], run: () => void createIgnore(dir, name, text) })) }
            : { label: k.label, key: k.key, icon: k.icon, run: () => void createKind(dir, k) });
    }

    /** Copy Path/Reference: what PyCharm offers to copy for a file or folder, each shown with its value. */
    function copyItems(path) {
        const absolute = rootPath ? `${rootPath}${rootPath.includes('\\') ? '\\' : '/'}${path.replace(/\//g, rootPath.includes('\\') ? '\\' : '/')}` : path;
        const list = [['Absolute path', absolute], ['Path from the project folder', path], ['File name', baseName(path)]];
        // a module's or package's dotted name, as an import names it
        if (/\.pyi?$/.test(path) || dirs.includes(path)) {
            const dotted = path.replace(/\.pyi?$/, '').replace(/\/__init__$/, '').split('/').join('.');
            list.push(['Reference', dotted]);
        }
        return list.map(([label, value]) => ({ label: `${label}: ${value.length > 60 ? '…' + value.slice(-59) : value}`, run: () => { void navigator.clipboard?.writeText(value); toast(`Copied ${value}`); } }));
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

    /** Moves a module to its package's __init__.py or back, then takes away the folder left empty (`emptied`). */
    async function convert(from, to, emptied) {
        await deps.saveAll();
        if (!(await deps.beforeMove(from, to))) return;
        if (!(await post({ op: 'rename', from, to }))) return;
        if (emptied && !(await post({ op: 'delete', path: emptied }))) return;
        await afterMove(from, to, false);
        toast(`${from} is now ${to}`);
    }

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

    /** Move… (F6): the folder is typed, the imports follow as with a drag. */
    async function moveTo(path) {
        const dir = await askText(`Move ${baseName(path)} to`, parentOf(path), 'The folder, from the project folder (empty: the project folder itself); a folder that is not there is made');
        if (dir === null) return;
        const target = dir.replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');
        if (target === parentOf(path)) return;
        await move(path, target);
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
        // Safe Delete: a module or package that is imported says where first (the places are listed in the Search panel)
        const used = (isDir || /\.pyi?$/.test(path)) && deps.usages ? await deps.usages(path) : 0;
        const warning = used ? ` It is imported or named in ${used} place${used === 1 ? '' : 's'} (listed in the Search panel), which will break.` : '';
        if (!(await askYes(used ? 'Safe delete' : 'Delete', `Delete ${what}?${warning} This cannot be undone.`, used ? 'Delete anyway' : 'Delete'))) return;
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
        const items = [{ label: 'New', submenu: newItems(dir) }];
        if (path) items.push('-', { label: 'Cut', key: 'Ctrl+X', icon: 'cut', run: () => take(path, true) }, { label: 'Copy', key: 'Ctrl+C', icon: 'copy', run: () => take(path, false) },
            { label: 'Copy path/reference', submenu: copyItems(path) });
        if (clip) items.push({ label: `Paste ${baseName(clip.path)}`, key: 'Ctrl+V', icon: 'paste', run: () => paste(dir) });
        if (path) {
            items.push('-', { label: 'Rename…', key: 'F2', run: () => rename(path, isDir) }, { label: 'Move…', key: 'F6', run: () => moveTo(path) },
                { label: 'Delete…', key: 'Del', run: () => remove(path, isDir) });
        }
        // Convert to Package / to Module: the file moves, imports of it stay the same
        if (path && !isDir && packageTarget(path, exists)) items.push('-', { label: 'Convert to package', run: () => void convert(path, packageTarget(path, exists), null) });
        const inside = isDir ? files.map((f) => f.path).filter((f) => f.startsWith(path + '/')) : [];
        if (isDir && path && moduleTarget(path, inside, exists)) items.push('-', { label: 'Convert to module', run: () => void convert(`${path}/__init__.py`, moduleTarget(path, inside, exists), path) });
        if (isDir && path) {
            const mark = markOf(path);
            items.push('-', { label: 'Mark directory as', submenu: [
                ...Object.entries(MARK_LABELS).map(([kind, label]) => ({ label: label[0].toUpperCase() + label.slice(1), checked: mark === kind, run: () => markAs(path, mark === kind ? 'none' : kind) })),
                '-',
                { label: 'Unmark', disabled: !mark, run: () => markAs(path, 'none') },
            ] });
        }
        // find, inspect, format, tests, history, Open In, .gitignore: the same as the tabs' (fileactions.js)
        if (deps.items) items.push('-', ...deps.items(path, isDir));
        items.push('-', { label: 'Refresh', icon: 'refresh', run: () => refresh() });
        // the project folder's menu ends with Close project (the desktop app only)
        if (isDir && path === '' && deps.closeProject) items.push('-', { label: 'Close project', run: () => void deps.closeProject() });
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
        else if (e.key === 'F6') { e.preventDefault(); void moveTo(selected); }
        else if (e.altKey && e.key === 'F7') { e.preventDefault(); void deps.findUsages?.(selected); }
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

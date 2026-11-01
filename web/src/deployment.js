// The Deployment tab of the dock: the servers a project goes to (a folder, FTP, SFTP), with the mappings of folders and what never goes up,
// upload / download / compare / sync of the file open or a folder, upload on save, and the files of a server browsed. The engine does the
// transfers (Deployment.cs); deploy-model.js says what a sync proposes and how things are told.
import * as monaco from 'monaco-editor';
import { showTab } from './panel';
import { actionsFor, defaultAction, describeServer, mappingText, parentOf, parseMappings, sizeText, STATUS_TEXT } from './deploy-model.js';
import { toast } from './toast';

const $ = (id) => document.getElementById(id);

export function setupDeployment({ editor, currentFile, askText, reloadFromDisk, saveAll }) {
    let servers = [];
    let chosen = '';            // the id of the server the panel is about
    let browsing = '';          // the folder of the server shown, relative to its root
    let listed = null;

    const post = (url, body) => fetch(url, { method: 'POST', body: JSON.stringify(body) }).then((r) => r.json()).catch((e) => ({ error: String(e) }));
    const server = () => servers.find((s) => s.id === chosen) ?? null;

    async function load() {
        servers = await fetch('/api/deploy/servers').then((r) => (r.ok ? r.json() : [])).catch(() => []);
        if (!servers.some((s) => s.id === chosen)) chosen = servers[0]?.id ?? '';
        draw();
        if (server()) void browse(browsing);
    }

    // ------------------------------------------------------------ the panel

    function draw() {
        const select = $('dep-server');
        select.replaceChildren(...servers.map((s) => new Option(`${s.name}${s.uploadOnSave ? ' ⇪' : ''}`, s.id)), new Option('', ''));
        select.options[select.options.length - 1].remove();
        select.value = chosen;
        const s = server();
        const has = !!s;
        for (const id of ['dep-edit', 'dep-remove', 'dep-upload', 'dep-download', 'dep-compare', 'dep-sync', 'dep-up']) $(id).disabled = !has;
        $('dep-info').textContent = has ? `${describeServer(s)} · ${s.mappings.map(mappingText).join(', ')}${s.uploadOnSave ? ' · uploads on save' : ''}` : '';
        if (!has) {
            $('dep-list').replaceChildren(Object.assign(document.createElement('div'), { className: 'side-empty', textContent: 'No server yet. + adds one: a folder, an FTP server or an SFTP server, with the folders of the project that go to it.' }));
            $('dep-path').textContent = '';
        }
    }

    async function browse(path) {
        const s = server();
        if (!s) return;
        browsing = path;
        $('dep-path').textContent = '/' + path;
        const box = $('dep-list');
        box.replaceChildren(Object.assign(document.createElement('div'), { className: 'side-empty', textContent: 'Reading…' }));
        const r = await fetch(`/api/deploy/list?server=${encodeURIComponent(s.id)}&path=${encodeURIComponent(path)}`).then((x) => x.json()).catch((e) => ({ error: String(e) }));
        box.replaceChildren();
        if (r.error) { box.append(Object.assign(document.createElement('div'), { className: 'side-empty', textContent: r.error })); return; }
        listed = r;
        if (!r.entries.length) box.append(Object.assign(document.createElement('div'), { className: 'side-empty', textContent: 'Nothing here.' }));
        for (const e of r.entries) {
            const row = document.createElement('div');
            row.className = 'dep-row';
            row.append(Object.assign(document.createElement('span'), { className: 'dep-icon', textContent: e.isDir ? '📁' : '·' }),
                Object.assign(document.createElement('span'), { className: 'dep-name', textContent: e.name }),
                Object.assign(document.createElement('span'), { className: 'dep-size', textContent: e.isDir ? '' : sizeText(e.size) }),
                Object.assign(document.createElement('span'), { className: 'dep-time', textContent: new Date(e.modified).toLocaleString() }),
                Object.assign(document.createElement('span'), { className: 'dep-local', textContent: e.local ? '↔ ' + e.local : '' }));
            row.title = e.isDir ? 'Open the folder' : e.local ? 'Double-click: compare with the file of the project' : 'No mapping covers this file';
            row.onclick = () => { if (e.isDir) void browse(path ? `${path}/${e.name}` : e.name); };
            row.ondblclick = () => { if (!e.isDir && e.local) void compare(e.local); };
            box.append(row);
        }
    }

    // ------------------------------------------------------------ transfers

    function report(verb, results) {
        const ok = results.filter((r) => r.ok).length;
        const bad = results.filter((r) => !r.ok);
        if (!results.length) toast(`Nothing to ${verb}.`);
        else if (!bad.length) toast(`${verb[0].toUpperCase() + verb.slice(1)}ed ${ok} file${ok === 1 ? '' : 's'}.`);
        else toast(`${ok} done, ${bad.length} failed: ${bad[0].path}: ${bad[0].error}`, true);
    }

    async function upload(paths) {
        const s = server();
        if (!s) { toast('Add a server first.', true); return; }
        await saveOpen();
        const r = await post('/api/deploy/upload', { server: s.id, paths });
        if (r.error) { toast(r.error, true); return; }
        report('upload', r.results);
        void browse(browsing);
    }

    async function download(paths) {
        const s = server();
        if (!s) { toast('Add a server first.', true); return; }
        const r = await post('/api/deploy/download', { server: s.id, paths });
        if (r.error) { toast(r.error, true); return; }
        report('download', r.results);
        for (const x of r.results.filter((x) => x.ok)) void reloadFromDisk(x.path);
        void browse(browsing);
    }

    /** A file of the editor that is not saved is saved first: what goes up is what is on the screen. */
    async function saveOpen() { await saveAll(); }

    async function compare(rel) {
        const s = server();
        if (!s || !rel) return;
        const c = await fetch(`/api/deploy/compare?server=${encodeURIComponent(s.id)}&path=${encodeURIComponent(rel)}`).then((r) => r.json()).catch((e) => ({ error: String(e) }));
        if (c.error) { toast(c.error, true); return; }
        if (!c.remoteExists) { toast(`${rel} is not on ${s.name}.`); return; }
        if (c.same) { toast(`${rel} is the same on ${s.name}.`); return; }
        if (c.local === null || c.remote === null) { toast(`${rel} differs from the server's (not text: no comparison to show).`); return; }
        const language = editor.getModel()?.getLanguageId() ?? 'plaintext';
        const shade = document.createElement('div');
        shade.className = 'settings-shade';
        const box = document.createElement('div');
        box.className = 'hist-diff';
        const head = document.createElement('div');
        head.className = 'hist-diff-head';
        const title = Object.assign(document.createElement('span'), { className: 'hist-diff-title', textContent: `${rel}: the server's file (left) and the project's (right)` });
        const up = Object.assign(document.createElement('button'), { textContent: 'Upload', title: 'Put the project\'s file on the server' });
        const down = Object.assign(document.createElement('button'), { textContent: 'Download', title: 'Put the server\'s file in the project' });
        const close = Object.assign(document.createElement('button'), { textContent: 'Close' });
        head.append(title, up, down, close);
        const body = Object.assign(document.createElement('div'), { className: 'hist-diff-body' });
        box.append(head, body);
        shade.append(box);
        document.body.append(shade);
        const a = monaco.editor.createModel(c.remote, language), b = monaco.editor.createModel(c.local, language);
        const diff = monaco.editor.createDiffEditor(body, { readOnly: true, automaticLayout: true, minimap: { enabled: false }, scrollBeyondLastLine: false });
        diff.setModel({ original: a, modified: b });
        const done = () => { document.removeEventListener('keydown', onKey, true); diff.dispose(); a.dispose(); b.dispose(); shade.remove(); editor.focus(); };
        const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); done(); } };
        document.addEventListener('keydown', onKey, true);
        close.onclick = done;
        shade.onmousedown = (e) => { if (e.target === shade) done(); };
        up.onclick = async () => { done(); await upload([rel]); };
        down.onclick = async () => { done(); await download([rel]); };
    }

    async function sync(folder) {
        const s = server();
        if (!s) return;
        const r = await fetch(`/api/deploy/sync?server=${encodeURIComponent(s.id)}&path=${encodeURIComponent(folder)}`).then((x) => x.json()).catch((e) => ({ error: String(e) }));
        if (r.error) { toast(r.error, true); return; }
        const shade = document.createElement('div');
        shade.className = 'settings-shade';
        const box = document.createElement('div');
        box.className = 'hist-diff dep-sync';
        const head = document.createElement('div');
        head.className = 'hist-diff-head';
        const title = Object.assign(document.createElement('span'), { className: 'hist-diff-title', textContent: `Sync ${folder || 'the project'} with ${s.name}` });
        const showSame = Object.assign(document.createElement('label'), { className: 'dep-same' });
        const sameBox = Object.assign(document.createElement('input'), { type: 'checkbox' });
        showSame.append(sameBox, ' show the same');
        const apply = Object.assign(document.createElement('button'), { className: 'primary', textContent: 'Apply' });
        const close = Object.assign(document.createElement('button'), { textContent: 'Close' });
        head.append(title, showSame, apply, close);
        const list = Object.assign(document.createElement('div'), { className: 'hist-diff-body dep-sync-list' });
        box.append(head, list);
        shade.append(box);
        document.body.append(shade);
        const choice = new Map(r.items.map((i) => [i.path, defaultAction(i.status)]));
        const render = () => {
            list.replaceChildren();
            const shown = r.items.filter((i) => sameBox.checked || i.status !== 'same');
            if (!shown.length) list.append(Object.assign(document.createElement('div'), { className: 'side-empty', textContent: 'Nothing differs.' }));
            for (const i of shown) {
                const row = document.createElement('div');
                row.className = 'dep-sync-row ' + i.status;
                const pick = document.createElement('select');
                for (const [value, label] of actionsFor(i.status)) pick.append(new Option(label, value));
                pick.value = choice.get(i.path);
                pick.onchange = () => choice.set(i.path, pick.value);
                row.append(pick, Object.assign(document.createElement('span'), { className: 'dep-name', textContent: i.path }),
                    Object.assign(document.createElement('span'), { className: 'dep-state', textContent: STATUS_TEXT[i.status] }),
                    Object.assign(document.createElement('span'), { className: 'dep-size', textContent: `${sizeText(i.localSize)} here · ${sizeText(i.remoteSize)} there` }));
                list.append(row);
            }
        };
        sameBox.onchange = render;
        render();
        const done = () => { document.removeEventListener('keydown', onKey, true); shade.remove(); };
        const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); done(); } };
        document.addEventListener('keydown', onKey, true);
        close.onclick = done;
        apply.onclick = async () => {
            const actions = [...choice].filter(([, a]) => a !== 'skip').map(([path, action]) => ({ path, action }));
            if (!actions.length) { toast('Nothing chosen.'); return; }
            apply.disabled = true;
            const out = await post('/api/deploy/sync', { server: s.id, actions });
            done();
            if (out.error) { toast(out.error, true); return; }
            report('sync', out.results);
            for (const x of out.results.filter((x) => x.ok && choice.get(x.path) === 'download')) void reloadFromDisk(x.path);
            void browse(browsing);
        };
    }

    // ------------------------------------------------------------ the form of a server

    function form(existing) {
        const shade = document.createElement('div');
        shade.className = 'ask-shade';
        const box = document.createElement('div');
        box.className = 'ask-box dep-form';
        const title = Object.assign(document.createElement('div'), { className: 'ask-title', textContent: existing ? `Edit ${existing.name}` : 'Add a server' });
        box.append(title);
        const f = {};
        const field = (key, label, el, hint) => {
            const row = document.createElement('label');
            row.className = 'db-form-row';
            row.append(Object.assign(document.createElement('span'), { textContent: label }), el);
            if (hint) el.title = hint;
            el.id = 'dep-form-' + key;
            f[key] = el;
            box.append(row);
        };
        const input = (value = '', type = 'text') => Object.assign(document.createElement('input'), { value, type, spellcheck: false, autocomplete: 'off' });
        const type = document.createElement('select');
        type.append(new Option('Folder', 'folder'), new Option('FTP', 'ftp'), new Option('SFTP', 'sftp'));
        type.value = existing?.type ?? 'sftp';
        field('type', 'Kind', type);
        field('name', 'Name', input(existing?.name ?? ''), 'Optional: how the server is listed');
        field('host', 'Host', input(existing?.host ?? ''));
        field('port', 'Port', input(String(existing?.port ?? 22)));
        field('user', 'User', input(existing?.user ?? ''));
        field('password', 'Password', input('', 'password'), existing?.hasPassword ? 'Leave empty to keep the one saved' : 'Kept in .pymagic/deployment.private.json: keep that file out of version control');
        field('keyFile', 'Key file', input(existing?.keyFile ?? ''), 'SFTP only: a private key file (the password above then opens the key)');
        field('root', 'Folder', input(existing?.root ?? ''), 'The folder on the server everything below goes into (for a folder server, the folder itself)');
        const maps = Object.assign(document.createElement('textarea'), { rows: 3, value: (existing?.mappings ?? [{ local: '', remote: '' }]).map(mappingText).join('\n'), spellcheck: false });
        field('mappings', 'Mappings', maps, 'One per line: a folder of the project → a folder of the server (. is the whole project)');
        field('excluded', 'Not sent', input((existing?.excluded ?? []).join(', ')), 'Names and patterns that never go up (*.log, secrets), besides .git, __pycache__, .venv and the like');
        const onSave = Object.assign(document.createElement('input'), { type: 'checkbox', checked: !!existing?.uploadOnSave });
        field('uploadOnSave', 'On save', onSave, 'Upload a file to this server whenever it is saved');
        const forget = Object.assign(document.createElement('input'), { type: 'checkbox' });
        if (existing?.hostKey) field('forgetKey', 'New host key', forget, 'Forget the remembered key of the server (after it was set up again)');
        const error = Object.assign(document.createElement('div'), { className: 'db-form-error' });
        const row = Object.assign(document.createElement('div'), { className: 'ask-buttons' });
        const ok = Object.assign(document.createElement('button'), { className: 'primary', textContent: 'Test and save' });
        const cancel = Object.assign(document.createElement('button'), { textContent: 'Cancel' });
        row.append(ok, cancel);
        box.append(error, row);
        shade.append(box);
        document.body.append(shade);
        const show = () => {
            const t = type.value;
            f.host.closest('label').hidden = t === 'folder';
            f.port.closest('label').hidden = t === 'folder';
            f.user.closest('label').hidden = t === 'folder';
            f.password.closest('label').hidden = t === 'folder';
            f.keyFile.closest('label').hidden = t !== 'sftp';
            f.root.closest('label').firstChild.textContent = t === 'folder' ? 'Folder' : 'Remote folder';
            if (!existing) f.port.value = t === 'ftp' ? '21' : '22';
        };
        type.onchange = show;
        show();
        const done = () => shade.remove();
        cancel.onclick = done;
        shade.onmousedown = (e) => { if (e.target === shade) done(); };
        ok.onclick = async () => {
            ok.disabled = true;
            error.textContent = 'Connecting…';
            const body = {
                id: existing?.id, type: type.value, name: f.name.value, host: f.host.value, port: f.port.value, user: f.user.value, root: f.root.value, keyFile: f.keyFile.value,
                uploadOnSave: onSave.checked, mappings: parseMappings(maps.value), excluded: f.excluded.value.split(',').map((x) => x.trim()).filter(Boolean), forgetKey: forget.checked,
            };
            if (f.password.value || !existing) body.password = f.password.value;
            const answer = await post('/api/deploy/servers', body);
            ok.disabled = false;
            if (answer.error) { error.textContent = answer.error; return; }
            done();
            chosen = answer.id;
            toast(`${answer.name} is ready.`);
            browsing = '';
            await load();
        };
        box.onkeydown = (e) => { if (e.key === 'Escape') done(); e.stopPropagation(); };
        (type.value === 'folder' ? f.root : f.host).focus();
    }

    // ------------------------------------------------------------ wiring

    $('t-deployment').onclick = () => { showTab('deployment'); void load(); };
    $('dep-server').onchange = () => { chosen = $('dep-server').value; browsing = ''; draw(); void browse(''); };
    $('dep-add').onclick = () => form(null);
    $('dep-edit').onclick = () => { const s = server(); if (s) form(s); };
    $('dep-remove').onclick = async () => {
        const s = server();
        if (!s || !window.confirm(`Forget ${s.name}? Its saved password goes with it; nothing on the server is touched.`)) return;
        await fetch('/api/deploy/servers?id=' + encodeURIComponent(s.id), { method: 'DELETE' });
        await load();
    };
    $('dep-refresh').onclick = () => void browse(browsing);
    $('dep-up').onclick = () => void browse(parentOf(browsing));
    $('dep-upload').onclick = () => { const f = currentFile(); if (f) void upload([f]); else toast('Open a file first.', true); };
    $('dep-download').onclick = () => { const f = currentFile(); if (f) void download([f]); else toast('Open a file first.', true); };
    $('dep-compare').onclick = () => { const f = currentFile(); if (f) void compare(f); else toast('Open a file first.', true); };
    $('dep-sync').onclick = async () => {
        const dir = await askText('Sync a folder', '', 'A folder of the project, from its root (empty: the whole project)');
        if (dir !== null) void sync(dir.trim().replace(/\\/g, '/').replace(/^\/+|\/+$/g, ''));
    };
    editor.addAction({ id: 'pymagic.deploy.upload', label: 'Deployment: Upload to the Server', run: () => { const f = currentFile(); if (f) void upload([f]); } });
    editor.addAction({ id: 'pymagic.deploy.download', label: 'Deployment: Download from the Server', run: () => { const f = currentFile(); if (f) void download([f]); } });
    editor.addAction({ id: 'pymagic.deploy.compare', label: 'Deployment: Compare with the Server', run: () => { const f = currentFile(); if (f) void compare(f); } });
    // what is saved goes up in the background: its result is told
    window.addEventListener('pymagic:saved', async () => {
        if (!servers.some((s) => s.uploadOnSave)) return;
        const rel = currentFile();
        await new Promise((ok) => setTimeout(ok, 1500));
        const log = await fetch('/api/deploy/log').then((r) => r.json()).catch(() => []);
        const mine = log.find((l) => l.path === rel && Date.now() - Date.parse(l.time) < 5000);
        if (mine) toast(mine.ok ? `Uploaded ${mine.path} to ${mine.server}.` : `Upload of ${mine.path} to ${mine.server} failed: ${mine.error}`, !mine.ok);
    });
    void load();
    return {
        load,
        servers: () => servers,
        /** For a menu of files: upload or download these paths of the project, to the server chosen. */
        upload, download, compare, sync,
    };
}

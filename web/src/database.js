// The Database panel: the project's SQLite files, their tables, a data editor and an SQL console. The engine answers
// through /api/db with the project's Python (pm_db.py, the standard sqlite3 module).
import * as monaco from 'monaco-editor';
import { showTab } from './panel';
import { toast } from './toast';
import { cellText, typedValue, statementAt, statementsOf, sqlCompletions, changesOf, toCsv, toJson, toInserts } from './db-model';
import { openMenu } from './menu';

const $ = (id) => document.getElementById(id);
const PAGE = 200;

async function ask(request) {
    const res = await fetch('/api/db', { method: 'POST', body: JSON.stringify(request) });
    if (!res.ok)
        return { error: (await res.text()) || `HTTP ${res.status}` };
    return res.json();
}

export function setupDatabase(deps) {
    const tree = $('db-tree');
    const grid = $('db-grid');
    const status = $('db-status');
    const sourceBox = $('db-source');
    let sources = [];
    const schemas = new Map();   // path -> [objects]
    const open = new Set();      // the tree's open nodes: "path", "path/table"

    // ------------------------------------------------------------ data sources and their schema
    async function loadSources() {
        sources = await fetch('/api/db/sources').then((r) => (r.ok ? r.json() : [])).catch(() => []);
        const chosen = sourceBox.value;
        sourceBox.replaceChildren(...sources.filter((s) => s.exists).map((s) => new Option(s.server ? `${s.name} (${s.server === 'postgres' ? 'PostgreSQL' : 'MySQL'})` : s.path, s.path)));
        for (const [path, list] of databaseLists)
            for (const name of list.databases)
                if (name !== list.current && sources.some((s) => s.path === path))
                    sourceBox.add(new Option(`${name} (${sources.find((s) => s.path === path).name})`, `${path}/${name}`));
        if ([...sourceBox.options].some((o) => o.value === chosen))
            sourceBox.value = chosen;
        if (sources.length === 1)
            open.add(sources[0].path);
        for (const s of sources)
            if (open.has(s.path) && s.exists) {
                if (s.server) await loadDatabases(s.path);
                await loadSchema(s.path);
            }
        for (const key of open)
            if (key.startsWith('db:server:'))
                await loadSchema(key.slice(3));
        paintTree();
    }
    async function loadSchema(path, again = false) {
        if (schemas.has(path) && !again)
            return schemas.get(path);
        const r = await ask({ op: 'schema', path });
        schemas.set(path, r.objects ?? []);
        extras.set(path, { triggers: r.triggers ?? [], routines: r.routines ?? [] });
        if (r.error)
            toast(r.error);
        return schemas.get(path);
    }
    const extras = new Map();   // path -> { triggers, routines }: the stored functions and procedures, the triggers of the tables
    /** A routine's or trigger's definition, put at the end of the console's text to read or change (and run). */
    function showDefinition(what, sql) {
        setMode('console');
        const ed = consoleEditor();
        const model = ed.getModel();
        const text = model.getValue();
        const at = text.trimEnd().length ? text.replace(/\s*$/, '\n\n') : '';
        const definition = `-- ${what}\n${String(sql ?? '').trim()}\n`;
        model.setValue(at + definition);
        const line = model.getPositionAt(at.length).lineNumber;
        ed.revealLineInCenter(line);
        ed.setSelection(new monaco.Selection(line, 1, model.getLineCount(), 1));
        ed.focus();
    }
    function node(label, cls, detail, depth, toggle, onOpen) {
        const row = document.createElement('div');
        row.className = 'db-node ' + cls;
        row.style.paddingLeft = `${6 + depth * 14}px`;
        const caret = document.createElement('span');
        caret.className = 'caret';
        caret.textContent = toggle === null ? '' : toggle ? '▾' : '▸';
        const name = document.createElement('span');
        name.className = 'db-name';
        name.textContent = label;
        const extra = document.createElement('span');
        extra.className = 'db-detail';
        extra.textContent = detail ?? '';
        row.append(caret, name, extra);
        if (onOpen)
            row.ondblclick = onOpen;
        tree.appendChild(row);
        return row;
    }
    function paintTree() {
        tree.textContent = '';
        if (!sources.length) {
            const empty = document.createElement('div');
            empty.className = 'db-empty';
            empty.textContent = 'No SQLite database in the project. + adds a file (a new one is made); Server… connects to PostgreSQL or MySQL.';
            tree.appendChild(empty);
            return;
        }
        for (const s of sources) {
            if (s.offer) {
                // a PostgreSQL or MySQL database of the Django settings, not connected yet: a click opens the form filled from them
                const row = node(s.name, 'db-source db-server db-missing', `${kindName(s.server)} · ${s.detail} · not connected · ${s.django}`, 0, null);
                row.title = `The database ${s.offer.alias} of ${s.django}. Click to connect to it (the form is filled from the settings)`;
                row.onclick = () => serverForm({ ...s.offer, kind: s.server });
                continue;
            }
            if (!s.exists) {
                // named by the Django settings, not made yet: manage.py migrate makes it, with Django's tables
                const row = node(s.path, 'db-source db-missing', `not made yet · ${s.django}`, 0, null);
                row.title = `The database of ${s.django}. Click to make it: ${s.manage} migrate, in the Run panel`;
                row.onclick = () => {
                    if (s.manage && confirm(`Make ${s.path} by running ${s.manage} migrate?`))
                        void deps.runScript(s.manage, ['migrate']);
                };
                continue;
            }
            const isOpen = open.has(s.path);
            const row = s.server
                ? node(s.name, 'db-source db-server', `${kindName(s.server)} · ${s.detail}`, 0, isOpen)
                : node(s.path, 'db-source', `${Math.round(s.size / 1024)} KB`, 0, isOpen);
            row.title = s.server ? 'A database server; right-click to change its connection or disconnect it (its data stays where it is)'
                : (s.added ? 'Added by hand; right-click to take it out of the list' : 'Found in the project') + (s.django ? ` · the database of ${s.django}` : '');
            row.onclick = async () => {
                toggleNode(s.path);
                if (open.has(s.path)) {
                    if (s.server) await loadDatabases(s.path);
                    await loadSchema(s.path);
                }
                paintTree();
            };
            row.oncontextmenu = (e) => {
                e.preventDefault();
                if (s.server) {
                    const id = s.path.slice('server:'.length);
                    openMenu({ x: e.clientX, y: e.clientY }, [
                        { label: 'Change the connection…', run: () => void editServer(id) },
                        { label: 'Show the other databases again', run: () => { databaseLists.delete(s.path); open.add(s.path); void loadDatabases(s.path).then(paintTree); } },
                        '-',
                        { label: 'Disconnect', run: () => {
                            if (confirm(`Disconnect ${s.name}? Its saved passwords are forgotten; the server is not touched.`))
                                void fetch('/api/db/servers?id=' + encodeURIComponent(id), { method: 'DELETE' }).then(loadSources);
                        } },
                    ]);
                } else if (s.added && confirm(`Take ${s.path} out of the list? The file stays.`))
                    void fetch('/api/db/sources?path=' + encodeURIComponent(s.path), { method: 'DELETE' }).then(loadSources);
            };
            if (!isOpen)
                continue;
            // a server with more than one database: each is a node of its own (the one it was connected with first), opened by a click
            const list = s.server ? databaseLists.get(s.path) : null;
            if (list && list.databases.length > 1) {
                for (const name of list.databases) {
                    const path = name === list.current ? s.path : `${s.path}/${name}`;
                    const dbOpen = open.has('db:' + path);
                    const d = node(name, 'db-database', name === list.current ? 'database · connected with' : 'database', 1, dbOpen);
                    d.title = 'A database of this server: click to see its tables';
                    d.onclick = async () => { toggleNode('db:' + path); if (open.has('db:' + path)) await loadSchema(path); paintTree(); };
                    if (dbOpen)
                        paintObjects(path, 2);
                }
                continue;
            }
            paintObjects(s.path, 1);
        }
    }
    /** The tables and views of a data source (each opening into its columns, indexes and triggers), then its routines. */
    function paintObjects(path, depth) {
        for (const o of schemas.get(path) ?? []) {
            const key = `${path}/${o.name}`;
            const tableOpen = open.has(key);
            const t = node(o.name, 'db-' + o.kind, o.kind === 'view' ? 'view' : `${o.columns.length} columns`, depth, tableOpen, () => void openTable(path, o.name));
            t.title = 'Double-click to see its rows' + (o.kind === 'table' ? ' and change them' : '');
            t.onclick = () => { toggleNode(key); paintTree(); };
            if (!tableOpen)
                continue;
            for (const c of o.columns) {
                const fk = o.foreignKeys.find((f) => f.column === c.name);
                node(c.name, 'db-column' + (c.pk ? ' db-pk' : ''), [c.type || 'any', c.pk ? 'primary key' : '', c.notNull ? 'not null' : '', fk ? `→ ${fk.table}.${fk.to ?? ''}` : '',
                    c.default !== null && c.default !== undefined ? `= ${c.default}` : ''].filter(Boolean).join(' · '), depth + 1, null);
            }
            for (const ix of o.indexes)
                node(ix.name, 'db-index', `${ix.unique ? 'unique ' : ''}index (${ix.columns.join(', ')})`, depth + 1, null);
            for (const tr of (extras.get(path)?.triggers ?? []).filter((x) => x.table === o.name)) {
                const row = node(tr.name, 'db-trigger', ['trigger', [tr.timing, tr.event].filter(Boolean).join(' ')].filter(Boolean).join(' · '), depth + 1, null, () => showDefinition(`trigger ${tr.name} on ${tr.table}`, tr.sql));
                row.title = 'Double-click to see its definition in the console';
            }
        }
        // the stored functions and procedures (PostgreSQL, MySQL; SQLite has none)
        for (const r of extras.get(path)?.routines ?? []) {
            const detail = `${r.kind} (${r.args ?? ''})${r.returns ? ` → ${r.returns}` : ''}`;
            const row = node(r.name, 'db-routine', detail, depth, null, () => showDefinition(`${r.kind} ${r.name}`, r.sql));
            row.title = 'Double-click to see its definition in the console';
        }
    }
    const kindName = (kind) => (kind === 'postgres' ? 'PostgreSQL' : 'MySQL');
    // a server's databases: { databases, current }, asked for once when it is first opened
    const databaseLists = new Map();
    async function loadDatabases(path) {
        if (databaseLists.has(path))
            return databaseLists.get(path);
        const r = await ask({ op: 'databases', path });
        const list = r.error ? { databases: [], current: null } : { databases: r.databases ?? [], current: r.current };
        databaseLists.set(path, list);
        // the console can run against each of them
        const server = sources.find((x) => x.path === path);
        for (const name of list.databases) {
            const other = `${path}/${name}`;
            if (name !== list.current && ![...sourceBox.options].some((o) => o.value === other))
                sourceBox.add(new Option(`${name} (${server?.name ?? path})`, other));
        }
        return list;
    }
    const toggleNode = (key) => { if (open.has(key)) open.delete(key); else open.add(key); };

    $('db-refresh').onclick = async () => { schemas.clear(); databaseLists.clear(); await loadSources(); };
    $('db-add').onclick = async () => {
        const path = await deps.askText('Add an SQLite database', 'db.sqlite3', 'A file of the project, from its folder; a new one is made');
        if (!path?.trim())
            return;
        const res = await fetch('/api/db/sources', { method: 'POST', body: JSON.stringify({ path: path.trim() }) });
        if (!res.ok) {
            toast(await res.text());
            return;
        }
        open.add(path.trim().replace(/\\/g, '/'));
        await loadSources();
    };

    // ------------------------------------------------------------ a PostgreSQL or MySQL server
    const DEFAULT_PORTS = { postgres: '5432', mysql: '3306' };
    const DRIVERS = { postgres: 'psycopg[binary]', mysql: 'PyMySQL' };
    // TLS as libpq names it; MySQL has no "allow" or "prefer" here (PyMySQL either encrypts or does not)
    const TLS_MODES = [['', "The driver's default"], ['disable', 'Off'], ['allow', 'Allow'], ['prefer', 'Prefer'], ['require', 'Require (not checked)'],
        ['verify-ca', 'Verify the CA'], ['verify-full', 'Verify the CA and the host name']];
    $('db-add-server').onclick = () => void serverForm();
    /** The form of a server that is kept already, filled with its settings (its passwords are kept unless new ones are typed). */
    async function editServer(id) {
        const list = await fetch('/api/db/servers').then((r) => (r.ok ? r.json() : [])).catch(() => []);
        const server = list.find((x) => x.id === id);
        if (server)
            serverForm({ ...server, replaces: id });
        else
            toast('That server is not in the project any more.');
    }
    /**
     * The form that connects to a server: where it is and who connects, TLS, and an SSH tunnel through another machine (the SSH/SSL tab of
     * PyCharm's data sources). `start` fills it: a server to change (with `replaces`, its id), or what the Django settings name.
     */
    function serverForm(start = {}) {
        const shade = document.createElement('div');
        shade.className = 'ask-shade';
        const box = document.createElement('div');
        box.className = 'ask-box db-server-form';
        const title = document.createElement('div');
        title.className = 'ask-title';
        title.textContent = start.replaces ? `Change the connection of ${start.name ?? 'the server'}` : 'Connect to a database server';
        const fields = {};
        const addField = (key, label, element, hint, into = box) => {
            const row = document.createElement('label');
            row.className = 'db-form-row';
            const name = document.createElement('span');
            name.textContent = label;
            element.id = 'db-form-' + key;
            if (hint)
                element.title = hint;
            row.append(name, element);
            fields[key] = element;
            into.append(row);
            return row;
        };
        const kind = document.createElement('select');
        kind.append(new Option('PostgreSQL', 'postgres'), new Option('MySQL', 'mysql'));
        kind.value = start.kind ?? 'postgres';
        const input = (type = 'text', value = '', placeholder = '') => { const i = document.createElement('input'); i.type = type; i.value = value; i.placeholder = placeholder; i.spellcheck = false; i.autocomplete = 'off'; return i; };
        const passwordHint = start.replaces ? 'Empty keeps the password that is kept' : '';
        box.append(title);
        addField('kind', 'Server', kind);
        addField('host', 'Host', input('text', start.host ?? 'localhost'));
        addField('port', 'Port', input('text', String(start.port ?? DEFAULT_PORTS[kind.value])));
        addField('user', 'User', input('text', start.user ?? (kind.value === 'postgres' ? 'postgres' : 'root')));
        addField('password', 'Password', input('password', start.password ?? '', passwordHint), 'Kept in .pymagic/db-servers.private.json of the project: keep that file out of version control');
        addField('database', 'Database', input('text', start.database ?? (kind.value === 'postgres' ? 'postgres' : '')), 'The one connected to first; the server\'s other databases are listed under it in the tree');
        addField('name', 'Name', input('text', start.name ?? ''), 'Optional: how the server is listed');

        // TLS: the drivers' own settings
        const tls = document.createElement('details');
        tls.className = 'db-form-group';
        tls.innerHTML = '<summary>TLS / SSL</summary>';
        const mode = document.createElement('select');
        addField('sslMode', 'Encryption', mode, 'require encrypts; the verify modes also check the server\'s certificate', tls);
        addField('sslCa', 'CA file', input('text', start.sslCa ?? '', 'root.crt'), 'The certificate of the authority that signed the server\'s; a path, or from the project\'s folder', tls);
        addField('sslCert', 'Client certificate', input('text', start.sslCert ?? '', 'optional'), 'When the server asks for one', tls);
        addField('sslKey', 'Client key', input('text', start.sslKey ?? '', 'optional'), 'The key of the client certificate', tls);
        const fillModes = () => {
            const was = mode.value || start.sslMode || '';
            mode.replaceChildren(...TLS_MODES.filter(([m]) => kind.value === 'postgres' || !['allow', 'prefer'].includes(m)).map(([m, label]) => new Option(label, m)));
            mode.value = [...mode.options].some((o) => o.value === was) ? was : '';
        };
        fillModes();
        tls.open = !!(start.sslMode || start.sslCa || start.sslCert);

        // SSH: the server reached through another machine
        const ssh = document.createElement('details');
        ssh.className = 'db-form-group';
        ssh.innerHTML = '<summary>SSH tunnel</summary>';
        const sshNote = document.createElement('div');
        sshNote.className = 'ask-hint';
        sshNote.textContent = "Through Windows' own ssh (OpenSSH): Host and Port above are then the server as the SSH machine sees it (often localhost). A key file, ssh-agent or a password.";
        ssh.append(sshNote);
        addField('sshHost', 'SSH host', input('text', start.sshHost ?? '', 'empty: no tunnel'), 'The machine to go through', ssh);
        addField('sshPort', 'SSH port', input('text', String(start.sshPort ?? 22)), '', ssh);
        addField('sshUser', 'SSH user', input('text', start.sshUser ?? ''), 'Empty: the one of ~/.ssh/config, or your Windows user name', ssh);
        addField('sshKey', 'Key file', input('text', start.sshKey ?? '', 'empty: ssh-agent and ~/.ssh/id_*'), 'A private key file (OpenSSH format); ~/ for your folder', ssh);
        addField('sshPassword', 'SSH password', input('password', '', start.replaces ? 'Empty keeps the one that is kept' : 'only without a key'), 'Kept in .pymagic/db-servers.private.json, given to ssh only', ssh);
        ssh.open = !!start.sshHost;

        kind.onchange = () => {
            fields.port.value = DEFAULT_PORTS[kind.value];
            fields.user.value = kind.value === 'postgres' ? 'postgres' : 'root';
            fields.database.value = kind.value === 'postgres' ? 'postgres' : '';
            fillModes();
        };
        const note = document.createElement('div');
        note.className = 'ask-hint db-form-note';
        note.textContent = "The passwords are kept in .pymagic/db-servers.private.json of the project: keep that file out of version control. PyMagic connects with the project's Python and its psycopg / PyMySQL package.";
        const error = document.createElement('div');
        error.className = 'db-form-error';
        const row = document.createElement('div');
        row.className = 'ask-buttons';
        const install = document.createElement('button');
        install.textContent = 'Install the driver';
        install.hidden = true;
        const ok = document.createElement('button');
        ok.textContent = start.replaces ? 'Save' : 'Connect';
        ok.className = 'primary';
        const cancel = document.createElement('button');
        cancel.textContent = 'Cancel';
        row.append(install, ok, cancel);
        box.append(tls, ssh, note, error, row);
        shade.append(box);
        const done = () => shade.remove();
        cancel.onclick = done;
        shade.onmousedown = (e) => { if (e.target === shade) done(); };
        install.onclick = () => {
            window.dispatchEvent(new CustomEvent('pymagic:installPackages', { detail: [DRIVERS[kind.value]] }));
            done();
        };
        ok.onclick = async () => {
            ok.disabled = true;
            error.textContent = fields.sshHost.value.trim() ? 'Opening the SSH tunnel and connecting…' : 'Connecting…';
            install.hidden = true;
            const body = {};
            for (const [key, element] of Object.entries(fields))
                body[key] = element.value;
            if (start.replaces)
                body.replaces = start.replaces;
            let answer;
            try { answer = await fetch('/api/db/servers', { method: 'POST', body: JSON.stringify(body) }).then((r) => r.json()); }
            catch (e) { answer = { error: String(e) }; }
            ok.disabled = false;
            if (answer.error) {
                error.textContent = answer.error;
                install.hidden = !answer.missingDriver;
                return;
            }
            done();
            toast(`Connected: ${answer.version ?? 'ok'}`);
            if (start.replaces)
                for (const key of [...schemas.keys()]) if (key.startsWith('server:' + start.replaces)) schemas.delete(key);
            databaseLists.delete(answer.path);
            open.add(answer.path);
            await loadSources();
            sourceBox.value = answer.path;
        };
        box.onkeydown = (e) => {
            if (e.key === 'Escape') done();
            else if (e.key === 'Enter' && e.target !== cancel && e.target !== install && e.target.tagName !== 'SUMMARY') { e.preventDefault(); ok.click(); }
            e.stopPropagation();
        };
        document.body.append(shade);
        fields.host.focus();
        fields.host.select();
    }

    // ------------------------------------------------------------ the grid: a table's rows, or the console's results
    let view = null;   // { kind: 'table', path, table, columns, rows, total, editable, offset, order, desc, where } or { kind: 'result', columns, rows }
    let edits = new Map(), added = [], deleted = new Set();
    const dirty = () => edits.size > 0 || added.length > 0 || deleted.size > 0;

    function setMode(mode) {
        $('db-data-bar').hidden = mode !== 'table';
        $('db-console').hidden = mode !== 'console';
        $('db-mode-data').classList.toggle('active', mode === 'table');
        $('db-mode-console').classList.toggle('active', mode === 'console');
        if (mode === 'console')
            consoleEditor().layout();
    }
    $('db-mode-data').onclick = () => { setMode('table'); if (view?.kind !== 'table') { grid.textContent = ''; status.textContent = 'Double-click a table to see its rows.'; } };
    $('db-mode-console').onclick = () => setMode('console');

    async function openTable(path, table, keep = {}) {
        if (dirty() && !confirm('Leave the changes that were not submitted?'))
            return;
        setMode('table');
        const options = { order: keep.order ?? '', desc: keep.desc ?? false, where: keep.where ?? '', offset: keep.offset ?? 0 };
        $('db-where').value = options.where;
        const r = await ask({ op: 'rows', path, table, where: options.where, order: options.order, desc: options.desc, limit: PAGE, offset: options.offset });
        if (r.error) {
            status.textContent = r.error;
            status.className = 'err';
            return;
        }
        view = { kind: 'table', path, table, ...options, ...r };
        edits = new Map(); added = []; deleted = new Set();
        showTab('database');
        paintGrid();
    }
    function reloadTable(change = {}) {
        if (view?.kind === 'table')
            void openTable(view.path, view.table, { order: view.order, desc: view.desc, where: view.where, offset: view.offset, ...change });
    }
    $('db-where').onkeydown = (e) => { if (e.key === 'Enter') reloadTable({ where: $('db-where').value, offset: 0 }); };
    $('db-prev').onclick = () => reloadTable({ offset: Math.max(0, (view?.offset ?? 0) - PAGE) });
    $('db-next').onclick = () => reloadTable({ offset: (view?.offset ?? 0) + PAGE });
    $('db-reload').onclick = () => { if (!dirty() || confirm('Leave the changes that were not submitted?')) { edits = new Map(); added = []; deleted = new Set(); reloadTable(); } };
    $('db-add-row').onclick = () => { if (view?.editable) { added.push({}); paintGrid(); } };
    $('db-delete-rows').onclick = () => {
        const chosen = [...grid.querySelectorAll('tr.sel')].map((tr) => Number(tr.dataset.index)).filter((i) => i >= 0);
        if (!chosen.length) {
            toast('Click the number of a row to choose it (Ctrl or Shift for more).');
            return;
        }
        for (const i of chosen)
            if (deleted.has(i)) deleted.delete(i); else deleted.add(i);
        paintGrid();
    };
    $('db-import').onclick = async () => {
        if (view?.kind !== 'table' || !view.editable) {
            toast('Open a table first (double-click it in the tree).');
            return;
        }
        const csv = await deps.askText(`Import a CSV file into ${view.table}`, 'data.csv', 'A file of the project; its first line names the columns, an empty field is NULL');
        if (!csv?.trim())
            return;
        const r = await ask({ op: 'import', path: view.path, table: view.table, csv: csv.trim() });
        if (r.error) {
            toast(r.error);
            return;
        }
        toast(`${r.imported} row${r.imported === 1 ? '' : 's'} imported into ${view.table}`);
        reloadTable();
    };
    $('db-revert').onclick = () => { edits = new Map(); added = []; deleted = new Set(); paintGrid(); };
    $('db-submit').onclick = async () => {
        if (view?.kind !== 'table' || !dirty())
            return;
        const changes = changesOf(view.columns, view.rows, edits, added, deleted);
        const r = await ask({ op: 'apply', path: view.path, table: view.table, changes });
        if (r.error) {
            toast(r.error);
            return;
        }
        toast(`${r.changed} row${r.changed === 1 ? '' : 's'} changed`);
        edits = new Map(); added = []; deleted = new Set();
        reloadTable();
    };

    function editCell(td, rowIndex, col, isNew) {
        if (view?.kind !== 'table' || !view.editable || td.querySelector('input'))
            return;
        const input = document.createElement('input');
        input.className = 'db-edit';
        const current = isNew ? added[rowIndex][col] : edits.get(rowIndex)?.[col] !== undefined ? edits.get(rowIndex)[col] : view.rows[rowIndex].values[col];
        if (current !== null && typeof current === 'object' && 'blob' in current) {
            toast('A blob cannot be changed here.');
            input.remove();
            paintGrid();
            return;
        }
        input.value = current === undefined ? '' : cellText(current);
        input.title = 'Enter keeps the value (NULL alone for NULL), Escape leaves it';
        td.textContent = '';
        td.appendChild(input);
        input.focus();
        input.select();
        let done = false;
        const finish = (keep) => {
            if (done)
                return;
            done = true;
            if (keep) {
                const value = typedValue(input.value);
                if (isNew)
                    added[rowIndex][col] = value;
                else if (!sameValue(value, view.rows[rowIndex].values[col]) || edits.get(rowIndex)?.[col] !== undefined) {
                    const cells = edits.get(rowIndex) ?? {};
                    cells[col] = value;
                    edits.set(rowIndex, cells);
                }
            }
            paintGrid();
        };
        input.onkeydown = (e) => {
            if (e.key === 'Enter') { e.preventDefault(); finish(true); }
            else if (e.key === 'Escape') { e.preventDefault(); finish(false); }
        };
        input.onblur = () => finish(true);
    }

    // a cell left as it was shown (2.0 typed back over 2.0) is not a change
    const sameValue = (typed, shown) => (typed === null) === (shown === null) && cellText(typed) === cellText(shown);
    let lastClicked = -1;
    function paintGrid() {
        grid.textContent = '';
        if (!view)
            return;
        const table = document.createElement('table');
        const head = table.createTHead().insertRow();
        head.appendChild(document.createElement('th'));
        view.columns.forEach((c) => {
            const th = document.createElement('th');
            th.textContent = c + (view.kind === 'table' && view.order === c ? (view.desc ? ' ▼' : ' ▲') : '');
            if (view.kind === 'table') {
                th.title = 'Click to sort by it';
                th.onclick = () => reloadTable({ order: c, desc: view.order === c ? !view.desc : false, offset: 0 });
            }
            head.appendChild(th);
        });
        const body = table.createTBody();
        const addRow = (values, index, cls, isNew) => {
            const tr = body.insertRow();
            tr.className = cls;
            tr.dataset.index = String(isNew ? -1 : index);
            const th = document.createElement('th');
            th.textContent = isNew ? '+' : String((view.offset ?? 0) + index + 1);
            th.onclick = (e) => {
                if (isNew)
                    return;
                if (e.shiftKey && lastClicked >= 0) {
                    const [a, b] = [Math.min(lastClicked, index), Math.max(lastClicked, index)];
                    body.querySelectorAll('tr').forEach((r) => { const i = Number(r.dataset.index); if (i >= a && i <= b) r.classList.add('sel'); });
                }
                else {
                    if (!e.ctrlKey)
                        body.querySelectorAll('tr.sel').forEach((r) => { if (r !== tr) r.classList.remove('sel'); });
                    tr.classList.toggle('sel');
                }
                lastClicked = index;
            };
            tr.appendChild(th);
            values.forEach((v, col) => {
                const td = tr.insertCell();
                const edited = isNew ? added[index][col] !== undefined : edits.get(index)?.[col] !== undefined;
                const shown = isNew ? added[index][col] : edited ? edits.get(index)[col] : v;
                td.textContent = isNew && shown === undefined ? '' : cellText(shown);
                if (shown === null)
                    td.classList.add('db-null');
                if (edited)
                    td.classList.add('db-changed');
                td.title = typeof shown === 'string' && shown.length > 60 ? shown.slice(0, 2000) : '';
                td.ondblclick = () => editCell(td, index, col, isNew);
            });
        };
        if (view.kind === 'table') {
            view.rows.forEach((r, i) => addRow(r.values, i, deleted.has(i) ? 'db-deleted' : '', false));
            added.forEach((cells, i) => addRow(view.columns.map((_, c) => cells[c]), i, 'db-added', true));
        }
        else
            view.rows.forEach((r, i) => addRow(r, i, '', false));
        grid.appendChild(table);
        status.className = '';
        if (view.kind === 'table') {
            const from = view.total ? view.offset + 1 : 0, to = view.offset + view.rows.length;
            const pending = dirty() ? ` · ${edits.size} changed, ${added.length} added, ${deleted.size} deleted (not submitted)` : '';
            status.textContent = `${view.table}: rows ${from}–${to} of ${view.total}${view.editable ? '' : ' · read only'}${pending}`;
            $('db-prev').disabled = view.offset === 0;
            $('db-next').disabled = to >= view.total;
            for (const id of ['db-add-row', 'db-delete-rows'])
                $(id).disabled = !view.editable;
            $('db-submit').disabled = $('db-revert').disabled = !dirty();
        }
    }

    // ------------------------------------------------------------ the console
    let editor = null;
    function consoleEditor() {
        if (editor)
            return editor;
        editor = monaco.editor.create($('db-sql'), {
            value: '', language: 'sql', minimap: { enabled: false }, lineNumbers: 'on', automaticLayout: true, scrollBeyondLastLine: false,
            fontSize: deps.editor.getOption(monaco.editor.EditorOption.fontSize), wordBasedSuggestions: 'off',
        });
        try { editor.setValue(localStorage.getItem('pymagic.db.console') ?? ''); } catch { /* storage unavailable */ }
        editor.onDidChangeModelContent(() => { try { localStorage.setItem('pymagic.db.console', editor.getValue()); } catch { /* storage unavailable */ } });
        return editor;
    }
    /**
     * Runs SQL of an editor against the console's data source: the selection or the statement at the cursor, or with
     * `whole` every statement of the text in turn (Run file), stopping at the first one SQLite refuses, which is selected.
     * The last result set is shown in the grid.
     */
    async function runSql(ed, whole = false) {
        const model = ed.getModel(), sel = ed.getSelection();
        const path = sourceBox.value;
        if (!path) {
            toast('No data source: add an SQLite file in the Database tab (+).');
            return;
        }
        if (dirty() && !confirm('Leave the changes that were not submitted?'))
            return;
        const statements = whole ? statementsOf(model.getValue())
            : sel && !sel.isEmpty() ? [{ text: model.getValueInRange(sel).trim().replace(/;\s*$/, '') }]
                : [statementAt(model.getValue(), model.getOffsetAt(ed.getPosition()))];
        if (!statements.length || !statements[0].text) {
            toast('No statement at the cursor.');
            return;
        }
        showTab('database');
        setMode(ed === editor ? 'console' : view?.kind === 'table' ? 'table' : 'console');
        status.className = '';
        status.textContent = 'Running…';
        let changed = 0, last = null, total = 0;
        for (const [i, s] of statements.entries()) {
            const r = await ask({ op: 'run', path, sql: s.text, limit: 1000 });
            total += r.time ?? 0;
            if (r.error) {
                status.className = 'err';
                status.textContent = (statements.length > 1 ? `Statement ${i + 1} of ${statements.length}: ` : '') + r.error;
                if (s.start !== undefined && whole) {
                    const from = model.getPositionAt(s.start + (model.getValue().slice(s.start).length - model.getValue().slice(s.start).trimStart().length));
                    const to = model.getPositionAt(s.end);
                    ed.setSelection(new monaco.Selection(from.lineNumber, from.column, to.lineNumber, to.column));
                    ed.revealLineInCenter(from.lineNumber);
                }
                break;
            }
            if (r.columns)
                last = r;
            else if (r.changed > 0)
                changed += r.changed;
            if (i === statements.length - 1)
                status.textContent = '';
        }
        if (status.className === 'err')
            return refreshSchema(path);
        const ms = `${Math.round(total * 1000)} ms`;
        const count = statements.length > 1 ? `${statements.length} statements, ` : '';
        if (last) {
            view = { kind: 'result', columns: last.columns, rows: last.rows, offset: 0 };
            edits = new Map(); added = []; deleted = new Set();
            paintGrid();
            status.textContent = `${count}${last.rows.length}${last.more ? '+' : ''} row${last.rows.length === 1 ? '' : 's'}${changed ? `, ${changed} changed` : ''} in ${ms}${last.more ? ' (the first 1000)' : ''}`;
        }
        else
            status.textContent = `${count}${changed} row${changed === 1 ? '' : 's'} changed in ${ms}`;
        await refreshSchema(path);
    }
    // a statement may have changed the tables
    async function refreshSchema(path) {
        schemas.delete(path);
        await loadSchema(path);
        paintTree();
    }
    const runStatement = () => runSql(consoleEditor());
    $('db-run').onclick = () => void runStatement();
    // .sql files of the project: Ctrl+Enter runs the statement at the cursor, Ctrl+Shift+Enter the whole file, against the console's source
    function addSqlActions(ed) {
        const when = 'editorLangId == sql';
        ed.addAction({ id: 'pymagic.sql.runStatement', label: 'Run SQL statement (Database)', keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter],
            precondition: when, contextMenuGroupId: 'navigation', contextMenuOrder: 0, run: () => runSql(ed) });
        ed.addAction({ id: 'pymagic.sql.runFile', label: 'Run SQL file (Database)', keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.Enter],
            precondition: when, contextMenuGroupId: 'navigation', contextMenuOrder: 1, run: () => runSql(ed, true) });
    }
    // the editors made before this panel (the main one), and every one made after it
    monaco.editor.getEditors().forEach(addSqlActions);
    monaco.editor.onDidCreateEditor(addSqlActions);
    /** What the grid shows, as CSV, JSON or INSERT statements (into the open table, or `result`), copied or saved as a file of the project. */
    $('db-export').onclick = (e) => {
        if (!view) {
            toast('Nothing to export: open a table or run a statement.');
            return;
        }
        const rows = view.kind === 'table' ? view.rows.map((r) => r.values) : view.rows;
        const table = view.kind === 'table' ? view.table : 'result';
        const formats = [['CSV', 'csv', () => toCsv(view.columns, rows)], ['JSON', 'json', () => toJson(view.columns, rows)], ['SQL inserts', 'sql', () => toInserts(table, view.columns, rows)]];
        const copy = (label, text) => void navigator.clipboard.writeText(text()).then(() => toast(`Copied as ${label}`), () => toast('Could not copy'));
        const save = async (label, ext, text) => {
            const path = await deps.askText(`Save as ${label}`, `${table}.${ext}`, 'A file of the project');
            if (!path?.trim())
                return;
            const res = await fetch('/api/file?path=' + encodeURIComponent(path.trim()), { method: 'PUT', body: text() });
            toast(res.ok ? `Saved ${path.trim()} (${rows.length} rows)` : `Could not save: ${await res.text()}`);
        };
        const box = e.currentTarget.getBoundingClientRect();
        openMenu({ x: box.left, y: box.bottom }, [
            ...formats.map(([label, , text]) => ({ label: `Copy as ${label}`, run: () => copy(label, text) })),
            '-',
            ...formats.map(([label, ext, text]) => ({ label: `Save as ${label}…`, run: () => void save(label, ext, text) })),
        ]);
    };
    // completion of tables, columns and keywords, from the console's data source
    monaco.languages.registerCompletionItemProvider('sql', {
        triggerCharacters: ['.', ' '],
        async provideCompletionItems(model, position) {
            if (!sourceBox.value)
                return { suggestions: [] };
            const objects = await loadSchema(sourceBox.value);
            const text = model.getValue(), offset = model.getOffsetAt(position);
            const statement = statementAt(text, offset);
            const before = text.slice(statement.start, offset);
            const word = model.getWordUntilPosition(position);
            const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
            const K = monaco.languages.CompletionItemKind;
            const kinds = { table: K.Class, view: K.Interface, column: K.Field, keyword: K.Keyword };
            return { suggestions: sqlCompletions(before, statement.text, objects).map((c, i) => ({ label: c.label, kind: kinds[c.kind], detail: c.detail, insertText: c.label, range, sortText: String(i).padStart(4, '0') })) };
        },
    });

    $('t-database').onclick = () => { showTab('database'); void loadSources(); };
    setMode('table');
    return {
        show: () => { showTab('database'); void loadSources(); },
        /** The tables and views of the chosen data source (the first when none is chosen), for SQL completed elsewhere. */
        async objects() {
            if (!sources.length) await loadSources();
            const path = sourceBox.value || sources.find((s) => s.exists)?.path;
            return path ? await loadSchema(path) : [];
        },
    };
}

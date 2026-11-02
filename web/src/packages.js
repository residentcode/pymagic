// Packages tab: what is installed in the project's interpreter, install / upgrade / uninstall with pip, requirements files.
import { RunClient } from './runclient';
import { showTab } from './panel';
import { countNodes, looksLikeSource, pinned, settingsFromForm, settingsSummary, staleText, toggled, treeLabel, treeRows, versionOptions } from './packages-model';
const $ = (id) => document.getElementById(id);
const norm = (n) => n.trim().replace(/[-_.]+/g, '-').toLowerCase();
const PROTECTED = new Set(['pip', 'setuptools', 'wheel']);
export function setupPackages() {
    let packages = [];
    let outdated = new Map();
    let selected = null;
    let client = null;
    let loaded = false;
    let isVenv = true;
    let hasPip = true;
    let armed = null; // the uninstall button that was clicked once
    let armTimer;
    let hintTimer;
    let treeMode = null; // { name, reverse, roots, count } while the table shows a dependency tree
    let flipped = new Set(); // tree rows turned the other way from their default (open or closed)
    let lockArmed = null;
    const table = $('pk-table');
    const log = $('pk-log');
    const banner = $('pk-banner');
    const input = $('pk-install');
    // ------------------------------------------------------------ loading
    async function load() {
        loaded = true;
        table.textContent = 'Loading…';
        let data;
        try {
            data = await (await fetch('/api/packages')).json();
        }
        catch {
            table.textContent = 'The host is not reachable.';
            return;
        }
        packages = data.packages ?? [];
        isVenv = data.isVenv ?? true;
        hasPip = data.hasPip !== false;
        setBanner(data);
        $('pk-user-label').style.display = !isVenv && data.interpreter ? '' : 'none';
        renderTable();
        void loadRequirements();
        void loadLocks();
        if (treeMode)
            void showTree(treeMode.name, treeMode.reverse);
    }
    function setBanner(data) {
        banner.textContent = '';
        banner.className = '';
        const add = (text, action) => {
            banner.className = 'warn';
            const span = document.createElement('span');
            span.textContent = text + ' ';
            banner.appendChild(span);
            if (action) {
                const b = document.createElement('button');
                b.textContent = action[0];
                b.onclick = action[1];
                banner.appendChild(b);
            }
        };
        if (data.error === 'no interpreter selected')
            add('No Python interpreter is selected.');
        else if (!hasPip)
            add('pip is not installed for this interpreter.', ['Install pip', () => void run('ensurepip', [])]);
        else if (data.error)
            add(data.error);
        else if (!isVenv && !data.canInstall)
            add('This is a system interpreter and its folders are read-only: installing may fail. Use a virtual environment.', ['Create .venv', () => $('b-venv').click()]);
        else if (data.manager === 'conda') {
            banner.className = 'info';
            banner.textContent = 'A conda environment: packages are listed, installed and removed with conda, from its channels. Paths, git addresses, requirement files and what pip installed go through pip.';
        }
        else if (data.manager === 'uv-project') {
            banner.className = 'info';
            banner.textContent = 'A uv project: Install adds the package to pyproject.toml (uv add) and Uninstall takes it out (uv remove); uv.lock and .venv follow.';
        }
        else if (data.manager === 'uv') {
            banner.className = 'info';
            banner.textContent = 'This environment was made by uv: its packages are listed, installed and removed with uv pip.';
        }
    }
    async function loadRequirements() {
        const holder = $('pk-reqs');
        holder.textContent = '';
        let files = [];
        try {
            files = await (await fetch('/api/requirements')).json();
        }
        catch {
            return;
        }
        for (const f of files) {
            const missing = f.requirements.filter((r) => !r.installed);
            const row = document.createElement('div');
            row.className = 'reqrow';
            const label = document.createElement('span');
            label.textContent = `${f.file}: ${f.requirements.length - missing.length} of ${f.requirements.length} installed`;
            row.appendChild(label);
            if (missing.length) {
                const miss = document.createElement('span');
                miss.className = 'missing';
                miss.textContent = ' — missing ' + missing.map((m) => m.name).join(', ');
                row.appendChild(miss);
                const b = document.createElement('button');
                b.textContent = 'Install missing';
                b.disabled = !hasPip;
                b.onclick = () => f.file.endsWith('.txt') ? void run('installRequirements', [], { file: f.file }) : void run('install', missing.map((m) => m.spec));
                row.appendChild(b);
            }
            holder.appendChild(row);
        }
    }
    // ------------------------------------------------------------ table
    function renderTable() {
        if (treeMode) {
            renderTree();
            return;
        }
        table.textContent = '';
        if (!hasPip || packages.length === 0) {
            table.textContent = hasPip ? 'No packages installed.' : '';
            return;
        }
        const filter = $('pk-filter').value.trim().toLowerCase();
        const head = document.createElement('div');
        head.className = 'pkrow head';
        head.innerHTML = '<span>Package</span><span>Version</span><span>Latest</span><span></span>';
        table.appendChild(head);
        for (const p of packages) {
            if (filter && !p.name.toLowerCase().includes(filter))
                continue;
            const row = document.createElement('div');
            row.className = 'pkrow' + (selected === norm(p.name) ? ' sel' : '');
            const name = document.createElement('span');
            name.textContent = p.name + (p.editable ? '  (editable)' : '');
            const version = document.createElement('span');
            version.textContent = p.version;
            const latest = document.createElement('span');
            const o = outdated.get(norm(p.name));
            if (o) {
                latest.textContent = o.latest;
                latest.className = 'newer';
            }
            const actions = document.createElement('span');
            actions.className = 'actions';
            if (o) {
                const up = document.createElement('button');
                up.textContent = '⬆';
                up.title = `Upgrade to ${o.latest}`;
                up.disabled = client !== null;
                up.onclick = (e) => { e.stopPropagation(); void run('install', [p.name], { upgrade: true }); };
                actions.appendChild(up);
            }
            const del = document.createElement('button');
            const key = norm(p.name);
            del.textContent = armed === key ? 'Really?' : '🗑';
            del.title = PROTECTED.has(key) ? 'pip, setuptools and wheel are needed to manage packages' : 'Uninstall';
            del.className = armed === key ? 'armed' : '';
            del.disabled = client !== null || PROTECTED.has(key);
            del.onclick = (e) => {
                e.stopPropagation();
                if (armed === key) {
                    armed = null;
                    void run('uninstall', [p.name]);
                    return;
                }
                armed = key;
                clearTimeout(armTimer);
                armTimer = window.setTimeout(() => { armed = null; renderTable(); }, 3000); // a second click is needed
                renderTable();
            };
            actions.appendChild(del);
            row.append(name, version, latest, actions);
            row.onclick = () => select(p);
            table.appendChild(row);
        }
    }
    async function select(p) {
        selected = norm(p.name);
        renderTable();
        $('pk-title').textContent = p.name;
        const detail = $('pk-detail');
        detail.textContent = 'Loading…';
        const res = await fetch('/api/packages/info?name=' + encodeURIComponent(p.name));
        if (!res.ok) {
            detail.textContent = 'No details available.';
            return;
        }
        const info = await res.json();
        detail.textContent = '';
        const line = (label, value) => {
            if (!value || (Array.isArray(value) && value.length === 0))
                return;
            const div = document.createElement('div');
            const b = document.createElement('b');
            b.textContent = label + ' ';
            div.append(b, Array.isArray(value) ? value.join(', ') : value);
            detail.appendChild(div);
        };
        line('Version', info.version);
        line('Summary', info.summary);
        line('Home page', info.homePage);
        line('License', info.license);
        line('Location', info.location);
        line('Requires', info.requires);
        line('Required by', info.requiredBy);
        if (info.editable)
            line('', 'Installed in editable mode');
        const acts = document.createElement('div');
        acts.className = 'pkacts';
        const button = (text, title, onclick) => {
            const b = document.createElement('button');
            b.textContent = text;
            b.title = title;
            b.onclick = onclick;
            acts.appendChild(b);
        };
        button('Versions…', 'List the releases the package index has and install one of them', () => void showVersions(p, info.version));
        button('Requires tree', 'What this package needs, and what those need', () => void showTree(p.name, false));
        button('Required by tree', 'Which packages need this one, and which need those', () => void showTree(p.name, true));
        detail.appendChild(acts);
        const holder = document.createElement('div');
        holder.id = 'pk-versions';
        detail.appendChild(holder);
    }
    // ------------------------------------------------------------ versions
    async function showVersions(p, current) {
        const holder = $('pk-versions');
        if (!holder)
            return;
        holder.textContent = 'Asking the package index…';
        let data;
        try {
            const res = await fetch('/api/packages/versions?name=' + encodeURIComponent(p.name));
            data = await res.json().catch(() => ({}));
            if (!res.ok) {
                holder.textContent = data.error ?? (res.status === 404 ? `"${p.name}" is not on the package index.` : 'The versions could not be listed.');
                return;
            }
        }
        catch {
            holder.textContent = 'The package index is not reachable.';
            return;
        }
        const versions = data.versions ?? [];
        holder.textContent = '';
        const select = document.createElement('select');
        select.id = 'pk-version-select';
        const pre = document.createElement('input');
        pre.type = 'checkbox';
        pre.id = 'pk-version-pre';
        const fill = () => {
            select.textContent = '';
            for (const o of versionOptions(versions, { pre: pre.checked, installed: current })) {
                const opt = document.createElement('option');
                opt.value = o.value;
                opt.textContent = o.label;
                select.appendChild(opt);
            }
            const mine = [...select.options].find((o) => o.value === current);
            if (mine)
                select.value = current;
        };
        fill();
        pre.onchange = fill;
        const go = document.createElement('button');
        go.id = 'pk-version-install';
        go.textContent = 'Install this version';
        go.disabled = client !== null;
        go.onclick = () => { if (select.value)
            void run('install', [pinned(p.name, select.value)]); };
        const label = document.createElement('label');
        label.append(pre, ' pre-releases');
        holder.append(`${versions.length} releases${data.source === 'pip' ? ' (from the configured index)' : ''}: `, select, ' ', go, ' ', label);
    }
    // ------------------------------------------------------------ dependency tree
    async function showTree(name, reverse) {
        treeMode = { name, reverse, roots: null, count: 0 };
        table.textContent = 'Reading the environment…';
        let data;
        try {
            const res = await fetch('/api/packages/tree' + (name ? `?name=${encodeURIComponent(name)}&reverse=${reverse}` : ''));
            data = await res.json();
            if (!res.ok) {
                table.textContent = data.error ?? 'The tree could not be read.';
                return;
            }
        }
        catch {
            table.textContent = 'The host is not reachable.';
            return;
        }
        if (!treeMode)
            return;
        treeMode.roots = data.roots ?? [];
        treeMode.count = data.count ?? 0;
        flipped = new Set();
        renderTree();
    }
    function renderTree() {
        table.textContent = '';
        const bar = document.createElement('div');
        bar.id = 'pk-treebar';
        const what = treeMode.name ? (treeMode.reverse ? `What needs ${treeMode.name}` : `What ${treeMode.name} needs`) : 'All packages, by what is needed';
        const back = document.createElement('button');
        back.textContent = '← Packages';
        back.id = 'pk-tree-back';
        back.onclick = () => { treeMode = null; $('pk-tree').classList.remove('on'); renderTable(); };
        const text = document.createElement('span');
        text.textContent = treeMode.roots ? `${what} (${countNodes(treeMode.roots)} of ${treeMode.count} installed packages shown)` : what;
        bar.append(back, text);
        if (treeMode.name) {
            const all = document.createElement('button');
            all.textContent = 'Whole environment';
            all.onclick = () => void showTree(null, false);
            bar.appendChild(all);
        }
        table.appendChild(bar);
        if (!treeMode.roots)
            return;
        if (treeMode.roots.length === 0) {
            table.appendChild(Object.assign(document.createElement('div'), { className: 'pktree', textContent: 'Nothing to show.' }));
            return;
        }
        $('pk-tree').classList.add('on');
        for (const row of treeRows(treeMode.roots, flipped)) {
            const div = document.createElement('div');
            div.className = 'pktree';
            div.style.paddingLeft = 8 + row.depth * 16 + 'px';
            const twisty = document.createElement('span');
            twisty.className = 'twisty';
            twisty.textContent = row.hasChildren ? (row.open ? '▾' : '▸') : '';
            if (row.hasChildren)
                twisty.onclick = () => { flipped = toggled(flipped, row); renderTree(); };
            const label = document.createElement('span');
            label.textContent = treeLabel(row.node);
            if (row.node.missing || row.node.cycle)
                label.className = 'bad';
            div.append(twisty, label);
            if (row.node.specifier) {
                const spec = document.createElement('span');
                spec.className = 'spec';
                spec.textContent = '  ' + row.node.specifier;
                div.appendChild(spec);
            }
            if (!row.node.missing)
                label.style.cursor = 'pointer';
            label.onclick = () => { const p = packages.find((x) => norm(x.name) === norm(row.node.name)); if (p)
                void select(p); };
            table.appendChild(div);
        }
    }
    // ------------------------------------------------------------ lock files
    async function loadLocks() {
        const holder = $('pk-locks');
        holder.textContent = '';
        let locks = [];
        try {
            locks = await (await fetch('/api/packages/locks')).json();
        }
        catch {
            return;
        }
        for (const lock of locks) {
            const row = document.createElement('div');
            row.className = 'lockrow';
            row.dataset.file = lock.file;
            const label = document.createElement('span');
            label.textContent = `${lock.file} (${lock.tool}): `;
            row.appendChild(label);
            const cmd = document.createElement('code');
            cmd.textContent = lock.command;
            row.appendChild(cmd);
            const b = document.createElement('button');
            b.className = 'locksync';
            b.textContent = lockArmed === lock.file ? 'Really? Packages the lock does not list are removed' : 'Sync';
            b.classList.toggle('armed', lockArmed === lock.file);
            b.disabled = !lock.available || client !== null;
            b.title = lock.available ? 'Make the environment match this lock file' : (lock.note ?? '');
            b.onclick = () => {
                if (lockArmed === lock.file) {
                    lockArmed = null;
                    void run('lockSync', [], { file: lock.file });
                    return;
                }
                lockArmed = lock.file;
                clearTimeout(armTimer);
                armTimer = window.setTimeout(() => { lockArmed = null; void loadLocks(); }, 4000);
                void loadLocks();
            };
            row.appendChild(b);
            if (lock.compile) {
                // requirements.txt made again from requirements.in (Pipfile.lock from the Pipfile): the file is rewritten, so a second click
                const c = document.createElement('button');
                c.className = 'lockcompile';
                const armedKey = 'compile:' + lock.file;
                c.textContent = lockArmed === armedKey ? `Really? ${lock.file} is written again` : lock.kind === 'pipenv' ? 'Lock' : 'Compile';
                c.classList.toggle('armed', lockArmed === armedKey);
                c.disabled = client !== null;
                c.title = lock.compile;
                c.onclick = () => {
                    if (lockArmed === armedKey) {
                        lockArmed = null;
                        void run('lockCompile', [], { file: lock.file });
                        return;
                    }
                    lockArmed = armedKey;
                    clearTimeout(armTimer);
                    armTimer = window.setTimeout(() => { lockArmed = null; void loadLocks(); }, 4000);
                    void loadLocks();
                };
                row.appendChild(c);
            }
            const fresh = staleText(lock);
            if (fresh) {
                const s = document.createElement('span');
                s.className = 'lockstate ' + fresh.kind;
                s.textContent = '  ' + fresh.text;
                row.appendChild(s);
            }
            if (!lock.available && lock.note) {
                const note = document.createElement('span');
                note.className = 'note';
                note.textContent = '  ' + lock.note;
                row.appendChild(note);
            }
            holder.appendChild(row);
        }
    }
    // ------------------------------------------------------------ index settings
    async function openConfig() {
        const box = $('pk-config');
        if (!box.hidden) {
            box.hidden = true;
            return;
        }
        $('pk-config-msg').textContent = '';
        // the fields are filled before the form shows, so what is typed into it is never overwritten by the answer
        try {
            const s = await (await fetch('/api/packages/settings')).json();
            $('pk-index-url').value = s.indexUrl ?? '';
            $('pk-extra-urls').value = (s.extraIndexUrls ?? []).join('\n');
            $('pk-trusted').value = (s.trustedHosts ?? []).join('\n');
            $('pk-proxy').value = s.proxy ?? '';
        }
        catch { /* the host is not reachable */ }
        box.hidden = false;
    }
    async function saveConfig() {
        const msg = $('pk-config-msg');
        msg.className = '';
        const body = settingsFromForm({ indexUrl: $('pk-index-url').value, extraIndexUrls: $('pk-extra-urls').value, trustedHosts: $('pk-trusted').value, proxy: $('pk-proxy').value });
        const res = await fetch('/api/packages/settings', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) {
            msg.className = 'err';
            msg.textContent = data.error ?? 'Not saved.';
            return;
        }
        msg.textContent = settingsSummary(data) ? 'Saved: ' + settingsSummary(data) : 'Saved: the defaults are used.';
        $('pk-config-open').classList.toggle('on', !!settingsSummary(data));
        outdated = new Map();
        $('pk-outdated').textContent = 'Check updates';
    }
    // ------------------------------------------------------------ install from a path, a git address, a link
    function openSource(text) {
        $('pk-source').hidden = false;
        if (text !== undefined)
            $('pk-source-text').value = text;
        $('pk-source-text').focus();
    }
    async function checkSource() {
        const confirm = $('pk-confirm');
        confirm.hidden = false;
        confirm.textContent = 'Checking…';
        let data;
        try {
            const res = await fetch('/api/packages/source', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ source: $('pk-source-text').value, editable: $('pk-source-editable').checked }) });
            data = await res.json();
            if (!res.ok) {
                confirm.textContent = '';
                confirm.appendChild(Object.assign(document.createElement('div'), { className: 'err', textContent: data.error ?? 'That cannot be installed.' }));
                return;
            }
        }
        catch {
            confirm.textContent = 'The host is not reachable.';
            return;
        }
        confirm.textContent = '';
        const title = document.createElement('h3');
        title.textContent = data.title + (data.editable ? ' (editable)' : '');
        confirm.appendChild(title);
        const src = document.createElement('div');
        src.className = 'detail';
        src.textContent = data.source;
        confirm.appendChild(src);
        for (const d of data.details ?? [])
            confirm.appendChild(Object.assign(document.createElement('div'), { className: 'detail', textContent: d }));
        for (const w of data.warnings ?? [])
            confirm.appendChild(Object.assign(document.createElement('div'), { className: 'warn', textContent: '⚠ ' + w }));
        const buttons = document.createElement('div');
        buttons.className = 'buttons';
        const yes = document.createElement('button');
        yes.id = 'pk-source-install';
        yes.textContent = 'I trust this: install';
        yes.disabled = client !== null || !hasPip;
        yes.onclick = () => {
            confirm.hidden = true;
            $('pk-source').hidden = true;
            void run('installSource', [], { source: data.source, token: data.token });
        };
        const no = document.createElement('button');
        no.textContent = 'Cancel';
        no.onclick = () => { confirm.hidden = true; };
        buttons.append(yes, no);
        confirm.appendChild(buttons);
    }
    // ------------------------------------------------------------ operations
    async function run(op, names, extra = {}) {
        if (client)
            return;
        log.textContent = '';
        showTab('packages');
        const append = (text, cls = '') => {
            const span = document.createElement('span');
            if (cls)
                span.className = cls;
            span.textContent = text.split('\r\n').join('\n');
            log.appendChild(span);
            log.scrollTop = log.scrollHeight;
        };
        const user = $('pk-user').checked;
        client = new RunClient({ type: 'startPip', op, packages: names, user, ...extra });
        setBusy(true);
        const done = () => { client?.close(); client = null; setBusy(false); void load(); window.dispatchEvent(new CustomEvent('pymagic:packagesChanged')); };
        client.onMessage = (m) => {
            if (m.type === 'output')
                append(m.text, m.stream === 'stderr' ? 'err' : '');
            else if (m.type === 'error') {
                append(m.message + '\n', 'err');
                done();
            }
            else if (m.type === 'exited') {
                append(m.code === 0 ? '\nDone.\n' : `\npip exited with code ${m.code}\n`, m.code === 0 ? 'info' : 'err');
                done();
            }
        };
        client.onClose = () => { if (client)
            done(); };
    }
    function setBusy(busy) {
        for (const id of ['pk-go', 'pk-outdated', 'pk-refresh'])
            $(id).disabled = busy;
        input.disabled = busy;
        $('packages-tab').classList.toggle('busy', busy);
        renderTable();
    }
    function install() {
        const names = input.value.split(/\s+/).filter(Boolean);
        if (names.length === 0)
            return;
        if (looksLikeSource(input.value)) {
            // a path, git address or link is never installed straight from the box: it is checked and confirmed first
            const text = input.value.trim();
            input.value = '';
            $('pk-hint').textContent = '';
            openSource(text);
            void checkSource();
            return;
        }
        input.value = '';
        $('pk-hint').textContent = '';
        void run('install', names);
    }
    async function checkUpdates() {
        $('pk-outdated').disabled = true;
        $('pk-outdated').textContent = 'Checking…';
        try {
            const list = await (await fetch('/api/packages/outdated')).json();
            outdated = new Map(list.map((o) => [norm(o.name), o]));
            $('pk-outdated').textContent = list.length ? `${list.length} updates` : 'Up to date';
        }
        catch {
            $('pk-outdated').textContent = 'Check updates';
        }
        $('pk-outdated').disabled = false;
        renderTable();
    }
    // a hint about what the package index knows while typing a name
    input.oninput = () => {
        clearTimeout(hintTimer);
        const hint = $('pk-hint');
        const first = input.value.trim().split(/\s+/).pop() ?? '';
        const name = first.split(/[\[<>=!~]/)[0];
        if (!/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(name)) {
            hint.textContent = '';
            return;
        }
        hintTimer = window.setTimeout(async () => {
            try {
                const res = await fetch('/api/pypi?name=' + encodeURIComponent(name));
                if (res.status === 404)
                    hint.textContent = `"${name}" is not on PyPI`;
                else if (res.ok) {
                    const j = await res.json();
                    hint.textContent = `${j.name} ${j.version} — ${j.summary}`;
                }
                else
                    hint.textContent = '';
            }
            catch {
                hint.textContent = '';
            }
        }, 450);
    };
    input.onkeydown = (e) => { if (e.key === 'Enter')
        install(); };
    $('pk-go').onclick = install;
    $('pk-refresh').onclick = () => void load();
    $('pk-tree').onclick = () => { if (treeMode) { treeMode = null; $('pk-tree').classList.remove('on'); renderTable(); } else void showTree(null, false); };
    $('pk-config-open').onclick = () => void openConfig();
    $('pk-config-close').onclick = () => { $('pk-config').hidden = true; };
    $('pk-config-save').onclick = () => void saveConfig();
    $('pk-source-open').onclick = () => { if ($('pk-source').hidden) openSource(); else $('pk-source').hidden = true; };
    $('pk-source-close').onclick = () => { $('pk-source').hidden = true; $('pk-confirm').hidden = true; };
    $('pk-source-check').onclick = () => void checkSource();
    $('pk-source-text').onkeydown = (e) => { if (e.key === 'Enter') void checkSource(); };
    $('pk-outdated').onclick = () => void checkUpdates();
    $('pk-filter').oninput = renderTable;
    $('t-packages').onclick = () => { showTab('packages'); if (!loaded)
        void load(); };
    window.addEventListener('pymagic:install', (e) => {
        showTab('packages');
        if (!loaded)
            void load();
        void run('install', [e.detail]);
    });
    window.addEventListener('pymagic:interpreter', () => { outdated = new Map(); $('pk-outdated').textContent = 'Check updates'; if (loaded)
        void load(); });
    // another panel asks for packages (the Database panel's driver): installed here, the log in the Packages tab
    window.addEventListener('pymagic:installPackages', (e) => void run('install', e.detail));
    window.addEventListener('pymagic:uninstallPackages', (e) => void run('uninstall', e.detail));
}

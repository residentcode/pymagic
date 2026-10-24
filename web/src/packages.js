// Packages tab: what is installed in the project's interpreter, install / upgrade / uninstall with pip, requirements files.
import { RunClient } from './runclient';
import { showTab } from './panel';
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
        const done = () => { client?.close(); client = null; setBusy(false); void load(); };
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
}

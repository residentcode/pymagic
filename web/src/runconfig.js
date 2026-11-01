// Run configurations: named ways to start a file (its parameters, the variables of its environment, the folder it starts in). The one that is chosen is what
// Run, Debug and Profile start; with none chosen they start the file in the editor. They are kept in the project, in .pymagic/run-configurations.json.
import { openMenu } from './menu';
import { KINDS, blank, formatEnv, parseEnv, read, splitShared, toStored, uniqueName } from './runconfig-model.js';

const $ = (id) => document.getElementById(id);
const FILE = '.pymagic/run-configurations.json';
// Store as project file: these go with the project's files (and Git), as PyCharm's .run folder
const SHARED = '.run/pymagic.json';

export function setupRunConfigurations({ currentFile }) {
    let list = [];
    let selected = null;
    let ready = Promise.resolve();
    const listeners = new Set();

    let hadShared = false;
    const save = () => {
        const { shared, own } = splitShared(list);
        fetch('/api/file?path=' + encodeURIComponent(FILE), { method: 'PUT', body: JSON.stringify(toStored(own, selected)) }).catch(() => { /* not kept this time */ });
        if (shared.length || hadShared)
            fetch('/api/file?path=' + encodeURIComponent(SHARED), { method: 'PUT', body: JSON.stringify(toStored(shared, null), null, 2) + '\n' }).catch(() => { /* not kept this time */ });
        hadShared = hadShared || shared.length > 0;
        for (const fn of listeners) fn();
    };

    const fetchJson = (file) => fetch('/api/file?path=' + encodeURIComponent(file)).then((r) => (r.ok ? r.json() : null)).catch(() => null);
    ready = Promise.all([fetchJson(FILE), fetchJson(SHARED)]).then(([stored, shared]) => {
        const sharedList = read(shared).configurations.map((c) => ({ ...c, shared: true }));
        hadShared = sharedList.length > 0;
        const own = read({ ...stored, configurations: [...(stored?.configurations ?? []), ...sharedList] });
        ({ configurations: list, selected } = own);
        for (const fn of listeners) fn();
    }).catch(() => { /* none stored */ });

    const choose = (name) => { selected = name; save(); };
    const current = () => list.find((c) => c.name === selected) ?? null;

    /** The menu under the chip of the command bar. */
    function menu(anchor) {
        const items = [{ label: 'Current file', checked: selected === null, run: () => choose(null) }];
        if (list.length) items.push('-');
        for (const c of list) items.push({ label: c.name, checked: c.name === selected, run: () => choose(c.name) });
        items.push('-', { label: 'Edit configurations…', run: () => edit() });
        openMenu(anchor, items);
    }

    // ---------------------------------------------------------------- the dialog
    let isOpen = false;
    async function edit() {
        if (isOpen) return;
        isOpen = true;
        await ready;
        const files = await fetch('/api/files').then((r) => r.json()).catch(() => []);
        const scripts = files.filter((f) => f.endsWith('.py'));
        const folders = [...new Set(files.map((f) => f.slice(0, Math.max(0, f.lastIndexOf('/')))).filter((d) => d))].sort();
        const draft = list.map((c) => ({ ...c, env: { ...c.env } }));
        let at = Math.max(0, draft.findIndex((c) => c.name === selected));
        let chosen = draft.find((c) => c.name === selected) ?? null;   // the chosen one, whatever it is renamed to

        const shade = document.createElement('div');
        shade.className = 'settings-shade';
        const box = document.createElement('div');
        box.className = 'settings runcfg';
        const nav = document.createElement('nav');
        nav.className = 'settings-nav';
        const title = document.createElement('h2');
        title.textContent = 'Run configurations';
        const listBox = document.createElement('div');
        listBox.className = 'runcfg-list';
        const tools = document.createElement('div');
        tools.className = 'runcfg-tools';
        const add = document.createElement('button');
        add.textContent = '+ Add';
        const copy = document.createElement('button');
        copy.textContent = 'Copy';
        const del = document.createElement('button');
        del.textContent = 'Remove';
        tools.append(add, copy, del);
        nav.append(title, listBox, tools);
        const body = document.createElement('div');
        body.className = 'settings-body';
        const form = document.createElement('div');
        form.className = 'settings-page';
        const foot = document.createElement('div');
        foot.className = 'settings-foot';
        const cancel = document.createElement('button');
        cancel.textContent = 'Cancel';
        const ok = document.createElement('button');
        ok.className = 'primary';
        ok.textContent = 'Save';
        foot.append(cancel, ok);
        body.append(form, foot);
        box.append(nav, body);
        shade.append(box);

        const close = () => { shade.remove(); isOpen = false; document.removeEventListener('keydown', onKey, true); };
        const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(); } };

        const field = (label, control, hint) => {
            const row = document.createElement('div');
            row.className = 'set-row';
            const l = document.createElement('label');
            l.className = 'set-label';
            l.textContent = label;
            const c = document.createElement('div');
            c.className = 'set-control';
            c.append(control);
            row.append(l, c);
            if (hint) { const h = document.createElement('div'); h.className = 'set-hint'; h.textContent = hint; row.append(h); }
            return row;
        };

        function drawList() {
            listBox.replaceChildren();
            draft.forEach((c, i) => {
                const b = document.createElement('button');
                b.textContent = c.name;
                b.className = i === at ? 'active' : '';
                b.onclick = () => { at = i; drawList(); drawForm(); };
                listBox.append(b);
            });
            copy.disabled = del.disabled = draft.length === 0;
        }

        function drawForm() {
            form.replaceChildren();
            const c = draft[at];
            if (!c) {
                const p = document.createElement('p');
                p.className = 'set-note';
                p.textContent = 'No run configurations. Without one, Run, Debug and Profile start the file in the editor. Add one to start a file with parameters, environment variables or in another folder.';
                form.append(p);
                return;
            }
            const h = document.createElement('h2');
            h.textContent = 'Configuration';
            form.append(h);
            const name = document.createElement('input');
            name.value = c.name;
            name.oninput = () => { c.name = name.value; const b = listBox.children[at]; if (b) b.textContent = c.name || '(no name)'; };
            const check = (label, key, hint) => {
                const box = document.createElement('input');
                box.type = 'checkbox';
                box.checked = c[key];
                box.onchange = () => { c[key] = box.checked; };
                return field(label, box, hint);
            };
            const text = (key, placeholder) => {
                const input = document.createElement('input');
                input.value = c[key] ?? '';
                input.placeholder = placeholder;
                input.spellcheck = false;
                input.oninput = () => { c[key] = input.value; };
                return input;
            };
            // the interpreters the project knows, offered as the box is typed in (any path may be typed)
            const interpreterBox = text('interpreter', "the project's interpreter");
            const known = document.createElement('datalist');
            known.id = 'rc-interpreters';
            interpreterBox.setAttribute('list', known.id);
            interpreterBox.after(known);
            void fetch('/api/interpreters').then((r) => r.json()).then((list) => { known.replaceChildren(...list.map((i) => Object.assign(document.createElement('option'), { value: i.path ?? i.spec ?? '', label: `${i.kind ?? ''} ${i.major ?? ''}.${i.minor ?? ''}`.trim() }))); }).catch(() => { });
            const kind = document.createElement('select');
            for (const [k, label] of KINDS) kind.add(new Option(label, k));
            kind.value = c.kind;
            kind.onchange = () => {
                const fresh = blank(c.name, kind.value);
                Object.assign(c, { kind: kind.value, app: fresh.app, port: fresh.port, script: kind.value === 'django' && !c.script ? 'manage.py' : c.script });
                drawForm();
            };
            const script = document.createElement('select');
            script.add(new Option('Choose a file…', ''));
            for (const f of scripts) script.add(new Option(f, f));
            if (c.script && !scripts.includes(c.script)) script.add(new Option(c.script + ' (not found)', c.script));
            script.value = c.script;
            script.onchange = () => { c.script = script.value; };
            const args = document.createElement('input');
            args.value = c.args;
            args.placeholder = '--port 8000 "a name"';
            args.oninput = () => { c.args = args.value; };
            const cwd = document.createElement('select');
            cwd.add(new Option('The project folder', ''));
            for (const d of folders) cwd.add(new Option(d, d));
            if (c.cwd && !folders.includes(c.cwd)) cwd.add(new Option(c.cwd + ' (not found)', c.cwd));
            cwd.value = c.cwd;
            cwd.onchange = () => { c.cwd = cwd.value; };
            const env = document.createElement('textarea');
            env.rows = 5;
            env.placeholder = 'NAME=value, one per line';
            env.value = formatEnv(c.env);
            env.oninput = () => { c.env = parseEnv(env.value); };
            // what each kind asks for
            const what = {
                script: [field('Script', script)],
                module: [field('Module', text('module', 'http.server'), 'Started as python -m module.')],
                django: [field('manage.py', script), field('Host', text('host', '127.0.0.1')), field('Port', text('port', '8000')), check('Reload on changes', 'reload', 'Off adds --noreload.'),
                    field('Settings', text('settings', 'site_cfg.settings'), 'DJANGO_SETTINGS_MODULE; empty for the one manage.py names.'),
                    check('Test server', 'testServer', 'manage.py testserver: a server on a test database filled from the fixtures below.'), field('Fixtures', text('fixtures', 'users.json items.json')),
                    check('Open the browser when the server is up', 'openBrowser'), check('Debug only my code', 'justMyCode', 'Off lets the debugger stop in Django and the libraries too.'),
                    field('Log files', text('logFiles', 'logs/debug.log'), 'Files of the project whose new lines show in the console while it runs, comma separated.'),
                    field('Before launch', text('before', 'the name of another configuration'), 'Run that configuration first (migrate, say) and start this one when it has finished.')],
                djangoTests: [field('Target', text('target', 'shop, shop.tests or shop.tests.ItemTests.test_create'), 'An app, a module, a class or one test; empty for all.'),
                    field('Settings', text('settings', 'site_cfg.settings'), 'Empty for the one manage.py names.'), check('Stop at the first failure', 'failfast', '--failfast.')],
                fastapi: [field('Application', text('app', 'main:app'), 'module:variable, given to uvicorn.'), field('Host', text('host', '127.0.0.1')), field('Port', text('port', '8000')), check('Reload on changes', 'reload', 'uvicorn --reload.')],
                flask: [field('Application', text('app', 'app'), 'The module or file of the Flask app (flask --app).'), field('Host', text('host', '127.0.0.1')), field('Port', text('port', '5000')), check('Debug mode', 'reload', 'flask run --debug: the reloader and the debugger page.')],
                pytest: [field('Target', text('target', 'tests/ or tests/test_a.py::test_x'), 'A folder, a file or one test; empty for all.')],
            }[c.kind] ?? [field('Script', script)];
            if (c.kind === 'compound') {
                // the others it starts together: a box for each configuration that can be started beside it
                const chosenNames = new Set((c.members ?? '').split(/\r?\n/).map((n) => n.trim()).filter(Boolean));
                const pick = document.createElement('div');
                pick.className = 'rc-members';
                const others = draft.filter((o) => o !== c && o.kind !== 'compound' && o.kind !== 'djangoTests');
                if (!others.length) pick.textContent = 'Add the configurations to start together first.';
                for (const o of others) {
                    const label = document.createElement('label');
                    label.style.display = 'block';
                    const box = document.createElement('input');
                    box.type = 'checkbox';
                    box.checked = chosenNames.has(o.name);
                    box.onchange = () => {
                        if (box.checked) chosenNames.add(o.name); else chosenNames.delete(o.name);
                        c.members = [...chosenNames].join('\n');
                    };
                    label.append(box, ' ' + o.name);
                    pick.append(label);
                }
                form.append(field('Name', name), field('Kind', kind), field('Run together', pick, 'Each one runs on its own and has its own row in the Services window, to stop or restart; without the debugger.'),
                    check('Store as project file', 'shared', 'Kept in .run/pymagic.json beside the code, for Git; the others stay in .pymagic.'), check('Open the Run panel when started', 'openPanel'));
                return;
            }
            form.append(
                field('Name', name),
                field('Kind', kind),
                ...what,
                field(c.kind === 'script' || c.kind === 'module' ? 'Parameters' : 'More options', args, 'Typed as on a command line: words separated by spaces, "quotes" keep a space in one word.'),
                field('Working folder', cwd),
                field('Python interpreter', interpreterBox, 'Empty: the project\'s interpreter. A path of this machine, or one of WSL, Docker or SSH as the interpreter list names it (wsl:Ubuntu|/usr/bin/python3).'),
                field('Environment variables', env, 'PATH, PYTHONPATH and the like are the interpreter\'s; a configuration cannot change them.'),
                field('.env files', text('envFiles', '.env, config/dev.env'), 'Files of the project, comma separated; their variables come first, the ones above win.'),
                check('Add the content root to PYTHONPATH', 'contentRoots', 'The project folder.'),
                check('Add the sources roots to PYTHONPATH', 'sourceRoots', 'The folders marked as sources roots in the project tree.'),
                check('Run with uv run', 'uv', 'uv starts the interpreter of the project\'s environment and makes it match pyproject.toml first.'),
                field('uv', text('uvPath', 'uv (or the path to uv.exe)')),
                check('Store as project file', 'shared', 'Kept in .run/pymagic.json beside the code, for Git; the others stay in .pymagic.'),
                check('Open the Run panel when started', 'openPanel'),
            );
        }

        add.onclick = () => openMenu(add, KINDS.map(([k, label]) => ({
            label,
            run: () => {
                const c = blank(uniqueName(draft, label), k);
                if (k === 'script') c.script = currentFile() ?? '';
                draft.push(c);
                at = draft.length - 1;
                chosen = draft[at];
                drawList();
                drawForm();
            },
        })));
        copy.onclick = () => { const c = draft[at]; if (!c) return; draft.push({ ...c, env: { ...c.env }, name: uniqueName(draft, c.name) }); at = draft.length - 1; drawList(); drawForm(); };
        del.onclick = () => { if (!draft[at]) return; if (chosen === draft[at]) chosen = null; draft.splice(at, 1); at = Math.max(0, at - 1); drawList(); drawForm(); };
        cancel.onclick = close;
        ok.onclick = () => {
            const names = draft.map((c) => c.name.trim());
            if (names.some((n) => !n) || new Set(names).size !== names.length) { window.alert('Every configuration needs a name of its own.'); return; }
            if (draft.some((c) => c.kind === 'compound' && !c.members.split(/\r?\n/).some((n) => n.trim()))) { window.alert('A compound configuration needs at least one configuration to start.'); return; }
            if (draft.some((c) => (c.kind === 'script' || c.kind === 'django') && !c.script)) { window.alert('Every script configuration needs its script.'); return; }
            if (draft.some((c) => c.kind === 'module' && !/^[A-Za-z_][\w]*(\.[A-Za-z_][\w]*)*$/.test(c.module))) { window.alert('A module configuration needs a module name, such as http.server.'); return; }
            list = draft.map((c) => ({ ...c, name: c.name.trim() }));
            selected = chosen ? chosen.name.trim() : null;
            save();
            close();
        };
        shade.onmousedown = (e) => { if (e.target === shade) close(); };
        document.addEventListener('keydown', onKey, true);
        document.body.append(shade);
        drawList();
        drawForm();
        (form.querySelector('input') ?? add).focus();
    }

    return { current, byName: (name) => list.find((c) => c.name === name) ?? null, menu, edit, choose, onChange: (fn) => { listeners.add(fn); return () => listeners.delete(fn); }, names: () => list.map((c) => c.name), selectedName: () => selected };
}

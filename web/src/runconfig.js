// Run configurations: named ways to start a file (its parameters, the variables of its environment, the folder it starts in). The one that is chosen is what
// Run, Debug and Profile start; with none chosen they start the file in the editor. They are kept in the project, in .pymagic/run-configurations.json.
import { openMenu } from './menu';
import { blank, formatEnv, parseEnv, read, toStored, uniqueName } from './runconfig-model.js';

const $ = (id) => document.getElementById(id);
const FILE = '.pymagic/run-configurations.json';

export function setupRunConfigurations({ currentFile }) {
    let list = [];
    let selected = null;
    let ready = Promise.resolve();
    const listeners = new Set();

    const save = () => {
        fetch('/api/file?path=' + encodeURIComponent(FILE), { method: 'PUT', body: JSON.stringify(toStored(list, selected)) }).catch(() => { /* not kept this time */ });
        for (const fn of listeners) fn();
    };

    ready = fetch('/api/file?path=' + encodeURIComponent(FILE)).then((r) => (r.ok ? r.json() : null)).then((stored) => {
        ({ configurations: list, selected } = read(stored));
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
            form.append(
                field('Name', name),
                field('Script', script),
                field('Parameters', args, 'Typed as on a command line: words separated by spaces, "quotes" keep a space in one word.'),
                field('Working folder', cwd, 'The project folder is always on PYTHONPATH, wherever the program starts.'),
                field('Environment variables', env, 'PATH, PYTHONPATH and the like are the interpreter\'s; a configuration cannot change them.'),
            );
        }

        add.onclick = () => { draft.push({ ...blank(uniqueName(draft, 'New configuration')), script: currentFile() ?? '' }); at = draft.length - 1; chosen = draft[at]; drawList(); drawForm(); };
        copy.onclick = () => { const c = draft[at]; if (!c) return; draft.push({ ...c, env: { ...c.env }, name: uniqueName(draft, c.name) }); at = draft.length - 1; drawList(); drawForm(); };
        del.onclick = () => { if (!draft[at]) return; if (chosen === draft[at]) chosen = null; draft.splice(at, 1); at = Math.max(0, at - 1); drawList(); drawForm(); };
        cancel.onclick = close;
        ok.onclick = () => {
            const names = draft.map((c) => c.name.trim());
            if (names.some((n) => !n) || new Set(names).size !== names.length) { window.alert('Every configuration needs a name of its own.'); return; }
            if (draft.some((c) => !c.script)) { window.alert('Every configuration needs a script.'); return; }
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

    return { current, menu, edit, choose, onChange: (fn) => { listeners.add(fn); return () => listeners.delete(fn); }, names: () => list.map((c) => c.name), selectedName: () => selected };
}

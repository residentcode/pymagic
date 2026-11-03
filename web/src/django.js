// The Django tab: the manage.py commands of the project as buttons (the output, the input box and the debugger are the ones of Run),
// and the apps with their migrations.
import { showTab } from './panel';
import { toast } from './toast';
import { COMMANDS, testRunOf, splitArguments, migrationLabel, parseCommands, parseOptions, completeManage, groupStructure, appOf, addModel, addView, registerModel } from './django-model';
import { defineCommand } from './commands.js';
const $ = (id) => document.getElementById(id);
function el(tag, className, text) {
    const e = document.createElement(tag);
    if (className)
        e.className = className;
    if (text !== undefined)
        e.textContent = text;
    return e;
}
export function setupDjango(deps) {
    const body = $('django-body');
    const summary = $('django-summary');
    let project = null;
    const opened = new Set();
    // the Django Structure: models, admin classes and views of the project, by app or by kind
    let structureBy = 'app', structure = [];
    async function loadStructure() {
        try {
            structure = ((await deps.lsp.request('pymagic/djangoStructure', {})) ?? []).map((i) => ({ ...i, rel: deps.relOfUri(i.uri) ?? '' }));
        }
        catch { structure = []; }
    }
    /** Changes a file of the project in the editor (the change is there to see and to save) and goes to its end. */
    async function change(rel, edit) {
        const model = await deps.modelFor(rel);
        if (!model) { toast(`${rel} cannot be opened.`, true); return; }
        model.pushEditOperations([], [{ range: model.getFullModelRange(), text: edit(model.getValue()) }], () => null);
        await deps.openFile(rel, model.getLineCount());
        toast(`${rel} is changed in the editor: save it to keep it.`);
    }
    async function newEntry(kind, app) {
        const name = await deps.askText(kind === 'model' ? 'New model' : 'New view', '', `in ${app.name}`);
        if (!name) return;
        if (!/^[A-Za-z_]\w*$/.test(name)) { toast('A name is a Python identifier.', true); return; }
        if (kind === 'model') await change(`${app.path}/models.py`, (t) => addModel(t, name));
        else await change(`${app.path}/views.py`, (t) => addView(t, name, app.name.split('.').pop()));
    }
    function structureSection() {
        const box = section('Structure');
        const bar = el('div', 'dj-row');
        for (const [by, label] of [['app', 'By app'], ['kind', 'By kind']]) {
            const b = button(label, `Group the models, admin classes and views ${label.toLowerCase()}`, () => { structureBy = by; paint(); });
            if (structureBy === by) b.classList.add('active');
            bar.append(b);
        }
        bar.append(button('Refresh', 'Read the structure again', async () => { await loadStructure(); paint(); }));
        box.append(bar);
        if (!structure.length) {
            box.append(el('div', 'dj-note', 'No models, admin classes or views found yet.'));
            return box;
        }
        const appsByName = new Map(project.apps.map((a) => [a.name, a]));
        for (const group of groupStructure(structure, project.apps, structureBy)) {
            const head = el('div', 'dj-app');
            head.append(el('span', 'dj-appname', group.title));
            const app = appsByName.get(group.title);
            if (structureBy === 'app' && app) {
                const actions = el('span', 'dj-actions');
                const label = app.name.split('.').pop();
                actions.append(
                    button('New model…', `A model at the end of ${app.path}/models.py`, () => void newEntry('model', app)),
                    button('New view…', `A view at the end of ${app.path}/views.py`, () => void newEntry('view', app)),
                    button('makemigrations', `manage.py makemigrations ${label}`, () => void runManage(['makemigrations', label])),
                    button('migrate', `manage.py migrate ${label}`, () => void runManage(['migrate', label])));
                head.append(actions);
            }
            box.append(head);
            for (const item of group.items) {
                const row = el('div', `dj-migration dj-${item.kind}`);
                const icon = { model: '▣', admin: '⚙', view: '◇' }[item.kind] ?? '·';
                row.append(el('span', 'dj-kind', icon), el('span', '', ` ${item.name}`));
                if (structureBy === 'kind') row.append(el('span', 'detail', `  ${item.app}`));
                if (item.kind === 'admin' && item.model) {
                    // from an admin class to its model
                    const to = structure.find((x) => x.kind === 'model' && x.name === item.model);
                    const link = el('button', 'dj-hint', `→ ${item.model}`);
                    link.title = `Go to the model ${item.model}`;
                    link.onclick = (e) => { e.stopPropagation(); if (to) void deps.openFile(to.rel, to.line + 1); };
                    row.append(link);
                }
                if (item.kind === 'model' && !item.registered) {
                    const owner = appOf(item.rel, project.apps);
                    if (owner) {
                        const reg = el('button', 'dj-hint', 'Register in admin');
                        reg.title = `admin.site.register(${item.name}) in ${owner.path}/admin.py`;
                        reg.onclick = (e) => { e.stopPropagation(); void change(`${owner.path}/admin.py`, (t) => registerModel(t, item.name)); };
                        row.append(reg);
                    }
                }
                row.title = `${item.rel}:${item.line + 1}`;
                row.onclick = () => void deps.openFile(item.rel, item.line + 1);
                box.append(row);
            }
        }
        return box;
    }
    // the manage.py console's history, and what Django said of its commands (asked once per page)
    let manageHistory = [];
    try { manageHistory = JSON.parse(localStorage.getItem('pymagic.manageHistory') ?? '[]'); } catch { /* none */ }
    const optionCache = new Map();
    let commandList = null;
    async function manageHelp(command) {
        const r = await fetch('/api/django/help' + (command ? '?command=' + encodeURIComponent(command) : '')).catch(() => null);
        return r?.ok ? r.text() : null;
    }
    async function manageCommands() {
        commandList ??= parseCommands(await manageHelp(null) ?? '');
        return commandList;
    }
    defineCommand({ id: 'pymagic.django.manage', label: 'manage.py console (Django tab)', group: 'Tools', keys: ['Ctrl+Alt+R'], everywhere: true, run: () => {
        // the tab fills itself when it is opened: its own button does that, then the console takes the keys
        document.getElementById('t-django')?.click();
        const focus = (tries) => { const box = document.getElementById('dj-manage'); if (box) box.focus(); else if (tries > 0) window.setTimeout(() => focus(tries - 1), 100); };
        focus(30);
    } });
    // the settings module commands run with: manage.py's own, or another one of the project's (kept per project)
    const settingsKey = () => `pymagic.djangoSettings.${project?.manage ?? ''}`;
    function chosenSettings() {
        try { return localStorage.getItem(settingsKey()) || ''; }
        catch { return ''; }
    }
    async function runManage(args, debug = false) {
        if (!project?.manage)
            return;
        const settings = chosenSettings();
        // manage.py test runs through the Tests panel, as a Django tests configuration does: the results in its tree, not as plain text
        const tests = debug ? null : testRunOf(args, settings && settings !== project.settingsModule ? settings : '');
        if (tests) {
            showTab('tests');
            window.dispatchEvent(new CustomEvent('pymagic:runTests', { detail: { ...tests, framework: 'django' } }));
            return;
        }
        const withSettings = settings && settings !== project.settingsModule && !args.some((a) => a.startsWith('--settings')) ? [...args, `--settings=${settings}`] : args;
        const started = await deps.run.runScript(project.manage, withSettings, debug);
        if (started === false)
            toast('A program is running: stop it first.', true);
    }
    function button(label, hint, onclick) {
        const b = el('button', 'dj-btn', label);
        b.title = hint;
        b.onclick = onclick;
        return b;
    }
    function section(title) {
        const s = el('div', 'dj-section');
        s.append(el('div', 'dj-title', title));
        return s;
    }
    function paint() {
        if (!project || !project.found) {
            summary.textContent = 'No Django project here: there is no manage.py in this folder (or the two levels below it).';
            body.replaceChildren();
            return;
        }
        summary.textContent = `${project.manage}` + (project.settingsModule ? `  ·  settings ${project.settingsModule}` : '') + `  ·  ${project.apps.length} apps`;
        const choices = project.settingsChoices ?? [];
        if (choices.length > 1) {
            const pick = el('select', 'dj-settings');
            pick.title = 'The settings module manage.py commands run with (--settings)';
            for (const module of choices)
                pick.append(new Option(module === project.settingsModule ? `${module} (manage.py)` : module, module));
            pick.value = chosenSettings() || project.settingsModule || choices[0];
            pick.onchange = () => { try { localStorage.setItem(settingsKey(), pick.value); } catch { /* not kept */ } };
            summary.append('  ·  run with ', pick);
        }
        const commands = section('manage.py');
        const row = el('div', 'dj-row');
        for (const [label, args, debug, hint] of COMMANDS)
            row.append(button(debug ? `🐞 ${label}` : label, hint, () => void runManage(args, debug)));
        commands.append(row);
        const custom = [...new Set(project.apps.flatMap((a) => a.commands))];
        if (custom.length) {
            const own = el('div', 'dj-row');
            own.append(el('span', 'dj-label', 'Commands of the project:'));
            for (const c of custom)
                own.append(button(c, `manage.py ${c}`, () => void runManage([c])));
            commands.append(own);
        }
        // the manage.py console (Ctrl+Alt+R): its history, Tab completing commands and their options, F1 for a command's help
        const free = el('div', 'dj-row');
        const input = el('input');
        input.id = 'dj-manage';
        input.placeholder = 'manage.py … (Tab completes, F1 shows the command\'s help, Up / Down the history)';
        input.spellcheck = false;
        input.autocomplete = 'off';
        input.className = 'dj-input';
        const hints = el('div', 'dj-hints');
        const go = () => {
            const words = splitArguments(input.value);
            if (!words.length)
                return;
            manageHistory = [...manageHistory.filter((h) => h !== input.value), input.value].slice(-100);
            try { localStorage.setItem('pymagic.manageHistory', JSON.stringify(manageHistory)); } catch { /* not kept */ }
            at = manageHistory.length;
            hints.replaceChildren();
            void runManage(words);
        };
        let at = manageHistory.length;
        input.onkeydown = async (e) => {
            if (e.key === 'Enter') { e.preventDefault(); go(); }
            else if (e.key === 'ArrowUp' && manageHistory.length) { e.preventDefault(); at = Math.max(0, at - 1); input.value = manageHistory[at]; }
            else if (e.key === 'ArrowDown' && manageHistory.length) { e.preventDefault(); at = Math.min(manageHistory.length, at + 1); input.value = manageHistory[at] ?? ''; }
            else if (e.key === 'Tab') {
                e.preventDefault();
                const command = splitArguments(input.value)[0];
                const done = completeManage(input.value.slice(0, input.selectionStart), await manageCommands(), (c) => optionCache.get(c));
                if (command && input.value.includes(' ') && !optionCache.has(command)) {
                    optionCache.set(command, parseOptions(await manageHelp(command) ?? ''));
                    return input.onkeydown(e);
                }
                if (done.items.length === 1)
                    input.setRangeText(done.items[0].slice(done.word.length) + ' ', input.selectionStart, input.selectionStart, 'end');
                hints.replaceChildren(...(done.items.length > 1 ? done.items.slice(0, 40).map((item) => {
                    const h = el('button', 'dj-hint', item);
                    h.onclick = () => { input.setRangeText(item.slice(done.word.length) + ' ', input.selectionStart, input.selectionStart, 'end'); input.focus(); hints.replaceChildren(); };
                    return h;
                }) : []));
            }
            else if (e.key === 'F1' || (e.key === 'q' && e.ctrlKey)) {
                e.preventDefault();
                const command = splitArguments(input.value)[0];
                const text = command ? await manageHelp(command) : (await manageCommands()).join('  ');
                const pre = el('pre', 'dj-help', text ?? `No help for '${command}'.`);
                hints.replaceChildren(pre);
            }
            else if (e.key === 'Escape')
                hints.replaceChildren();
        };
        free.append(input, button('Run', 'Run manage.py with these arguments', go), button('Django console', 'A Python console with Django set up, as manage.py shell (its models ready to use)', () => window.dispatchEvent(new CustomEvent('pymagic:djangoConsole'))));
        commands.append(free, hints);
        const startapp = el('div', 'dj-row');
        const name = el('input');
        name.placeholder = 'New app name';
        name.spellcheck = false;
        name.autocomplete = 'off';
        name.className = 'dj-input short';
        startapp.append(name, button('startapp', 'manage.py startapp NAME', async () => {
            const value = name.value.trim();
            if (!/^[A-Za-z_]\w*$/.test(value)) {
                toast('An app name is a Python identifier.', true);
                return;
            }
            if (await deps.run.runScript(project.manage, ['startapp', value], false))
                name.value = '';
            else
                toast('A program is running: stop it first.', true);
        }));
        commands.append(startapp);
        const apps = section('Apps');
        if (!project.apps.length)
            apps.append(el('div', 'dj-note', 'No app found: an app is a folder with an __init__.py and an apps.py or models.'));
        for (const app of project.apps) {
            const head = el('div', 'dj-app');
            const twisty = el('span', 'dj-twisty', opened.has(app.path) ? '▾' : '▸');
            const title = el('span', 'dj-appname', app.name);
            const detail = el('span', 'detail', `${app.path}  ·  ${app.hasModels ? 'models' : 'no models'}  ·  ${app.migrations.length} migrations`);
            if (app.installed === false) {
                const missing = el('span', 'dj-warn', '  ·  not in INSTALLED_APPS');
                missing.title = `INSTALLED_APPS does not list ${app.module}: Django does not load its models, migrations or templates`;
                detail.append(missing);
            }
            const actions = el('span', 'dj-actions');
            const label = app.name.split('.').pop();
            actions.append(button('makemigrations', `manage.py makemigrations ${label}`, (e) => { e.stopPropagation(); void runManage(['makemigrations', label]); }), button('migrate', `manage.py migrate ${label}`, (e) => { e.stopPropagation(); void runManage(['migrate', label]); }), button('test', `manage.py test ${label}`, (e) => { e.stopPropagation(); void runManage(['test', label]); }));
            head.append(twisty, title, detail, actions);
            head.onclick = () => { opened.has(app.path) ? opened.delete(app.path) : opened.add(app.path); paint(); };
            apps.append(head);
            if (opened.has(app.path)) {
                if (!app.migrations.length)
                    apps.append(el('div', 'dj-migration dim', 'no migrations'));
                for (const m of app.migrations) {
                    const item = el('div', 'dj-migration', migrationLabel(m));
                    item.title = m.path;
                    item.onclick = () => void deps.openFile(m.path);
                    apps.append(item);
                }
            }
        }
        body.replaceChildren(commands, apps, structureSection());
    }
    async function refresh() {
        try {
            const res = await fetch('/api/django');
            project = await res.json();
        }
        catch (e) {
            summary.textContent = String(e.message ?? e);
            return;
        }
        if (project?.found)
            await loadStructure();
        paint();
    }
    $('t-django').onclick = () => { showTab('django'); void refresh(); };
    window.addEventListener('pymagic:saved', () => { if ($('django-tab').classList.contains('active')) void loadStructure().then(paint); });
    $('django-refresh').onclick = () => void refresh();
    window.addEventListener('pymagic:saved', () => { if ($('django-tab').classList.contains('active'))
        void refresh(); });
    return { refresh };
}

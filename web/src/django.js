// The Django tab: the manage.py commands of the project as buttons (the output, the input box and the debugger are the ones of Run),
// and the apps with their migrations.
import { showTab } from './panel';
import { toast } from './toast';
import { COMMANDS, splitArguments, migrationLabel, parseCommands, parseOptions, completeManage } from './django-model';
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
    document.addEventListener('keydown', (e) => {
        if (e.ctrlKey && e.altKey && !e.shiftKey && (e.key === 'r' || e.key === 'R')) {
            e.preventDefault();
            // the tab fills itself when it is opened: its own button does that, then the console takes the keys
            document.getElementById('t-django')?.click();
            const focus = (tries) => { const box = document.getElementById('dj-manage'); if (box) box.focus(); else if (tries > 0) window.setTimeout(() => focus(tries - 1), 100); };
            focus(30);
        }
    });
    async function runManage(args, debug = false) {
        if (!project?.manage)
            return;
        const started = await deps.run.runScript(project.manage, args, debug);
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
        body.replaceChildren(commands, apps);
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
        paint();
    }
    $('t-django').onclick = () => { showTab('django'); void refresh(); };
    $('django-refresh').onclick = () => void refresh();
    window.addEventListener('pymagic:saved', () => { if ($('django-tab').classList.contains('active'))
        void refresh(); });
    return { refresh };
}

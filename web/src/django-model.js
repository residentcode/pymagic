// The Django tab's logic that needs no page: the manage.py commands, reading a command line, and how a migration is named.
// [label, arguments, debug, hint]
export const COMMANDS = [
    ['Run server', ['runserver'], false, 'manage.py runserver'],
    ['Debug server', ['runserver', '--noreload'], true, 'manage.py runserver --noreload, under the debugger: breakpoints in your views stop the request (the reloader would start a second process the debugger does not see)'],
    ['migrate', ['migrate'], false, 'Apply the migrations'],
    ['makemigrations', ['makemigrations'], false, 'Create migrations for the changes of the models'],
    ['Missing migrations?', ['makemigrations', '--check', '--dry-run'], false, 'Changes nothing: fails when the models have changes without a migration'],
    ['showmigrations', ['showmigrations'], false, 'List the migrations and which are applied'],
    ['check', ['check'], false, 'Check the project for problems'],
    ['test', ['test'], false, 'Run the tests with Django\'s runner'],
    ['shell', ['shell'], false, 'A Python shell with the project loaded (type in the input box under the output)'],
    ['createsuperuser', ['createsuperuser'], false, 'Create an administrator (answer the questions in the input box)'],
    ['collectstatic', ['collectstatic', '--noinput'], false, 'Copy the static files to STATIC_ROOT'],
];
/** The words of a command line, with "double" and 'single' quotes grouping words. */
export function splitArguments(text) {
    const words = [];
    const re = /"([^"]*)"|'([^']*)'|(\S+)/g;
    let m;
    while ((m = re.exec(text)) !== null)
        words.push(m[1] ?? m[2] ?? m[3]);
    return words;
}
/** A migration's text for the list: `0001_initial` shown as its number and name. */
export function migrationLabel(m) {
    const at = m.name.indexOf('_');
    return at > 0 ? `${m.name.slice(0, at)}  ${m.name.slice(at + 1).replace(/_/g, ' ')}` : m.name;
}

// ------------------------------------------------------------ the manage.py console

/** The commands of `manage.py help --commands`: one name per line. */
export function parseCommands(text) {
    return [...new Set(text.split(/\r?\n/).map((l) => l.trim()).filter((l) => /^[A-Za-z_][\w-]*$/.test(l)))].sort();
}

/** The options of `manage.py help <command>` (argparse's help): each with its flags and what it does. */
export function parseOptions(text) {
    const out = [];
    for (const line of text.split(/\r?\n/)) {
        const m = /^\s{2,}(-{1,2}[\w-]+(?:[ =][A-Z0-9_{}\[\],.|-]+)?(?:,\s*-{1,2}[\w-]+(?:[ =][A-Z0-9_{}\[\],.|-]+)?)*)(?:\s{2,}(.*))?$/.exec(line);
        if (m) {
            const flags = [...m[1].matchAll(/-{1,2}[\w-]+/g)].map((f) => f[0]);
            out.push({ flags, help: (m[2] ?? '').trim() });
        }
        else if (out.length && /^\s{20,}\S/.test(line))
            out[out.length - 1].help = (out[out.length - 1].help + ' ' + line.trim()).trim();   // argparse continues a long help on the next lines
    }
    return out;
}

/** What Tab completes in the manage.py console: a command for the first word, else an option of that command (the long ones first). */
export function completeManage(text, commands, optionsOf) {
    const words = text.split(/\s+/);
    const word = words[words.length - 1];
    if (words.length === 1)
        return { word, items: commands.filter((c) => c.startsWith(word)) };
    if (!word.startsWith('-'))
        return { word, items: [] };
    const flags = (optionsOf(words[0]) ?? []).flatMap((o) => o.flags);
    return { word, items: [...new Set(flags.filter((f) => f.startsWith(word)))].sort((a, b) => (b.startsWith('--') - a.startsWith('--')) || a.localeCompare(b)) };
}

// ------------------------------------------------------------ the Django Structure

const KIND_TITLES = { model: 'Models', admin: 'Admin', view: 'Views' };

/** The app a file belongs to: the app whose folder holds it (the deepest one), else null. */
export function appOf(rel, apps) {
    let best = null;
    for (const a of apps)
        if ((rel === a.path || rel.startsWith(a.path + '/')) && (!best || a.path.length > best.path.length))
            best = a;
    return best;
}

/** The structure's items in groups: by app (each app's models, admin and views) or by kind (each kind with its app beside each item). */
export function groupStructure(items, apps, by) {
    const withApp = items.map((i) => ({ ...i, app: appOf(i.rel, apps)?.name ?? '(no app)' }));
    const groups = new Map();
    const key = (i) => (by === 'kind' ? KIND_TITLES[i.kind] ?? i.kind : i.app);
    for (const i of withApp) {
        if (!groups.has(key(i)))
            groups.set(key(i), []);
        groups.get(key(i)).push(i);
    }
    const order = by === 'kind' ? Object.values(KIND_TITLES) : [...groups.keys()].sort();
    return order.filter((k) => groups.has(k)).map((title) => ({
        title,
        items: groups.get(title).sort((a, b) => (by === 'kind' ? a.app.localeCompare(b.app) : Object.keys(KIND_TITLES).indexOf(a.kind) - Object.keys(KIND_TITLES).indexOf(b.kind)) || a.name.localeCompare(b.name)),
    }));
}

const endWith = (text, add) => (text.length && !text.endsWith('\n') ? text + '\n' : text) + add;
/** An import line put after the module's other imports (or at the top), unless the module has it. */
function withImport(text, line) {
    if (text.split(/\r?\n/).some((l) => l.trim() === line))
        return text;
    const lines = text.split('\n');
    let at = 0;
    lines.forEach((l, i) => { if (/^(from|import)\s/.test(l)) at = i + 1; });
    lines.splice(at, 0, line);
    return lines.join('\n');
}

/** models.py with a new model at its end. */
export function addModel(text, name) {
    return endWith(withImport(text, 'from django.db import models'), `\n\nclass ${name}(models.Model):\n    name = models.CharField(max_length=100)\n\n    def __str__(self):\n        return self.name\n`);
}

/** views.py with a new view at its end, rendering a template named after it. */
export function addView(text, name, app) {
    return endWith(withImport(text, 'from django.shortcuts import render'), `\n\ndef ${name}(request):\n    return render(request, "${app}/${name}.html")\n`);
}

/** admin.py with a model registered (its import added). */
export function registerModel(text, model) {
    let out = withImport(text, 'from django.contrib import admin');
    const importLine = out.split('\n').findIndex((l) => /^from \.models import /.test(l));
    if (importLine >= 0) {
        const names = out.split('\n')[importLine].replace(/^from \.models import /, '').split(',').map((s) => s.trim()).filter(Boolean);
        if (!names.includes(model)) {
            const lines = out.split('\n');
            lines[importLine] = `from .models import ${[...names, model].join(', ')}`;
            out = lines.join('\n');
        }
    }
    else
        out = withImport(out, `from .models import ${model}`);
    return endWith(out, `\nadmin.site.register(${model})\n`);
}

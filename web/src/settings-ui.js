// The settings dialog (the gear, Ctrl+,): pages for how PyMagic looks, the editor, files, the formatter, the interpreter, Git, the keys and PyMagic itself.
// What a page edits is either a setting of the person (settings.js; changes show at once) or a control PyMagic already has (the formatter's engine,
// the interpreter, the Git switches), which the page mirrors and presses.
import { change, resetAll, settings } from './settings';
import { buildLabel, shortTime } from './build-info.js';
import { KEYMAP } from './keymap';
import { readSnippets, writeSnippets, readFileTemplates } from './codegen-model.js';

const SNIPPETS = '.pymagic/snippets.json', FILE_TEMPLATES = '.pymagic/fileTemplates.json';
let liveTemplateSource = null;
/** Where the Templates page gets the built-in live and postfix templates (the language server). */
export function setLiveTemplateSource(fn) {
    liveTemplateSource = fn;
}

const $ = (id) => document.getElementById(id);

const FONTS = [
    ['"Cascadia Code", "Cascadia Mono", Consolas, "Courier New", monospace', 'Cascadia Code'],
    ['"Cascadia Mono", Consolas, "Courier New", monospace', 'Cascadia Mono'],
    ['Consolas, "Courier New", monospace', 'Consolas'],
    ['"JetBrains Mono", "Cascadia Code", Consolas, monospace', 'JetBrains Mono (when installed)'],
    ['"Fira Code", "Cascadia Code", Consolas, monospace', 'Fira Code (when installed)'],
    ['"Courier New", monospace', 'Courier New'],
];

/** The pages that edit settings of the person: what each field is, so the page and the reset know them. */
const PAGES = [
    ['appearance', 'Appearance', [
        { key: 'theme', label: 'Theme', type: 'choice', options: [['dark', 'Dark'], ['light', 'Light'], ['system', 'Follow the system']], hint: 'The editor and every panel change with it.' },
        { key: 'confirmGit', label: 'Ask before update and push', type: 'bool', hint: 'The Update and Push buttons of the command bar ask first.' },
    ]],
    ['editor', 'Editor', [
        { key: 'fontFamily', label: 'Font', type: 'font' },
        { key: 'fontSize', label: 'Font size', type: 'number', min: 8, max: 40, unit: 'px' },
        { key: 'lineHeight', label: 'Line height', type: 'number', min: 12, max: 80, unit: 'px' },
        { key: 'ligatures', label: 'Font ligatures', type: 'bool', hint: 'Fonts such as Cascadia Code and Fira Code draw -> and != as one sign.' },
        { key: 'wordWrap', label: 'Wrap long lines', type: 'choice', options: [['off', 'No'], ['on', 'Yes']] },
        { key: 'lineNumbers', label: 'Line numbers', type: 'choice', options: [['on', 'Absolute'], ['relative', 'Relative to the cursor'], ['off', 'Hidden']] },
        { key: 'minimap', label: 'Minimap', type: 'bool', hint: 'A small picture of the whole file beside the scroll bar.' },
        { key: 'stickyScroll', label: 'Keep the enclosing class and function in view', type: 'bool' },
        { key: 'bracketColors', label: 'Colour matching brackets', type: 'bool' },
        { key: 'breadcrumbs', label: 'Breadcrumbs', type: 'bool', hint: 'The file, class and function of the cursor above the editor; a right click on one lists those beside it.' },
        { key: 'methodSeparators', label: 'Method separators', type: 'bool', hint: 'A thin line above each function and method.' },
        { key: 'lensMode', label: 'Lens mode', type: 'bool', hint: 'Hovering the scroll bar shows that part of the file.' },
        { key: 'whitespace', label: 'Show spaces and tabs', type: 'choice', options: [['none', 'Never'], ['selection', 'In the selection'], ['boundary', 'Not between words'], ['all', 'Always']] },
        { key: 'ruler', label: 'Vertical guide at column', type: 'number', min: 0, max: 400, unit: '0 for none' },
        { key: 'smoothScrolling', label: 'Smooth scrolling', type: 'bool' },
        { key: 'cursorStyle', label: 'Cursor', type: 'choice', options: [['line', 'Line'], ['block', 'Block'], ['underline', 'Underline']] },
        { key: 'cursorBlinking', label: 'Cursor blinking', type: 'choice', options: [['blink', 'Blink'], ['smooth', 'Smooth'], ['phase', 'Phase'], ['expand', 'Expand'], ['solid', 'None']] },
    ]],
    ['hints', 'Code vision and hints', [
        { key: 'codeVision', label: 'Usages and inheritors above functions and classes', type: 'bool', hint: 'A click lists them.' },
        { key: 'codeVisionAuthor', label: 'Who wrote most of each function and class', type: 'bool', hint: 'From Git blame; only in a Git repository.' },
        { key: 'hintParams', label: 'Parameter names before arguments', type: 'bool', hint: 'Also the names of @pytest.mark.parametrize values and of positional class patterns in match.' },
        { key: 'hintTypes', label: 'Inferred types', type: 'bool', hint: 'The return type of a function without one, the type of a variable when its value does not show it, and each call of a method chain.' },
    ]],
    ['tabs', 'Editor tabs', [
        { key: 'tabLimit', label: 'Tab limit', type: 'number', min: 0, max: 100, unit: '0 for none', hint: 'Past it, the tabs used longest ago close; pinned tabs and those with unsaved changes stay.' },
        { key: 'tabClose', label: 'A close button on each tab', type: 'bool', hint: 'Without it a tab closes with a middle click or its menu.' },
        { key: 'previewTab', label: 'Preview tab', type: 'bool', hint: 'A file opened with one click in the project tree replaces the one opened so before (its name in italics) until it is edited or double-clicked.' },
    ]],
    ['files', 'Files', [
        { key: 'autoSave', label: 'Save files automatically', type: 'choice', options: [['off', 'Never'], ['blur', 'When the window is left'], ['delay', 'A second after the last change']], hint: 'Saving also formats the file when "format on save" is on.' },
    ]],
];

function row(label, control, hint) {
    const r = document.createElement('div');
    r.className = 'set-row';
    const l = document.createElement('label');
    l.className = 'set-label';
    l.textContent = label;
    const c = document.createElement('div');
    c.className = 'set-control';
    c.append(control);
    r.append(l, c);
    if (hint) {
        const h = document.createElement('div');
        h.className = 'set-hint';
        h.textContent = hint;
        r.append(h);
    }
    return r;
}

function field(f) {
    const s = settings();
    let control;
    if (f.type === 'bool') {
        control = document.createElement('input');
        control.type = 'checkbox';
        control.checked = !!s[f.key];
        control.onchange = () => change({ [f.key]: control.checked });
    }
    else if (f.type === 'number') {
        control = document.createElement('input');
        control.type = 'number';
        control.min = String(f.min);
        control.max = String(f.max);
        control.value = String(s[f.key]);
        control.onchange = () => { change({ [f.key]: Number(control.value) }); control.value = String(settings()[f.key]); };
    }
    else if (f.type === 'font') {
        control = document.createElement('select');
        const known = FONTS.some(([v]) => v === s[f.key]);
        for (const [v, name] of known ? FONTS : [[s[f.key], 'Custom: ' + s[f.key].split(',')[0]], ...FONTS]) control.add(new Option(name, v));
        control.value = s[f.key];
        control.onchange = () => change({ [f.key]: control.value });
    }
    else {
        control = document.createElement('select');
        for (const [v, name] of f.options) control.add(new Option(name, v));
        control.value = s[f.key];
        control.onchange = () => change({ [f.key]: control.value });
    }
    if (f.unit && f.type === 'number') {
        const box = document.createElement('span');
        box.className = 'set-number';
        const u = document.createElement('span');
        u.className = 'set-unit';
        u.textContent = f.unit;
        box.append(control, u);
        return row(f.label, box, f.hint);
    }
    return row(f.label, control, f.hint);
}

/** A select or a checkbox of the page that stands for one PyMagic already has: it shows its state and presses it. */
function mirrorSelect(original, label, hint) {
    const sel = document.createElement('select');
    for (const o of original.options) sel.add(new Option(o.textContent, o.value));
    sel.value = original.value;
    sel.onchange = () => { original.value = sel.value; original.dispatchEvent(new Event('change', { bubbles: true })); };
    return row(label, sel, hint);
}
function mirrorCheck(original, label, hint) {
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.checked = original.checked;
    box.onchange = () => { original.checked = box.checked; original.dispatchEvent(new Event('change', { bubbles: true })); };
    return row(label, box, hint);
}
function pressButton(text, target, close) {
    const b = document.createElement('button');
    b.textContent = text;
    b.onclick = () => { close(); target.click(); };
    return b;
}

function note(text) {
    const p = document.createElement('p');
    p.className = 'set-note';
    p.textContent = text;
    return p;
}

const SPECIAL = {
    formatter(page, close) {
        page.append(
            mirrorSelect($('fmt-engine'), 'Formatter', 'The built-in one needs nothing; black and ruff are used when the project\'s interpreter has them. The options of black and ruff come from pyproject.toml.'),
            mirrorCheck($('fmt-save'), 'Format the file when it is saved'),
            note('Imports are sorted by the same command; Shift+Alt+O only does that.'),
        );
    },
    python(page, close) {
        page.append(mirrorSelect($('interp'), 'Python interpreter', $('interp-note').textContent || undefined));
        const buttons = document.createElement('div');
        buttons.className = 'set-buttons';
        buttons.append(pressButton('Create a .venv…', $('b-venv'), close), pressButton('WSL, Docker or SSH…', $('b-remote'), close), pressButton('Packages', $('t-packages'), close));
        page.append(buttons);
    },
    inspections(page, close) {
        page.append(note('Which findings the editor shows, and how serious each is, is set in the Inspections panel (one list of every inspection with its level; the file is .pymagic/inspections.json). A line can also say # noqa or # noqa: code.'));
        const buttons = document.createElement('div');
        buttons.className = 'set-buttons';
        buttons.append(pressButton('Open the Inspections panel', $('t-insp'), close));
        page.append(buttons);
    },
    terminal(page) {
        const sel = document.createElement('select');
        sel.add(new Option('Automatic', 'auto'));
        sel.value = 'auto';
        sel.onchange = () => change({ terminalShell: sel.value });
        page.append(row('Shell', sel, 'New terminals start it; open ones keep theirs. The ▾ beside + in the Terminal panel starts one terminal in another shell.'));
        fetch('/api/shells').then((r) => r.json()).then(({ auto, shells }) => {
            sel.options[0].textContent = `Automatic (${auto})`;
            for (const s of shells) sel.add(new Option(s.name, s.id));
            const want = settings().terminalShell;
            sel.value = shells.some((s) => s.id === want) ? want : 'auto';
        }).catch(() => { /* only Automatic */ });
    },
    git(page) {
        page.append(
            mirrorCheck($('git-annotate'), 'Show who last changed each line', 'In the line numbers.'),
            mirrorCheck($('git-inline'), 'Show who changed the current line', 'After the line, in grey.'),
        );
    },
    django(page) {
        page.append(note('Kept in .pymagic/django.json. The engine finds manage.py and the settings by itself; what is set here wins.'));
        const box = document.createElement('div');
        page.append(box);
        fetch('/api/django/config').then((r) => r.json()).then((c) => {
            const input = (value, placeholder) => { const el = document.createElement('input'); el.value = value ?? ''; el.placeholder = placeholder; el.spellcheck = false; return el; };
            const check = (on) => { const el = document.createElement('input'); el.type = 'checkbox'; el.checked = on; return el; };
            const enabled = check(c.enabled !== false), rootBox = input(c.root, 'the project folder'), settingsBox = input(c.settings, 'as manage.py says (site_cfg.settings)');
            const manageBox = input(c.manage, 'found by itself (manage.py)'), runner = check(c.testRunner !== false);
            const env = document.createElement('textarea');
            env.rows = 3;
            env.placeholder = 'NAME=value, one per line: given to manage.py';
            env.value = Object.entries(c.env ?? {}).map(([k, v]) => `${k}=${v}`).join('\n');
            const languages = {};
            const langRows = ['.html', '.htm', '.jinja', '.jinja2', '.j2'].map((ext) => {
                const sel = document.createElement('select');
                for (const [v, l] of [['django', 'Django template'], ['jinja', 'Jinja2'], ['none', 'Plain HTML']]) sel.add(new Option(l, v));
                sel.value = (c.templateLanguages ?? {})[ext] ?? (ext.startsWith('.j') ? 'jinja' : 'django');
                languages[ext] = sel;
                return row(`Template language of ${ext}`, sel);
            });
            const save = document.createElement('button');
            save.className = 'primary';
            save.textContent = 'Save';
            save.onclick = async () => {
                const vars = {};
                for (const line of env.value.split(/\r?\n/)) { const at = line.indexOf('='); if (at > 0) vars[line.slice(0, at).trim()] = line.slice(at + 1).trim(); }
                const body = { enabled: enabled.checked, root: rootBox.value, settings: settingsBox.value, manage: manageBox.value, env: vars, testRunner: runner.checked,
                    templateLanguages: Object.fromEntries(Object.entries(languages).map(([k, s]) => [k, s.value])) };
                const res = await fetch('/api/django/config', { method: 'PUT', body: JSON.stringify(body) }).catch(() => null);
                const saved = res?.ok ? await res.json() : null;
                save.textContent = saved ? 'Saved' : 'Not saved';
                if (saved) {
                    window.dispatchEvent(new CustomEvent('pymagic:djangoConfig', { detail: saved }));
                    window.dispatchEvent(new CustomEvent('pymagic:folders'));   // the language server reads the template types again
                    settingsBox.value = saved.settings ?? '';
                    manageBox.value = saved.manage ?? '';
                    rootBox.value = saved.root ?? '';
                }
                window.setTimeout(() => { save.textContent = 'Save'; }, 1500);
            };
            const buttons = document.createElement('div');
            buttons.className = 'set-buttons';
            buttons.append(save);
            box.append(
                row('Django support', enabled, 'Off: no Django tab features, and .html files are plain HTML.'),
                row('Django project folder', rootBox, 'Where manage.py is looked for, from the project folder.'),
                row('Settings module', settingsBox),
                row('manage.py', manageBox, 'A path from the project folder.'),
                row('Variables for manage.py', env),
                row("Use Django's test runner", runner, 'Off: Django is set up and the tests run under unittest, without a test database.'),
                ...langRows,
                note('A file type set to plain HTML is coloured as before but not read as a template; open files take a new language when they are opened again.'),
                buttons);
        }).catch(() => { box.append(note('The engine did not answer.')); });
    },
    templates(page) {
        const section = (title, hint) => {
            const h = document.createElement('h3');
            h.textContent = title;
            page.append(h);
            if (hint) page.append(note(hint));
        };
        const input = (value, placeholder, onInput, cls = '') => {
            const el = document.createElement(cls === 'body' ? 'textarea' : 'input');
            el.value = value;
            el.placeholder = placeholder;
            el.spellcheck = false;
            el.className = 'tpl-' + (cls || 'field');
            el.oninput = () => onInput(el.value);
            return el;
        };
        // an editable list kept in a file of the project: rows of fields, Add and Save
        function editable(file, read, write, blankItem, fields, empty) {
            const box = document.createElement('div');
            box.className = 'tpl-list';
            const buttons = document.createElement('div');
            buttons.className = 'set-buttons';
            const add = document.createElement('button');
            add.textContent = 'Add';
            const save = document.createElement('button');
            save.className = 'primary';
            save.textContent = 'Save';
            buttons.append(add, save);
            page.append(box, buttons);
            let items = [];
            const draw = () => {
                box.replaceChildren();
                if (items.length === 0) box.append(note(empty));
                items.forEach((item, i) => {
                    const row = document.createElement('div');
                    row.className = 'tpl-row';
                    for (const [key, placeholder, kind] of fields) row.append(input(item[key], placeholder, (v) => { item[key] = v; }, kind));
                    const del = document.createElement('button');
                    del.textContent = 'Remove';
                    del.onclick = () => { items.splice(i, 1); draw(); };
                    row.append(del);
                    box.append(row);
                });
            };
            add.onclick = () => { items.push(blankItem()); draw(); box.lastElementChild?.querySelector('input,textarea')?.focus(); };
            save.onclick = async () => {
                const res = await fetch('/api/file?path=' + encodeURIComponent(file), { method: 'PUT', body: write(items) }).catch(() => null);
                save.textContent = res?.ok ? 'Saved' : 'Not saved';
                window.setTimeout(() => { save.textContent = 'Save'; }, 1500);
            };
            fetch('/api/file?path=' + encodeURIComponent(file)).then((r) => (r.ok ? r.text() : '')).catch(() => '').then((text) => { items = read(text); draw(); });
        }

        section('Live templates', 'Typed at the start of a statement, then Tab or Enter. Postfix templates are typed after an expression: items.for, x.if, value.par.');
        const builtIn = document.createElement('div');
        builtIn.className = 'keymap';
        page.append(builtIn);
        liveTemplateSource?.().then((list) => {
            for (const t of list?.live ?? []) {
                const a = document.createElement('kbd');
                a.textContent = t.prefix;
                const b = document.createElement('span');
                b.textContent = t.description + (t.classBody ? ' (in a class)' : '') + (t.import ? ` (imports ${t.import})` : '');
                b.title = t.body.replace(/\$\{\d+:([^}]*)\}/g, '$1').replace(/\$\{?\d+\}?/g, '').replaceAll('\t', '    ');
                builtIn.append(b, a);
            }
            for (const t of list?.postfix ?? []) {
                const a = document.createElement('kbd');
                a.textContent = '.' + t.key;
                const b = document.createElement('span');
                b.textContent = 'postfix: ' + t.example;
                builtIn.append(b, a);
            }
        }).catch(() => { /* the server is not there yet */ });

        section('This project\'s templates', `Kept in ${SNIPPETS}, in VS Code's form. In the text, $1 and \${1:name} are the places Tab goes to and $0 is where the cursor ends.`);
        editable(SNIPPETS, (text) => readSnippets(text) ?? [], writeSnippets, () => ({ name: '', prefix: '', description: '', body: '' }),
            [['prefix', 'abbreviation'], ['description', 'description'], ['body', 'text', 'body']], 'None yet.');

        section('File templates', `What a new file starts with, the first whose pattern fits its name; kept in ${FILE_TEMPLATES}. \${NAME}, \${FILE_NAME}, \${DATE}, \${TIME}, \${YEAR} and \${PROJECT_NAME} are filled in.`);
        editable(FILE_TEMPLATES, (text) => readFileTemplates(text).map((t) => ({ ...t })), (items) => JSON.stringify(items, null, 2) + '\n',
            () => ({ name: '', pattern: '*.py', body: '' }), [['name', 'name'], ['pattern', 'pattern such as test_*.py'], ['body', 'text', 'body']], 'None: new files start empty.');
    },
    keymap(page) {
        for (const [group, keys] of KEYMAP) {
            const h = document.createElement('h3');
            h.textContent = group;
            const t = document.createElement('div');
            t.className = 'keymap';
            for (const [what, key] of keys) {
                const a = document.createElement('span');
                a.textContent = what;
                const k = document.createElement('kbd');
                k.textContent = key;
                t.append(a, k);
            }
            page.append(h, t);
        }
    },
    about(page) {
        const p = document.createElement('div');
        p.className = 'about';
        p.innerHTML = '<svg class="mark" viewBox="0 0 256 256" aria-hidden="true"><rect x="40" y="40" width="176" height="176" rx="44" fill="url(#g-brand)"/><path fill="#fff" fill-rule="evenodd" d="M88 76 H134 A38 38 0 0 1 134 152 H112 V180 H88 Z M112 98 H132 A15 15 0 0 1 132 128 H112 Z"/><path fill="#ffd866" d="M184 62 L190 82 L210 88 L190 94 L184 114 L178 94 L158 88 L178 82 Z"/></svg>'
            + '<div><div class="about-name">PyMagic</div><div class="about-line">A Python IDE for Windows.</div></div>';
        page.append(p);
        const info = document.createElement('div');
        info.className = 'keymap about-table';
        page.append(info);
        fetch('/api/info').then((r) => r.json()).then((i) => {
            const items = [['Project', i.root], ['Page', buildLabel()], ['Engine', 'C# on .NET, built ' + (i.engineBuiltAt ? shortTime(i.engineBuiltAt) : 'at an unknown time')], ['Editor', 'Monaco'], ['Typeshed', i.typeshed ?? 'bundled stubs'], ['Settings file', '%LOCALAPPDATA%\\PyMagic\\settings.json']];
            for (const [a, b] of items) {
                const x = document.createElement('span');
                x.textContent = a;
                const y = document.createElement('span');
                y.className = 'about-value';
                y.textContent = b;
                info.append(x, y);
            }
        }).catch(() => { /* the table stays empty */ });
    },
};

const EXTRA = [['formatter', 'Formatter'], ['python', 'Python'], ['inspections', 'Inspections'], ['terminal', 'Terminal'], ['git', 'Git'], ['django', 'Django'], ['templates', 'Templates'], ['keymap', 'Keymap'], ['about', 'About']];

let isOpen = false;

export function openSettings(start = 'appearance') {
    if (isOpen) return;
    isOpen = true;
    const shade = document.createElement('div');
    shade.className = 'settings-shade';
    const box = document.createElement('div');
    box.className = 'settings';
    box.setAttribute('role', 'dialog');
    box.setAttribute('aria-label', 'Settings');
    const nav = document.createElement('nav');
    nav.className = 'settings-nav';
    const title = document.createElement('h2');
    title.textContent = 'Settings';
    nav.append(title);
    const body = document.createElement('div');
    body.className = 'settings-body';
    const content = document.createElement('div');
    content.className = 'settings-page';
    const foot = document.createElement('div');
    foot.className = 'settings-foot';
    const reset = document.createElement('button');
    reset.textContent = 'Reset the editor and appearance';
    reset.title = 'Back to what PyMagic starts with; the formatter, interpreter and Git choices are not touched';
    const done = document.createElement('button');
    done.className = 'primary';
    done.textContent = 'Done';
    foot.append(reset, done);
    body.append(content, foot);
    box.append(nav, body);
    shade.append(box);

    const close = () => { shade.remove(); isOpen = false; document.removeEventListener('keydown', onKey, true); };
    const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(); } };
    const buttons = new Map();
    function show(id) {
        for (const [k, b] of buttons) b.classList.toggle('active', k === id);
        content.replaceChildren();
        const page = PAGES.find(([k]) => k === id);
        const head = document.createElement('h2');
        head.textContent = (page ?? EXTRA.find(([k]) => k === id))[1] ?? '';
        content.append(head);
        if (page) for (const f of page[2]) content.append(field(f));
        else SPECIAL[id](content, close);
        reset.style.display = page ? '' : 'none';
    }
    for (const [id, name] of [...PAGES.map(([k, n]) => [k, n]), ...EXTRA]) {
        const b = document.createElement('button');
        b.textContent = name;
        b.onclick = () => show(id);
        buttons.set(id, b);
        nav.append(b);
    }
    reset.onclick = () => { resetAll(); show([...buttons].find(([, b]) => b.classList.contains('active'))?.[0] ?? 'appearance'); };
    done.onclick = close;
    shade.onmousedown = (e) => { if (e.target === shade) close(); };
    document.addEventListener('keydown', onKey, true);
    document.body.append(shade);
    show(buttons.has(start) ? start : 'appearance');
    nav.querySelector('button.active')?.focus();
}

export function setupSettingsKey() {
    document.addEventListener('keydown', (e) => {
        if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && e.key === ',') { e.preventDefault(); openSettings(); }
    });
}


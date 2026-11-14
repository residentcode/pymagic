// Settings > Templates, the live templates: the key that expands them, the groups with their templates (a box each to switch one or a whole
// group on and off), Add / Remove / Copy / Restore, and the selected template's abbreviation, description, text, key and where it applies.
// Every change is kept at once (settings.templates) and the editor follows.
import { change, settings } from './settings';
import { openMenu } from './menu';
import {
    CONTEXTS, EXPAND_KEYS, keyName, contextNames, allTemplates, groupsOf, cleanConf, setOn, edited, restored, added, removed,
    groupAdded, groupRenamed, groupRemoved, preview, placesOf, EXPRESSIONS,
} from './live-templates-model.js';

const el = (tag, cls, text) => {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
};

/** Builds the live templates part of the page; <paramref name="source"/> gives the language server's templates (null while it is not there). */
export function liveTemplatesSection(page, source) {
    let server = null;
    let conf = cleanConf(settings().templates);
    let templates = [], groups = [];
    let selected = null;            // { template: id } or { group: name }
    const open = new Set();         // the groups shown with their templates
    let filter = '';

    const save = (next) => { conf = cleanConf(next); change({ templates: conf }); conf = cleanConf(settings().templates); };
    const refresh = () => { templates = allTemplates(server, conf); groups = groupsOf(templates, conf); };
    const current = () => (selected?.template ? templates.find((t) => t.id === selected.template) : null);
    const currentGroup = () => (selected?.group ? groups.find((g) => g.name === selected.group) : null);

    const root = el('div', 'lt');
    root.id = 'live-templates';

    // the key
    const keyBox = el('select');
    keyBox.id = 'lt-expand';
    for (const [id, name] of EXPAND_KEYS) keyBox.add(new Option(name, id));
    keyBox.value = conf.expand;
    keyBox.onchange = () => { save({ ...conf, expand: keyBox.value }); refresh(); drawEdit(); };
    const keyRow = el('label', 'lt-key', 'By default expand with ');
    keyRow.append(keyBox);

    // the bar
    const bar = el('div', 'lt-bar');
    const button = (id, text, title) => { const b = el('button', 'lt-tool', text); b.id = id; b.title = title; bar.append(b); return b; };
    const addBtn = button('lt-add', '+', 'Add a template or a group');
    const removeBtn = button('lt-remove', '−', 'Remove (your own templates and groups)');
    const copyBtn = button('lt-copy', 'Copy', 'A copy of the template, as one of your own');
    const restoreBtn = button('lt-restore', 'Restore', 'The template, or every template of the group, as it comes');
    const search = el('input');
    search.type = 'search';
    search.id = 'lt-search';
    search.placeholder = 'Find a template';
    search.oninput = () => { filter = search.value.trim().toLowerCase(); drawTree(); };
    bar.append(search);

    const tree = el('div', 'lt-tree');
    tree.id = 'lt-tree';
    const edit = el('div', 'lt-edit');
    edit.id = 'lt-edit';
    root.append(keyRow, bar, tree, edit);
    page.append(root);

    const shown = (t) => !filter || t.abbr.toLowerCase().includes(filter) || t.description.toLowerCase().includes(filter) || t.group.toLowerCase().includes(filter);

    function drawTree() {
        const top = tree.scrollTop;
        tree.replaceChildren();
        for (const g of groups) {
            const list = g.templates.filter(shown);
            if (filter && list.length === 0) continue;
            const isOpen = filter ? true : open.has(g.name);
            const head = el('div', 'lt-group' + (selected?.group === g.name ? ' sel' : ''));
            head.dataset.group = g.name;
            const twist = el('span', 'lt-twist', isOpen ? '▾' : '▸');
            const box = el('input');
            box.type = 'checkbox';
            const on = g.templates.filter((t) => t.on).length;
            box.checked = g.templates.length > 0 && on === g.templates.length;
            box.indeterminate = on > 0 && on < g.templates.length;
            box.disabled = g.templates.length === 0;
            box.onclick = (e) => e.stopPropagation();
            box.onchange = () => { save(setOn(conf, g.templates.map((t) => t.id), box.checked)); refresh(); draw(); };
            head.append(twist, box, el('span', 'lt-name', g.name), el('span', 'lt-count', String(g.templates.length)));
            twist.onclick = (e) => { e.stopPropagation(); if (open.has(g.name)) open.delete(g.name); else open.add(g.name); drawTree(); };
            head.onclick = () => { selected = { group: g.name }; draw(); };
            head.ondblclick = () => { if (open.has(g.name)) open.delete(g.name); else open.add(g.name); drawTree(); };
            tree.append(head);
            if (!isOpen) continue;
            for (const t of list) {
                const row = el('div', 'lt-row' + (selected?.template === t.id ? ' sel' : '') + (t.changed || !t.builtIn ? ' changed' : '') + (t.on ? '' : ' off'));
                row.dataset.id = t.id;
                const one = el('input');
                one.type = 'checkbox';
                one.checked = t.on;
                one.onclick = (e) => e.stopPropagation();
                one.onchange = () => { save(setOn(conf, [t.id], one.checked)); refresh(); draw(); };
                row.append(one, el('span', 'lt-abbr', t.abbr || '<abbreviation>'), el('span', 'lt-desc', t.description ? `(${t.description})` : ''));
                row.onclick = () => { selected = { template: t.id }; draw(); };
                tree.append(row);
            }
        }
        if (!tree.firstChild) tree.append(el('div', 'lt-none', 'No template has this in its abbreviation, description or group.'));
        tree.scrollTop = top;
    }

    /** A change of the selected template's fields: kept, the tree's row redrawn (the fields stay as they are, the cursor in them). */
    function patch(fields) {
        const t = current();
        if (!t) return;
        save(edited(conf, t, fields));
        refresh();
        drawTree();
        drawBar();
    }

    function drawBar() {
        const t = current(), g = currentGroup();
        removeBtn.disabled = !((t && !t.builtIn) || (g && g.own));
        copyBtn.disabled = !t;
        restoreBtn.disabled = !((t && t.builtIn && (t.changed || !t.on)) || (g && g.templates.some((x) => x.builtIn && (x.changed || !x.on))));
    }

    let drawVars = () => {};
    function drawEdit() {
        drawVars = () => {};
        edit.replaceChildren();
        const t = current(), g = currentGroup();
        if (g) {
            const name = el('input', 'lt-field');
            name.id = 'lt-group-name';
            name.value = g.name;
            name.disabled = !g.own;
            name.onchange = () => {
                const to = name.value.trim();
                if (!to || to === g.name || groups.some((x) => x.name === to)) { name.value = g.name; return; }
                if (open.delete(g.name)) open.add(to);
                save(groupRenamed(conf, g.name, to));
                selected = { group: to };
                refresh();
                draw();
            };
            const row = el('label', 'lt-line', 'Group ');
            row.append(name);
            const on = g.templates.filter((x) => x.on).length;
            edit.append(row, el('p', 'set-note', g.own
                ? `One of your own groups: ${g.templates.length} template${g.templates.length === 1 ? '' : 's'}. + adds a template to it.`
                : `${g.templates.length} templates, ${on} switched on. The box before the name switches all of them.`));
            return;
        }
        if (!t) {
            edit.append(el('p', 'set-note', 'Select a template to see and change its text. In a file, type the abbreviation and press the key above; the templates of the file\'s language are in the completion list too.'));
            return;
        }
        const postfix = t.server === 'postfix';
        const field = (id, value, onInput, tag = 'input') => {
            const f = el(tag, tag === 'textarea' ? 'lt-body' : 'lt-field');
            f.id = id;
            f.value = value;
            f.spellcheck = false;
            f.disabled = postfix;
            f.oninput = () => onInput(f.value);
            return f;
        };
        const top = el('div', 'lt-top');
        const abbr = el('label', 'lt-line', 'Abbreviation ');
        abbr.append(field('lt-abbr', t.abbr, (v) => { if (v.trim()) patch({ abbr: v.trim() }); }));
        const desc = el('label', 'lt-line lt-wide', 'Description ');
        desc.append(field('lt-description', t.description, (v) => patch({ description: v })));
        top.append(abbr, desc);
        if (!t.builtIn) {
            // one of the person's own can go to another of their groups
            const into = el('select');
            into.id = 'lt-group';
            for (const g of groups.filter((x) => x.own)) into.add(new Option(g.name, g.name));
            into.value = t.group;
            into.onchange = () => { open.add(into.value); save(edited(conf, t, { group: into.value })); refresh(); draw(); };
            const line = el('label', 'lt-line', 'Group ');
            line.append(into);
            top.append(line);
        }

        const body = field('lt-body', t.body, (v) => { patch({ body: v }); shows.textContent = preview(v, current()?.vars); drawVars(); }, 'textarea');
        body.onkeydown = (e) => {
            // Tab writes an indentation in the text, as in an editor
            if (e.key !== 'Tab' || e.shiftKey || e.ctrlKey || e.altKey) return;
            e.preventDefault();
            body.setRangeText('    ', body.selectionStart, body.selectionEnd, 'end');
            body.oninput();
        };
        const left = el('div', 'lt-left');
        left.append(el('div', 'lt-label', postfix ? 'Example' : 'Template text'), body);

        const right = el('div', 'lt-right');
        right.append(el('div', 'lt-label', 'Options'));
        const key = el('select');
        key.id = 'lt-key';
        key.add(new Option(`Default (${keyName(conf.expand)})`, ''));
        for (const [id, name] of EXPAND_KEYS) key.add(new Option(name, id));
        key.value = t.expand || '';
        key.disabled = postfix;
        key.onchange = () => patch({ expand: key.value });
        const keyLine = el('label', 'lt-line', 'Expand with ');
        keyLine.append(key);
        const start = el('input');
        start.type = 'checkbox';
        start.id = 'lt-linestart';
        start.checked = !!t.lineStart;
        start.disabled = postfix || !!t.server;
        start.onchange = () => patch({ lineStart: start.checked });
        const startLine = el('label', 'lt-check');
        startLine.append(start, ' Only at the start of a line');
        const tidy = el('input');
        tidy.type = 'checkbox';
        tidy.id = 'lt-reformat';
        tidy.checked = !!t.reformat;
        tidy.disabled = postfix;
        tidy.onchange = () => patch({ reformat: tidy.checked });
        const tidyLine = el('label', 'lt-check');
        tidyLine.title = 'Once the template is filled in, its lines are formatted by the language\'s formatter';
        tidyLine.append(tidy, ' Reformat according to style');
        const varsBtn = el('button', 'lt-tool', 'Edit Variables…');
        varsBtn.id = 'lt-vars-button';
        varsBtn.disabled = postfix;
        right.append(keyLine, startLine, tidyLine, varsBtn);
        // the places of the text, each with what fills it: an expression, a default, and whether it is still stopped at
        const varsBox = el('div', 'lt-vars');
        varsBox.id = 'lt-vars';
        varsBox.hidden = true;
        varsBtn.onclick = () => { varsBox.hidden = !varsBox.hidden; drawVars(); };
        const list = el('datalist');
        list.id = 'lt-expressions';
        for (const [form, what] of EXPRESSIONS) { const o = new Option(what, form); list.append(o); }
        drawVars = () => {
            if (varsBox.hidden) return;
            const now = current();
            const names = placesOf(now?.body ?? '');
            varsBox.replaceChildren(list);
            if (!names.length) { varsBox.append(el('p', 'set-note', 'The text has no $name$ places yet.')); return; }
            for (const h of ['Name', 'Expression', 'Default value', 'Skip if defined']) varsBox.append(el('div', 'lt-label', h));
            for (const name of names) {
                const v = now.vars?.[name] ?? { expr: '', def: '', skip: false };
                const change = (part) => {
                    const fresh = current();
                    const vars = { ...(fresh.vars ?? {}) };
                    const one = { expr: '', def: '', skip: false, ...(vars[name] ?? {}), ...part };
                    if (one.expr.trim() || one.def) vars[name] = one; else delete vars[name];
                    patch({ vars });
                    shows.textContent = preview(fresh.body, vars);
                };
                const expr = el('input', 'lt-field');
                expr.value = v.expr;
                expr.setAttribute('list', 'lt-expressions');
                expr.placeholder = 'fileName(), date(), capitalize(other)…';
                expr.dataset.name = name;
                expr.oninput = () => change({ expr: expr.value });
                const def = el('input', 'lt-field');
                def.value = v.def;
                def.placeholder = name;
                def.dataset.default = name;
                def.oninput = () => change({ def: def.value });
                const skip = el('input');
                skip.type = 'checkbox';
                skip.checked = !!v.skip;
                skip.dataset.skip = name;
                skip.onchange = () => change({ skip: skip.checked });
                varsBox.append(el('code', '', name), expr, def, skip);
            }
        };
        if (!postfix) {
            const shows = el('pre', 'lt-preview');
            shows.id = 'lt-preview';
            right.append(el('div', 'lt-label', 'Writes'), shows);
        }
        const shows = right.querySelector('#lt-preview') ?? el('pre');
        shows.textContent = preview(t.body, t.vars);

        const mid = el('div', 'lt-mid');
        mid.append(left, right);

        // where it applies
        const where = el('div', 'lt-where');
        const names = contextNames(t.contexts);
        const says = el('span', names.length ? '' : 'lt-warn', names.length ? `Applicable in ${names.join(', ')}.` : 'No applicable contexts yet.');
        says.id = 'lt-where';
        const choose = el('button', 'lt-link', 'Change ▾');
        choose.id = 'lt-contexts';
        choose.disabled = postfix;
        const boxes = el('div', 'lt-contexts');
        boxes.hidden = true;
        for (const [id, name] of CONTEXTS) {
            const c = el('input');
            c.type = 'checkbox';
            c.value = id;
            c.checked = t.contexts.includes(id);
            c.onchange = () => {
                const now = [...boxes.querySelectorAll('input')].filter((x) => x.checked).map((x) => x.value);
                patch({ contexts: now });
                const n = contextNames(now);
                says.className = n.length ? '' : 'lt-warn';
                says.textContent = n.length ? `Applicable in ${n.join(', ')}.` : 'No applicable contexts yet.';
            };
            const l = el('label', 'lt-check');
            l.append(c, ' ' + name);
            boxes.append(l);
        }
        choose.onclick = () => { boxes.hidden = !boxes.hidden; };
        where.append(says, ' ', choose, boxes);

        edit.append(top, mid, varsBox, where);
        const hints = [];
        if (t.note) hints.push(t.note);
        if (!postfix) hints.push('In the text, $name$ is a place Tab goes to (the same name twice is typed once), $END$ where the cursor ends and $SELECTION$ the selected text; $1, ${1:name} and $0 are read too.');
        if (t.server && !postfix) hints.push('With another abbreviation or place, the template is offered wherever the abbreviation is typed, without its import.');
        for (const h of hints) edit.append(el('p', 'set-note', h));
    }

    function draw() { drawTree(); drawBar(); drawEdit(); }

    addBtn.onclick = () => {
        const r = addBtn.getBoundingClientRect();
        openMenu({ x: r.left, y: r.bottom + 2 }, [
            { label: 'Live Template', run: () => {
                const t = current(), g = currentGroup();
                const into = g?.own ? g.name : t && !t.builtIn ? t.group : 'user';
                const [next, id] = added(conf, into);
                save(next);
                open.add(into);
                selected = { template: id };
                refresh();
                draw();
                edit.querySelector('#lt-abbr')?.focus();
            } },
            { label: 'Template Group', run: () => {
                const [next, name] = groupAdded(conf, groups.map((x) => x.name));
                save(next);
                selected = { group: name };
                refresh();
                draw();
                const f = edit.querySelector('#lt-group-name');
                f?.focus();
                f?.select();
            } },
        ]);
    };
    removeBtn.onclick = () => {
        const t = current(), g = currentGroup();
        if (t && !t.builtIn) save(removed(conf, [t.id]));
        else if (g?.own) save(groupRemoved(conf, g.name));
        else return;
        selected = null;
        refresh();
        draw();
    };
    copyBtn.onclick = () => {
        const t = current();
        if (!t) return;
        const into = t.builtIn ? 'user' : t.group;
        const [next, id] = added(conf, into, t);
        save(next);
        open.add(into);
        selected = { template: id };
        refresh();
        draw();
        edit.querySelector('#lt-abbr')?.focus();
    };
    restoreBtn.onclick = () => {
        const t = current(), g = currentGroup();
        const ids = t?.builtIn ? [t.id] : g ? g.templates.filter((x) => x.builtIn).map((x) => x.id) : [];
        if (!ids.length) return;
        save(restored(conf, ids));
        refresh();
        draw();
    };

    refresh();
    draw();
    Promise.resolve(source?.()).then((list) => { if (list?.live) { server = list; refresh(); draw(); } }).catch(() => { /* the server is not there yet */ });
}

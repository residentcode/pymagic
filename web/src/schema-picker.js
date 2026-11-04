// The status bar's "Schema: ..." for a JSON, YAML, TOML or Markdown file, as PyCharm has it: which schema checks the file, and a menu to
// ignore schemas for the file, map it to another of the built-in schemas, or make a new mapping (a name, a schema, the files it is for).
// The mappings are the project's (.pymagic/schema-mappings.json); Settings > JSON Schemas lists and edits them (schemaMappingsPage).
import { openMenu } from './menu';
import { toast } from './toast';

const $ = (id) => document.getElementById(id);

async function getJson(url, fallback) {
    try { const r = await fetch(url); return r.ok ? await r.json() : fallback; } catch { return fallback; }
}
const listNow = () => getJson('/api/schemas/list', { registered: [], ids: {} });
const mappingsNow = () => getJson('/api/schemas/mappings', { mappings: [], ignored: [] });

/** Saves the project's mappings and tells the schema check to look at the open files again. */
async function saveMappings(next) {
    const res = await fetch('/api/schemas/mappings', { method: 'PUT', body: JSON.stringify(next) }).catch(() => null);
    if (!res?.ok) { toast('The schema mappings could not be saved', true); return false; }
    window.dispatchEvent(new CustomEvent('pymagic:schema-mappings'));
    return true;
}

/**
 * The dialog of one mapping: its name, its schema (a built-in one, or a .json of the project) and the files it is for (one path or pattern a
 * line: * within a name, ** for any folders). Resolves to the mapping, or null when it is cancelled.
 */
export function mappingDialog(title, registered, initial = { name: '', schema: '', patterns: [] }) {
    return new Promise((resolve) => {
        const shade = document.createElement('div');
        shade.className = 'ask-shade';
        const box = document.createElement('div');
        box.className = 'ask-box schema-mapping';
        const h = document.createElement('div');
        h.className = 'ask-title';
        h.textContent = title;
        const label = (text, control) => { const l = document.createElement('label'); l.className = 'schema-field'; const s = document.createElement('span'); s.textContent = text; l.append(s, control); return l; };
        const name = document.createElement('input');
        name.value = initial.name ?? '';
        name.placeholder = 'My config';
        const schema = document.createElement('select');
        for (const r of registered) schema.append(new Option(r.title, r.name));
        schema.append(new Option('A JSON file of the project…', 'file:'));
        const file = document.createElement('input');
        file.placeholder = 'schemas/config.schema.json';
        file.spellcheck = false;
        if (initial.schema?.startsWith('file:')) { schema.value = 'file:'; file.value = initial.schema.slice(5); }
        else if (initial.schema) schema.value = initial.schema;
        const fileRow = label('Schema file', file);
        const showFile = () => { fileRow.style.display = schema.value === 'file:' ? '' : 'none'; };
        schema.onchange = showFile;
        showFile();
        const patterns = document.createElement('textarea');
        patterns.rows = 4;
        patterns.spellcheck = false;
        patterns.value = (initial.patterns ?? []).join('\n');
        patterns.placeholder = 'config/app.json\nconfig/*.yaml\n**/settings.json';
        const note = document.createElement('div');
        note.className = 'ask-hint';
        note.textContent = 'Files: one a line, from the project folder; * stands for any name, ** for any folders. A line without a / is that file name in any folder.';
        const row = document.createElement('div');
        row.className = 'ask-buttons';
        const ok = document.createElement('button');
        ok.textContent = 'OK';
        ok.className = 'primary';
        const cancel = document.createElement('button');
        cancel.textContent = 'Cancel';
        row.append(ok, cancel);
        box.append(h, label('Name', name), label('Schema', schema), fileRow, label('Files', patterns), note, row);
        shade.append(box);
        const done = (value) => { shade.remove(); document.removeEventListener('keydown', onKey, true); resolve(value); };
        const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); done(null); } };
        ok.onclick = () => {
            const list = patterns.value.split('\n').map((s) => s.trim()).filter(Boolean);
            const target = schema.value === 'file:' ? file.value.trim() : schema.value;
            if (!list.length) { note.textContent = 'Name at least one file.'; patterns.focus(); return; }
            if (schema.value === 'file:' && !/\.json$/i.test(target)) { note.textContent = 'The schema file is a .json file of the project.'; file.focus(); return; }
            const chosen = schema.value === 'file:' ? 'file:' + target : target;
            done({ name: name.value.trim() || (registered.find((r) => r.name === chosen)?.title ?? target), schema: chosen, patterns: list });
        };
        cancel.onclick = () => done(null);
        shade.onmousedown = (e) => { if (e.target === shade) done(null); };
        document.addEventListener('keydown', onKey, true);
        document.body.append(shade);
        name.focus();
    });
}

/** The status bar's schema picker. `checker` is what setupSchemaCheck returned; `relOf` gives a model's project path. */
export function setupSchemaPicker({ editor, relOf, checker, openSettings }) {
    const item = document.createElement('span');
    item.id = 'schema';
    item.className = 'status-pick';
    item.title = 'The JSON Schema this file is checked against: a click chooses another, ignores schemas for the file, or makes a mapping';
    $('status').insertBefore(item, $('cursor'));

    function paint() {
        const model = editor.getModel();
        if (!model || !checker.isSchemaFile(model)) { item.style.display = 'none'; return; }
        const chosen = checker.choiceOf(model);
        item.style.display = '';
        item.textContent = chosen?.choice?.ignored ? 'Schema: ignored' : chosen?.label ? `Schema: ${chosen.label}` : 'No JSON schema';
    }
    editor.onDidChangeModel(paint);
    window.addEventListener('pymagic:schema-choice', (e) => { if (e.detail?.model === editor.getModel()) paint(); });
    paint();

    item.onclick = async () => {
        const model = editor.getModel();
        const rel = model && relOf(model);
        if (!rel) return;
        const [{ registered }, project] = await Promise.all([listNow(), mappingsNow()]);
        const chosen = checker.choiceOf(model)?.choice;
        const ignored = (project.ignored ?? []).includes(rel);
        // the file in a mapping of its own: out of every mapping that names it alone, and into the one of that schema (PyCharm's way)
        const assign = async (name, title) => {
            const mappings = (project.mappings ?? []).map((m) => ({ ...m, patterns: m.patterns.filter((p) => p !== rel) })).filter((m) => m.patterns.length);
            const own = mappings.find((m) => m.schema === name && m.name === title);
            if (own) own.patterns.push(rel);
            else mappings.push({ name: title, schema: name, patterns: [rel] });
            await saveMappings({ mappings, ignored: (project.ignored ?? []).filter((p) => p !== rel) });
        };
        const items = [
            {
                label: 'Ignore JSON schema for file', checked: ignored,
                run: () => void saveMappings({ ...project, ignored: ignored ? project.ignored.filter((p) => p !== rel) : [...(project.ignored ?? []), rel] }),
            },
            {
                label: 'New Schema Mapping…',
                run: async () => {
                    const m = await mappingDialog('New Schema Mapping', registered, { name: '', schema: chosen?.name ?? registered[0]?.name ?? '', patterns: [rel] });
                    if (m) await saveMappings({ ...project, mappings: [...(project.mappings ?? []), m] });
                },
            },
            { label: 'Edit Schema Mappings…', run: () => openSettings('schemas') },
            '-',
            ...registered.map((r) => ({ label: r.title, checked: !ignored && chosen?.name === r.name, run: () => void assign(r.name, r.title) })),
        ];
        openMenu(item, items);
    };
}

/** Settings > JSON Schemas: the project's mappings (name, schema, files) and its ignored files, each to edit or remove. */
export async function schemaMappingsPage(content) {
    const intro = document.createElement('p');
    intro.className = 'settings-note';
    intro.textContent = 'Which JSON Schema checks which files of this project, over what PyMagic chooses by a file\'s name or its "$schema" (kept in .pymagic/schema-mappings.json). The status bar\'s "Schema: ..." does the same for the open file.';
    const list = document.createElement('div');
    list.className = 'schema-mappings';
    const add = document.createElement('button');
    add.textContent = 'Add mapping…';
    content.append(intro, list, add);
    const { registered } = await listNow();
    let project = await mappingsNow();
    const titleOf = (schema) => (schema.startsWith('file:') ? schema.slice(5) : registered.find((r) => r.name === schema)?.title ?? schema);
    const save = async (next) => { if (await saveMappings(next)) { project = next; paint(); } };
    function paint() {
        list.replaceChildren();
        if (!project.mappings.length && !project.ignored.length) {
            const none = document.createElement('div');
            none.className = 'detail';
            none.textContent = 'No mappings: every file gets the schema of its name or its "$schema".';
            list.append(none);
        }
        project.mappings.forEach((m, i) => {
            const row = document.createElement('div');
            row.className = 'schema-mapping-row';
            const text = document.createElement('span');
            text.textContent = `${m.name} → ${titleOf(m.schema)}`;
            const files = document.createElement('span');
            files.className = 'detail';
            files.textContent = m.patterns.join(', ');
            const edit = document.createElement('button');
            edit.textContent = 'Edit…';
            edit.onclick = async () => {
                const next = await mappingDialog('Edit Schema Mapping', registered, m);
                if (next) await save({ ...project, mappings: project.mappings.map((x, j) => (j === i ? next : x)) });
            };
            const remove = document.createElement('button');
            remove.textContent = 'Remove';
            remove.onclick = () => void save({ ...project, mappings: project.mappings.filter((_, j) => j !== i) });
            row.append(text, files, edit, remove);
            list.append(row);
        });
        for (const path of project.ignored) {
            const row = document.createElement('div');
            row.className = 'schema-mapping-row';
            const text = document.createElement('span');
            text.textContent = `${path} (no schema)`;
            const remove = document.createElement('button');
            remove.textContent = 'Check it again';
            remove.onclick = () => void save({ ...project, ignored: project.ignored.filter((p) => p !== path) });
            row.append(text, remove);
            list.append(row);
        }
    }
    add.onclick = async () => {
        const m = await mappingDialog('New Schema Mapping', registered);
        if (m) await save({ ...project, mappings: [...project.mappings, m] });
    };
    paint();
}

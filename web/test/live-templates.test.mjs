// Live templates as data (web/src/live-templates-model.js): the built-in groups, the text of a template, the person's changes and the
// template the text before the cursor names.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { GROUPS } from '../src/live-templates-data.js';
import {
    CONTEXTS, toSnippet, preview, cleanConf, builtIns, allTemplates, groupsOf, setOn, edited, restored, added, removed, groupAdded, groupRenamed,
    groupRemoved, matchAt, offered, serverItems, servedByServer, applicable,
} from '../src/live-templates-model.js';

const SERVER = {
    live: [{ prefix: 'if', description: 'if statement', body: 'if ${1:condition}:\n\t${0:pass}', classBody: false, import: null },
        { prefix: 'log', description: 'logger for this module', body: 'logger = logging.getLogger(__name__)', classBody: false, import: 'logging' }],
    template: [{ prefix: 'if', description: '{% if %}', body: '{% if ${1:condition} %}\n\t$0\n{% endif %}' }],
    postfix: [{ key: 'par', description: '(expr)', example: '(x)' }],
};
const empty = cleanConf(undefined);
const find = (list, id) => list.find((t) => t.id === id);

test('the built-in groups: every template whole, no abbreviation twice in a group, known contexts', () => {
    const contexts = new Set(CONTEXTS.map((c) => c[0]));
    assert.ok(GROUPS.length >= 17);
    for (const g of GROUPS) {
        assert.ok(g.contexts.every((c) => contexts.has(c)), g.name);
        const seen = new Set();
        for (const t of g.templates) {
            assert.ok(t.abbr && t.description && t.body, `${g.name}/${t.abbr}`);
            assert.ok(!seen.has(t.abbr), `${g.name}/${t.abbr} twice`);
            seen.add(t.abbr);
            // a text with a $ has its places by name (a bare $ would be read as a snippet's)
            if (t.body.includes('$')) assert.match(t.body, /\$[A-Za-z_]\w*\$/, `${g.name}/${t.abbr}`);
        }
    }
    assert.ok(builtIns(null).length > 200);
    // one abbreviation, one template in a language (the key has no list to choose from)
    const all = builtIns(SERVER).filter((t) => t.server !== 'postfix');
    for (const [, , languages] of CONTEXTS)
        for (const language of languages) {
            const seen = new Map();
            for (const t of all.filter((x) => applicable(x, language))) {
                assert.ok(!seen.has(t.abbr), `${t.abbr} in ${language}: ${seen.get(t.abbr)} and ${t.id}`);
                seen.set(t.abbr, t.id);
            }
        }
    const names = groupsOf(allTemplates(SERVER, empty), empty).map((g) => g.name);
    for (const n of ['Django', 'Django Templates', 'Flask', 'HTML/XML', 'HTTP Request', 'JavaScript', 'JavaScript Testing', 'Python', 'Python postfix', 'Shell Script', 'SQL'])
        assert.ok(names.includes(n), n);
});

test('the text of a template: named places, the cursor, a $ of the language', () => {
    assert.equal(toSnippet('for (let $i$ = 0; $i$ < $n$; $i$++) {\n    $END$\n}'), 'for (let ${1:i} = 0; ${1:i} < ${2:n}; ${1:i}++) {\n\t$0\n\\}');
    assert.equal(toSnippet('foreach ($$items$ as $$item$) {'), 'foreach (\\$${1:items} as \\$${2:item}) {');
    assert.equal(toSnippet('$validated = $request->validate([\n    \'$field$\' => 1,\n]);'), "\\$validated = \\$request->validate([\n\t'${1:field}' => 1,\n]);");
    assert.equal(toSnippet('<b>$SELECTION$</b>$END$'), '<b>${TM_SELECTED_TEXT}</b>$0');
    // a snippet as it is
    assert.equal(toSnippet('if ${1:condition}:\n\t${0:pass}'), 'if ${1:condition}:\n\t${0:pass}');
    assert.equal(preview('if ($$a$) {\n    $END$\n}'), 'if ($a) {\n    \n}');
    assert.equal(preview('if ${1:condition}:\n\t${0:pass}'), 'if condition:\n    pass');
});

test('switched off, edited, restored', () => {
    let conf = setOn(empty, ['JavaScript/fori', 'Python/if'], false);
    let all = allTemplates(SERVER, conf);
    assert.equal(find(all, 'JavaScript/fori').on, false);
    assert.equal(find(all, 'JavaScript/forof').on, true);
    conf = edited(conf, find(all, 'JavaScript/log'), { body: 'console.debug($END$);' });
    all = allTemplates(SERVER, conf);
    assert.equal(find(all, 'JavaScript/log').body, 'console.debug($END$);');
    assert.equal(find(all, 'JavaScript/log').changed, true);
    assert.deepEqual(conf.changed, { 'JavaScript/log': { body: 'console.debug($END$);' } });
    // put back by hand: no change left
    conf = edited(conf, find(all, 'JavaScript/log'), { body: find(all, 'JavaScript/log').original.body });
    assert.deepEqual(conf.changed, {});
    conf = edited(conf, find(allTemplates(SERVER, conf), 'JavaScript/log'), { abbr: 'cl2', expand: 'space' });
    conf = restored(conf, ['JavaScript/log', 'JavaScript/fori']);
    assert.deepEqual(conf.changed, {});
    assert.deepEqual(conf.off, ['Python/if']);
    // what is kept survives the settings file, a broken one changes nothing
    assert.deepEqual(cleanConf(JSON.parse(JSON.stringify(conf))), conf);
    assert.deepEqual(cleanConf({ expand: 'f1', off: 'x', changed: [1], own: [{ id: 'other/1' }, null], groups: [3] }), empty);
});

test('the person\'s own templates and groups', () => {
    let [conf, id] = added(empty, 'user');
    conf = edited(conf, find(allTemplates(null, conf), id), { abbr: 'hdr', body: '// $name$\n$END$', contexts: ['javascript', 'css'] });
    let all = allTemplates(null, conf);
    assert.equal(find(all, id).abbr, 'hdr');
    assert.ok(applicable(find(all, id), 'typescript') && applicable(find(all, id), 'scss') && !applicable(find(all, id), 'python'));
    let name;
    [conf, name] = groupAdded(conf, groupsOf(all, conf).map((g) => g.name));
    assert.equal(name, 'New group');
    conf = groupRenamed(conf, 'user', 'mine');
    assert.equal(find(allTemplates(null, conf), id).group, 'mine');
    let copy;
    [conf, copy] = added(conf, 'mine', find(allTemplates(null, conf), 'SQL/sel'));
    assert.equal(find(allTemplates(null, conf), copy).body, find(allTemplates(null, conf), 'SQL/sel').body);
    const groups = groupsOf(allTemplates(null, conf), conf);
    assert.deepEqual(groups.filter((g) => g.own).map((g) => [g.name, g.templates.length]), [['mine', 2], ['New group', 0]]);
    conf = removed(conf, [copy]);
    conf = groupRemoved(conf, 'mine');
    assert.deepEqual([conf.own, conf.groups], [[], ['New group']]);
});

test('the template the text before the cursor names', () => {
    const all = allTemplates(SERVER, empty);
    assert.equal(matchAt(all, empty, 'javascript', '    fori', 'tab').id, 'JavaScript/fori');
    assert.equal(matchAt(all, empty, 'typescript', 'x = fori', 'tab').id, 'JavaScript/fori');
    assert.equal(matchAt(all, empty, 'javascript', 'before', 'tab'), null);            // no template of this name
    assert.equal(matchAt(all, empty, 'javascript', 'xfori', 'tab'), null);             // inside a word
    assert.equal(matchAt(all, empty, 'javascript', 'fori', 'enter'), null);            // not this key
    assert.equal(matchAt(all, empty, 'python', 'fori', 'tab'), null);                  // not this language
    assert.equal(matchAt(all, empty, 'python', '    route', 'tab').id, 'Flask/route');
    assert.equal(matchAt(all, empty, 'python', 'x = route', 'tab'), null);             // only at the start of a line
    assert.equal(matchAt(all, empty, 'python', 'x.par', 'tab'), null);                 // postfix ones are the server's completion
    assert.equal(matchAt(all, empty, 'django-html', '<p>if', 'tab').id, 'Django Templates/if');
    assert.equal(matchAt(all, empty, 'sql', 'selw', 'tab').id, 'SQL/selw');            // the longest
    // its own key, and the settings' key
    const spaced = edited(empty, find(all, 'JavaScript/log'), { expand: 'space' });
    assert.equal(matchAt(allTemplates(SERVER, spaced), spaced, 'javascript', 'log', 'space').id, 'JavaScript/log');
    assert.equal(matchAt(allTemplates(SERVER, spaced), spaced, 'javascript', 'log', 'tab'), null);
    const enter = { ...empty, expand: 'enter' };
    assert.equal(matchAt(all, enter, 'javascript', 'fori', 'enter').id, 'JavaScript/fori');
    const off = setOn(empty, ['JavaScript/fori'], false);
    assert.equal(matchAt(allTemplates(SERVER, off), off, 'javascript', 'fori', 'tab'), null);
});

test('completion: the editor\'s own templates, and the language server\'s items with the changes', () => {
    const all = allTemplates(SERVER, empty);
    const js = offered(all, 'javascript', '').map((t) => t.id);
    assert.ok(js.includes('JavaScript/fori') && js.includes('Express/exget') && !js.includes('TypeScript/int') && !js.includes('Python/if'));
    assert.ok(offered(all, 'typescript', '').some((t) => t.id === 'TypeScript/int'));
    assert.ok(!offered(all, 'python', 'x = ').some((t) => t.id === 'Flask/route'));
    assert.ok(!offered(all, 'python', '').some((t) => t.server));                      // Python's own are the server's to offer

    const items = [
        { label: 'if', isSnippet: true, sortText: '4if', insertText: 'if ${1:condition}:\n\t${0:pass}', detail: 'if statement' },
        { label: 'log', isSnippet: true, sortText: '4log', insertText: 'logger = logging.getLogger(__name__)', additionalTextEdits: [{}] },
        { label: 'par', isSnippet: true, sortText: 'z9par', insertText: '(x)' },
        { label: 'items', isSnippet: false, sortText: '1items' },
    ];
    assert.deepEqual(serverItems(items, all, 'python'), items);
    let conf = setOn(empty, ['Python/if', 'Python postfix/par'], false);
    assert.deepEqual(serverItems(items, allTemplates(SERVER, conf), 'python').map((i) => i.label), ['log', 'items']);
    // in a template "if" is the Django template one: still there
    assert.deepEqual(serverItems([items[0]], allTemplates(SERVER, conf), 'django-html').length, 1);
    conf = edited(empty, find(all, 'Python/log'), { body: 'log = logging.getLogger($name$)', description: 'a logger' });
    const one = serverItems(items, allTemplates(SERVER, conf), 'python')[1];
    assert.deepEqual([one.insertText, one.detail, one.additionalTextEdits.length], ['log = logging.getLogger(${1:name})', 'a logger', 1]);
    // another abbreviation: the editor offers it, the server's item is left out
    conf = edited(empty, find(all, 'Python/log'), { abbr: 'logger' });
    const moved = allTemplates(SERVER, conf);
    assert.equal(servedByServer(find(moved, 'Python/log')), false);
    assert.deepEqual(serverItems(items, moved, 'python').map((i) => i.label), ['if', 'par', 'items']);
    assert.ok(offered(moved, 'python', '').some((t) => t.abbr === 'logger'));
});

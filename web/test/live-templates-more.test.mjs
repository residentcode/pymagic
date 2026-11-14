// Live templates (web/src/live-templates-model.js): the variables of a template's places, templates sharing an abbreviation, a template
// moved to another group, and the project's own templates for any language.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readSnippets } from '../src/codegen-model.js';
import {
    toSnippet, preview, placesOf, cleanConf, allTemplates, edited, added, matchesAt, matchAt, offered, projectTemplates, servedByServer, serverItems, groupAdded, groupsOf,
} from '../src/live-templates-model.js';

const empty = cleanConf(undefined);
const find = (list, id) => list.find((t) => t.id === id);

test('a place\'s variable: a default, a value typed over or just written, a place following another', () => {
    const body = '// $file$, $day$\nclass $Name$:\n    $field$ = $FIELD$  # $name$';
    assert.deepEqual(placesOf(body), ['file', 'day', 'Name', 'field', 'FIELD', 'name']);
    const vars = {
        file: { expr: 'fileName()', def: '', skip: true },
        day: { expr: 'date()', def: '', skip: false },
        Name: { expr: '', def: 'Shop', skip: false },
        FIELD: { expr: 'upperCase(field)', def: '', skip: false },
        name: { expr: 'camelCase(Name)', def: '', skip: false },
    };
    assert.equal(toSnippet(body, vars),
        '// ${TM_FILENAME}, ${1:${CURRENT_YEAR}-${CURRENT_MONTH}-${CURRENT_DATE}}\nclass ${2:Shop}:\n\t${3:field} = ${3/(.*)/${1:/upcase}/}  # ${2/(.*)/${1:/camelcase}/}');
    assert.equal(preview(body, vars), '// views.py, 2026-01-31\nclass Shop:\n    field = FIELD  # shop');
    // nothing said: as before
    assert.equal(toSnippet(body, {}), toSnippet(body));
    // an expression that is none, a place following itself, one following a place not in the text (it is given a place): no harm
    assert.equal(toSnippet('$a$ $b$', { a: { expr: 'nothing()', def: 'x', skip: true }, b: { expr: 'capitalize(b)', def: '', skip: false } }), '${1:x} ${2:b}');
    assert.equal(toSnippet('$a$', { a: { expr: 'lowerCase(other)', def: '', skip: false } }), '${1/(.*)/${1:/downcase}/}');
    assert.equal(preview('$a$ $b$', { a: { expr: 'capitalize(b)' }, b: { expr: 'capitalize(a)' } }), 'A B');
});

test('variables and reformat are kept with a template, built in or the person\'s own', () => {
    const all = allTemplates(null, empty);
    let conf = edited(empty, find(all, 'Django/model'), { vars: { Name: { expr: 'capitalize(field)', def: '', skip: false } }, reformat: true });
    assert.deepEqual(conf.changed['Django/model'], { vars: { Name: { expr: 'capitalize(field)', def: '', skip: false } }, reformat: true });
    assert.deepEqual(cleanConf(JSON.parse(JSON.stringify(conf))), conf);
    const now = find(allTemplates(null, conf), 'Django/model');
    assert.match(toSnippet(now.body, now.vars), /^class \$\{1\/\(\.\*\)\/\$\{1:\/capitalize\}\/\}\(models\.Model\):\n\t\$\{1:field\} = /);
    // put back: no change left
    conf = edited(conf, now, { vars: {}, reformat: false });
    assert.deepEqual(conf.changed, {});
    // a broken file: what is not a variable is dropped
    assert.deepEqual(cleanConf({ changed: { 'Django/model': { vars: { 'a b': { expr: 'x' }, ok: { expr: ' date() ', def: 3 }, none: { expr: '', def: '' } } } } }).changed,
        { 'Django/model': { vars: { ok: { expr: 'date()', def: '', skip: false } } } });
});

test('templates sharing an abbreviation are all found; one of the person\'s own goes to another group', () => {
    let [conf, id] = added(empty, 'user');
    conf = edited(conf, find(allTemplates(null, conf), id), { abbr: 'fori', body: 'for (;;) {}', contexts: ['javascript'] });
    const all = allTemplates(null, conf);
    assert.deepEqual(matchesAt(all, conf, 'javascript', 'fori', 'tab').map((t) => t.id), ['JavaScript/fori', id]);
    assert.equal(matchAt(all, conf, 'javascript', 'fori', 'tab').id, 'JavaScript/fori');
    assert.deepEqual(matchesAt(all, conf, 'css', 'fori', 'tab'), []);
    // a longer abbreviation alone
    assert.deepEqual(matchesAt(all, conf, 'sql', 'selw', 'tab').map((t) => t.id), ['SQL/selw']);
    let name;
    [conf, name] = groupAdded(conf, groupsOf(all, conf).map((g) => g.name), 'loops');
    conf = edited(conf, find(allTemplates(null, conf), id), { group: name });
    assert.deepEqual(groupsOf(allTemplates(null, conf), conf).filter((g) => g.own).map((g) => [g.name, g.templates.length]), [['loops', 1], ['user', 0]]);
});

test('the project\'s templates: each for the languages its scope names', () => {
    const snippets = readSnippets(JSON.stringify({
        header: { prefix: 'hdr', body: ['# ${1:title}'], description: 'a header' },
        card: { prefix: 'card', body: '<div class="card">$0</div>', scope: 'django-html' },
        query: { prefix: 'q', body: 'SELECT ${1:*} FROM ${2:t};', scope: 'sql' },
        both: { prefix: 'lg', body: 'log($1)', scope: 'python,javascript,typescript' },
    }));
    const project = projectTemplates(snippets);
    const all = allTemplates(null, empty, project);
    const of = (language) => all.filter((t) => t.project && t.languages.includes(language)).map((t) => `${t.abbr}${t.server ? ' (server)' : ''}`);
    assert.deepEqual(of('python'), ['hdr (server)', 'lg (server)']);
    assert.deepEqual(of('django-html'), ['card (server)']);
    assert.deepEqual(of('sql'), ['q']);
    assert.deepEqual(of('javascript'), ['lg']);
    assert.deepEqual(of('typescript'), ['lg']);
    // the editor's completion offers its own ones, the key finds all
    assert.deepEqual(offered(all, 'sql', '').filter((t) => t.project).map((t) => t.abbr), ['q']);
    assert.deepEqual(offered(all, 'python', '').filter((t) => t.project), []);
    assert.equal(matchAt(all, empty, 'sql', 'q', 'tab').body, 'SELECT ${1:*} FROM ${2:t};');
    assert.equal(servedByServer(matchAt(all, empty, 'python', 'hdr', 'tab')), true);
    assert.equal(matchAt(all, empty, 'javascript', 'x = lg', 'tab').server, undefined);
    assert.equal(matchAt(all, empty, 'python', 'x = hdr', 'tab'), null);                // the server's Python ones start a statement
    // the server's own items of the project pass as they are
    const items = [{ label: 'hdr', isSnippet: true, sortText: '3hdr', insertText: '# ${1:title}' }];
    assert.deepEqual(serverItems(items, all, 'python'), items);
});

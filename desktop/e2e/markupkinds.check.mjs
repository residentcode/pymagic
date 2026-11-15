// Handlebars, EJS and Astro in the built PyMagic.exe: a file as it should be has no mark; an element left open is told while typing, and a
// line not where its block asks gets the indentation's weak warning. Then a live template's variables: a place written as snake_case of
// another, one cut by a regular expression, and complete() offering completion at its place.
// Run: node desktop/e2e/markupkinds.check.mjs
import assert from 'node:assert/strict';
import { launch, project, sleep } from './app.mjs';

const files = {
    'views/list.hbs': '<ul>\n  {{#each items}}\n    <li>{{this.name}}</li>\n  {{else}}\n    <li>none</li>\n  {{/each}}\n</ul>\n',
    'views/page.ejs': '<ul>\n  <% items.forEach((item) => { %>\n    <li><%= item.name %></li>\n  <% }) %>\n</ul>\n<% if (user) { %>\n  <p>Hello</p>\n<% } else { %>\n  <p>Guest</p>\n<% } %>\n',
    'src/pages/index.astro': '---\nconst items: Array<string> = ["a", "b"];\nconst less = 1 < 2;\n---\n<main>\n  <ul class={items.length > 1 ? "many" : "one"}>\n    {items.map((item) => (\n      <li>{item}</li>\n    ))}\n  </ul>\n  <p>text</p>\n</main>\n',
    'main.py': 'import os\n',
};
const settings = {
    templates: {
        own: [
            { id: 'own/1', group: 'user', abbr: 'cst', description: 'a constant of a name', body: '$name$ = "$NAME$"  # $short$', contexts: ['python'],
                vars: { NAME: { expr: 'capitalizeAndUnderscore(name)' }, short: { expr: 'regularExpression(name, "[aeiou]", "")' } } },
            { id: 'own/2', group: 'user', abbr: 'imp', description: 'an import, its module completed', body: 'import $module$', contexts: ['python'], vars: { module: { expr: 'complete()' } } },
        ],
    },
};
const app = await launch(project(files), settings);
const { page } = app;
const open = async (f) => {
    await page.evaluate(`window.pymagic.openFile('${f}')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('${f.split('/').pop()}')`, 20000, f);
    await sleep(300);
};
const marks = (owner) => page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ resource: window.pymagic.editor.getModel().uri, owner: '${owner}' }).map((m) => m.startLineNumber + ':' + m.startColumn + ' ' + m.message).sort()`);
const settle = async (owner, test, tries = 30) => {
    let last = null;
    for (let i = 0; i < tries; i++) {
        const now = JSON.stringify(await marks(owner));
        if (now === last && test(JSON.parse(now))) return JSON.parse(now);
        last = now;
        await sleep(500);
    }
    assert.fail(`${owner}: the marks stayed ${last}`);
};
const edit = (range, text) => page.evaluate(`(() => { const m = window.pymagic.editor.getModel(); m.pushEditOperations([], [{ range: new window.pymagic.monaco.Range(${range.join(', ')}), text: ${JSON.stringify(text)} }], () => null); })()`);
const language = () => page.evaluate(`window.pymagic.editor.getModel().getLanguageId()`);

try {
    // Handlebars
    await open('views/list.hbs');
    await page.waitFor(`window.pymagic.editor.getModel().getLanguageId() === 'handlebars'`, 10000, 'Handlebars as the language');
    assert.deepEqual(await settle('syntax', (f) => f.length === 0), []);
    assert.deepEqual(await settle('markup-indent', (f) => f.length === 0), []);
    await edit([3, 9, 3, 9], '<b>');
    const hbs = await settle('syntax', (f) => f.length === 1);
    console.log('Handlebars:', hbs);
    assert.match(hbs[0], /^3:9 Element <b> is not closed: add <\/b>/);
    await edit([3, 9, 3, 12], '');
    await edit([4, 1, 4, 3], '      ');
    const hbsIndent = await settle('markup-indent', (f) => f.length === 1);
    console.log('Handlebars indentation:', hbsIndent);
    assert.match(hbsIndent[0], /^4:1 /);

    // EJS
    await open('views/page.ejs');
    await page.waitFor(`window.pymagic.editor.getModel().getLanguageId() === 'ejs'`, 10000, 'EJS as the language');
    assert.deepEqual(await settle('syntax', (f) => f.length === 0), []);
    assert.deepEqual(await settle('markup-indent', (f) => f.length === 0), []);
    await edit([3, 9, 3, 9], '<b>');
    const ejs = await settle('syntax', (f) => f.length === 1);
    console.log('EJS:', ejs);
    assert.match(ejs[0], /^3:9 Element <b> is not closed/);
    await edit([3, 9, 3, 12], '');
    await edit([8, 1, 8, 1], '    ');
    const ejsIndent = await settle('markup-indent', (f) => f.length === 1);
    console.log('EJS indentation:', ejsIndent);
    assert.match(ejsIndent[0], /^8:1 /);

    // Astro
    await open('src/pages/index.astro');
    assert.deepEqual(await settle('syntax', (f) => f.length === 0), []);
    assert.deepEqual(await settle('markup-indent', (f) => f.length === 0), []);
    await edit([8, 11, 8, 11], '<em>');
    const astro = await settle('syntax', (f) => f.length === 1);
    console.log('Astro:', astro);
    assert.match(astro[0], /^8:11 Element <em> is not closed/);
    await edit([8, 11, 8, 15], '');
    await edit([11, 1, 11, 3], '       ');
    const astroIndent = await settle('markup-indent', (f) => f.length === 1);
    console.log('Astro indentation:', astroIndent);
    assert.match(astroIndent[0], /^11:1 /);
    // Alt+Enter's fix puts the line back
    const fixed = await page.evaluate(`window.pymagic.mcp.call('apply_quick_fix', { filePath: 'src/pages/index.astro', line: 11, title: 'Fix indentation' })`);
    console.log(fixed.text);
    assert.equal(await page.evaluate(`window.pymagic.editor.getModel().getLineContent(11)`), '  <p>text</p>');

    // a live template's variables
    await open('main.py');
    const value = () => page.evaluate(`window.pymagic.editor.getModel().getValue(1)`);
    const type = async (text) => { for (const ch of text) { await page.evaluate(`window.pymagic.editor.trigger('keyboard', 'type', { text: ${JSON.stringify(ch)} })`); await sleep(80); } };
    const start = async (abbr) => {
        await page.evaluate(`(() => { const ed = window.pymagic.editor, m = ed.getModel(); m.setValue(''); ed.setPosition({ lineNumber: 1, column: 1 }); ed.focus(); })()`);
        await type(abbr);
        await sleep(400);
        await page.evaluate(`window.pymagic.editor.trigger('keyboard', 'hideSuggestWidget', {})`);
        await sleep(150);
        await page.key(9, 'Tab');
        await sleep(600);
    };
    await start('cst');
    await type('maxSize');
    await page.evaluate(`window.pymagic.editor.trigger('keyboard', 'hideSuggestWidget', {})`);
    await page.key(9, 'Tab');
    await sleep(500);
    console.log('cst:', await value());
    assert.equal(await value(), 'maxSize = "MAX_SIZE"  # mxSz');
    await page.key(27, 'Escape');
    await start('imp');
    assert.equal(await value(), 'import ');
    const offered = await page.waitFor(`(() => { const rows = [...document.querySelectorAll('.suggest-widget.visible .monaco-list-row .label-name')].map((e) => e.textContent); return rows.length > 3 ? rows : null; })()`, 15000, 'completion at the place of complete()');
    console.log('imp, offered at once:', offered.slice(0, 5));
    assert.ok(offered.some((n) => /^(os|sys|json|abc)$/.test(n)), offered.join(' '));
    console.log('OK');
} finally {
    await app.close();
}

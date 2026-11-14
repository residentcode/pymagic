// Live templates in the built PyMagic.exe (Settings > Templates): an abbreviation typed key by key and Tab write the template in JavaScript,
// TypeScript, Python (the language server's own, with its import, and Flask's), a Django template, HTML, CSS, SQL, a shell script, an HTTP
// request file, PHP and YAML; not inside a string; taken from the completion list too. The page: the groups with their boxes, the key that
// expands changed to Space, a template switched off, one edited and restored, one of the person's own added for CSS, and a group switched off.
// Run: node desktop/e2e/livetemplates.check.mjs
import assert from 'node:assert/strict';
import { launch, project, sleep } from './app.mjs';

const app = await launch(project({
    'main.py': 'x = 1\n', 'manage.py': 'import django\n', 'app.js': '\n', 'types.ts': '\n', 'page.html': '\n', 'style.css': '\n', 'query.sql': '\n',
    'run.sh': '\n', 'api.http': '\n', 'index.php': '<?php\n', 'api.yaml': '\n', 'shop/templates/shop/list.html': '{% extends "base.html" %}\n',
}));
const { page } = app;
const value = () => page.evaluate(`window.pymagic.editor.getModel().getValue(1)`);
async function open(f) {
    await page.evaluate(`window.pymagic.openFile('${f}')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('${f.split('/').pop()}')`, 20000, f);
    await sleep(400);
}
/** The file's text set, the cursor at its end, then the text typed key by key. */
async function type(start, text, { list = false } = {}) {
    await page.evaluate(`(() => { const ed = window.pymagic.editor, m = ed.getModel(); m.setValue(${JSON.stringify(start)}); const n = m.getLineCount(); ed.setPosition({ lineNumber: n, column: m.getLineMaxColumn(n) }); ed.focus(); })()`);
    for (const ch of text) { await page.evaluate(`window.pymagic.editor.trigger('keyboard', 'type', { text: ${JSON.stringify(ch)} })`); await sleep(90); }
    await sleep(500);
    if (!list) { await page.evaluate(`window.pymagic.editor.trigger('keyboard', 'hideSuggestWidget', {})`); await sleep(150); }
}
const KEYS = { tab: [9, 'Tab'], space: [32, ' '], enter: [13, 'Enter'] };
async function press(key) {
    const [code, name] = KEYS[key];
    if (key === 'space') {
        // a key that types: its character comes with it
        await page.send('Input.dispatchKeyEvent', { type: 'keyDown', windowsVirtualKeyCode: code, key: name, code: 'Space', text: ' ' });
        await page.send('Input.dispatchKeyEvent', { type: 'keyUp', windowsVirtualKeyCode: code, key: name, code: 'Space' });
    }
    else await page.key(code, name);
    await sleep(700);
}
/** Types an abbreviation in a file, presses the key and gives the text. */
async function expand(start, abbr, key = 'tab', opts) {
    await type(start, abbr, opts);
    await press(key);
    return value();
}
const settingsPage = async () => {
    await page.evaluate(`window.pymagic.runCommand('pymagic.window.settings')`);
    await page.waitFor(`!!document.querySelector('.settings-nav')`, 10000, 'the settings');
    await page.evaluate(`[...document.querySelectorAll('.settings-nav button')].find((b) => b.textContent === 'Templates').click()`);
    await page.waitFor(`document.querySelectorAll('#lt-tree .lt-group').length > 15 && [...document.querySelectorAll('#lt-tree .lt-group')].some((g) => g.dataset.group === 'Python')`, 15000, 'the groups');
};
const closeSettings = async () => {
    await page.evaluate(`[...document.querySelectorAll('button')].find((b) => b.textContent === 'Done').click()`);
    await sleep(500);
};
const js = (code) => page.evaluate(`(() => { ${code} })()`);
const selectTemplate = (group, id) => js(`
    const g = [...document.querySelectorAll('#lt-tree .lt-group')].find((x) => x.dataset.group === ${JSON.stringify(group)});
    if (!document.querySelector('#lt-tree .lt-row[data-id=' + JSON.stringify(${JSON.stringify(id)}) + ']')) g.querySelector('.lt-twist').click();
    document.querySelector('#lt-tree .lt-row[data-id=' + JSON.stringify(${JSON.stringify(id)}) + ']').click();`);
const setField = (id, text) => js(`const f = document.getElementById(${JSON.stringify(id)}); f.value = ${JSON.stringify(text)}; f.dispatchEvent(new Event('input', { bubbles: true })); f.dispatchEvent(new Event('change', { bubbles: true }));`);

try {
    // --- the key, in every kind of file
    await open('app.js');
    assert.equal(await expand('', 'fori'), 'for (let i = 0; i < array.length; i++) {\n    \n}');
    assert.equal(await expand('', 'describe'), "describe('subject', () => {\n    \n});");
    assert.equal(await expand('', 'exget'), "app.get('/path', (req, res) => {\n    \n});");
    // inside a string the key is the key
    assert.match(await expand('', 'const s = "fori'), /^const s = "fori +"$/);
    // from the completion list (it is open: Tab takes its first item, the template named as typed)
    assert.equal(await expand('', 'forof', 'tab', { list: true }), 'for (const item of items) {\n    \n}');
    console.log('JavaScript: fori, describe, exget, not in a string, from the list');

    await open('types.ts');
    assert.equal(await expand('', 'int'), 'interface Name {\n    \n}');
    assert.equal(await expand('', 'arf'), 'const name = (params) => {\n    \n};');

    await open('main.py');
    assert.equal(await expand('', 'if'), 'if condition:\n    pass');                       // the language server's
    assert.equal(await expand('', 'log'), 'import logging\n\nlogger = logging.getLogger(__name__)');
    assert.equal(await expand('', 'route'), "@app.route('/path')\ndef name():\n    return ");
    assert.doesNotMatch(await expand('', 'x = route'), /@app/);                                 // a statement's template, not in an expression
    assert.equal(await expand('', 'model'), 'class Name(models.Model):\n    field = models.CharField(max_length=length)\n\n    def __str__(self):\n        return self.field');
    console.log('Python: if (the server\'s), log with its import, Flask\'s route, Django\'s model');

    await open('shop/templates/shop/list.html');
    assert.equal(await expand('', 'block'), '{% block content %}\n    \n{% endblock %}');
    assert.equal(await expand('', 'spaceless'), '{% spaceless %}\n    \n{% endspaceless %}');
    assert.equal(await expand('', 'ahref'), '<a href="href">text</a>');

    await open('page.html');
    assert.match(await expand('', 'html5'), /^<!DOCTYPE html>\n<html lang="lang">\n<head>\n    <meta charset="UTF-8">/);
    assert.doesNotMatch(await expand('', '<div cmt'), /<!--/);                               // inside a tag
    await open('style.css');
    assert.equal(await expand('', 'a {\nflexc'), 'a {\n    display: flex;\n    align-items: center;\n    justify-content: center;\n}');
    await open('query.sql');
    assert.equal(await expand('', 'selw'), 'SELECT columns\nFROM table\nWHERE condition;');
    await open('run.sh');
    assert.equal(await expand('', 'for'), 'for item in items; do\n    \ndone');
    assert.equal(await expand('', 'readl'), 'while IFS= read -r line; do\n    \ndone < "$file"');
    await open('api.http');
    assert.equal(await expand('', 'ptr'), 'POST http://host/path\nContent-Type: application/json\n\n{\n    \n}\n\n###');
    await open('index.php');
    assert.equal(await expand('<?php\n', 'fore'), '<?php\nforeach ($items as $item) {\n    \n}');
    assert.equal(await expand('<?php\n', 'valid'), "<?php\n$validated = $request->validate([\n    'field' => 'required|rule',\n]);");
    await open('api.yaml');
    assert.match(await expand('', 'oapi'), /^openapi: 3\.1\.0\ninfo:\n {2,4}title: title\n/);
    console.log('templates, HTML, CSS, SQL, shell, HTTP, PHP, YAML: written');

    // --- the page
    await settingsPage();
    const groups = await js(`return [...document.querySelectorAll('#lt-tree .lt-group')].map((g) => g.dataset.group + ' ' + g.querySelector('.lt-count').textContent);`);
    console.log('groups:', groups.join(', '));
    for (const g of ['Django', 'Django Templates', 'Flask', 'HTML/XML', 'HTTP Request', 'JavaScript', 'JavaScript Testing', 'Python', 'Python postfix', 'Shell Script', 'SQL'])
        assert.ok(groups.some((x) => x.startsWith(g + ' ')), g);
    // the key: Space
    await js(`const s = document.getElementById('lt-expand'); s.value = 'space'; s.dispatchEvent(new Event('change'));`);
    // fori switched off
    await selectTemplate('JavaScript', 'JavaScript/fori');
    assert.equal(await js(`return document.getElementById('lt-abbr').value + '|' + document.getElementById('lt-where').textContent + '|' + document.getElementById('lt-key').selectedOptions[0].textContent;`),
        'fori|Applicable in JavaScript and TypeScript.|Default (Space)');
    await js(`document.querySelector('#lt-tree .lt-row.sel input').click();`);
    // log edited
    await selectTemplate('JavaScript', 'JavaScript/log');
    await setField('lt-body', "console.debug('$what$', $END$);");
    assert.equal(await js(`return document.getElementById('lt-preview').textContent + '|' + document.querySelector('#lt-tree .lt-row.sel').classList.contains('changed') + '|' + document.getElementById('lt-restore').disabled;`),
        "console.debug('what', );|true|false");
    // one of the person's own, for CSS, with Tab as its key
    await js(`document.getElementById('lt-add').click();`);
    await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((x) => x.textContent.trim() === 'Live Template')`, 5000, 'the Add menu');
    await js(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((x) => x.textContent.trim() === 'Live Template').click();`);
    await page.waitFor(`document.getElementById('lt-abbr')?.value === '' && document.getElementById('lt-where')?.textContent === 'No applicable contexts yet.'`, 5000, 'the new template');
    await setField('lt-abbr', 'card');
    await setField('lt-description', 'a card');
    await setField('lt-body', 'border: 1px solid $color$;\nborder-radius: $radius$;');
    await js(`document.getElementById('lt-contexts').click(); const c = document.querySelector('.lt-contexts input[value=css]'); c.click();`);
    await js(`const k = document.getElementById('lt-key'); k.value = 'tab'; k.dispatchEvent(new Event('change'));`);
    assert.equal(await js(`return document.getElementById('lt-where').textContent + '|' + [...document.querySelectorAll('#lt-tree .lt-group')].find((g) => g.dataset.group === 'user').querySelector('.lt-count').textContent;`),
        'Applicable in CSS, SCSS and Less.|1');
    // Flask switched off as a group
    await js(`[...document.querySelectorAll('#lt-tree .lt-group')].find((g) => g.dataset.group === 'Flask').querySelector('input').click();`);
    await closeSettings();

    await open('app.js');
    assert.doesNotMatch(await expand('', 'fori', 'tab'), /let i = 0/);                            // Tab is no longer the key
    assert.equal(await expand('', 'fori', 'space'), 'fori ');                               // and fori is switched off
    assert.equal(await expand('', 'forof', 'space'), 'for (const item of items) {\n    \n}');
    assert.equal(await expand('', 'log', 'space'), "console.debug('what', );");
    await open('style.css');
    assert.equal(await expand('', 'a {\ncard', 'tab'), 'a {\n    border: 1px solid color;\n    border-radius: radius;\n}');
    await open('main.py');
    assert.equal(await expand('', 'route', 'space'), 'route ');                             // Flask's group is off
    assert.equal(await expand('', 'if', 'space'), 'if condition:\n    pass');              // the server's, by the new key
    console.log('the page: Space as the key, a template off, one edited, one added, a group off');

    // --- restored
    await settingsPage();
    assert.equal(await js(`return document.getElementById('lt-expand').value;`), 'space');   // kept
    await selectTemplate('JavaScript', 'JavaScript/log');
    await js(`document.getElementById('lt-restore').click();`);
    assert.equal(await js(`return document.getElementById('lt-body').value;`), 'console.log($END$);');
    await js(`[...document.querySelectorAll('#lt-tree .lt-group')].find((g) => g.dataset.group === 'JavaScript').click(); document.getElementById('lt-restore').click();`);
    await js(`[...document.querySelectorAll('#lt-tree .lt-group')].find((g) => g.dataset.group === 'user').click(); document.getElementById('lt-remove').click();`);
    assert.equal(await js(`return [...document.querySelectorAll('#lt-tree .lt-group')].some((g) => g.dataset.group === 'user');`), false);
    await js(`const s = document.getElementById('lt-expand'); s.value = 'tab'; s.dispatchEvent(new Event('change'));`);
    await closeSettings();
    await open('app.js');
    assert.equal(await expand('', 'fori'), 'for (let i = 0; i < array.length; i++) {\n    \n}');
    assert.equal(await expand('', 'log'), 'console.log();');
    console.log('OK');
} finally { await app.close(); }

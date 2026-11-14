// Syntax errors of HTML, templates and YAML while typing in the built PyMagic.exe: an HTML file's element never closed, a closing tag that
// matches nothing and a tag never ended, gone when fixed; a template whose {% if %} branches each open a <div> without a false finding, and a
// {{ name } never closed (Django's) marked; a YAML file's wrong indentation and duplicate key, and a Helm template left alone.
// Run: node desktop/e2e/syntax.check.mjs
import assert from 'node:assert/strict';
import { launch, project, sleep } from './app.mjs';

const app = await launch(project({
    'main.py': 'x = 1\n', 'manage.py': 'import django\n',
    'page.html': '<!DOCTYPE html>\n<html>\n<body>\n<p>fine</p>\n</body>\n</html>\n',
    'shop/templates/shop/list.html': '{% extends "base.html" %}\n{% block content %}\n{% if wide %}<div class="wide">{% else %}<div>{% endif %}\n<p>{{ count }}</p>\n</div>\n{% endblock %}\n',
    'conf.yaml': 'name: shop\nlist:\n  - a\n',
    'chart/templates/deploy.yaml': 'kind: Deployment\n{{- if .Values.on }}\nspec: {}\n{{- end }}\n',
}));
const { page } = app;
const errors = (owner) => page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ ${owner ? `owner: '${owner}', ` : ''}resource: window.pymagic.editor.getModel().uri }).filter((m) => m.severity === 8).map((m) => m.startLineNumber + ':' + m.startColumn + ' ' + m.message)`);
async function settle(owner, want, ms = 15000) {
    const end = Date.now() + ms;
    let found = [];
    while (Date.now() < end) { found = await errors(owner); if (want(found)) return found; await sleep(250); }
    assert.fail(`the findings stayed ${JSON.stringify(found)}`);
}
async function open(f) {
    await page.evaluate(`window.pymagic.openFile('${f}')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('${f.split('/').pop()}')`, 20000, f);
}
async function typeAt(line, column, text) {
    await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: ${line}, column: ${column} }); ed.focus(); })()`);
    for (const ch of text) { await page.evaluate(`window.pymagic.editor.trigger('keyboard', 'hideSuggestWidget', {}); window.pymagic.editor.trigger('keyboard', 'type', { text: ${JSON.stringify(ch)} })`); await sleep(20); }
}
try {
    // HTML
    await open('page.html');
    assert.deepEqual(await settle('syntax', (f) => f.length === 0), []);
    await page.evaluate(`(() => { const m = window.pymagic.editor.getModel(); m.pushEditOperations([], [{ range: new window.pymagic.monaco.Range(4, 1, 4, 12), text: '<div>\\n<p>hi</div>\\n<section>' }], () => null); })()`);
    // (the <p> ends by itself at </div>, as HTML has it: only the <section> is left open)
    const html = await settle('syntax', (f) => f.length === 1);
    console.log('HTML:', html);
    assert.deepEqual(html, ['6:1 Element <section> is not closed: add </section> before </body>']);
    await page.evaluate(`window.pymagic.editor.getModel().setValue('<div>\\n<p>hi</p></span>\\n<img src="a.png"\\n</div>\\n')`);
    const more = await settle('syntax', (f) => f.length === 2);
    console.log('HTML 2:', more);
    assert.ok(more.includes('2:10 Closing tag </span> matches nothing') && more.includes('3:1 Tag <img> is not ended: add >'), more.join(' | '));
    await page.evaluate(`window.pymagic.editor.getModel().setValue('<div>\\n<p>hi</p>\\n')`);
    await typeAt(3, 1, '</div>');
    assert.deepEqual(await settle('syntax', (f) => f.length === 0), []);

    // a template with branches: no finding of its divs; then a {{ never closed, Django's own finding
    await open('shop/templates/shop/list.html');
    await sleep(3000);
    assert.deepEqual(await settle(null, (f) => f.length === 0), []);
    await page.evaluate(`(() => { const m = window.pymagic.editor.getModel(); m.pushEditOperations([], [{ range: new window.pymagic.monaco.Range(4, 13, 4, 15), text: '}' }], () => null); })()`);
    const tpl = await settle(null, (f) => f.some((m) => m.includes('Unclosed variable')));
    console.log('template:', tpl);
    assert.ok(tpl.includes("4:4 Unclosed variable '{{': add }} on the same line"), tpl.join(' | '));

    // YAML
    await open('conf.yaml');
    assert.deepEqual(await settle('syntax', (f) => f.length === 0), []);
    await typeAt(4, 1, 'name: again\n');
    const dup = await settle('syntax', (f) => f.length > 0);
    console.log('YAML:', dup);
    assert.ok(dup.some((m) => /^4:1 Map keys must be unique/.test(m)), dup.join(' | '));
    await page.evaluate(`window.pymagic.editor.getModel().setValue('a: 1\\n  b: 2\\n')`);
    const nested = await settle('syntax', (f) => f.some((m) => /Nested mappings/.test(m)));
    console.log('YAML 2:', nested);
    assert.ok(nested.some((m) => /Nested mappings are not allowed/.test(m)), nested.join(' | '));
    await open('chart/templates/deploy.yaml');
    await sleep(1500);
    assert.deepEqual(await errors('syntax'), []);
    console.log('OK');
} finally { await app.close(); }

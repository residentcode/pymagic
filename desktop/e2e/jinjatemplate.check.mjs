// A Jinja2 template's breakpoint in a Flask app, in the built PyMagic.exe: a request (Flask's test client) renders templates/page.html,
// which extends base.html; the breakpoint in its block stops there at the template's line, with the template open and the line marked, the
// frames under it at their templates' lines, the template's names in Variables (Flask's request among them, Jinja's own l_0_ names not), and
// a breakpoint in a for loop stopping once for each item. Resume, and the page is answered.
// Run: node desktop/e2e/jinjatemplate.check.mjs   (needs uv with Flask in its cache: installed --offline into the project's .venv)
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const root = project({
    'templates/base.html': '<main>\n{% block content %}{% endblock %}\n</main>\n',
    'templates/page.html': "{% extends 'base.html' %}\n{% block content %}\n<h1>{{ title }}</h1>\n{% for item in items %}\n  <li>{{ item.name }}</li>\n{% endfor %}\n{% endblock %}\n",
    'app.py': [
        'from flask import Flask, render_template',
        '',
        'app = Flask(__name__)',
        '',
        '',
        "@app.route('/')",
        'def home():',
        "    return render_template('page.html', title='Shop', items=[{'name': 'pen'}, {'name': 'book'}])",
        '',
        '',
        "print(app.test_client().get('/').get_data(as_text=True))",
        '',
    ].join('\n'),
});
for (const step of [['venv', '-q', '.venv'], ['pip', 'install', '-q', '--offline', '-p', join(root, '.venv', 'Scripts', 'python.exe'), 'flask']])
    assert.equal(spawnSync('uv', step, { cwd: root, encoding: 'utf8' }).status, 0, 'uv ' + step.join(' '));
const app = await launch(root);
const { page } = app;
const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;
const frames = () => page.evaluate(`[...document.querySelectorAll('#frames div.frame')].map((d) => d.textContent.trim().replace(/\\s+/g, ' '))`);
const variables = () => page.evaluate(`[...document.querySelectorAll('#vars li > div')].map((d) => (d.querySelector('.name')?.textContent ?? '') + '=' + (d.querySelector('.val')?.textContent ?? ''))`);
async function margin(line) {
    const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; const box = ed.getContainerDomNode().getBoundingClientRect();
        return { x: box.left + ed.getLayoutInfo().glyphMarginLeft + 5, y: box.top + ed.getTopForLineNumber(${line}) - ed.getScrollTop() + 8 }; })()`);
    await page.click(at.x, at.y);
    await sleep(400);
}
async function stopAt(what) {
    return page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 120000, what);
}
try {
    await page.evaluate(`window.pymagic.openFile('templates/page.html')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('page.html')`, 20000, 'page.html');
    await margin(3);
    await margin(5);
    await page.evaluate(`window.pymagic.openFile('app.py')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('app.py')`, 20000, 'app.py');
    await sleep(500);
    await page.evaluate(`${$('b-debug')}.click()`);
    const stop = await stopAt('the stop in the template');
    console.log('stopped:', stop);
    assert.match(stop, /page\.html:3/);
    const marked = await page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); if (!m.uri.path.endsWith('page.html')) return null;
        const d = m.getAllDecorations().find((x) => x.options.className === 'debug-line'); return d ? d.range.startLineNumber : null; })()`, 20000, 'the line marked');
    assert.equal(marked, 3);
    await page.evaluate(`${$('t-debug')}.click()`);
    await sleep(800);
    const stack = await frames();
    console.log('frames:', stack.slice(0, 6));
    assert.ok(stack.some((f) => /base\.html:2/.test(f)), 'base.html at its line');
    assert.ok(stack.some((f) => /home .*app\.py:8/.test(f)), 'the view under it');
    let names = await variables();
    console.log('variables:', names.slice(0, 12));
    assert.ok(names.includes("title='Shop'"), 'title');
    assert.ok(names.some((n) => n.startsWith('request=')), "Flask's request");
    assert.ok(!names.some((n) => /^l_\d|^range=|^_pm_/.test(n)), names.join(' '));
    for (const want of ["{'name': 'pen'}", "{'name': 'book'}"]) {
        await page.evaluate(`${$('b-debug')}.click()`);
        await sleep(500);
        assert.match(await stopAt('the loop'), /page\.html:5/);
        await sleep(600);
        names = await variables();
        assert.ok(names.includes(`item=${want}`), names.join(' '));
        console.log('in the loop:', names.find((n) => n.startsWith('item=')));
    }
    await page.evaluate(`${$('b-debug')}.click()`);
    await page.waitFor(`/<li>book<\\/li>/.test(${$('output')}.textContent)`, 30000, 'the page answered after Resume');
    console.log('OK');
} finally { await app.close(); }

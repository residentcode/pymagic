// A Django template's breakpoint in the built PyMagic.exe: the render stops in templates/page.html at its line, the template is open with
// the line marked (amber band, scrollbar mark), the window asked to come up, the context's names in Variables, and Copy of a row's selection.
// Run: node desktop/e2e/djangotemplate.check.mjs   (needs uv with Django in its cache: installed --offline into the project's .venv)
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const root = project({
    'templates/page.html': '<h1>{{ title }}</h1>\n<ul>\n{% for item in items %}\n  <li>{{ item.name }}</li>\n{% endfor %}\n</ul>\n',
    'app.py': [
        'import django',
        'from django.conf import settings',
        "settings.configure(DEBUG=True, TEMPLATES=[{'BACKEND': 'django.template.backends.django.DjangoTemplates', 'DIRS': ['templates']}])",
        'django.setup()',
        'from django.template.loader import render_to_string',
        "html = render_to_string('page.html', {'title': 'Shop', 'items': [{'name': 'pen'}, {'name': 'book'}]})",
        'print(html)',
        '',
    ].join('\n'),
});
for (const step of [['venv', '-q', '.venv'], ['pip', 'install', '-q', '--offline', '-p', join(root, '.venv', 'Scripts', 'python.exe'), 'django']])
    assert.equal(spawnSync('uv', step, { cwd: root, encoding: 'utf8' }).status, 0, 'uv ' + step.join(' '));
const app = await launch(root);
const { page } = app;
const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;
const mouse = (type, x, y, extra = {}) => page.send('Input.dispatchMouseEvent', { type, x, y, ...extra });
try {
    await page.evaluate(`window.pymagic.openFile('templates/page.html')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('page.html')`, 20000, 'page.html');
    const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; const box = ed.getContainerDomNode().getBoundingClientRect();
        return { x: box.left + ed.getLayoutInfo().glyphMarginLeft + 5, y: box.top + ed.getTopForLineNumber(4) - ed.getScrollTop() + 8 }; })()`);
    await page.click(at.x, at.y);
    await sleep(400);
    await page.evaluate(`window.pymagic.openFile('app.py')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('app.py')`, 20000, 'app.py');
    await sleep(500);
    await page.evaluate(`${$('b-debug')}.click()`);
    const stop = await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 120000, 'the stop in the template');
    console.log('stopped:', stop);
    assert.match(stop, /page\.html:4/);
    const marked = await page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); if (!m.uri.path.endsWith('page.html')) return null;
        const d = m.getAllDecorations().find((x) => x.options.className === 'debug-line'); const el = document.querySelector('.debug-line');
        return d && el ? { line: d.range.startLineNumber, ruler: d.options.overviewRuler?.color ?? null, bg: getComputedStyle(el).backgroundColor } : null; })()`, 20000, 'the stopped line marked');
    const focus = await page.evaluate('window.__pmLastStopFocus');
    console.log('marked:', JSON.stringify(marked), 'window:', JSON.stringify(focus));
    assert.equal(marked.line, 4);
    assert.equal(marked.ruler, '#ffc861');
    assert.equal(focus?.done, 'bring-up');
    await page.evaluate(`${$('t-debug')}.click()`);
    await sleep(800);
    const vars = await page.evaluate(`${$('vars')}.innerText`);
    console.log('variables:', JSON.stringify(vars.slice(0, 300)));
    // a part of a row selected, Copy from its menu
    const row = await page.evaluate(`(() => { const v = [...document.querySelectorAll('#vars li > div')].find((d) => d.querySelector('.name')?.textContent === 'title'); if (!v) return null;
        const r = v.querySelector('.val').getBoundingClientRect(); return { x: r.x, y: r.y + r.height / 2, w: r.width }; })()`);
    assert.ok(row, 'title in Variables');
    await mouse('mousePressed', row.x + 1, row.y, { button: 'left', clickCount: 1 });
    for (let i = 1; i <= 5; i++) await mouse('mouseMoved', row.x + 1 + (row.w - 2) * i / 5, row.y, { button: 'left', buttons: 1 });
    await mouse('mouseReleased', row.x + row.w - 1, row.y, { button: 'left', clickCount: 1 });
    await sleep(200);
    await mouse('mousePressed', row.x + 3, row.y, { button: 'right', clickCount: 1 });
    await mouse('mouseReleased', row.x + 3, row.y, { button: 'right', clickCount: 1 });
    await sleep(400);
    const copied = await page.evaluate(`(() => { const i = [...document.querySelectorAll('.ctx-menu .ctx-item')].find((e) => e.querySelector('.ctx-label')?.textContent.trim() === 'Copy'); if (!i) return false; i.click(); return true; })()`);
    assert.ok(copied, 'Copy in the row menu');
    await sleep(500);
    console.log('clipboard:', JSON.stringify(spawnSync('powershell', ['-NoProfile', '-Command', 'Get-Clipboard'], { encoding: 'utf8' }).stdout.trim()));
    await page.evaluate(`${$('b-debug')}.click()`);
    await page.waitFor(`/<li>pen<\\/li>/.test(${$('output')}.textContent)`, 30000, 'the page rendered after Resume');
    console.log('OK');
} finally { await app.close(); }

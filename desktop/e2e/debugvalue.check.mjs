// The debugger's value popup in the built PyMagic.exe, as PyCharm's: stopped at a breakpoint, the mouse on `request` (a real Django
// WSGIRequest, with cookies) opens a tree of its value; COOKIES opens inside it, a long value has View, F2 sets a member, Escape closes it,
// and the language's own hover does not show over it. Run: node desktop/e2e/debugvalue.check.mjs   (needs uv; Django is installed in the
// project's .venv)
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const script = [
    'import django',
    'from django.conf import settings',
    "settings.configure(DEBUG=True, ALLOWED_HOSTS=['*'], SECRET_KEY='not-a-secret')",
    'django.setup()',
    'from django.test import RequestFactory',
    '',
    "request = RequestFactory().get('/contact/', HTTP_COOKIE='csrftoken=abc123; sessionid=s3ss10n')",
    "data = {'items': [1, 2, {'deep': 'x' * 200}], 'name': 'box'}",
    'print(request.path, len(data))',
    '',
].join('\n');
const root = project({ 'contact.py': script });
for (const step of [['venv', '-q', '.venv'], ['pip', 'install', '-q', '-p', join(root, '.venv', 'Scripts', 'python.exe'), 'django']])
    assert.equal(spawnSync('uv', step, { cwd: root, encoding: 'utf8' }).status, 0, 'uv ' + step.join(' '));

const app = await launch(root);
const { page } = app;
const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;
const POPUP = `document.querySelector('.value-popup')`;
// each row as "caret name = value type View", its parts apart
const rows = () => page.evaluate(`[...document.querySelectorAll('.value-popup li > div')].map((d) => ['.caret', '.name', null, '.val', '.type', '.view-link']
    .map((s) => (s === null ? '=' : d.querySelector(s)?.textContent ?? '')).filter(Boolean).join(' ').replace(/\\s+/g, ' ').trim())`);
/** The mouse on a word of a line of the editor (its middle), as a person points at it. */
async function pointAt(line, word) {
    const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; const m = ed.getModel(); const col = m.getLineContent(${line}).indexOf(${JSON.stringify(word)}) + 1;
        const p = ed.getScrolledVisiblePosition({ lineNumber: ${line}, column: col + Math.floor(${word.length} / 2) }); const box = ed.getContainerDomNode().getBoundingClientRect();
        return { x: box.left + p.left + 2, y: box.top + p.top + p.height / 2 }; })()`);
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: at.x - 30, y: at.y + 40 });
    await sleep(100);
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: at.x, y: at.y });
}
try {
    await page.evaluate(`window.pymagic.openFile('contact.py')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('contact.py')`, 20000, 'contact.py');
    const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.revealLine(9); const box = ed.getContainerDomNode().getBoundingClientRect();
        return { x: box.left + ed.getLayoutInfo().glyphMarginLeft + 5, y: box.top + ed.getTopForLineNumber(9) - ed.getScrollTop() + 8 }; })()`);
    await page.click(at.x, at.y);
    await sleep(300);
    await page.evaluate(`${$('b-debug')}.click()`);
    const stop = await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 90000, 'the stop at line 9');
    console.log('stopped:', stop);
    assert.match(stop, /contact\.py:9/);

    // the mouse on `request`: the popup with its value, open one level
    await pointAt(9, 'request');
    await page.waitFor(`${POPUP} && document.querySelectorAll('.value-popup li > div').length > 5`, 15000, 'the value popup');
    let shown = await rows();
    console.log('popup:', shown.map((r) => r.slice(0, 90)));
    assert.match(shown[0], /^▾ request = <WSGIRequest: GET '\/contact\/'> WSGIRequest/);
    assert.ok(shown.some((r) => /^▸ COOKIES = \{.*csrftoken.*\} dict/.test(r)), 'COOKIES among its members');
    // by name as PyCharm lists them: the upper-case ones first, then the others, the _protected ones last
    const names = shown.slice(1).map((r) => r.replace(/^[▸▾] /, '').split(' = ')[0]);
    assert.deepEqual(names.slice(0, 5), ['COOKIES', 'FILES', 'GET', 'META', 'POST']);
    assert.ok(names.includes('body') && names.includes('headers') && names.includes('method') && names.includes('path'), names.join(', '));
    assert.ok(!names.includes('get_host') && !names.includes('build_absolute_uri'), 'no methods');
    assert.ok(names.findIndex((n) => n.startsWith('_')) > names.findIndex((n) => n === 'path'), 'the _protected ones last');
    assert.ok(shown.some((r) => /^▸ META = .*View$/.test(r)), 'META is long: View');
    // the language's own hover is not shown over it
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.monaco-hover')].filter((h) => h.offsetParent && h.textContent.trim()).length`), 0, 'no language hover');

    // COOKIES opens inside the popup: its two items
    await page.evaluate(`[...document.querySelectorAll('.value-popup li > div')].find((d) => d.querySelector('.name')?.textContent === 'COOKIES').querySelector('.caret').click()`);
    await page.waitFor(`[...document.querySelectorAll('.value-popup li > div')].some((d) => d.querySelector('.name')?.textContent === "'sessionid'")`, 10000, 'the cookies');
    shown = await rows();
    const cookies = shown.filter((r) => /'(csrftoken|sessionid)'/.test(r) && !r.startsWith('▸ COOKIES') && !r.startsWith('▾ COOKIES'));
    console.log('COOKIES opened:', cookies);
    // PYMAGIC_SHOT=<file.png>: a picture of the popup as it is now
    if (process.env.PYMAGIC_SHOT) {
        const shot = await page.send('Page.captureScreenshot', { format: 'png' });
        writeFileSync(process.env.PYMAGIC_SHOT, Buffer.from(shot.result.data, 'base64'));
    }
    assert.deepEqual(cookies.map((r) => r.replace(/^[▸▾] /, '')), ["'csrftoken' = 'abc123' str", "'sessionid' = 's3ss10n' str"]);

    // F2 on a cookie sets it; the popup stays and shows the new value
    await page.evaluate(`[...document.querySelectorAll('.value-popup li > div')].find((d) => d.querySelector('.name')?.textContent === "'sessionid'").focus()`);
    await page.key(113, 'F2');
    await page.waitFor(`document.querySelector('.value-popup input.edit')`, 5000, 'the input');
    await page.evaluate(`document.querySelector('.value-popup input.edit').value = "'changed'"`);
    await page.key(13, 'Enter');
    await page.waitFor(`[...document.querySelectorAll('.value-popup li > div')].some((d) => d.textContent.includes("'sessionid' = 'changed'"))`, 10000, 'the value set');
    const really = await page.evaluate(`(async () => { const r = await fetch('/api/info'); return true; })()`);
    assert.ok(really);
    console.log('F2 set a cookie in the popup');

    // View of META: the whole value in a dialog
    await page.evaluate(`[...document.querySelectorAll('.value-popup li > div')].find((d) => d.querySelector('.name')?.textContent === 'META').querySelector('.view-link').click()`);
    const whole = await page.waitFor(`document.querySelector('#sigdialog .value-text')?.textContent`, 10000, 'the whole value');
    console.log('View of META:', whole.length, 'characters');
    assert.ok(whole.length > 200 && whole.includes('PATH_INFO'));
    await page.evaluate(`[...document.querySelectorAll('#sigdialog button')].find((b) => b.textContent === 'Close').click()`);

    // Escape closes the popup; a dict's members open as deep as it goes
    await page.evaluate(`document.querySelector('.value-popup li > div').focus()`);
    await page.key(27, 'Escape');
    await page.waitFor(`!${POPUP}`, 5000, 'the popup closed');
    await pointAt(8, 'data');
    await page.waitFor(`${POPUP} && document.querySelector('.value-popup .name')?.textContent === 'data'`, 15000, 'the popup of data');
    const open = async (name) => {
        await page.evaluate(`[...document.querySelectorAll('.value-popup li > div')].find((d) => d.querySelector('.name')?.textContent === ${JSON.stringify(name)}).querySelector('.caret').click()`);
        await sleep(600);
    };
    await open("'items'");
    await open('2');
    shown = await rows();
    const deep = shown.map((r) => r.replace(/^[▸▾] /, '')).find((r) => r.startsWith("'deep' ="));
    console.log('three levels down:', deep?.slice(0, 60) + '…');
    assert.match(deep ?? '', /^'deep' = 'xxxx.*str View$/);
    console.log('OK');
} finally {
    await app.close();
}

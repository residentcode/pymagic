// Laravel in the built PyMagic.exe, on a copy of a Laravel app (PYMAGIC_TEST_LARAVEL_DIR, its vendor installed) with the PHP PYMAGIC_PHP names:
// Blade coloured in a .blade.php, Settings > PHP saying what is found, Run Artisan Command (Ctrl+Shift+X) listing the project's commands and
// running route:list, the Laravel server configuration answering, and Debug of it with Xdebug: a request stopping at a breakpoint of a route, its
// frames, the Variables pane, the value popup of a PHP array, Resume. Run: PYMAGIC_PHP=… PYMAGIC_TEST_LARAVEL_DIR=… node desktop/e2e/laravel.check.mjs
import assert from 'node:assert/strict';
import { appendFileSync, cpSync, mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { launch, sleep } from './app.mjs';

const source = process.env.PYMAGIC_TEST_LARAVEL_DIR;
assert.ok(source && process.env.PYMAGIC_PHP, 'PYMAGIC_TEST_LARAVEL_DIR and PYMAGIC_PHP');
const root = join(mkdtempSync(join(tmpdir(), 'pymagic-e2e-laravel-')), 'shop');
cpSync(source, root, { recursive: true });
const port = await new Promise((ok) => { const s = createServer(); s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => ok(p)); }); });
mkdirSync(join(root, '.pymagic'), { recursive: true });
writeFileSync(join(root, '.pymagic', 'run-configurations.json'), JSON.stringify({ configurations: [{ name: 'Laravel', kind: 'laravel', host: '127.0.0.1', port: String(port), script: '', args: '', cwd: '', env: {} }], selected: 'Laravel' }, null, 2));
const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;
// a route of the copy with values to look at (its return line is line ORDER_LINE of routes/web.php)
appendFileSync(join(root, 'routes', 'web.php'), [
    '',
    "Route::get('/order', function () {",
    "    $items = [['name' => 'pen', 'price' => 3], ['name' => 'book', 'price' => 12]];",
    "    $total = array_sum(array_column($items, 'price'));",
    "    return response()->json(['total' => $total]);",
    '});',
    '',
].join('\n'));
const ORDER_LINE = 12;

async function clickMargin(page, line) {
    const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.revealLine(${line}); const box = ed.getContainerDomNode().getBoundingClientRect();
        return { x: box.left + ed.getLayoutInfo().glyphMarginLeft + 5, y: box.top + ed.getTopForLineNumber(${line}) - ed.getScrollTop() + 8 }; })()`);
    await page.click(at.x, at.y);
    await sleep(300);
    return page.evaluate(`window.pymagic.editor.getModel().getLineDecorations(${line}).some((d) => d.options.glyphMarginClassName?.startsWith('bp-glyph'))`);
}

const app = await launch(root);
const { page } = app;
try {
    // Blade: its directives, echoes and HTML coloured as their own
    await page.evaluate(`window.pymagic.openFile('resources/views/welcome.blade.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.getLanguageId() === 'blade'`, 20000, 'the Blade language');
    const tokens = await page.evaluate(`(() => { const m = window.pymagic.monaco; const t = m.editor.tokenize("@if (Route::has('login'))\\n  <a href=\\"{{ route('login') }}\\">{{-- note --}}Log in</a>\\n@endif", 'blade');
        return [...new Set(t.flat().map((x) => x.type))]; })()`);
    console.log('Blade tokens:', tokens.join(', '));
    for (const t of ['keyword.blade.html', 'delimiter.blade.html', 'function.html', 'string.html', 'tag.html', 'comment.html']) assert.ok(tokens.includes(t), `${t} in ${tokens}`);

    // Settings > PHP: what is found
    await page.evaluate(`window.pymagic.changeSetting && 0`);
    const found = await page.evaluate(`fetch('/api/php').then((r) => r.json())`);
    console.log('found:', JSON.stringify({ php: found.version, composer: !!found.composer, laravel: found.laravel?.version }));
    assert.match(found.version, /^8\.\d+\.\d+/);
    assert.match(found.laravel.version, /^\d+\.\d+\.\d+$/);

    // Run Artisan Command: the dialog, filtered, then route:list run in the Run panel
    await page.waitFor(`document.querySelector('.monaco-editor')`, 10000, 'the editor');
    await page.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', windowsVirtualKeyCode: 88, key: 'X', code: 'KeyX', modifiers: 2 | 8 });
    await page.send('Input.dispatchKeyEvent', { type: 'keyUp', windowsVirtualKeyCode: 88, key: 'X', code: 'KeyX', modifiers: 2 | 8 });
    const all = await page.waitFor(`document.querySelectorAll('.artisan-row').length > 20 && document.querySelectorAll('.artisan-row').length`, 60000, 'the Artisan commands');
    await page.evaluate(`(() => { const f = document.querySelector('.artisan-box input'); f.value = 'route:l'; f.dispatchEvent(new Event('input')); })()`);
    const rows = await page.evaluate(`[...document.querySelectorAll('.artisan-row .artisan-name')].map((x) => x.textContent)`);
    console.log(`Run Artisan command: ${all} commands; "route:l" gives`, rows);
    assert.equal(rows[0], 'route:list');
    await page.evaluate(`document.querySelector('.artisan-box input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))`);
    const listed = await page.waitFor(`(() => { const t = ${$('output')}.textContent; return /exit code \\d/.test(t) ? t : null; })()`, 60000, 'route:list');
    console.log('route:list:', JSON.stringify(listed.trim().split('\n').slice(0, 4)));
    assert.match(listed, /GET\|HEAD/);
    assert.match(listed, /exit code 0/);

    // the Laravel server configuration: Run, a request answered, Stop
    await page.evaluate(`${$('b-run')}.click()`);
    await page.waitFor(`${$('output')}.textContent.includes('127.0.0.1:${port}')`, 60000, 'the server');
    const answer = await fetch(`http://127.0.0.1:${port}/`);
    const html = await answer.text();
    console.log('the Laravel server answered', answer.status, html.includes('Laravel') ? '(its welcome page)' : '');
    assert.equal(answer.status, 200);
    await page.evaluate(`${$('b-stop')}.click()`);
    await page.waitFor(`/exit code/.test(${$('output')}.textContent.split('Running').pop())`, 30000, 'Stop');

    // Debug: the server under Xdebug, a request stopped at the route's breakpoint
    await page.evaluate(`window.pymagic.openFile('routes/web.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('web.php') && window.pymagic.editor.getModel().getLineContent(${ORDER_LINE}).includes('response()')`, 20000, 'routes/web.php');
    assert.equal(await clickMargin(page, ORDER_LINE), true, 'the breakpoint');
    await page.evaluate(`${$('b-debug')}.click()`);
    await page.waitFor(`${$('output')}.textContent.split('Running').pop().includes('127.0.0.1:${port}')`, 90000, 'the server under the debugger')
        .catch(async (e) => { console.log('output:', (await page.evaluate(`${$('output')}.textContent`))); throw e; });
    const order = fetch(`http://127.0.0.1:${port}/order`);
    const stop = await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 90000, 'the stop');
    console.log('stopped:', stop);
    assert.ok(stop.includes(`web.php:${ORDER_LINE}`), stop);
    const vars = await page.waitFor(`(() => { const t = [...document.querySelectorAll('#vars li > div')].map((d) => d.textContent.replace(/\\s+/g, ' ').trim()); return t.some((x) => x.includes('$total = 15')) ? t : null; })()`, 20000, 'the Locals');
    console.log('Variables:', vars.slice(0, 4));
    assert.ok(vars.some((x) => /\$items = array\(2\)/.test(x)), '$items');
    // the value popup on $items
    const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; const line = ${ORDER_LINE - 1}; const col = ed.getModel().getLineContent(line).indexOf('items') + 3;
        const p = ed.getScrolledVisiblePosition({ lineNumber: line, column: col }); const box = ed.getContainerDomNode().getBoundingClientRect(); return { x: box.left + p.left, y: box.top + p.top + p.height / 2 }; })()`);
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: at.x - 40, y: at.y + 60 });
    await sleep(100);
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: at.x, y: at.y });
    const popup = await page.waitFor(`document.querySelectorAll('.value-popup li > div').length >= 3 && [...document.querySelectorAll('.value-popup li > div')].map((d) => d.textContent.replace(/\\s+/g, ' ').trim())`, 20000, 'the value popup');
    console.log('value popup of $items:', popup.slice(0, 3));
    assert.match(popup[0], /\$items = array\(2\)/);
    await page.key(27, 'Escape');
    // Resume: the request is answered
    await page.evaluate(`${$('b-debug')}.click()`);
    const answered = await order;
    const body = await answered.json();
    console.log('the request went on:', answered.status, JSON.stringify(body));
    assert.equal(body.total, 15);
    await page.evaluate(`${$('b-stop')}.click()`);
    await page.waitFor(`/exit code/.test(${$('output')}.textContent.split('Running').pop())`, 30000, 'Stop');
    console.log('OK');
} finally {
    await app.close();
}

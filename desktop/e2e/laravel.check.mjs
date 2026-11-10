// Laravel in the built PyMagic.exe, on a copy of a Laravel app (PYMAGIC_TEST_LARAVEL_DIR, its vendor installed) with the PHP PYMAGIC_PHP names:
// Blade coloured in a .blade.php, Settings > PHP saying what is found, Run Artisan Command (Ctrl+Shift+X) listing the project's commands and
// running route:list, the Laravel server configuration answering, and Debug of it with Xdebug: a request stopping at a breakpoint of a route, its
// frames, the Variables pane, the value popup of a PHP array, Resume. Then code insight with this computer's PHP (Phpactor's completion), a view's
// name as a link and completed, a route of two nested groups opened from its name on a line of its own in a Blade @php block, Run Composer Command (dump-autoload), a Blade template's breakpoint stopping a request in the template, and
// Pause stopping a slow request where it is.
// Run: PYMAGIC_PHP=… PYMAGIC_COMPOSER=… PYMAGIC_TEST_LARAVEL_DIR=… PYMAGIC_PHPACTOR_HOME=(a folder in %TEMP%) node desktop/e2e/laravel.check.mjs
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
    "Route::get('/slow', function () {",
    '    for ($i = 0; $i < 3000; $i++) {',
    '        usleep(5000);',
    '    }',
    "    return 'slow done';",
    '});',
    // groups in groups, written over several lines: the route's name is admin.reports.index
    "Route::middleware('web')",
    "    ->name('admin.')",
    "    ->group(function () {",
    "        Route::name('reports.')->group(function () {",
    "            Route::get('/admin/reports', fn () => 'reports')->name('index');",
    '        });',
    '    });',
    '',
].join('\n'));
// a template whose @php block names that route on a line of its own
mkdirSync(join(root, 'resources', 'views', 'admin'), { recursive: true });
// a template with Blade inside its <script> and <style>, where JavaScript and CSS are completed
writeFileSync(join(root, 'resources', 'views', 'admin', 'page.blade.php'), ['<script>', '    const users = @json($users);', '    @if ($admin)', "        const name = '{{ $name }}';", '    @endif', '    // JS', '</script>',
    '<style>', '    .dark {', '        /* CSS */', '    }', '</style>', ''].join('\n'));
writeFileSync(join(root, 'resources', 'views', 'admin', 'links.blade.php'), ['@php', '    $reports = route(', "        'admin.reports.index'", '    );', '@endphp', '<a href="{{ $reports }}">Reports</a>', ''].join('\n'));
const ORDER_LINE = 12;
const SLOW_LINE = 16;   // the loop's lines: 15 (for), 16 (usleep)

async function clickMargin(page, line) {
    const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.revealLineInCenter(${line}, 1); const box = ed.getContainerDomNode().getBoundingClientRect();
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
    // <script> and <style> in a template: JavaScript and CSS (their tokenizers load on first use: asked again until they have)
    const embedded = await page.waitFor(`(() => { const t = window.pymagic.monaco.editor.tokenize("<script defer>\\n// note\\nif (a) { x = 'b' }\\n</script>\\n<style>\\n.dark { color: red }\\n</style>\\n<p>{{ $x }}</p>", 'blade');
        const types = [...new Set(t.flat().map((x) => x.type))]; return types.includes('keyword.js') && types.some((x) => x.endsWith('.css')) ? types : null; })()`, 20000, 'JavaScript and CSS in Blade');
    console.log('script and style tokens:', embedded.join(', '));
    for (const t of ['keyword.js', 'comment.js', 'string.js', 'tag.html', 'attribute.name.html', 'delimiter.blade.html']) assert.ok(embedded.includes(t), `${t} in ${embedded}`);

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

    // code insight with this computer's PHP: Phpactor's completion after `$user->` (asked again until it has indexed the project)
    assert.ok(found.phpactor, 'the Phpactor PyMagic carries');
    await page.evaluate(`window.pymagic.openFile('routes/web.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('web.php')`, 20000, 'routes/web.php');
    await page.evaluate(`(() => { const ed = window.pymagic.editor; const m = ed.getModel(); const last = m.getLineCount();
        ed.setPosition({ lineNumber: last, column: m.getLineMaxColumn(last) }); ed.focus(); ed.trigger('test', 'type', { text: '\\n$user = new \\\\App\\\\Models\\\\User();\\n$user->noti' }); })()`);
    let labels = [];
    for (let i = 0; i < 40 && !labels.includes('notify'); i++) {
        await page.evaluate(`window.pymagic.editor.trigger('test', 'hideSuggestWidget', {})`);
        await page.evaluate(`window.pymagic.editor.trigger('test', 'editor.action.triggerSuggest', {})`);
        await sleep(2500);
        labels = await page.evaluate(`[...document.querySelectorAll('.suggest-widget.visible .monaco-list-row')].map((x) => x.getAttribute('aria-label'))`);
    }
    console.log('completion after $user->noti:', labels.slice(0, 6));
    assert.ok(labels.includes('notify'), labels.join(','));
    await page.key(27, 'Escape');
    await page.evaluate(`(() => { const ed = window.pymagic.editor; while (ed.getModel().getValue().includes('$user')) ed.trigger('test', 'undo', {}); })()`);

    // a view's name is a link: Go to Definition on 'welcome' opens resources/views/welcome.blade.php
    await page.evaluate(`(() => { const ed = window.pymagic.editor; const m = ed.getModel(); const at = m.getValue().indexOf("view('welcome')") + 7;
        ed.setPosition(m.getPositionAt(at)); ed.focus(); return ed.trigger('test', 'editor.action.revealDefinition', {}); })()`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('welcome.blade.php')`, 20000, 'the view opened from its name');
    // completion in view('…'): the project's views
    const views = await page.evaluate(`(async () => { const r = await fetch('/api/laravel/complete', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text: "view('", offset: 6 }) }); return (await r.json()).items.map((i) => i.label); })()`);
    console.log('views in view(\'…\'):', views.slice(0, 4));
    assert.ok(views.includes('welcome'), views.join(','));

    // a route's name in a Blade @php block, under the prefixes of two groups: Go to Definition opens routes/web.php at its ->name
    await page.evaluate(`window.pymagic.openFile('resources/views/admin/links.blade.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('links.blade.php')`, 20000, 'links.blade.php');
    await page.evaluate(`(() => { const ed = window.pymagic.editor; const m = ed.getModel(); const at = m.getValue().indexOf('admin.reports.index') + 3;
        ed.setPosition(m.getPositionAt(at)); ed.focus(); return ed.trigger('test', 'editor.action.revealDefinition', {}); })()`);
    const routeAt = await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('routes/web.php') && window.pymagic.editor.getPosition().lineNumber`, 20000, 'the route opened from its name');
    const routeLine = await page.evaluate(`window.pymagic.editor.getModel().getLineContent(${routeAt})`);
    console.log(`admin.reports.index in a @php block leads to routes/web.php:${routeAt}:`, routeLine.trim());
    assert.ok(routeLine.includes("->name('index')"), routeLine);

    // completion in a template's <script> (JavaScript's, of the TypeScript service) and <style> (CSS properties); Blade's @json, @if and {{ }}
    // in the script are no JavaScript errors
    await page.evaluate(`window.pymagic.openFile('resources/views/admin/page.blade.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('page.blade.php')`, 20000, 'page.blade.php');
    async function suggestAfter(marker, text, wanted) {
        await page.evaluate(`(() => { const ed = window.pymagic.editor; const m = ed.getModel(); const p = m.getPositionAt(m.getValue().indexOf(${JSON.stringify(marker)}) + ${marker.length});
            ed.setPosition(p); ed.focus(); ed.trigger('test', 'type', { text: ${JSON.stringify(text)} }); })()`);
        let rows = [];
        for (let i = 0; i < 15 && !rows.some((l) => l?.startsWith(wanted)); i++) {
            await page.evaluate(`window.pymagic.editor.trigger('test', 'hideSuggestWidget', {})`);
            await page.evaluate(`window.pymagic.editor.trigger('test', 'editor.action.triggerSuggest', {})`);
            await sleep(1500);
            rows = await page.evaluate(`[...document.querySelectorAll('.suggest-widget.visible .monaco-list-row')].map((x) => x.getAttribute('aria-label'))`);
        }
        await page.key(27, 'Escape');
        return rows;
    }
    const jsRows = await suggestAfter('// JS', '\n    document.querySel', 'querySelector');
    console.log('completion in <script> after document.querySel:', jsRows.slice(0, 4));
    assert.ok(jsRows.some((l) => l?.startsWith('querySelector')), jsRows.join(','));
    const cssRows = await suggestAfter('/* CSS */', '\n        backgr', 'background');
    console.log('completion in <style> after backgr:', cssRows.slice(0, 4));
    assert.ok(cssRows.some((l) => l?.startsWith('background')), cssRows.join(','));
    await page.evaluate(`(() => { const ed = window.pymagic.editor; const m = ed.getModel(); ed.executeEdits('test', [{ range: m.getFullModelRange(), text: m.getValue().replace('document.querySel', 'document.title;').replace('backgr', 'color: red;') }]); })()`);
    await sleep(3000);
    const problems = await page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ resource: window.pymagic.editor.getModel().uri }).map((x) => x.source + ': ' + x.message)`);
    console.log('problems in the script and style of the template:', problems.length ? problems : 'none');
    assert.deepEqual(problems.filter((p) => p.startsWith('javascript') || p.startsWith('css')), []);

    // /* */ typed around tags of the HTML: a warning that it is no comment there, and the quick fix that makes it {{-- --}}
    await page.evaluate(`(() => { const ed = window.pymagic.editor; const m = ed.getModel(); const last = m.getLineCount();
        ed.executeEdits('test', [{ range: new window.pymagic.monaco.Range(last, 1, last, 1), text: '/*<link rel="preconnect" href="https://fonts.bunny.net">\\n<link href="https://fonts.bunny.net/css" rel="stylesheet"/>*/\\n' }]); })()`);
    const warning = await page.waitFor(`window.pymagic.monaco.editor.getModelMarkers({ owner: 'html-comment', resource: window.pymagic.editor.getModel().uri })[0]?.message`, 10000, 'the /* */ warning');
    console.log('/* */ around HTML:', warning);
    await page.evaluate(`(() => { const ed = window.pymagic.editor; const mk = window.pymagic.monaco.editor.getModelMarkers({ owner: 'html-comment', resource: ed.getModel().uri })[0];
        ed.setPosition({ lineNumber: mk.startLineNumber, column: mk.startColumn + 3 }); ed.focus(); return ed.trigger('test', 'editor.action.quickFix', {}); })()`);
    await page.waitFor(`[...document.querySelectorAll('.action-widget .monaco-list-row')].some((x) => (x.getAttribute('aria-label') ?? '').startsWith('Change to a Blade comment'))`, 10000, 'the quick fix in the menu');
    await page.key(13, 'Enter');
    const fixed = await page.waitFor(`(() => { const v = window.pymagic.editor.getModel().getValue(); return v.includes('{{-- <link') ? v.slice(v.indexOf('{{-- <link')) : null; })()`, 10000, 'the quick fix');
    console.log('after the quick fix:', JSON.stringify(fixed));
    assert.equal(fixed, '{{-- <link rel="preconnect" href="https://fonts.bunny.net">\n<link href="https://fonts.bunny.net/css" rel="stylesheet"/> --}}\n');

    // Run Composer command: its list (composer.json's scripts in it), and dump-autoload run in the Run panel
    await page.evaluate(`window.pymagic.runCommand('pymagic.laravel.composer')`);
    const composerRows = await page.waitFor(`document.querySelectorAll('.artisan-row').length > 8 && [...document.querySelectorAll('.artisan-row .artisan-name')].map((x) => x.textContent)`, 20000, 'the Composer commands');
    console.log('Run Composer command:', composerRows.length, 'commands, scripts:', composerRows.filter((n) => n.startsWith('run-script')).join(', '));
    assert.ok(composerRows.includes('require') && composerRows.some((n) => n.startsWith('run-script ')), composerRows.join(','));
    await page.evaluate(`(() => { const f = document.querySelector('.artisan-box input'); f.value = 'dump-autoload'; f.dispatchEvent(new Event('input')); f.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
    const dumped = await page.waitFor(`(() => { const t = ${$('output')}.textContent.split('Running').pop(); return /exit code \\d/.test(t) ? t : null; })()`, 120000, 'composer dump-autoload');
    console.log('composer dump-autoload:', JSON.stringify(dumped.trim().split('\n').filter(Boolean).slice(-2)));
    assert.match(dumped, /exit code 0/);

    // a breakpoint of a Blade template: the request of / stops in welcome.blade.php, at its line
    await page.evaluate(`window.pymagic.openFile('resources/views/welcome.blade.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('welcome.blade.php')`, 20000, 'welcome.blade.php');
    const bladeLine = await page.evaluate(`window.pymagic.editor.getModel().getLinesContent().findIndex((l) => l.includes("@if (Route::has('login'))")) + 1`);
    assert.ok(bladeLine > 0, 'the @if line');
    assert.equal(await clickMargin(page, bladeLine), true, 'the template breakpoint');
    await page.evaluate(`${$('b-debug')}.click()`);
    await page.waitFor(`${$('output')}.textContent.split('Running').pop().includes('127.0.0.1:${port}')`, 120000, 'the server under the debugger again');
    const home = fetch(`http://127.0.0.1:${port}/`);
    const bladeStop = await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 90000, 'the stop in the template');
    console.log('stopped in the template:', bladeStop);
    assert.ok(bladeStop.includes(`welcome.blade.php:${bladeLine}`), bladeStop);
    await page.evaluate(`${$('b-debug')}.click()`);
    assert.equal((await home).status, 200);

    // Pause: a slow request, stopped where it is (Xdebug's control socket), with no breakpoint there; Resume, and it is answered
    const slow = fetch(`http://127.0.0.1:${port}/slow`);
    await sleep(1500);
    await page.waitFor(`!${$('b-pause')}.disabled`, 10000, 'Pause enabled');
    await page.evaluate(`${$('b-pause')}.click()`);
    const paused = await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 30000, 'the pause');
    console.log('paused:', paused);
    assert.match(paused, new RegExp(`web\\.php:(${SLOW_LINE - 1}|${SLOW_LINE})\\b`), paused);
    await page.evaluate(`${$('b-debug')}.click()`);
    const slowBody = await (await slow).text();
    console.log('the slow request went on:', slowBody);
    assert.equal(slowBody, 'slow done');
    await page.evaluate(`${$('b-stop')}.click()`);
    await page.waitFor(`/exit code/.test(${$('output')}.textContent.split('Running').pop())`, 30000, 'Stop');
    console.log('OK');
} finally {
    await app.close();
}

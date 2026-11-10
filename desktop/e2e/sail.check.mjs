// Laravel Sail in the built PyMagic.exe (Settings > PHP: sail), on a Sail app whose container Docker Desktop runs (PYMAGIC_TEST_SAIL_DIR:
// laravel/sail installed with sail:install and its image built; APP_PORT the port it serves on): Settings > PHP saying it is Sail with the
// container's PHP and Xdebug, the Laravel server configuration answering from the container, Debug of it stopping a request at a route's
// breakpoint in the file here, then Stop making the container again without PyMagic's Xdebug; and the Tests panel running Pest there.
// Run: PYMAGIC_TEST_SAIL_DIR=… APP_PORT=… node desktop/e2e/sail.check.mjs
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { cpSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join } from 'node:path';
import { launch, sleep } from './app.mjs';

const source = process.env.PYMAGIC_TEST_SAIL_DIR;
const port = process.env.APP_PORT;
assert.ok(source && port, 'PYMAGIC_TEST_SAIL_DIR and APP_PORT');
// a copy of the app (the test's end removes it), under a folder of the same name: compose's project is the same, so Sail's container (its
// image built already) is made again with the copy shared; at the end it is made again with the app's folder
const root = join(mkdtempSync(join(tmpdir(), 'pymagic-e2e-sail-')), basename(source));
cpSync(source, root, { recursive: true, filter: (f) => !/[\\/](node_modules|\.git)$/.test(f) });
const composeEnv = { ...process.env, WWWUSER: '1000', WWWGROUP: '1000' };
execFileSync('docker', ['compose', 'up', '-d', '--force-recreate', 'laravel.test'], { cwd: root, env: composeEnv, stdio: 'ignore' });
const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;
const routesFile = join(root, 'routes', 'web.php');
const routesBefore = readFileSync(routesFile, 'utf8');
writeFileSync(routesFile, routesBefore + "\nRoute::get('/pm-sail', function () {\n    $where = PHP_OS_FAMILY;\n    return 'sail ' . $where;\n});\n");
const ROUTE_LINE = routesBefore.split('\n').length + 3;
const testFile = join(root, 'tests', 'Unit', 'PmSailTest.php');
writeFileSync(testFile, "<?php\n\ntest('runs in sail', function () {\n    expect(getcwd())->toBe('/var/www/html');\n});\n");
mkdirSync(join(root, '.pymagic'), { recursive: true });
writeFileSync(join(root, '.pymagic', 'run-configurations.json'), JSON.stringify({ configurations: [{ name: 'Laravel', kind: 'laravel', host: '127.0.0.1', port: '8000', script: '', args: '', cwd: '', env: {} }], selected: 'Laravel' }, null, 2));
const xdebugMode = () => execFileSync('docker', ['compose', 'exec', '-T', 'laravel.test', 'printenv', 'XDEBUG_MODE'], { cwd: root, env: composeEnv }).toString().trim();

async function clickMargin(page, line) {
    const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.revealLineInCenter(${line}, 1); const box = ed.getContainerDomNode().getBoundingClientRect();
        return { x: box.left + ed.getLayoutInfo().glyphMarginLeft + 5, y: box.top + ed.getTopForLineNumber(${line}) - ed.getScrollTop() + 8 }; })()`);
    await page.click(at.x, at.y);
    await sleep(300);
    return page.evaluate(`window.pymagic.editor.getModel().getLineDecorations(${line}).some((d) => d.options.glyphMarginClassName?.startsWith('bp-glyph'))`);
}

const app = await launch(root, { phpPath: 'sail' });
const { page } = app;
try {
    // Settings > PHP: Sail, the container's PHP and Xdebug
    const found = await page.evaluate(`fetch('/api/php').then((r) => r.json())`);
    console.log('found:', JSON.stringify({ php: found.php, version: found.version, xdebug: found.xdebug?.version, sail: found.sail, error: found.error }));
    assert.equal(found.php, 'Laravel Sail (laravel.test)');
    assert.match(found.version, /^8\.\d+\.\d+/);
    assert.ok(found.xdebug?.loaded && found.sail?.running, JSON.stringify(found));

    // Run: the container's server answers
    await page.evaluate(`${$('b-run')}.click()`);
    await page.waitFor(`${$('output')}.textContent.includes('localhost:${port}')`, 120000, 'Laravel in the container');
    let body = '';
    for (let i = 0; i < 30 && body !== 'sail Linux'; i++) { body = await fetch(`http://localhost:${port}/pm-sail`).then((r) => r.text()).catch(() => ''); if (body !== 'sail Linux') await sleep(1000); }
    console.log('the Sail server answered:', body);
    assert.equal(body, 'sail Linux');
    await page.evaluate(`${$('b-stop')}.click()`);
    await page.waitFor(`/exit code/.test(${$('output')}.textContent.split('Running').pop())`, 60000, 'Stop');

    // Debug: a breakpoint of the route stops a request (the container made again with Xdebug for PyMagic), then Stop makes it again without
    await page.evaluate(`window.pymagic.openFile('routes/web.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('web.php')`, 20000, 'routes/web.php');
    assert.equal(await clickMargin(page, ROUTE_LINE), true, 'the breakpoint');
    await page.evaluate(`${$('b-debug')}.click()`);
    await page.waitFor(`${$('output')}.textContent.split('Debugging').pop().includes('localhost:${port}')`, 180000, 'Laravel in the container under the debugger')
        .catch(async (e) => { throw new Error(e.message + '; ' + await page.evaluate(`${$('output')}.textContent.slice(-800)`)); });
    const answer = fetch(`http://localhost:${port}/pm-sail`).then((r) => r.text());
    const stop = await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 90000, 'the stop at the route');
    console.log('stopped:', stop);
    assert.ok(stop.includes(`routes/web.php:${ROUTE_LINE}`), stop);
    await page.evaluate(`${$('b-debug')}.click()`);
    assert.equal(await answer, 'sail Linux');
    await page.evaluate(`${$('b-stop')}.click()`);
    await page.waitFor(`${$('output')}.textContent.includes("without PyMagic's Xdebug")`, 180000, 'the container made again');
    console.log("after Stop, the container's XDEBUG_MODE:", xdebugMode());
    assert.equal(xdebugMode(), 'off');
    assert.equal(await clickMargin(page, ROUTE_LINE), false, 'the breakpoint off');

    // the Tests panel: Pest in the container
    await page.evaluate(`(() => { const s = ${$('tests-fw')}; s.value = 'auto'; s.dispatchEvent(new Event('change')); ${$('t-tests')}.click(); })()`);
    await page.waitFor(`[...document.querySelectorAll('#tests-tree .trow .tname')].some((x) => x.textContent === 'PmSailTest.php')`, 30000, 'the Pest tests in the tree');
    await page.evaluate(`(() => { [...document.querySelectorAll('#tests-tree .trow')].find((r) => r.querySelector('.tname').textContent === 'PmSailTest.php').click(); ${$('tests-sel')}.click(); })()`);
    await page.waitFor(`!${$('tests-all')}.disabled && ${$('tests-console')}.textContent.includes('finished')`, 180000, 'the tests in the container');
    for (let i = 0; i < 4; i++) await page.evaluate(`[...document.querySelectorAll('#tests-tree .trow .caret')].filter((c) => c.textContent === '▸').forEach((c) => c.click())`);
    const icon = await page.evaluate(`[...document.querySelectorAll('#tests-tree .trow')].find((r) => r.querySelector('.tname').textContent === 'runs in sail')?.querySelector('.ticon').textContent`);
    console.log('Pest in Sail:', icon);
    assert.equal(icon, '✔');
    console.log('OK');
} finally {
    await app.close();
    execFileSync('docker', ['compose', 'up', '-d', '--force-recreate', 'laravel.test'], { cwd: source, env: composeEnv, stdio: 'ignore' });
}

// Debugging a FastAPI run configuration (uvicorn --reload, on by default; a Starlette app, as FastAPI is one) in the built PyMagic.exe: uvicorn's reloader starts the server
// as a process of multiprocessing's (spawn, not subprocess), and that process runs under the debugger too (pm_debug.py follow_children):
// breakpoints in an async and in a plain endpoint stop, the request goes on after Resume; after a change of main.py uvicorn ends its server
// (on Windows by a Ctrl+C, which pm_debug.py's windows_ctrl_c carries) and the new one stops at the breakpoint again. Stop ends every
// process. Then a Run without the debugger: after a change the new server answers with it.
// Run: node desktop/e2e/uvicornreload.check.mjs   (needs uv; Starlette and uvicorn are installed in the project's .venv)
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const port = await new Promise((done) => { const s = createServer(); s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => done(p)); }); });
const main = [
    'from starlette.applications import Starlette',
    'from starlette.responses import JSONResponse',
    'from starlette.routing import Route',
    '',
    '',
    'async def bloblo(request):',
    '    pass',
    '    user = await fetch_user()',
    "    return JSONResponse({'hello': user})",
    '',
    '',
    'def plain(request):',
    '    pass',
    '    x = 1',
    "    return JSONResponse({'plain': x})",
    '',
    '',
    'async def fetch_user():',
    "    return 'anonymous'",
    '',
    '',
    "app = Starlette(routes=[Route('/a/', bloblo), Route('/p/', plain)])",
    '',
].join('\n');
const root = project({
    'main.py': main,
    '.pymagic/run-configurations.json': JSON.stringify({ configurations: [{ name: 'FastAPI', kind: 'fastapi', app: 'main:app', host: '127.0.0.1', port: String(port), reload: true, script: '', args: '', cwd: '', env: {} }], selected: 'FastAPI' }),
});
const python = join(root, '.venv', 'Scripts', 'python.exe');
for (const step of [['venv', '-q', '.venv'], ['pip', 'install', '-q', '--offline', '-p', python, 'starlette', 'uvicorn']])
    assert.equal(spawnSync('uv', step, { cwd: root, encoding: 'utf8' }).status, 0, 'uv ' + step.join(' '));

const app = await launch(root);
const { page } = app;
const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;
/** A request to the server, as a browser makes it; its answer (or the error) when it comes. */
const get = (path) => fetch(`http://127.0.0.1:${port}${path}`).then((r) => r.text(), (e) => 'error: ' + e.message);
async function serving() {
    for (let i = 0; i < 120; i++) {
        const r = await fetch(`http://127.0.0.1:${port}/nothing/`).then((x) => x.status, () => 0);
        if (r) return;
        await sleep(500);
    }
    throw new Error('the server did not start');
}
const stopAt = (what) => page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 60000, what);
const resume = () => page.evaluate(`${$('b-debug')}.click()`);
async function breakAt(line) {
    // F9 on the line, as a person sets a breakpoint (the window is minimized: no clicks in its margin)
    const shown = `window.pymagic.editor.getModel().getAllDecorations().some((d) => d.options.glyphMarginClassName?.startsWith('bp-glyph') && d.range.startLineNumber === ${line})`;
    await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: ${line}, column: 1 }); window.pymagic.runCommand('pymagic.run.toggleBreakpoint'); })()`);
    await page.waitFor(shown, 10000, `the breakpoint at line ${line}`);
}
const pids = () => spawnSync('powershell', ['-NoProfile', '-Command', `(Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'python*' -and $_.CommandLine -like '*${root.replace(/'/g, "''")}*' }).ProcessId`], { encoding: 'utf8' }).stdout.trim().split(/\s+/).filter(Boolean);

try {
    await page.evaluate(`window.pymagic.openFile('main.py')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('main.py')`, 20000, 'main.py');
    await breakAt(8);
    await breakAt(14);
    await page.evaluate(`${$('b-debug')}.click()`);
    await serving();
    console.log('processes of the server:', pids().length);
    assert.ok(pids().length >= 2, 'the reloader and its server process');

    // the async endpoint: the line with an await
    let answer = get('/a/');
    let stop = await stopAt('the stop in the async endpoint');
    console.log('stopped:', stop);
    assert.match(stop, /bloblo.*main\.py:8/);
    await resume();
    assert.equal(await answer, '{"hello":"anonymous"}');

    answer = get('/p/');
    stop = await stopAt('the stop in the plain endpoint');
    console.log('stopped:', stop);
    assert.match(stop, /plain.*main\.py:14/);
    await resume();
    assert.equal(await answer, '{"plain":1}');

    // a change of the file: uvicorn ends its server and starts a new one, which stops at the breakpoint too
    const change = (from, to) => writeFileSync(join(root, 'main.py'), readFileSync(join(root, 'main.py'), 'utf8').replace(from, to));
    change("{'plain': x}", "{'plain again': x}");
    let changed = false;
    for (let i = 0; i < 60 && !changed; i++) {
        await sleep(1000);
        changed = /Started server process[\s\S]*Started server process/.test(await page.evaluate(`document.getElementById('output').innerText`));
    }
    assert.ok(changed, 'a new server process after the change');
    await serving();
    answer = get('/p/');
    stop = await stopAt('the stop after the reload');
    console.log('stopped after the reload:', stop);
    assert.match(stop, /plain.*main\.py:14/);
    await resume();
    assert.equal(await answer, '{"plain again":1}');

    await page.evaluate(`${$('b-stop')}.click()`);
    for (let i = 0; i < 40 && pids().length; i++) await sleep(250);
    assert.deepEqual(pids(), [], 'Stop ends the reloader and its server process');

    // Run, without the debugger: the change is served by the new server
    await page.evaluate(`${$('b-run')}.click()`);
    await serving();
    assert.equal(await get('/p/'), '{"plain again":1}');
    change("{'plain again': x}", "{'plain third': x}");
    let third = '';
    for (let i = 0; i < 40 && !third.includes('third'); i++) { await sleep(1000); third = await get('/p/'); }
    console.log('Run after a change:', third);
    assert.equal(third, '{"plain third":1}');
    await page.evaluate(`${$('b-stop')}.click()`);
    for (let i = 0; i < 40 && pids().length; i++) await sleep(250);
    assert.deepEqual(pids(), [], 'Stop ends the Run too');
    console.log('OK');
} catch (e) {
    console.log('output:', (await page.evaluate(`document.getElementById('output').innerText`)).slice(-3000));
    throw e;
} finally {
    await app.close();
}

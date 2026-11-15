// Debugging a Django run configuration with "Reload on changes" in the built PyMagic.exe: the reloader starts manage.py runserver again
// as a child of its own, the one that serves the requests, and that child runs under the debugger too (pm_debug.py follow_children):
// a breakpoint in an async class-based view (await request.auser()) and in a plain view stop, the view goes on after Resume; after a
// change of views.py the reloader starts a new child, which is given the breakpoints and stops again. Stop ends both processes.
// Run: node desktop/e2e/djangoreload.check.mjs   (needs uv; Django is installed in the project's .venv)
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const port = await new Promise((done) => { const s = createServer(); s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => done(p)); }); });
const views = [
    'from django.http import HttpResponse',
    'from django.views import View',
    '',
    '',
    'class Blabla(View):',
    '    async def get(self, request):',
    '        user = await request.auser()',
    "        return HttpResponse('hello %s' % user)",
    '',
    '',
    'def plain(request):',
    '    x = 1',
    "    return HttpResponse('plain %s' % x)",
    '',
].join('\n');
const root = project({
    'manage.py': "import os, sys\nos.environ.setdefault('DJANGO_SETTINGS_MODULE', 'settings')\nfrom django.core.management import execute_from_command_line\nexecute_from_command_line(sys.argv)\n",
    'settings.py': "SECRET_KEY = 'not-a-secret'\nDEBUG = True\nALLOWED_HOSTS = ['*']\nROOT_URLCONF = 'urls'\nINSTALLED_APPS = ['django.contrib.auth', 'django.contrib.contenttypes', 'django.contrib.sessions']\n"
        + "MIDDLEWARE = ['django.contrib.sessions.middleware.SessionMiddleware', 'django.contrib.auth.middleware.AuthenticationMiddleware']\n"
        + "DATABASES = {'default': {'ENGINE': 'django.db.backends.sqlite3', 'NAME': 'db.sqlite3'}}\n",
    'urls.py': "from django.urls import path\nfrom app.views import Blabla, plain\nurlpatterns = [path('a/', Blabla.as_view()), path('p/', plain)]\n",
    'app/__init__.py': '',
    'app/views.py': views,
    '.pymagic/run-configurations.json': JSON.stringify({ configurations: [{ name: 'Django', kind: 'django', host: '127.0.0.1', port: String(port), reload: true, script: '', args: '', cwd: '', env: {} }], selected: 'Django' }),
});
const python = join(root, '.venv', 'Scripts', 'python.exe');
for (const step of [['venv', '-q', '.venv'], ['pip', 'install', '-q', '-p', python, 'django']])
    assert.equal(spawnSync('uv', step, { cwd: root, encoding: 'utf8' }).status, 0, 'uv ' + step.join(' '));
assert.equal(spawnSync(python, ['manage.py', 'migrate', '-v0'], { cwd: root, encoding: 'utf8' }).status, 0, 'migrate');

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
const pids = () => spawnSync('powershell', ['-NoProfile', '-Command', `(Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'python*' -and $_.CommandLine -like '*${root.replace(/'/g, "''")}*manage.py*' }).ProcessId`], { encoding: 'utf8' }).stdout.trim().split(/\s+/).filter(Boolean);

try {
    await page.evaluate(`window.pymagic.openFile('app/views.py')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('views.py')`, 20000, 'views.py');
    await breakAt(7);
    await breakAt(12);
    await page.evaluate(`${$('b-debug')}.click()`);
    await serving();
    console.log('processes of manage.py:', pids().length);
    assert.ok(pids().length >= 2, 'the reloader and its child');

    // the async view: the line with await request.auser()
    let answer = get('/a/');
    let stop = await stopAt('the stop in the async view');
    console.log('stopped:', stop);
    assert.match(stop, /get.*views\.py:7/);
    await resume();
    assert.equal(await answer, 'hello AnonymousUser');

    answer = get('/p/');
    stop = await stopAt('the stop in the plain view');
    console.log('stopped:', stop);
    assert.match(stop, /plain.*views\.py:12/);
    await resume();
    assert.equal(await answer, 'plain 1');

    // a change of the file: the reloader starts a new child, which stops at the breakpoints too
    const before = pids();
    writeFileSync(join(root, 'app', 'views.py'), readFileSync(join(root, 'app', 'views.py'), 'utf8').replace("'plain %s'", "'plain again %s'"));
    for (let i = 0; i < 60 && pids().join() === before.join(); i++) await sleep(500);
    await sleep(1500);
    await serving();
    answer = get('/p/');
    stop = await stopAt('the stop after the reload');
    console.log('stopped after the reload:', stop);
    assert.match(stop, /plain.*views\.py:12/);
    await resume();
    assert.equal(await answer, 'plain again 1');

    await page.evaluate(`${$('b-stop')}.click()`);
    for (let i = 0; i < 40 && pids().length; i++) await sleep(250);
    assert.deepEqual(pids(), [], 'Stop ends the reloader and its child');
    console.log('OK');
} catch (e) {
    console.log('output:', (await page.evaluate(`document.getElementById('output').innerText`)).slice(-3000));
    throw e;
} finally {
    await app.close();
}

// JavaScript Debug in the built PyMagic.exe: a Vite React app (made by Vite's own generator), its dev server running, a breakpoint set in the
// margin of src/App.tsx, and the JavaScript Debug configuration debugged: Edge (or Chrome) opens the page, the stop shows in the Run panel
// on the .tsx line with its frames and values, the value popup shows `count`, Resume lets the page go on, Stop closes the browser.
// Run: node desktop/e2e/browserdebug.check.mjs   (needs npm, the internet once for Vite, and Edge or Chrome)
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { launch, removeTree, sleep } from './app.mjs';

const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;
const parent = mkdtempSync(join(tmpdir(), 'pymagic-e2e-vite-'));
const made = spawnSync('npm', ['create', '--yes', 'vite@latest', 'shop', '--', '--template', 'react-ts'], { cwd: parent, encoding: 'utf8', shell: true });
assert.equal(made.status, 0, 'npm create vite: ' + made.stderr);
const root = join(parent, 'shop');
assert.equal(spawnSync('npm', ['install', '--no-audit', '--no-fund'], { cwd: root, encoding: 'utf8', shell: true }).status, 0, 'npm install');
const port = 5931;
mkdirSync(join(root, '.pymagic'), { recursive: true });
writeFileSync(join(root, '.pymagic', 'run-configurations.json'), JSON.stringify({ configurations: [{ name: 'Page', kind: 'browser', url: `http://localhost:${port}/`, script: '', args: '', cwd: '', env: {} }], selected: 'Page' }, null, 2));
// the dev server, as `npm run dev` runs it
const vite = spawn(process.execPath, [join(root, 'node_modules', 'vite', 'bin', 'vite.js'), '--port', String(port), '--strictPort'], { cwd: root, stdio: 'ignore' });
for (let i = 0; i < 120; i++) { if (await fetch(`http://localhost:${port}/`).then((r) => r.ok).catch(() => false)) break; await sleep(500); }

const app = await launch(root);
const { page } = app;
try {
    await page.evaluate(`window.pymagic.openFile('src/App.tsx')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('App.tsx')`, 20000, 'App.tsx');
    await sleep(1500);
    const line = await page.evaluate(`window.pymagic.editor.getModel().getLinesContent().findIndex((l) => l.includes('useState(0)')) + 1`);
    assert.ok(line > 0, 'the useState line');
    const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.revealLine(${line}); const box = ed.getContainerDomNode().getBoundingClientRect();
        return { x: box.left + ed.getLayoutInfo().glyphMarginLeft + 5, y: box.top + ed.getTopForLineNumber(${line}) - ed.getScrollTop() + 8 }; })()`);
    await page.click(at.x, at.y);
    await sleep(300);
    await page.evaluate(`${$('b-debug')}.click()`);
    const stop = await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 90000, 'the stop in the browser');
    console.log('stopped:', stop, '— output:', JSON.stringify((await page.evaluate(`${$('output')}.textContent`)).trim().split('\n').slice(0, 2)));
    assert.equal(stop, `App src/App.tsx:${line}`);
    // a step over the useState line, then `count` in the value popup
    await page.evaluate(`${$('b-over')}.click()`);
    await page.waitFor(`!${$('b-over')}.disabled && !/:${line}$/.test(document.querySelector('#frames div.sel')?.textContent ?? '') && document.querySelector('#frames div.sel').textContent`, 20000, 'the step');
    const p = await page.evaluate(`(() => { const ed = window.pymagic.editor; const col = ed.getModel().getLineContent(${line}).indexOf('count') + 2;
        const q = ed.getScrolledVisiblePosition({ lineNumber: ${line}, column: col }); const box = ed.getContainerDomNode().getBoundingClientRect(); return { x: box.left + q.left, y: box.top + q.top + q.height / 2 }; })()`);
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: p.x - 40, y: p.y + 60 });
    await sleep(100);
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: p.x, y: p.y });
    const popup = await page.waitFor(`document.querySelector('.value-popup li > div')?.textContent.replace(/\\s+/g, ' ').trim()`, 15000, 'the value popup');
    console.log('value popup:', popup);
    assert.match(popup, /count = 0 number/);
    await page.key(27, 'Escape');
    // Resume: the page goes on; Stop closes the browser and ends the run
    await page.evaluate(`${$('b-debug')}.click()`);
    await sleep(1000);
    await page.evaluate(`${$('b-stop')}.click()`);
    await page.waitFor(`/exit code/.test(${$('output')}.textContent)`, 30000, 'the end');
    console.log('JavaScript Debug of a Vite app: the stop in App.tsx, a step, the value popup, Resume and Stop: OK');
    console.log('OK');
} finally {
    // the dev server first, so its folder can be removed with the project
    spawnSync('taskkill', ['/PID', String(vite.pid), '/T', '/F'], { stdio: 'ignore' });
    await sleep(800);
    await app.close();
    removeTree(parent);
}

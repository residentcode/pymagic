// JavaScript debugging beyond a program PyMagic starts, in the built PyMagic.exe (Node and Edge or Chrome, nothing downloaded):
// an Attach to Node.js configuration joining a program started with --inspect (a margin breakpoint stops it, Stop lets it go on), Run > Attach
// to process… listing a Node program started without the inspector and attaching to it, and JavaScript Debug of a page served here stopping in
// its worker and in its frame of another site. Run: node desktop/e2e/jsattach.check.mjs
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;
async function clickMargin(page, line) {
    const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.revealLine(${line}); const box = ed.getContainerDomNode().getBoundingClientRect();
        return { x: box.left + ed.getLayoutInfo().glyphMarginLeft + 5, y: box.top + ed.getTopForLineNumber(${line}) - ed.getScrollTop() + 8 }; })()`);
    await page.click(at.x, at.y);
    await sleep(300);
    return page.evaluate(`window.pymagic.editor.getModel().getLineDecorations(${line}).some((d) => d.options.glyphMarginClassName?.startsWith('bp-glyph'))`);
}
const stopText = (page, ms = 60000) => page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, ms, 'a stop');
const output = (page) => page.evaluate(`${$('output')}.textContent`);
const counter = 'let count = 0;\nfunction tick() {\n    count += 1;\n    return count;\n}\nsetInterval(tick, 50);\n';

const root = project({
    'counter.js': counter,
    'site/index.html': '<!doctype html><html><body><h1>main</h1><script src="main.js"></script></body></html>',
    'site/main.js': "const worker = new Worker('worker.js');\nconst frame = document.createElement('iframe');\nframe.src = location.href.replace('127.0.0.1', 'localhost').replace('index.html', 'frame.html');\ndocument.body.appendChild(frame);\n",
    'site/worker.js': 'let total = 0;\nfor (let i = 1; i <= 3; i++) {\n    total += i;\n}\npostMessage(total);\n',
    'site/frame.html': '<!doctype html><html><body><script src="frame.js"></script></body></html>',
    'site/frame.js': "const where = location.hostname;\ndocument.body.textContent = 'frame on ' + where;\n",
});
// the program to attach to, running on its own with its inspector on a port of its own
const inspected = spawn(process.execPath, ['--inspect=127.0.0.1:9341', 'counter.js'], { cwd: root, stdio: 'ignore' });
// one without its inspector, for Attach to process (its inspector opened on 9229)
const plain = spawn(process.execPath, ['counter.js'], { cwd: root, stdio: 'ignore' });
// the page's site, served here: 127.0.0.1 and localhost are two sites, so the frame runs in a process of its own
const site = createServer((req, res) => {
    try { const body = readFileSync(join(root, 'site', new URL(req.url, 'http://x').pathname)); res.writeHead(200, { 'Content-Type': req.url.endsWith('.js') ? 'text/javascript' : 'text/html' }); res.end(body); }
    catch { res.writeHead(404); res.end(); }
});
await new Promise((ok) => site.listen(0, ok));
const sitePort = site.address().port;
mkdirSync(join(root, '.pymagic'), { recursive: true });
writeFileSync(join(root, '.pymagic', 'run-configurations.json'), JSON.stringify({ configurations: [
    { name: 'Attach', kind: 'nodeAttach', host: '127.0.0.1', port: '9341', script: '', args: '', cwd: '', env: {} },
    { name: 'Page', kind: 'browser', url: `http://127.0.0.1:${sitePort}/index.html`, packageDir: 'site', script: '', args: '', cwd: '', env: {} },
], selected: 'Attach' }, null, 2));

const app = await launch(root);
const { page } = app;
try {
    // Attach to Node.js: a margin breakpoint in tick(), then Debug of the configuration joins the program and stops there
    await page.evaluate(`window.pymagic.openFile('counter.js')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('counter.js')`, 20000, 'counter.js');
    assert.equal(await clickMargin(page, 4), true, 'the breakpoint');
    await page.evaluate(`${$('b-debug')}.click()`);
    const stop = await stopText(page);
    console.log('attached and stopped:', stop, '|', (await output(page)).trim().split('\n').slice(-1)[0]);
    assert.match(stop, /counter\.js:4\b/);
    await page.evaluate(`${$('b-stop')}.click()`);
    await page.waitFor(`${$('output')}.textContent.includes('Detached: the program goes on.')`, 30000, 'the detach');
    await sleep(500);
    assert.equal(inspected.exitCode, null, 'the program goes on after Stop');
    console.log('after Stop the program still runs:', inspected.exitCode === null);

    // Run > Attach to process…: the Node program without its inspector is listed, and chosen: its inspector opened, it stops at the breakpoint
    const items = `[...document.querySelectorAll('.ctx-menu .ctx-item')]`;
    await page.evaluate(`document.querySelector('#brand .mark').dispatchEvent(new MouseEvent('click', { bubbles: true }))`);
    await page.waitFor(`${items}.some((d) => d.textContent.trim() === 'Run')`, 5000, 'the main menu');
    await page.evaluate(`${items}.find((d) => d.textContent.trim() === 'Run').click()`);
    await page.waitFor(`${items}.some((d) => d.textContent.includes('Attach to process'))`, 5000, 'the Run menu');
    await page.evaluate(`${items}.find((d) => d.textContent.includes('Attach to process')).click()`);
    const listed = await page.waitFor(`[...document.querySelectorAll('.attach-row')].map((r) => r.textContent).find((t) => t.startsWith('${plain.pid} '))`, 30000, 'the Node program in Attach to Process');
    console.log('Attach to Process lists:', listed);
    assert.match(listed, /node — counter\.js/);
    await page.evaluate(`[...document.querySelectorAll('.attach-row')].find((r) => r.textContent.startsWith('${plain.pid} ')).click()`);
    const stop2 = await stopText(page);
    console.log('attached by its process id:', stop2, '|', (await output(page)).split('\n').find((l) => l.includes('opened its inspector')));
    assert.match(stop2, /counter\.js:4\b/);
    await page.evaluate(`${$('b-stop')}.click()`);
    await page.waitFor(`${$('output')}.textContent.includes('Detached: the program goes on.')`, 30000, 'the detach');
    assert.equal(await clickMargin(page, 4), false, 'the breakpoint off');

    // JavaScript Debug: the page's worker and its frame of another site stop at their breakpoints, each named by what it is
    for (const [file, line] of [['site/worker.js', 5], ['site/frame.js', 2]]) {
        await page.evaluate(`window.pymagic.openFile('${file}')`);
        await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('${file.split('/').pop()}')`, 20000, file);
        assert.equal(await clickMargin(page, line), true, `the breakpoint of ${file}`);
    }
    await page.evaluate(`${$('run-config')}.click()`);
    await page.waitFor(`${items}.some((r) => r.textContent.trim() === 'Page')`, 10000, 'the configurations');
    await page.evaluate(`${items}.find((r) => r.textContent.trim() === 'Page').click()`);
    await sleep(300);
    await page.evaluate(`${$('b-debug')}.click()`);
    const seen = [];
    for (let i = 0; i < 2; i++) {
        const s = await stopText(page, 90000);
        const thread = await page.evaluate(`document.querySelector('#threads, #thread-name, .thread-name')?.textContent ?? ''`);
        seen.push(s);
        console.log('stopped in the browser:', s, thread ? `(${thread})` : '');
        await page.evaluate(`${$('b-debug')}.click()`);
        await sleep(800);
    }
    assert.ok(seen.some((s) => /worker\.js:5\b/.test(s)) && seen.some((s) => /frame\.js:2\b/.test(s)), seen.join(' | '));
    await page.evaluate(`${$('b-stop')}.click()`);
    await page.waitFor(`/exit code/.test(${$('output')}.textContent.split('Running').pop()) || !${$('b-stop')}.offsetParent || ${$('b-stop')}.disabled`, 30000, 'Stop');
    console.log('OK');
} finally {
    // the programs first: they run in the project's folder, which the end removes
    inspected.kill();
    plain.kill();
    site.close();
    await sleep(500);
    await app.close();
}

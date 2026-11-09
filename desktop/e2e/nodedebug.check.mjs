// JavaScript run and debugged in the built PyMagic.exe, as a person does it: a Node.js file run (by Node, not Python), then debugged with a
// breakpoint set in the margin (the stop, its frames, the Variables pane, the value popup of an object, a step, the end); and an Express app
// debugged through an npm run configuration, stopping in a route handler when a request comes. Run: node desktop/e2e/nodedebug.check.mjs
// (needs Node and npm, and the internet once for Express)
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
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

// ------------------------------------------------------------ a Node.js file
{
    const root = project({
        'order.js': [
            "const prices = [10, 20, 12];",
            "function total(items, tax) {",
            "  let sum = 0;",
            "  for (const p of items) sum += p;",
            "  const withTax = sum * (1 + tax);",
            "  return withTax;",
            "}",
            "const order = { id: 7, customer: { name: 'Ada', tags: ['vip'] } };",
            "const result = total(prices, 0.5);",
            "console.log('result', result, order.id);",
            '',
        ].join('\n'),
    });
    const app = await launch(root);
    const { page } = app;
    try {
        await page.evaluate(`window.pymagic.openFile('order.js')`);
        await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('order.js')`, 20000, 'order.js');
        await sleep(1000);
        // Run: Node runs it
        await page.evaluate(`${$('b-run')}.click()`);
        const ran = await page.waitFor(`(() => { const t = ${$('output')}.textContent; return /exit code \\d/.test(t) ? t : null; })()`, 30000, 'the run');
        console.log('Run:', JSON.stringify(ran.trim().split('\n').slice(-3)));
        assert.match(ran, /result 63 7/);
        assert.ok(!/SyntaxError|invalid syntax/.test(ran), 'not run by Python');
        assert.match(ran, /exit code 0/);

        // Debug with a breakpoint on `const withTax = …`
        assert.equal(await clickMargin(page, 5), true, 'the breakpoint');
        await page.evaluate(`${$('b-debug')}.click()`);
        const stop = await stopText(page);
        console.log('stopped:', stop);
        assert.match(stop, /^total order\.js:5$/);
        const frames = await page.evaluate(`[...document.querySelectorAll('#frames div')].map((d) => d.textContent)`);
        console.log('frames:', frames);
        assert.ok(frames.some((f) => /order\.js:9/.test(f)), 'the caller');
        const locals = await page.waitFor(`(() => { const t = [...document.querySelectorAll('#vars li > div')].map((d) => d.textContent.replace(/\\s+/g, ' ').trim()); return t.some((x) => x.startsWith('sum')) ? t : null; })()`, 15000, 'the Locals');
        console.log('Variables:', locals.slice(0, 6));
        assert.ok(locals.some((x) => /^▸? ?sum = 42 number/.test(x.replace('▸', '').trim()) || /sum = 42/.test(x)), 'sum = 42');
        assert.ok(locals.some((x) => /items = \[10, 20, 12\]/.test(x)), 'items');
        // the value popup on `items`
        const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; const col = ed.getModel().getLineContent(4).indexOf('items') + 3;
            const p = ed.getScrolledVisiblePosition({ lineNumber: 4, column: col }); const box = ed.getContainerDomNode().getBoundingClientRect(); return { x: box.left + p.left, y: box.top + p.top + p.height / 2 }; })()`);
        await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: at.x - 40, y: at.y + 60 });
        await sleep(100);
        await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: at.x, y: at.y });
        const popup = await page.waitFor(`document.querySelectorAll('.value-popup li > div').length > 3 && [...document.querySelectorAll('.value-popup li > div')].map((d) => d.textContent.replace(/\\s+/g, ' ').trim())`, 15000, 'the value popup');
        console.log('value popup of items:', popup.slice(0, 5));
        assert.match(popup[0], /items = \[10, 20, 12\] Array/);
        assert.ok(popup.some((r) => /^0 = 10 number/.test(r.replace(/^[▸▾]\s*/, ''))), 'its first item');
        await page.key(27, 'Escape');
        // a step, and the end
        await page.evaluate(`${$('b-over')}.click()`);
        assert.match(await page.waitFor(`!${$('b-over')}.disabled && /order\\.js:6$/.test(document.querySelector('#frames div.sel')?.textContent ?? '') && document.querySelector('#frames div.sel').textContent`, 20000, 'the step'), /order\.js:6/);
        await page.evaluate(`${$('b-debug')}.click()`);
        const end = await page.waitFor(`(() => { const t = ${$('output')}.textContent; return /exit code 0/.test(t) ? t : null; })()`, 30000, 'the end');
        assert.match(end, /result 63 7/);
        assert.ok(!/Debugger listening|Debugger attached/.test(end), 'no inspector lines in the output');
        console.log('Node.js file: Run, Debug, the panes, the value popup and a step: OK');
    } finally {
        await app.close();
    }
}

// ------------------------------------------------------------ an Express app through an npm run configuration
{
    const root = project({
        'package.json': JSON.stringify({ name: 'shop', version: '1.0.0', private: true, scripts: { start: 'node app.js' } }, null, 2) + '\n',
        'app.js': [
            "const express = require('express');",
            "const app = express();",
            "app.get('/items/:id', (req, res) => {",
            "  const item = { id: Number(req.params.id), name: 'box', tags: req.query.tag ?? [] };",
            "  res.json(item);",
            "});",
            "const server = app.listen(0, '127.0.0.1', () => console.log('listening on', server.address().port));",
            '',
        ].join('\n'),
    });
    const npm = spawnSync(process.platform === 'win32' ? 'npm.cmd' : 'npm', ['install', '--silent', '--no-audit', '--no-fund', 'express@4'], { cwd: root, encoding: 'utf8', shell: true });
    assert.equal(npm.status, 0, 'npm install express: ' + npm.stderr);
    mkdirSync(join(root, '.pymagic'), { recursive: true });
    writeFileSync(join(root, '.pymagic', 'run-configurations.json'), JSON.stringify({ configurations: [{ name: 'Server', kind: 'npm', npmScript: 'start', script: '', args: '', cwd: '', env: {} }], selected: 'Server' }, null, 2));
    const app = await launch(root);
    const { page } = app;
    try {
        await page.evaluate(`window.pymagic.openFile('app.js')`);
        await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('app.js')`, 20000, 'app.js');
        await sleep(1500);
        assert.equal(await clickMargin(page, 5), true, 'a breakpoint in the handler');
        await page.evaluate(`${$('b-debug')}.click()`);
        const listening = await page.waitFor(`(() => { const m = /listening on (\\d+)/.exec(${$('output')}.textContent); return m ? Number(m[1]) : null; })()`, 60000, 'the server')
            .catch(async (e) => { console.log('the Run panel said:', JSON.stringify(await output(page))); throw e; });
        console.log('Express is listening on', listening, '— output:', JSON.stringify((await output(page)).trim().split('\n').slice(0, 4)));
        // a request: the handler stops at the breakpoint
        const answer = fetch(`http://127.0.0.1:${listening}/items/42?tag=new`).then((r) => r.json());
        const stop = await stopText(page);
        console.log('stopped:', stop);
        assert.match(stop, /app\.js:5$/);
        const item = await page.evaluate(`(async () => { const r = await fetch('/api/info'); return true; })()`);
        assert.ok(item);
        const vars = await page.waitFor(`(() => { const t = [...document.querySelectorAll('#vars li > div')].map((d) => d.textContent.replace(/\\s+/g, ' ').trim()); return t.some((x) => /item = /.test(x)) ? t : null; })()`, 15000, 'the Locals');
        const itemRow = vars.find((x) => /item = /.test(x));
        console.log('item:', itemRow);
        assert.match(itemRow, /item = \{id: 42, name: "box", tags: "new"\} Object/);
        await page.evaluate(`${$('b-debug')}.click()`);   // Resume
        const json = await answer;
        console.log('the answer after Resume:', JSON.stringify(json));
        assert.deepEqual(json, { id: 42, name: 'box', tags: 'new' });
        await page.evaluate(`${$('b-stop')}.click()`);
        await page.waitFor(`/exit code/.test(${$('output')}.textContent)`, 20000, 'Stop');
        console.log('Express through npm start: the stop in the handler and Resume: OK');

        // package.json: ▶ Run and Debug above each script; ▶ Run starts npm run start in the Run panel
        await page.evaluate(`window.pymagic.openFile('package.json')`);
        const lenses = await page.waitFor(`(() => { const t = [...document.querySelectorAll('.codelens-decoration a')].map((a) => a.textContent.trim()); return t.length ? t : null; })()`, 20000, 'the lenses');
        console.log('package.json lenses:', lenses);
        assert.deepEqual(lenses, ['▶ Run', 'Debug']);
        // a click as a person makes it (Monaco's lenses answer the mouse, not an element's click())
        const lens = await page.evaluate(`(() => { ${$('output')}.textContent = ''; const r = [...document.querySelectorAll('.codelens-decoration a')].find((a) => a.textContent.trim() === '▶ Run').getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
        await page.click(lens.x, lens.y);
        const again = await page.waitFor(`(() => { const m = /listening on (\\d+)/.exec(${$('output')}.textContent); return m ? ${$('output')}.textContent : null; })()`, 60000, 'npm start from the lens');
        console.log('▶ Run of the start script:', JSON.stringify(again.trim().split('\n').slice(0, 4)));
        assert.match(again, /> node app\.js/);
        await page.evaluate(`${$('b-stop')}.click()`);
        await page.waitFor(`/exit code/.test(${$('output')}.textContent)`, 20000, 'Stop');
    } finally {
        await app.close();
    }
}
console.log('OK');

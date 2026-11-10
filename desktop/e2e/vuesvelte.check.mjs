// JavaScript Debug of a Vue and a Svelte app in the built PyMagic.exe (each made by Vite's own generator, its dev server running): a margin
// breakpoint in the component's <script> stops the page on the .vue / .svelte line, then, while stopped, a line is added above it and saved:
// the breakpoint moves with its line, Resume lets Vite's hot reload run the new component, and it stops again on the moved line.
// Run: node desktop/e2e/vuesvelte.check.mjs   (needs npm, the internet once for Vite, and Edge or Chrome)
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { launch, removeTree, sleep } from './app.mjs';

const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;
const apps = [
    { template: 'vue', file: 'src/components/HelloWorld.vue', after: 'const count = ref(0)', add: "const label = 'clicks: ' + count.value", port: 5932 },
    { template: 'svelte', file: 'src/lib/Counter.svelte', after: 'let count = $state(0)', add: "  const label = 'clicks: ' + count", port: 5933 },
];
const stopText = (page, not = null, ms = 90000) => page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent${not ? ` !== ${JSON.stringify(not)} && document.querySelector('#frames div.sel').textContent` : ''}`, ms, 'a stop');
const parent = mkdtempSync(join(tmpdir(), 'pymagic-e2e-vue-'));
let failed = false;
for (const a of apps.filter((x) => !process.env.ONLY || x.template === process.env.ONLY)) {
    const made = spawnSync('npm', ['create', '--yes', 'vite@latest', a.template + 'app', '--', '--template', a.template, '--no-interactive'], { cwd: parent, encoding: 'utf8', shell: true });
    assert.equal(made.status, 0, 'npm create vite: ' + made.stderr);
    const root = join(parent, a.template + 'app');
    assert.equal(spawnSync('npm', ['install', '--no-audit', '--no-fund'], { cwd: root, encoding: 'utf8', shell: true }).status, 0, 'npm install');
    // a line of the component's script that runs each time the component is made
    const text = readFileSync(join(root, a.file), 'utf8');
    assert.ok(text.includes(a.after), a.after);
    writeFileSync(join(root, a.file), text.replace(a.after, a.after + '\n' + a.add));
    const line = text.slice(0, text.indexOf(a.after)).split('\n').length + 1;
    mkdirSync(join(root, '.pymagic'), { recursive: true });
    writeFileSync(join(root, '.pymagic', 'run-configurations.json'), JSON.stringify({ configurations: [{ name: 'Page', kind: 'browser', url: `http://localhost:${a.port}/`, script: '', args: '', cwd: '', env: {} }], selected: 'Page' }, null, 2));
    const vite = spawn(process.execPath, [join(root, 'node_modules', 'vite', 'bin', 'vite.js'), '--port', String(a.port), '--strictPort'], { cwd: root, stdio: 'ignore' });
    for (let i = 0; i < 120; i++) { if (await fetch(`http://localhost:${a.port}/`).then((r) => r.ok).catch(() => false)) break; await sleep(500); }

    const app = await launch(root);
    const { page } = app;
    try {
        const name = a.file.split('/').pop();
        await page.evaluate(`window.pymagic.openFile('${a.file}')`);
        await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('${name}')`, 20000, name);
        await sleep(1500);
        const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.revealLine(${line}); const box = ed.getContainerDomNode().getBoundingClientRect();
            return { x: box.left + ed.getLayoutInfo().glyphMarginLeft + 5, y: box.top + ed.getTopForLineNumber(${line}) - ed.getScrollTop() + 8 }; })()`);
        await page.click(at.x, at.y);
        await sleep(300);
        await page.evaluate(`${$('b-debug')}.click()`);
        const stop = await stopText(page);
        console.log(`${a.template}: stopped:`, stop);
        assert.match(stop, new RegExp(`${name.replace('.', '\\.')}:${line}$`));

        // while stopped: a line added above the breakpoint and saved; the breakpoint goes down with its line
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.focus(); ed.executeEdits('check', [{ range: new window.pymagic.monaco.Range(${line}, 1, ${line}, 1), text: ${JSON.stringify(a.add.match(/^\s*/)[0] + 'const before = 1\n')} }]); })()`);
        await page.evaluate(`window.pymagic.runCommand('pymagic.file.save')`);
        await page.waitFor(`!document.querySelector('.tab.active, .tab.sel')?.classList.contains('dirty')`, 10000, 'the save').catch(() => {});
        await sleep(800);
        const moved = await page.evaluate(`window.pymagic.editor.getModel().getLineDecorations(${line + 1}).some((d) => d.options.glyphMarginClassName?.startsWith('bp-glyph'))`);
        console.log(`${a.template}: the breakpoint moved to line ${line + 1}:`, moved, '| on disk:', readFileSync(join(root, a.file), 'utf8').includes('const before = 1'));
        assert.equal(moved, true, 'the breakpoint on the moved line');
        // Resume: Vite's hot reload runs the new component, which stops on the moved line
        await page.evaluate(`${$('b-debug')}.click()`);
        const again = await stopText(page, stop, 60000);
        console.log(`${a.template}: after the hot reload:`, again);
        assert.match(again, new RegExp(`${name.replace('.', '\\.')}:${line + 1}$`));
        await page.evaluate(`${$('b-debug')}.click()`);
        await sleep(1000);
        await page.evaluate(`${$('b-stop')}.click()`);
        await page.waitFor(`/exit code/.test(${$('output')}.textContent)`, 30000, 'the end');
        console.log(`${a.template}: OK`);
    } catch (e) {
        failed = true;
        console.log(`${a.template}: FAILED`, e.message, '\n', (await page.evaluate(`${$('output')}.textContent`).catch(() => '')).slice(-1500));
    } finally {
        spawnSync('taskkill', ['/PID', String(vite.pid), '/T', '/F'], { stdio: 'ignore' });
        await sleep(800);
        await app.close();
    }
}
removeTree(parent);
if (failed) process.exit(1);
console.log('OK');

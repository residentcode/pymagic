// Breakpoints in server-side code under Vite's dev server, in the built PyMagic.exe: SvelteKit's +page.server.ts load and an Astro page's
// frontmatter, debugged through an npm run configuration of the project's dev script; a request stops on the file's own line (Vite runs a
// changed copy of it under the file's name, with an inline map naming the file beside it), the Variables are the function's, the line is
// marked in the editor, and Resume answers the request. The real projects (made by their own tools, see realprojects.check.mjs) are left as
// they were. Run: PYMAGIC_REAL_DIR=<folder with svelte-app and astro-app, packages installed> node desktop/e2e/ssrdebug.check.mjs
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, rmSync, mkdirSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { createServer } from 'node:net';
import { launch, sleep } from './app.mjs';

const base = process.env.PYMAGIC_REAL_DIR;
assert.ok(base && existsSync(join(base, 'svelte-app', 'node_modules')), 'PYMAGIC_REAL_DIR with svelte-app and astro-app, packages installed');
const free = () => new Promise((ok) => { const s = createServer(); s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => ok(p)); }); });
const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;

async function probe(name, file, text, line, script, path, wantFrame, wantVar) {
    const root = join(base, name);
    const pkgPath = join(root, 'package.json');
    const pkg = readFileSync(pkgPath, 'utf8');
    const cfgPath = join(root, '.pymagic', 'run-configurations.json');
    const hadCfg = existsSync(cfgPath) ? readFileSync(cfgPath, 'utf8') : null;
    const port = await free();
    const json = JSON.parse(pkg);
    json.scripts['dev:pm'] = script.replace('PORT', port);
    writeFileSync(pkgPath, JSON.stringify(json, null, 2) + '\n');
    mkdirSync(dirname(join(root, file)), { recursive: true });
    writeFileSync(join(root, file), text);
    mkdirSync(join(root, '.pymagic'), { recursive: true });
    writeFileSync(cfgPath, JSON.stringify({ configurations: [{ name: 'Dev', kind: 'npm', npmScript: 'dev:pm', script: '', args: '', cwd: '', env: {} }], selected: 'Dev' }, null, 2));
    console.log(`== ${name}: ${file}:${line}`);
    const app = await launch(root, null, null, { keep: true });
    const { page } = app;
    try {
        await page.evaluate(`window.pymagic.openFile(${JSON.stringify(file)})`);
        await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith(${JSON.stringify(file.split('/').pop())})`, 20000, file);
        await sleep(1500);
        const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; const box = ed.getContainerDomNode().getBoundingClientRect();
            return { x: box.left + ed.getLayoutInfo().glyphMarginLeft + 5, y: box.top + ed.getTopForLineNumber(${line}) - ed.getScrollTop() + 8 }; })()`);
        await page.click(at.x, at.y);
        await sleep(400);
        await page.evaluate(`${$('b-debug')}.click()`);
        const url = await page.waitFor(`(() => { const m = /(?:Local:?)\\s+(http:\\/\\/localhost:\\d+)/.exec(${$('output')}.textContent); return m ? m[1] : null; })()`, 120000, 'the dev server').catch(async (e) => { console.log('output:', await page.evaluate(`${$('output')}.textContent.slice(-1500)`)); throw e; });
        console.log('dev server up; output:', JSON.stringify((await page.evaluate(`${$('output')}.textContent`)).slice(0, 400)));
        const answer = fetch(`${url}${path}`).then(async (r) => `${r.status} ${(await r.text()).slice(0, 2000)}`).catch((e) => 'fetch failed: ' + e.message);
        const stop = await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 60000, 'the stop').catch(() => null);
        console.log('stopped:', stop);
        assert.ok(stop, 'a stop');
        assert.match(stop, wantFrame);
        {
            await page.evaluate(`${$('t-debug')}?.click()`);
            await sleep(1500);
            console.log('frames:', await page.evaluate(`[...document.querySelectorAll('#frames div.frame')].slice(0, 5).map((d) => d.textContent.trim().replace(/\\s+/g, ' '))`));
            const vars = await page.evaluate(`[...document.querySelectorAll('#vars li > div')].slice(0, 10).map((d) => d.textContent.trim().replace(/\\s+/g, ' ').slice(0, 100))`);
            console.log('variables:', vars);
            assert.ok(vars.some((v) => v.includes(wantVar)), vars.join(' | '));
            const marked = await page.evaluate(`window.pymagic.editor.getModel().uri.path.endsWith(${JSON.stringify(file.split('/').pop())}) ? window.pymagic.editor.getModel().getAllDecorations().filter((d) => d.options.className === 'debug-line').map((d) => d.range.startLineNumber) : []`);
            console.log('marked in the editor:', marked);
            assert.deepEqual(marked, [line]);
            await page.evaluate(`${$('b-debug')}.click()`);
        }
        const got = await Promise.race([answer, sleep(30000).then(() => 'no answer in 30s')]);
        console.log('answer:', got.slice(0, 60).replace(/\s+/g, ' '));
        assert.match(got, /^200 /);
        await page.evaluate(`${$('b-stop')}.click()`);
        await sleep(2000);
    } finally {
        await app.close();
        writeFileSync(pkgPath, pkg);
        rmSync(join(root, file), { force: true });
        if (hadCfg === null) rmSync(cfgPath, { force: true }); else writeFileSync(cfgPath, hadCfg);
    }
}

const which = process.argv[2] ?? 'both';
if (which !== 'astro')
    await probe('svelte-app', 'src/routes/dbg/+page.server.ts',
        "import type { PageServerLoad } from './$types';\n\nexport const load: PageServerLoad = async () => {\n  const items = [{ id: 1, name: 'pen' }];\n  const total = items.length;\n  return { total, label: 'probe-value' };\n};\n",
        5, 'vite dev --port PORT --strictPort', '/dbg', /^load .*\+page\.server\.ts:5$/, 'items = ');
if (which !== 'svelte')
    await probe('astro-app', 'src/pages/dbg.astro',
        "---\nconst items = [{ id: 1, name: 'pen' }];\nconst total = items.length;\nconst label = 'probe-value';\n---\n<p>{label} {total}</p>\n",
        3, 'astro dev --port PORT', '/dbg', /dbg\.astro:3$/, 'items = ');
console.log('OK');

// The welcome screen of the built PyMagic.exe (after Close project): its pages (Projects, Plugins, Learn), Plugins (Settings at its Plugins
// page, Done back to the welcome screen), and Get from VCS (an address git does not take refused, then a small public repository cloned and
// opened in this window). Run: node desktop/e2e/welcome.check.mjs   (needs git and the internet for github.com/octocat/Hello-World)
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { launch, project, removeTree, sleep } from './app.mjs';

const root = project({ 'a.py': 'x = 1\n' });
const clone = root + '-clone';
const app = await launch(root);
const { page } = app;

/** A page of this PyMagic whose address passes `test`, to run code in. */
async function target(test, what) {
    for (let i = 0; i < 120; i++) {
        const list = await (await fetch(`http://127.0.0.1:${app.port}/json`)).json().catch(() => []);
        const t = list.find((x) => x.type === 'page' && test(x.url));
        if (t) {
            const s = new WebSocket(t.webSocketDebuggerUrl);
            await new Promise((ok, fail) => { s.addEventListener('open', ok); s.addEventListener('error', fail); });
            let k = 0;
            const run = (expression) => new Promise((ok) => {
                const id = ++k;
                const on = (e) => { const m = JSON.parse(e.data); if (m.id === id) { s.removeEventListener('message', on); ok(m.result?.result?.value); } };
                s.addEventListener('message', on);
                s.send(JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, awaitPromise: true, returnByValue: true } }));
            });
            const fire = (expression) => s.send(JSON.stringify({ id: 9000 + ++k, method: 'Runtime.evaluate', params: { expression } }));
            const until = async (expression, label, tries = 160) => { for (let j = 0; j < tries; j++) { const v = await run(expression).catch(() => null); if (v) return v; await sleep(250); } throw new Error('timed out: ' + label); };
            return { run, fire, until, close: () => s.close() };
        }
        await sleep(500);
    }
    throw new Error('no page: ' + what);
}
const welcomeAt = (u) => u.startsWith('file:') && u.endsWith('welcome.html');

try {
    await page.waitFor(`!!window.pymagic?.closeProject`, 20000, 'the page');
    page.ws.send(JSON.stringify({ id: 99001, method: 'Runtime.evaluate', params: { expression: 'window.pymagic.closeProject()' } }));
    let w = await target(welcomeAt, 'the welcome screen');
    assert.deepEqual(await w.until(`(() => { const b = [...document.querySelectorAll('nav button')].map((x) => x.textContent); return b.length ? b : null; })()`, 'the pages'), ['Projects', 'Customize', 'Plugins', 'Learn']);
    assert.deepEqual(await w.run(`[...document.querySelectorAll('.actions button')].map((x) => x.textContent)`), ['New project…', 'Open folder…', 'Get from VCS…']);
    console.log('the welcome screen: Projects, Customize, Plugins, Learn; New project, Open folder, Get from VCS');

    // Learn
    await w.run(`document.querySelector('nav button[data-page="learn"]').click()`);
    const learn = await w.run(`[...document.querySelectorAll('#learn li .name')].map((x) => x.textContent)`);
    console.log('Learn:', learn);
    assert.equal(learn.length, 4);
    assert.equal(await w.run(`document.getElementById('learn').hidden || document.getElementById('projects').hidden === false`), false, 'only Learn shown');

    // Plugins: Settings at its Plugins page, and Done back to the welcome screen
    await w.run(`document.querySelector('nav button[data-page="plugins"]').click()`);
    w.fire(`document.getElementById('plugins-open').click()`);
    w.close();
    const settings = await target((u) => /\/welcome-tool\.html\?tool=plugins/.test(u), 'Settings');
    const heading = await settings.until(`document.querySelector('.settings-page h2')?.textContent`, 'the Plugins page');
    const rows = await settings.until(`document.querySelectorAll('.settings-page input[type=checkbox]').length`, 'the plugins listed');
    console.log(`Plugins from the welcome screen: "${heading}", ${rows} plugins listed`);
    assert.equal(heading, 'Plugins');
    assert.ok(rows >= 10);
    settings.fire(`[...document.querySelectorAll('.settings-foot button')].find((b) => b.textContent === 'Done').click()`);
    settings.close();
    w = await target(welcomeAt, 'the welcome screen after Done');
    await w.until(`document.querySelectorAll('nav button').length`, 'the welcome screen');
    console.log('Done went back to the welcome screen');

    // Get from VCS: the folder suggested beside the last project, named after the repository; an address git is not given refused
    await w.run(`document.getElementById('vcs').click()`);
    await w.until(`!document.getElementById('vcs-form').hidden`, 'the form');
    await w.run(`(() => { const u = document.getElementById('vcs-url'); u.value = 'https://github.com/octocat/Hello-World.git'; u.dispatchEvent(new Event('input')); })()`);
    const suggested = await w.until(`(() => { const v = document.getElementById('vcs-dir').value; return v.endsWith('Hello-World') ? v : null; })()`, 'the suggested folder');
    console.log('Get from VCS suggests:', suggested);
    await w.run(`(() => { document.getElementById('vcs-url').value = '--upload-pack=calc'; document.getElementById('vcs-clone').click(); })()`);
    const refused = await w.until(`!document.getElementById('vcs-error').hidden && document.getElementById('vcs-error').textContent`, 'the refusal');
    console.log('an option for an address:', refused);
    assert.match(refused, /An address of a repository/);
    // the clone, opened in this window
    await w.run(`(() => { document.getElementById('vcs-url').value = 'https://github.com/octocat/Hello-World.git'; const d = document.getElementById('vcs-dir'); d.value = ${JSON.stringify(clone)}; d.dispatchEvent(new Event('input')); })()`);
    w.fire(`document.getElementById('vcs-clone').click()`);
    w.close();
    const opened = await target((u) => /^http:\/\/127\.0\.0\.1:\d+\/$/.test(u), 'the clone opened');
    const openedRoot = await opened.until(`window.pymagic?.editor && fetch('/api/info').then((r) => r.json()).then((i) => i.root)`, "the clone's engine");
    console.log('cloned and opened:', openedRoot);
    assert.equal(openedRoot.replace(/[\\/]+$/, '').toLowerCase(), clone.toLowerCase());
    assert.ok(existsSync(join(clone, 'README')) && existsSync(join(clone, '.git')), 'the repository is there');
    opened.close();
    console.log('OK');
} finally {
    await app.close();
    removeTree(clone);
}

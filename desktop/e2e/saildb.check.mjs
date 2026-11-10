// Sail's services in the built PyMagic.exe (Settings > PHP: sail), on a Sail app with MySQL and Redis that Docker Desktop runs
// (PYMAGIC_TEST_SAIL_DB_DIR: sail:add mysql,redis, its containers up): Phpactor in the app's container (completion after $user->, the model's
// methods and its columns), the .env's MySQL and Redis offered in the Database panel, Redis connected from the filled form, its keys in the
// tree, a key's value shown and changed, a hash's fields, and Redis commands in the console.
// Run: PYMAGIC_TEST_SAIL_DB_DIR=… node desktop/e2e/saildb.check.mjs
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { cpSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join } from 'node:path';
import { launch, sleep } from './app.mjs';

const source = process.env.PYMAGIC_TEST_SAIL_DB_DIR;
assert.ok(source, 'PYMAGIC_TEST_SAIL_DB_DIR');
// a copy under a folder of the same name: compose's project is the same, so its MySQL and Redis go on and the app's container is made again
// with the copy shared; at the end it is made again with the app's own folder
const root = join(mkdtempSync(join(tmpdir(), 'pymagic-e2e-saildb-')), basename(source));
cpSync(source, root, { recursive: true, filter: (f) => !/[\\/](node_modules|\.git|\.pymagic)$/.test(f) });
const composeEnv = { ...process.env, WWWUSER: '1000', WWWGROUP: '1000' };
execFileSync('docker', ['compose', 'up', '-d', '--force-recreate', 'laravel.test'], { cwd: root, env: composeEnv, stdio: 'ignore' });
const redisCli = (...args) => execFileSync('docker', ['compose', 'exec', '-T', 'redis', 'redis-cli', ...args], { cwd: root, env: composeEnv }).toString().trim();
redisCli('SET', 'pm:greeting', 'hello');
redisCli('HSET', 'pm:user', 'name', 'Ada', 'age', '36');
writeFileSync(join(root, 'app', 'PmTyping.php'), '<?php\n\nuse App\\Models\\User;\n\n$user = new User();\n$user->\n');
const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;
const rowsOf = `[...document.querySelectorAll('#db-tree > *')].map((r) => r.textContent.replace(/\\s+/g, ' ').trim())`;

const app = await launch(root, { phpPath: 'sail' });
const { page } = app;
try {
    // Phpactor in the container: after $user-> the model's methods (Notifiable's notify) and the users table's columns
    await page.evaluate(`window.pymagic.openFile('app/PmTyping.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('PmTyping.php')`, 20000, 'PmTyping.php');
    // (the list shows only the rows in view: a few letters typed narrow it to the ones looked for)
    async function after(typed, wanted) {
        await page.evaluate(`(() => { const ed = window.pymagic.editor; const m = ed.getModel(); ed.executeEdits('test', [{ range: new window.pymagic.monaco.Range(6, 1, 6, m.getLineMaxColumn(6)), text: '$user->${typed}' }]); ed.setPosition({ lineNumber: 6, column: ${8 + typed.length} }); ed.focus(); })()`);
        let rows = [];
        for (let i = 0; i < 60 && !rows.includes(wanted); i++) {
            await page.evaluate(`window.pymagic.editor.trigger('test', 'hideSuggestWidget', {})`);
            await page.evaluate(`window.pymagic.editor.trigger('test', 'editor.action.triggerSuggest', {})`);
            await sleep(3000);
            rows = await page.evaluate(`[...document.querySelectorAll('.suggest-widget.visible .monaco-list-row')].map((x) => x.getAttribute('aria-label')?.split(',')[0])`);
        }
        console.log(`Phpactor in the container, after $user->${typed}:`, rows.slice(0, 8));
        assert.ok(rows.includes(wanted), rows.join(','));
        await page.key(27, 'Escape');
    }
    await after('noti', 'notify');
    await after('em', 'email');
    const inContainer = execFileSync('docker', ['compose', 'exec', '-T', 'laravel.test', 'sh', '-c', 'ps -eo args | grep [p]hpactor.phar || true'], { cwd: root, env: composeEnv }).toString().trim();
    console.log('in the container:', inContainer.split('\n')[0]);
    assert.match(inContainer, /phpactor\.phar language-server/);
    await page.key(27, 'Escape');

    // the Database panel: the .env's MySQL and Redis offered, not connected yet
    await page.evaluate(`${$('t-database')}.click()`);
    const offered = await page.waitFor(`(() => { const r = ${rowsOf}; return r.some((t) => t.includes('redis')) && r.some((t) => t.includes('mysql')) ? r : null; })()`, 30000, 'the offers');
    console.log('offered:', offered);
    assert.ok(offered.some((t) => /MySQL · 127\.0\.0\.1:3306\/laravel · not connected · \.env/.test(t)), offered.join(' | '));
    // Redis from the filled form: Connect
    await page.evaluate(`[...document.querySelectorAll('#db-tree > *')].find((r) => /Redis · 127\\.0\\.0\\.1:6379/.test(r.textContent)).click()`);
    await page.waitFor(`${$('db-form-kind')}?.value === 'redis'`, 10000, 'the form');
    const form = await page.evaluate(`['host', 'port', 'user', 'database'].map((k) => k + '=' + document.getElementById('db-form-' + k).value).join(' ')`);
    console.log('the form:', form);
    assert.equal(form, 'host=127.0.0.1 port=6379 user= database=0');
    await page.evaluate(`document.querySelector('.db-server-form .ask-buttons button.primary').click()`);
    await page.waitFor(`!document.querySelector('.db-server-form')`, 30000, 'connected');
    // its keys in the tree (opened on connecting), then a pattern
    const keys = await page.waitFor(`(() => { const r = ${rowsOf}; return r.some((t) => t.startsWith('pm:greeting')) ? r : null; })()`, 30000, 'the keys');
    console.log('the tree:', keys.filter((t) => t.startsWith('pm:') || t.includes('Redis')));
    assert.ok(keys.some((t) => /^pm:user\s*hash/.test(t)) && keys.some((t) => /^pm:greeting\s*string/.test(t)), keys.join(' | '));
    await page.evaluate(`(() => { const box = document.querySelector('.db-redis-pattern'); box.value = 'pm:u*'; box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
    const filtered = await page.waitFor(`(() => { const r = ${rowsOf}.filter((t) => t.startsWith('pm:')); return r.length === 1 ? r : null; })()`, 20000, 'the pattern');
    console.log('pm:u*:', filtered);
    // the hash's fields in the grid; a field changed by a double-click
    await page.evaluate(`[...document.querySelectorAll('#db-tree > *')].find((r) => r.textContent.startsWith('pm:user')).dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))`);
    const table = await page.waitFor(`(() => { const t = document.querySelector('.db-redis-table'); return t ? [...t.rows].map((r) => [...r.cells].map((c) => c.textContent).join('=')) : null; })()`, 20000, 'the hash');
    console.log('pm:user:', table, '|', await page.evaluate(`document.querySelector('.db-redis-head').textContent`));
    assert.ok(table.includes('name=Ada=✕') && table.includes('age=36=✕'), table.join(' | '));
    // the string shown and changed: Save
    await page.evaluate(`(() => { const box = document.querySelector('.db-redis-pattern'); box.value = ''; box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
    await page.waitFor(`${rowsOf}.some((t) => t.startsWith('pm:greeting'))`, 20000, 'all the keys again');
    await page.evaluate(`[...document.querySelectorAll('#db-tree > *')].find((r) => r.textContent.startsWith('pm:greeting')).dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))`);
    await page.waitFor(`document.querySelector('.db-redis-text')?.value === 'hello'`, 20000, 'the string');
    await page.evaluate(`(() => { const t = document.querySelector('.db-redis-text'); t.value = 'hello from PyMagic'; [...document.querySelectorAll('#db-grid button')].find((b) => b.textContent === 'Save').click(); })()`);
    await sleep(1500);
    assert.equal(redisCli('GET', 'pm:greeting'), 'hello from PyMagic');
    console.log('pm:greeting is now:', redisCli('GET', 'pm:greeting'));

    // the console: Redis commands, one per line, answered as redis-cli answers them
    await page.evaluate(`${$('db-mode-console')}.click()`);
    await page.evaluate(`(() => { const box = ${$('db-source')}; box.value = [...box.options].find((o) => o.textContent.includes('Redis')).value; })()`);
    await page.evaluate(`(() => { const ed = window.pymagic.monaco.editor.getEditors().find((e) => e.getContainerDomNode().closest('#db-sql')); ed.setValue('HGETALL pm:user\\nINCR pm:count\\nNOSUCH x'); ed.setSelection(ed.getModel().getFullModelRange()); })()`);
    await page.evaluate(`${$('db-run')}.click()`);
    const replies = await page.waitFor(`document.querySelector('.db-redis-replies')?.textContent ?? null`, 20000, 'the replies');
    console.log('the console:\n' + replies);
    assert.match(replies, /> HGETALL pm:user\n1\) "name"\n2\) "Ada"\n3\) "age"\n4\) "36"\n> INCR pm:count\n\(integer\) 1\n> NOSUCH x\n\(error\) ERR unknown command/);
    console.log('OK');
} finally {
    try { redisCli('DEL', 'pm:greeting', 'pm:user', 'pm:count'); } catch { /* gone */ }
    await app.close();
    // the app's container made again with its own folder
    execFileSync('docker', ['compose', 'up', '-d', '--force-recreate', 'laravel.test'], { cwd: source, env: composeEnv, stdio: 'ignore' });
}

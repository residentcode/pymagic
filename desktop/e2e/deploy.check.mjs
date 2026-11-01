// A one-off check, not part of the suite (it needs an SFTP server): Deployment over SFTP in the built exe.
//   PYMAGIC_TEST_SFTP=host:port:user:password PYMAGIC_SFTP_ROOT=<the server's folder on disk> node desktop/e2e/deploy.check.mjs
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const [host, port, user, password] = (process.env.PYMAGIC_TEST_SFTP ?? '').split(':');
const diskRoot = process.env.PYMAGIC_SFTP_ROOT;
assert.ok(host && diskRoot, 'see the top of this file');
const app = await launch(project({ 'main.py': 'print("deployed")\n', 'pkg/util.py': 'X = 1\n' }));
const page = app.page;
const fill = (id, value) => page.evaluate(`(() => { const e = document.getElementById(${JSON.stringify(id)}); e.value = ${JSON.stringify(value)}; e.dispatchEvent(new Event('change', { bubbles: true })); })()`);
try {
    await page.evaluate(`document.getElementById('t-deployment').click()`);
    await page.evaluate(`document.getElementById('dep-add').click()`);
    await page.waitFor(`document.getElementById('dep-form-type')`, 5000, 'the form');
    await fill('dep-form-type', 'sftp');
    await fill('dep-form-host', host); await fill('dep-form-port', port); await fill('dep-form-user', user); await fill('dep-form-password', password); await fill('dep-form-root', '/site');
    await page.evaluate(`[...document.querySelectorAll('.dep-form button')].find((b) => b.textContent === 'Test and save').click()`);
    await page.waitFor(`document.querySelectorAll('#dep-server option').length === 1`, 20000, 'the SFTP server to be accepted');
    const info = await page.evaluate(`document.getElementById('dep-info').textContent`);
    console.log('info:', info);
    await page.evaluate(`window.pymagic.openFile('main.py')`);
    await sleep(800);
    await page.evaluate(`document.getElementById('dep-upload').click()`);
    for (let i = 0; i < 40 && !existsSync(join(diskRoot, 'site', 'main.py')); i++) await sleep(300);
    assert.equal(readFileSync(join(diskRoot, 'site', 'main.py'), 'utf8'), 'print("deployed")\n');
    await page.waitFor(`[...document.querySelectorAll('#dep-list .dep-name')].some((n) => n.textContent === 'main.py')`, 15000, 'the file in the server listing');
    const servers = await page.evaluate(`fetch('/api/deploy/servers').then((r) => r.json())`);
    console.log('host key learned:', servers[0].hostKey);
    assert.ok(servers[0].hostKey.length > 20, 'the host key was remembered');
    assert.equal(servers[0].hasPassword, true);
    const saved = readFileSync(join(app.root, '.pymagic', 'deployment.json'), 'utf8');
    assert.ok(!saved.includes(password), 'no password in the settings file');
    // a wrong password is told, and nothing is kept
    await page.evaluate(`document.getElementById('dep-add').click()`);
    await page.waitFor(`document.getElementById('dep-form-type')`, 5000, 'the form');
    await fill('dep-form-type', 'sftp'); await fill('dep-form-host', host); await fill('dep-form-port', port); await fill('dep-form-user', 'someone-else'); await fill('dep-form-password', 'nope'); await fill('dep-form-root', '/site');
    await page.evaluate(`[...document.querySelectorAll('.dep-form button')].find((b) => b.textContent === 'Test and save').click()`);
    await page.waitFor(`document.querySelector('.db-form-error')?.textContent.length > 12`, 20000, 'the error');
    console.log('wrong login says:', await page.evaluate(`document.querySelector('.db-form-error').textContent`));
    assert.equal(await page.evaluate(`document.querySelectorAll('#dep-server option').length`), 1);
    console.log('sftp deployment ok');
} finally { await app.close(); }

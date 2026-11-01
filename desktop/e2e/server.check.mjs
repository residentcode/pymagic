// A one-off check, not part of the suite (it needs running servers): PostgreSQL and MySQL in the built exe's Database panel.
//   PYMAGIC_CHECK_PYTHON=<python with psycopg and PyMySQL> PYMAGIC_TEST_POSTGRES=host:port:user:password:database \
//   PYMAGIC_TEST_MYSQL=host:port:user:password:database node desktop/e2e/server.check.mjs
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { dirname } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const python = process.env.PYMAGIC_CHECK_PYTHON;
const servers = { postgres: process.env.PYMAGIC_TEST_POSTGRES, mysql: process.env.PYMAGIC_TEST_MYSQL };
assert.ok(python && servers.postgres && servers.mysql, 'see the top of this file');
// the exe finds `python` first on the PATH: the one with the drivers
process.env.PATH = dirname(python) + ';' + process.env.PATH;

const made = spawnSync(python, ['-c', `
import sys, psycopg, pymysql
h, p, u, pw, d = sys.argv[1].split(':')
c = psycopg.connect(host=h, port=int(p), user=u, password=pw, dbname=d, autocommit=True)
c.execute("DROP TABLE IF EXISTS pm_check"); c.execute("CREATE TABLE pm_check (id serial PRIMARY KEY, name varchar(20), price numeric(6,2))")
c.execute("INSERT INTO pm_check (name, price) VALUES ('pen', 2), ('book', 12.5)")
h, p, u, pw, d = sys.argv[2].split(':')
m = pymysql.connect(host=h, port=int(p), user=u, password=pw, database=d, autocommit=True).cursor()
m.execute("DROP TABLE IF EXISTS pm_check"); m.execute("CREATE TABLE pm_check (id int AUTO_INCREMENT PRIMARY KEY, name varchar(20), price decimal(6,2))")
m.execute("INSERT INTO pm_check (name, price) VALUES ('pen', 2), ('book', 12.5)")
`, servers.postgres, servers.mysql], { encoding: 'utf8' });
assert.equal(made.status, 0, made.stderr);

const root = project({ 'app.py': 'print(1)\n' });
const app = await launch(root);
const page = app.page;
const grid = () => page.evaluate(`[...document.querySelectorAll('#db-grid tr')].map((tr) => [...tr.children].map((c) => c.textContent).join('|'))`);
try {
    await page.evaluate(`document.getElementById('t-database').click()`);
    for (const [kind, spec] of Object.entries(servers)) {
        const [host, port, user, password, database] = spec.split(':');
        await page.evaluate(`document.getElementById('db-add-server').click()`);
        await page.waitFor(`document.getElementById('db-form-kind')`, 5000, 'the form');
        await page.evaluate(`(() => {
            const set = (id, v) => { const e = document.getElementById('db-form-' + id); e.value = v; e.dispatchEvent(new Event('change', { bubbles: true })); };
            set('kind', ${JSON.stringify(kind)});
            set('host', ${JSON.stringify(host)}); set('port', ${JSON.stringify(port)}); set('user', ${JSON.stringify(user)});
            set('password', ${JSON.stringify(password)}); set('database', ${JSON.stringify(database)}); set('name', ${JSON.stringify('check ' + kind)});
            [...document.querySelectorAll('.db-server-form button')].find((b) => b.textContent === 'Connect').click();
        })()`);
        await page.waitFor(`[...document.querySelectorAll('#db-tree .db-server')].some((n) => n.textContent.includes(${JSON.stringify('check ' + kind)}))`, 20000, kind + ' in the tree');
        await page.waitFor(`[...document.querySelectorAll('#db-tree .db-table')].some((n) => n.querySelector('.db-name').textContent === 'pm_check')`, 20000, 'its table');
        await page.evaluate(`[...document.querySelectorAll('#db-tree .db-table')].filter((n) => n.querySelector('.db-name').textContent === 'pm_check').pop().dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))`);
        await page.waitFor(`document.getElementById('db-status').textContent.startsWith('pm_check: rows 1–2 of 2') && document.querySelectorAll('#db-grid tbody tr')[0]?.cells[3]?.textContent === '2.00'`, 15000, 'the rows of this server').catch(async (e) => { console.log(kind, 'status:', await page.evaluate(`document.getElementById('db-status').textContent`), JSON.stringify(await grid())); throw e; });
        assert.deepEqual(await grid(), ['|id|name|price', '1|1|pen|2.00', '2|2|book|12.50']);
        // the pen's price changed and submitted, then read again
        await page.evaluate(`document.querySelectorAll('#db-grid tbody tr')[0].cells[3].dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))`);
        await page.evaluate(`(() => { const i = document.querySelector('#db-grid input.db-edit'); i.value = '9'; i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
        await page.evaluate(`document.getElementById('db-submit').click()`);
        await page.waitFor(`document.querySelectorAll('#db-grid tbody tr')[0]?.cells[3]?.textContent === '9.00'`, 15000, 'the changed price');
        await sleep(300);
        console.log(kind, 'grid after submit:', JSON.stringify(await grid()), '|', await page.evaluate(`document.getElementById('db-status').textContent`));
    }
    // the settings and passwords on disk
    const fs = await import('node:fs');
    const kept = fs.readFileSync(root + '/.pymagic/db-servers.json', 'utf8');
    const priv = fs.readFileSync(root + '/.pymagic/db-servers.private.json', 'utf8');
    assert.ok(!kept.includes(servers.postgres.split(':')[3]), 'no password in the settings');
    assert.ok(priv.includes(servers.postgres.split(':')[3]));
    console.log('servers check ok');
} finally {
    await app.close();
}

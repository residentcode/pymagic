// Get from VCS's other hosts in the built PyMagic.exe (after Close project), with one stand-in server for GitHub Enterprise's, GitLab's and
// Bitbucket's APIs (nothing reaches those hosts, no account of the person's is used): GitLab refusing a wrong token, then taking one (its
// PRIVATE-TOKEN header, two pages of projects, one chosen for the clone); Bitbucket with an email and an API token (Basic, its clone address
// without the account in it); GitHub Enterprise on a server of its own (Bearer, /api/v3); the accounts still there after a restart of the
// window, and Sign out forgetting one.
// Run: node desktop/e2e/vcshosts.check.mjs
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { launch, project, sleep } from './app.mjs';

const LAB = 'glpat-' + 'L'.repeat(20), BB = 'ATATT' + 'B'.repeat(30), GHE = 'ghp_' + 'E'.repeat(36);
const seen = [];
const api = createServer((req, res) => {
  const u = new URL(req.url, 'http://x');
  seen.push(req.method + ' ' + u.pathname);
  res.setHeader('Content-Type', 'application/json');
  const no = () => { res.writeHead(401); res.end('{"message":"401 Unauthorized"}'); };
  if (u.pathname.startsWith('/lab/api/v4/')) {
    if (req.headers['private-token'] !== LAB) return no();
    if (u.pathname === '/lab/api/v4/user') return res.end(JSON.stringify({ username: 'lab-user' }));
    if (u.pathname === '/lab/api/v4/projects') {
      const page = Number(u.searchParams.get('page'));
      const p = (i) => ({ path_with_namespace: 'group/p' + i, http_url_to_repo: 'https://lab.example/group/p' + i + '.git', description: 'project ' + i, visibility: i === 0 ? 'private' : 'public', last_activity_at: `2026-0${1 + (i % 9)}-01T00:00:00Z` });
      // a full first page (100), then one more
      return res.end(JSON.stringify(page === 1 ? Array.from({ length: 100 }, (_, i) => p(i)) : [{ ...p(100), path_with_namespace: 'group/last-one', http_url_to_repo: 'https://lab.example/group/last-one.git', last_activity_at: '2026-10-10T00:00:00Z' }]));
    }
  }
  if (u.pathname.startsWith('/bb/')) {
    if (req.headers.authorization !== 'Basic ' + Buffer.from('me@example.com:' + BB).toString('base64')) return no();
    if (u.pathname === '/bb/user') return res.end(JSON.stringify({ username: 'bucket-user', display_name: 'Bucket' }));
    if (u.pathname === '/bb/repositories') return res.end(JSON.stringify({ values: [{ full_name: 'team/site', is_private: true, updated_on: '2026-05-05T00:00:00Z', description: 'The site', links: { clone: [{ name: 'https', href: 'https://bucket-user@bitbucket.org/team/site.git' }] } }] }));
  }
  if (u.pathname.startsWith('/ghe/api/v3/')) {
    if (req.headers.authorization !== 'Bearer ' + GHE) return no();
    if (u.pathname === '/ghe/api/v3/user') return res.end(JSON.stringify({ login: 'corp-user' }));
    if (u.pathname === '/ghe/api/v3/user/repos') return res.end(JSON.stringify([{ full_name: 'corp/api', clone_url: 'https://ghe.example/corp/api.git', description: 'The API', private: true, pushed_at: '2026-04-04T00:00:00Z' }]));
  }
  res.writeHead(404); res.end('{}');
});
await new Promise((ok) => api.listen(0, '127.0.0.1', ok));
const base = `http://127.0.0.1:${api.address().port}`;
process.env.PYMAGIC_BITBUCKET_API = base + '/bb';

const root = project({ 'a.py': 'x = 1\n' });
const app = await launch(root);
const { page } = app;

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
      const until = async (expression, label, tries = 120) => { for (let j = 0; j < tries; j++) { const v = await run(expression).catch(() => null); if (v) return v; await sleep(250); } throw new Error('timed out: ' + label + ' | ' + await run(`document.getElementById('host-other').innerText`)); };
      return { run, until, close: () => s.close(), reload: () => s.send(JSON.stringify({ id: 99999, method: 'Page.reload' })) };
    }
    await sleep(500);
  }
  throw new Error('no page: ' + what);
}
const welcomeAt = (u) => u.startsWith('file:') && u.endsWith('welcome.html');
const js = JSON.stringify;
const host = (kind) => `(() => { const e = document.getElementById('vcs-host'); e.value = ${js(kind)}; e.dispatchEvent(new Event('change')); })()`;
const fill = (fields) => `(() => { for (const [id, v] of Object.entries(${js(fields)})) document.getElementById(id).value = v; document.getElementById('acc-save').click(); })()`;
const names = `(() => { const r = [...document.querySelectorAll('#acc-repos li .name')].map((x) => x.firstChild.textContent); return r.length && !document.getElementById('acc-list').hidden && !document.querySelector('#acc-repos li.empty') ? r : null; })()`;
const status = `document.getElementById('acc-status').textContent`;

try {
  await page.waitFor(`!!window.pymagic?.closeProject`, 20000, 'the page');
  page.ws.send(JSON.stringify({ id: 99001, method: 'Runtime.evaluate', params: { expression: 'window.pymagic.closeProject()' } }));
  let w = await target(welcomeAt, 'the welcome screen');
  await w.until(`document.querySelectorAll('nav button').length`, 'the welcome screen');
  await w.run(`document.getElementById('vcs').click()`);
  assert.equal(await w.run(`document.getElementById('host-github').hidden`), false, 'GitHub is the first host');

  // GitLab: a wrong token refused, then the right one; both pages of projects
  await w.run(host('gitlab'));
  await w.until(`!document.getElementById('acc-signin').hidden`, 'GitLab sign-in');
  assert.equal(await w.run(status), 'GitLab: not signed in');
  assert.equal(await w.run(`document.getElementById('acc-server').value`), 'https://gitlab.com');
  assert.equal(await w.run(`document.getElementById('acc-user-row').hidden`), true);
  await w.run(fill({ 'acc-server': base + '/lab', 'acc-token': 'glpat-wrong-token-xx' }));
  const refused = await w.until(`!document.getElementById('acc-error').hidden && document.getElementById('acc-error').textContent`, 'the refusal');
  console.log('GitLab, a wrong token:', refused);
  assert.match(refused, /GitLab did not take the token \(401\)/);
  await w.run(fill({ 'acc-server': base + '/lab', 'acc-token': LAB }));
  const lab = await w.until(names, "GitLab's projects");
  console.log('GitLab:', await w.run(status), '|', lab.length, 'projects, first', lab.slice(0, 2));
  assert.match(await w.run(status), /^GitLab: lab-user on http:\/\/127\.0\.0\.1:\d+\/labSign outAnother server…$/);
  assert.equal(lab.length, 101);
  assert.equal(lab[0], 'group/last-one', 'the latest changed first');
  assert.equal(await w.run(`document.getElementById('acc-token').value`), '', 'the token is not left in the field');
  await w.run(`(() => { const f = document.getElementById('acc-filter'); f.value = 'p0 project'; f.dispatchEvent(new Event('input')); })()`);
  assert.deepEqual(await w.run(`[...document.querySelectorAll('#acc-repos li .name')].map((x) => x.textContent)`), ['group/p0private']);
  await w.run(`document.querySelector('#acc-repos li').click()`);
  await w.until(`document.getElementById('vcs-dir').value.endsWith('p0')`, 'the folder');
  console.log('chosen:', await w.run(`document.getElementById('vcs-url').value + ' -> ' + document.getElementById('vcs-dir').value`));
  assert.equal(await w.run(`document.getElementById('vcs-url').value`), 'https://lab.example/group/p0.git');

  // Bitbucket: the email is asked for; the clone address has no account in it
  await w.run(host('bitbucket'));
  await w.until(`!document.getElementById('acc-signin').hidden && document.getElementById('acc-server-row').hidden && !document.getElementById('acc-user-row').hidden`, 'Bitbucket sign-in');
  await w.run(fill({ 'acc-user': '', 'acc-token': BB }));
  assert.match(await w.until(`!document.getElementById('acc-error').hidden && document.getElementById('acc-error').textContent`, 'no email'), /email/);
  await w.run(fill({ 'acc-user': 'me@example.com', 'acc-token': BB }));
  assert.deepEqual(await w.until(names, "Bitbucket's repositories"), ['team/site']);
  await w.run(`document.querySelector('#acc-repos li').click()`);
  const bbUrl = await w.until(`document.getElementById('vcs-url').value.includes('site') && document.getElementById('vcs-url').value`, 'the address');
  console.log('Bitbucket:', await w.run(status), '|', bbUrl);
  assert.equal(bbUrl, 'https://bitbucket.org/team/site.git');

  // GitHub Enterprise: a server of its own, its API's path pasted with it
  await w.run(host('ghe'));
  await w.until(`!document.getElementById('acc-signin').hidden`, 'Enterprise sign-in');
  assert.equal(await w.run(`document.getElementById('acc-server').value`), '');
  await w.run(fill({ 'acc-server': base + '/ghe/api/v3', 'acc-token': GHE }));
  assert.deepEqual(await w.until(names, "Enterprise's repositories"), ['corp/api']);
  console.log('GitHub Enterprise:', await w.run(status));
  assert.match(await w.run(status), /^GitHub Enterprise: corp-user on http:\/\/127\.0\.0\.1:\d+\/ghe/);

  // the page again (as a new window would): the accounts are kept, encrypted; Sign out forgets GitLab's only
  w.reload();
  w.close();
  await sleep(1500);
  w = await target(welcomeAt, 'the welcome screen again');
  await w.until(`document.querySelectorAll('nav button').length`, 'the welcome screen');
  await w.run(`document.getElementById('vcs').click()`);
  await w.run(host('gitlab'));
  assert.equal((await w.until(names, 'GitLab again')).length, 101);
  await w.run(`[...document.querySelectorAll('#acc-status button')].find((b) => b.textContent === 'Sign out').click()`);
  await w.until(`!document.getElementById('acc-signin').hidden`, 'signed out');
  assert.equal(await w.run(status), 'GitLab: not signed in');
  await w.run(host('bitbucket'));
  assert.deepEqual(await w.until(names, 'Bitbucket still'), ['team/site']);
  await w.run(host('github'));
  assert.equal(await w.run(`document.getElementById('host-other').hidden && !document.getElementById('host-github').hidden`), true);
  console.log('after the reload: GitLab signed out, Bitbucket kept');
  // only the stand-in was asked
  assert.ok(seen.includes('GET /lab/api/v4/projects') && seen.includes('GET /bb/repositories') && seen.includes('GET /ghe/api/v3/user/repos'), seen.join(', '));
  console.log('OK');
} finally {
  await app.close();
  api.close();
}

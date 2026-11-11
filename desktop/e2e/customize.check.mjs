// The welcome screen's Customize and GitHub in the built PyMagic.exe (after Close project), with a stand-in for the GitHub CLI and for GitHub's
// API (nothing reaches github.com, no account of the person's is used): Customize's theme, font size and keymap written to the person's
// settings (the welcome screen takes the theme), All settings… showing them (Settings > Keymap with PyCharm's keys); Get from VCS signing in
// with a token (checked by the API, the repositories listed, one chosen for the clone, Sign out), then with the GitHub CLI's browser sign-in
// (its one-time code shown, its account's repositories listed).
// Run: node desktop/e2e/customize.check.mjs
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { existsSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { launch, project, removeTree, sleep } from './app.mjs';

const work = mkdtempSync(join(tmpdir(), 'pymagic-e2e-customize-'));
// the GitHub CLI's stand-in: auth status, auth login --web (its code, Enter, then signed in), api user/repos (two pages)
const fakeGh = join(work, 'gh.js');
const state = join(work, 'gh-state');
writeFileSync(fakeGh, String.raw`
const fs = require('fs');
const state = ${JSON.stringify(state)};
const [a, b] = process.argv.slice(2);
if (a === 'auth' && b === 'status') {
  if (fs.existsSync(state)) { console.log('github.com\n  ✓ Logged in to github.com account cli-user (keyring)'); process.exit(0); }
  console.error('You are not logged into any GitHub hosts. To log in, run: gh auth login'); process.exit(1);
}
if (a === 'auth' && b === 'login') {
  process.stderr.write('! First copy your one-time code: AB12-CD34\nOpen this URL to continue in your web browser: https://github.com/login/device\nPress Enter to open github.com in your browser... ');
  process.stdin.once('data', () => setTimeout(() => { fs.writeFileSync(state, 'in'); console.error('✓ Authentication complete.'); process.exit(0); }, 2500));
} else if (a === 'api') {
  const repo = (name, pushed, extra = {}) => ({ full_name: name, clone_url: 'https://github.com/' + name + '.git', description: name + ' of the CLI', pushed_at: pushed, owner: { login: name.split('/')[0] }, ...extra });
  console.log(JSON.stringify([repo('cli-user/tool', '2026-01-01T00:00:00Z')]));
  console.log(JSON.stringify([repo('team/service', '2026-03-01T00:00:00Z', { private: true })]));
} else process.exit(2);
`);
// GitHub's API's stand-in: /user for the token it knows, /user/repos
const TOKEN = 'ghp_' + 'T'.repeat(36);
const api = createServer((req, res) => {
  const ok = req.headers.authorization === 'Bearer ' + TOKEN;
  res.setHeader('Content-Type', 'application/json');
  if (!ok) { res.writeHead(401); res.end('{"message":"Bad credentials"}'); return; }
  if (req.url === '/user') { res.end(JSON.stringify({ login: 'token-user' })); return; }
  if (req.url.startsWith('/user/repos')) {
    res.end(JSON.stringify([
      { full_name: 'token-user/shop', clone_url: 'https://github.com/token-user/shop.git', description: 'The shop', private: true, pushed_at: '2026-10-01T00:00:00Z', owner: { login: 'token-user' } },
      { full_name: 'token-user/notes', clone_url: 'https://github.com/token-user/notes.git', description: null, pushed_at: '2025-01-01T00:00:00Z', owner: { login: 'token-user' } },
    ]));
    return;
  }
  res.writeHead(404); res.end('{}');
});
await new Promise((ok) => api.listen(0, '127.0.0.1', ok));
process.env.PYMAGIC_GH = fakeGh;
process.env.PYMAGIC_NODE = process.execPath;
process.env.PYMAGIC_GITHUB_API = `http://127.0.0.1:${api.address().port}`;

const root = project({ 'a.py': 'x = 1\n' });
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
const set = (id, value) => `(() => { const e = document.getElementById(${JSON.stringify(id)}); e.value = ${JSON.stringify(value)}; e.dispatchEvent(new Event('change')); })()`;

try {
  await page.waitFor(`!!window.pymagic?.closeProject`, 20000, 'the page');
  page.ws.send(JSON.stringify({ id: 99001, method: 'Runtime.evaluate', params: { expression: 'window.pymagic.closeProject()' } }));
  let w = await target(welcomeAt, 'the welcome screen');
  await w.until(`document.querySelectorAll('nav button').length`, 'the welcome screen');

  // Customize: what the settings have, then a light theme (the welcome screen takes it), a bigger font, PyCharm's keymap
  await w.run(`document.querySelector('nav button[data-page="customize"]').click()`);
  const before = await w.until(`(() => { const v = ['c-theme', 'c-colors', 'c-size', 'c-keymap'].map((id) => document.getElementById(id).value); return v[0] ? v : null; })()`, 'Customize');
  console.log('Customize at first:', before);
  assert.deepEqual(before, ['dark', 'vscode', '14', 'pymagic']);
  await w.run(set('c-theme', 'light'));
  await w.until(`document.getElementById('c-saved').textContent.includes('Saved')`, 'saved');
  assert.equal(await w.run(`document.documentElement.dataset.theme`), 'light');
  assert.equal(await w.run(`getComputedStyle(document.body).backgroundColor`), 'rgb(245, 246, 248)');
  await w.run(set('c-size', '17'));
  await sleep(400);
  await w.run(set('c-keymap', 'pycharm'));
  await sleep(400);
  assert.equal(await w.run(`document.getElementById('c-sample').style.fontSize`), '17px');
  console.log('Customize: light theme taken by the welcome screen, font size 17, PyCharm keymap');

  // All settings…: Settings shows them; its Keymap has PyCharm's keys (step over F8, duplicate line Ctrl+D)
  w.fire(`document.getElementById('c-all').click()`);
  w.close();
  const st = await target((u) => /\/welcome-tool\.html\?tool=settings/.test(u), 'Settings');
  await st.until(`document.querySelector('.settings-page h2')?.textContent === 'Appearance'`, 'Appearance');
  assert.equal(await st.run(`document.documentElement.dataset.theme`), 'light');
  const shown = await st.run(`[...document.querySelectorAll('.settings-page select')].map((s) => s.value)`);
  console.log('Settings > Appearance:', shown);
  assert.ok(shown.includes('light'));
  await st.run(`[...document.querySelectorAll('.settings-nav button, .settings-nav a, nav button')].find((b) => b.textContent.trim() === 'Keymap').click()`);
  const keys = await st.until(`(() => { const box = document.getElementById('keymap-choice'); if (!box) return null; document.getElementById('keymap-search').value = 'step over'; document.getElementById('keymap-search').dispatchEvent(new Event('input')); return box.value + ' | ' + [...document.querySelectorAll('.keymap-row')].map((r) => r.querySelector('.keymap-name').textContent + ' ' + r.querySelector('.keymap-keys').textContent).join(' / '); })()`, 'the keymap');
  console.log('Settings > Keymap:', keys, '| rows without a filter:', await st.run(`(() => { const s = document.getElementById('keymap-search'); s.value = ''; s.dispatchEvent(new Event('input')); return document.querySelectorAll('.keymap-row').length + ' | ' + document.getElementById('keymap-list').textContent.slice(0, 200); })()`));
  // (from the welcome screen Settings has no editor and so no commands to list: it says so; the keys are checked in a project below)
  assert.match(keys, /^pycharm \| /);
  assert.match(await st.run(`document.getElementById('keymap-list').textContent`), /listed here when a project is open/);
  st.fire(`[...document.querySelectorAll('.settings-foot button')].find((b) => b.textContent === 'Done').click()`);
  st.close();
  w = await target(welcomeAt, 'the welcome screen after Done');
  await w.until(`document.querySelectorAll('nav button').length`, 'the welcome screen');

  // Get from VCS: not signed in; a token that is not one refused; the token the API knows: its repositories, one chosen
  await w.run(`document.getElementById('vcs').click()`);
  await w.until(`!document.getElementById('gh-signin').hidden`, 'not signed in');
  assert.equal(await w.run(`document.getElementById('gh-status').textContent`), 'GitHub: not signed in');
  await w.run(`document.getElementById('gh-token-show').click()`);
  await w.run(`(() => { document.getElementById('gh-token').value = 'my-password'; document.getElementById('gh-token-save').click(); })()`);
  const refused = await w.until(`!document.getElementById('gh-error').hidden && document.getElementById('gh-error').textContent`, 'the refusal');
  console.log('not a token:', refused);
  await w.run(`(() => { document.getElementById('gh-token').value = ${JSON.stringify(TOKEN)}; document.getElementById('gh-token-save').click(); })()`);
  const listed = await w.until(`(() => { const r = [...document.querySelectorAll('#gh-repos li .name')].map((x) => x.textContent); return r.length === 2 ? r : null; })()`, "the token's repositories");
  console.log('signed in with a token:', await w.run(`document.getElementById('gh-status').textContent`), '|', listed);
  assert.match(await w.run(`document.getElementById('gh-status').textContent`), /token-user \(a token\)/);
  assert.deepEqual(listed, ['token-user/shopprivate', 'token-user/notes']);
  assert.equal(await w.run(`document.getElementById('gh-token').value`), '', 'the token is not left in the field');
  await w.run(`(() => { const f = document.getElementById('gh-filter'); f.value = 'note'; f.dispatchEvent(new Event('input')); })()`);
  await w.run(`document.querySelector('#gh-repos li').click()`);
  const chosen = await w.until(`(() => { const u = document.getElementById('vcs-url').value; const d = document.getElementById('vcs-dir').value; return u && d.endsWith('notes') ? u + ' -> ' + d : null; })()`, 'the repository chosen');
  console.log('chosen:', chosen);
  assert.match(chosen, /^https:\/\/github\.com\/token-user\/notes\.git -> .*notes$/);

  // Sign out (the token forgotten), then the GitHub CLI's sign-in: its one-time code shown, its account's repositories
  await w.run(`[...document.querySelectorAll('#gh-status button')].find((b) => b.textContent === 'Sign out').click()`);
  await w.until(`!document.getElementById('gh-signin').hidden`, 'signed out');
  w.fire(`document.getElementById('gh-cli').click()`);
  const code = await w.until(`!document.getElementById('gh-device').hidden && document.getElementById('gh-code').textContent`, 'the one-time code');
  console.log('the GitHub CLI asks for the code:', code);
  assert.equal(code, 'AB12-CD34');
  const cli = await w.until(`(() => { const r = [...document.querySelectorAll('#gh-repos li .name')].map((x) => x.textContent); return r.length === 2 ? r : null; })()`, "the CLI's repositories", 160)
    .catch(async (e) => { throw new Error(e.message + ' | ' + await w.run(`document.getElementById('gh-status').textContent + ' | ' + document.getElementById('gh-error').textContent + ' | ' + document.getElementById('gh-repos').textContent`)); });
  console.log('signed in with the GitHub CLI:', await w.run(`document.getElementById('gh-status').textContent`), '|', cli);
  assert.match(await w.run(`document.getElementById('gh-status').textContent`), /cli-user \(the GitHub CLI’s sign-in\)/);
  assert.deepEqual(cli, ['team/serviceprivate', 'cli-user/tool']);
  assert.equal(await w.run(`[...document.querySelectorAll('#gh-status button')].length`), 0, 'no Sign out of the CLI');

  // the project opened again (the recent one): Customize's choices are its own now: the light theme, the font size, PyCharm's keys
  w.fire(`document.querySelector('#recent li').click()`);
  w.close();
  const p = await target((u) => /^http:\/\/127\.0\.0\.1:\d+\/$/.test(u), 'the project');
  await p.until(`window.pymagic?.editor?.getModel() && window.pymagic.runCommand`, 'the editor');
  const look = await p.until(`(() => { const s = window.pymagic.editor.getOption(window.pymagic.monaco.editor.EditorOption.fontSize); return s === 17 ? document.documentElement.dataset.theme + ' ' + s : null; })()`, 'the font size');
  console.log('the project: theme and font size', look);
  assert.equal(look, 'light 17');
  p.fire(`window.pymagic.runCommand('pymagic.window.settings')`);
  await p.until(`document.querySelector('.settings-nav')`, 'Settings');
  await p.run(`[...document.querySelectorAll('.settings-nav button, .settings-nav a')].find((b) => b.textContent.trim() === 'Keymap').click()`);
  const rows = await p.until(`(() => { const s = document.getElementById('keymap-search'); if (!s) return null; s.value = 'step over'; s.dispatchEvent(new Event('input')); const r = [...document.querySelectorAll('.keymap-row')].map((x) => x.querySelector('.keymap-name').textContent + ' ' + x.querySelector('.keymap-keys').textContent); return r.length ? r : null; })()`, 'the keymap rows');
  console.log('Settings > Keymap in the project:', rows);
  assert.ok(rows.some((r) => /^Step over\s*F8/.test(r)), rows.join(' | '));
  p.close();
  console.log('OK');
} finally {
  await app.close();
  api.close();
  removeTree(work);
}

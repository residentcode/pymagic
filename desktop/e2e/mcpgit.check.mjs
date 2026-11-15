// The MCP server's Git tools for the remotes and joining branches in the built PyMagic.exe, used as an AI client uses them, against a real
// repository and a bare remote of its own (both in the temp folder): git_push publishes the branch, a change pushed from another clone is
// fetched and pulled, a merge that conflicts says which files and git_abort gives it up, and a clean merge joins the branch. Also a quick
// fix that only runs a command of the editor: a Markdown file's unknown word saved to the project's dictionary.
// Run: node desktop/e2e/mcpgit.check.mjs   (needs git on PATH)
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const port = await new Promise((ok) => { const s = createServer(); s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => ok(p)); }); });
const root = project({ 'a.txt': 'one\ntwo\nthree\n', 'main.py': 'print("hi")\n', 'notes.md': '# Notes\n\nThe zorbleflux is ready.\n' });
const outside = mkdtempSync(join(tmpdir(), 'pm-mcpgit-'));
const remote = join(outside, 'remote.git');
const other = join(outside, 'other');
const id = ['-c', 'user.name=Check', '-c', 'user.email=check@example.invalid'];
const git = (cwd, ...args) => execFileSync('git', args, { cwd, stdio: 'pipe' }).toString();
git(outside, 'init', '-q', '--bare', remote);
git(root, 'init', '-q', '-b', 'main');
git(root, 'config', 'user.name', 'Check');
git(root, 'config', 'user.email', 'check@example.invalid');
git(root, 'add', '-A');
git(root, 'commit', '-q', '-m', 'first');
git(root, 'remote', 'add', 'origin', remote);

const app = await launch(root, null, { mcp: { enabled: true, port, brave: true } });
const url = `http://127.0.0.1:${port}/mcp`;
let n = 0;
async function rpc(method, params) {
    const res = await fetch(url, { method: 'POST', headers: { 'content-type': 'application/json', accept: 'application/json, text/event-stream' }, body: JSON.stringify({ jsonrpc: '2.0', id: ++n, method, params }) });
    assert.equal(res.status, 200, method);
    return res.json();
}
async function call(name, args = {}) {
    const r = await rpc('tools/call', { name, arguments: args });
    assert.ok(r.result, JSON.stringify(r));
    return { text: r.result.content[0].text, isError: r.result.isError };
}
const disk = (rel) => readFileSync(join(root, rel), 'utf8').replace(/\r\n/g, '\n');

try {
    let init = null;
    for (let i = 0; i < 40 && !init; i++) { init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'check', version: '1' } }).catch(() => null); if (!init) await sleep(500); }
    assert.ok(init, 'the MCP server answers');
    const tools = (await rpc('tools/list', {})).result.tools.map((t) => t.name);
    for (const t of ['git_fetch', 'git_pull', 'git_push', 'git_merge', 'git_abort']) assert.ok(tools.includes(t), t);

    // push: main is published to origin and tracks it
    const pushed = await call('git_push');
    console.log('push:', pushed.text.replace(/\n/g, ' | '));
    assert.ok(!pushed.isError, pushed.text);
    assert.equal(git(remote, 'rev-parse', 'main'), git(root, 'rev-parse', 'HEAD'));
    assert.match(git(root, 'rev-parse', '--abbrev-ref', 'main@{upstream}'), /origin\/main/);

    // a change pushed from another clone: fetched (no branch changes), then pulled
    git(outside, 'clone', '-q', '-b', 'main', remote, other);
    writeFileSync(join(other, 'b.txt'), 'from the other clone\n');
    git(other, 'add', 'b.txt');
    git(other, ...id, 'commit', '-q', '-m', 'from the other clone');
    git(other, 'push', '-q', 'origin', 'main');
    const fetched = await call('git_fetch');
    console.log('fetch:', fetched.text.replace(/\n/g, ' | '));
    assert.ok(!fetched.isError, fetched.text);
    assert.ok(!existsSync(join(root, 'b.txt')), 'fetch changes no file');
    assert.match((await call('get_project_vcs_status')).text, /1 behind/);
    const pulled = await call('git_pull', { mode: 'ff-only' });
    console.log('pull:', pulled.text.replace(/\n/g, ' | '));
    assert.ok(!pulled.isError, pulled.text);
    assert.equal(disk('b.txt'), 'from the other clone\n');

    // a merge that conflicts: the files are named, git_abort gives it up
    git(root, 'switch', '-q', '-c', 'feature');
    writeFileSync(join(root, 'a.txt'), 'one\nTWO on feature\nthree\n');
    git(root, 'commit', '-q', '-am', 'feature changes two');
    git(root, 'switch', '-q', 'main');
    writeFileSync(join(root, 'a.txt'), 'one\ntwo on main\nthree\n');
    git(root, 'commit', '-q', '-am', 'main changes two');
    await sleep(1500);   // the page notices the branch and files changed outside
    const merged = await call('git_merge', { branch: 'feature' });
    console.log('merge:', merged.text.replace(/\n/g, ' | '));
    assert.equal(merged.isError, true);
    assert.match(merged.text, /stopped on conflicts in: a\.txt/);
    assert.ok(existsSync(join(root, '.git', 'MERGE_HEAD')));
    const aborted = await call('git_abort');
    console.log('abort:', aborted.text);
    assert.match(aborted.text, /^Gave up the merge/);
    assert.ok(!existsSync(join(root, '.git', 'MERGE_HEAD')));
    assert.equal(disk('a.txt'), 'one\ntwo on main\nthree\n');
    assert.equal((await call('git_abort')).isError, true, 'nothing left to abort');

    // a clean merge, with a merge commit
    git(root, 'switch', '-q', '-c', 'docs');
    writeFileSync(join(root, 'c.txt'), 'docs\n');
    git(root, 'add', 'c.txt');
    git(root, 'commit', '-q', '-m', 'docs');
    git(root, 'switch', '-q', 'main');
    await sleep(1500);
    const clean = await call('git_merge', { branch: 'docs', noFastForward: true });
    console.log('clean merge:', clean.text.replace(/\n/g, ' | '));
    assert.ok(!clean.isError, clean.text);
    assert.equal(disk('c.txt'), 'docs\n');
    assert.equal(git(root, 'rev-list', '--parents', '-n', '1', 'HEAD').trim().split(' ').length, 3, 'a merge commit');
    assert.equal((await call('git_merge', { branch: 'no-such' })).isError, true);

    // a quick fix that runs a command: the word goes to .pymagic/dictionary.txt
    let fixes = null;
    for (let i = 0; i < 30 && !/zorbleflux/.test(fixes?.text ?? ''); i++) { fixes = await call('get_quick_fixes', { filePath: 'notes.md', line: 3 }); if (!/zorbleflux/.test(fixes.text)) await sleep(1000); }
    console.log('fixes:', fixes.text.replace(/\n/g, ' | '));
    assert.match(fixes.text, /notes\.md:3 Save 'zorbleflux' to the project's dictionary/);
    const saved = await call('apply_quick_fix', { filePath: 'notes.md', line: 3, title: "Save 'zorbleflux'" });
    console.log('saved:', saved.text);
    assert.match(saved.text, /^Ran "Save 'zorbleflux' to the project's dictionary" \(pymagic\.spell\.save\)\.$/);
    for (let i = 0; i < 20 && !existsSync(join(root, '.pymagic', 'dictionary.txt')); i++) await sleep(250);
    assert.match(disk('.pymagic/dictionary.txt'), /^zorbleflux$/m);
    console.log('OK');
} finally {
    await app.close();
    rmSync(outside, { recursive: true, force: true });
}

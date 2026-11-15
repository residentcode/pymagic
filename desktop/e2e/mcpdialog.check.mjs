// The question PyMagic asks before an AI agent's action over MCP, on screen in the built PyMagic.exe (Settings > MCP Server's "ask first",
// the default): an agent asks to commit; the dialog (moved off-screen at once by dialog.ps1, so it is not in front of the person, and drawn
// into a PNG) says what is asked; "Don't commit" leaves the repository as it was and tells the agent so; asked again, "Commit" commits.
// Run: node desktop/e2e/mcpdialog.check.mjs   (needs git on PATH)
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launch, project, sleep } from './app.mjs';

const helper = fileURLToPath(new URL('./dialog.ps1', import.meta.url));
const root = project({ 'a.py': 'total = 1\n' });
const git = (...args) => execFileSync('git', args, { cwd: root, stdio: 'pipe' }).toString();
git('init', '-q');
git('config', 'user.name', 'Check');
git('config', 'user.email', 'check@example.invalid');
git('add', '-A');
git('commit', '-q', '-m', 'first');
writeFileSync(join(root, 'a.py'), 'total = 2\n');

const port = await new Promise((ok) => { const s = createServer(); s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => ok(p)); }); });
const app = await launch(root, null, { mcp: { enabled: true, port, brave: false } });
let n = 0;
async function rpc(method, params) {
    const res = await fetch(`http://127.0.0.1:${port}/mcp`, { method: 'POST', headers: { 'content-type': 'application/json', accept: 'application/json, text/event-stream' }, body: JSON.stringify({ jsonrpc: '2.0', id: ++n, method, params }) });
    assert.equal(res.status, 200, method);
    return res.json();
}
/** The dialog answered by dialog.ps1 while the call waits for it: { text, isError } of the call and what the helper printed. */
async function asked(name, args, click, shot) {
    const out = [];
    const ps = spawn('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', helper, '-Title', 'MCP: commit', '-Click', click, ...(shot ? ['-Shot', shot] : [])], { windowsHide: true });
    ps.stdout.on('data', (d) => out.push(String(d)));
    ps.stderr.on('data', (d) => out.push(String(d)));
    const done = new Promise((ok) => ps.on('exit', ok));
    const r = await rpc('tools/call', { name, arguments: args });
    const code = await done;
    return { text: r.result.content[0].text, isError: r.result.isError, helper: out.join('').trim(), code };
}

try {
    let init = null;
    for (let i = 0; i < 40 && !init; i++) { init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'check', version: '1' } }).catch(() => null); if (!init) await sleep(500); }
    assert.ok(init, 'the MCP server answers');
    const shot = join(tmpdir(), 'pymagic-mcp-dialog.png');

    const no = await asked('commit_changes', { message: 'Fix the total', paths: 'a.py' }, "Don't commit", shot);
    console.log('dialog:', no.helper.replace(/\r?\n/g, ' | '));
    console.log('answer:', no.text);
    assert.equal(no.code, 0, no.helper);
    assert.match(no.helper, /Commit/);
    assert.match(no.helper, /Don't commit/);
    assert.equal(no.isError, true);
    assert.equal(no.text, 'The person chose not to commit it.');
    assert.match(git('log', '--oneline'), /^[0-9a-f]+ first\s*$/, 'nothing committed');

    const yes = await asked('commit_changes', { message: 'Fix the total', paths: 'a.py' }, 'Commit');
    console.log('answer:', yes.text);
    assert.equal(yes.code, 0, yes.helper);
    assert.ok(!yes.isError, yes.text);
    assert.match(git('log', '--oneline', '-1'), /Fix the total/);
    console.log('screenshot:', shot);
    console.log('OK');
} finally {
    await app.close();
}

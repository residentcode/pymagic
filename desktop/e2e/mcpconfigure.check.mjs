// Settings > MCP Server's Configure in the built PyMagic.exe against the AI clients' real file formats, and a real client connecting. The
// person's own files are never touched: the app runs with a home and an AppData folder of the temp folder (USERPROFILE, HOME, APPDATA,
// CLAUDE_CONFIG_DIR), where the clients' files are made first with servers of their own. Configure (as the Settings page's button does it)
// adds PyMagic's entry beside the others, leaves a file with comments as it is (its entry to copy instead), and for Claude Code runs Claude
// Code's own `claude mcp add`; then the real Claude Code CLI, run with the same home, lists PyMagic's server as connected.
// Run: node desktop/e2e/mcpconfigure.check.mjs   (the claude CLI on PATH for the last part; skipped without)
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const home = mkdtempSync(join(tmpdir(), 'pm-mcphome-'));
const appData = join(home, 'AppData', 'Roaming');
const env = { USERPROFILE: home, HOME: home, APPDATA: appData, CLAUDE_CONFIG_DIR: home, LOCALAPPDATA: join(home, 'AppData', 'Local') };
const put = (file, text) => { mkdirSync(dirname(file), { recursive: true }); writeFileSync(file, text); };
const read = (file) => readFileSync(file, 'utf8');
// what the clients have before: their own servers (and settings), one file with comments
const files = {
    cursor: join(home, '.cursor', 'mcp.json'),
    vscode: join(appData, 'Code', 'User', 'mcp.json'),
    claudeDesktop: join(appData, 'Claude', 'claude_desktop_config.json'),
    gemini: join(home, '.gemini', 'settings.json'),
    windsurf: join(home, '.codeium', 'windsurf', 'mcp_config.json'),
    codex: join(home, '.codex', 'config.toml'),
    claude: join(home, '.claude.json'),
};
put(files.cursor, JSON.stringify({ mcpServers: { other: { url: 'http://localhost:9/mcp' } } }, null, 2));
put(files.vscode, '{\n  // the person\'s own notes\n  "servers": { "other": { "type": "http", "url": "http://localhost:9/mcp" } }\n}\n');
put(files.claudeDesktop, JSON.stringify({ mcpServers: { other: { command: 'other.exe' } }, globalShortcut: 'Ctrl+Space' }, null, 2));
put(files.gemini, JSON.stringify({ theme: 'Default', mcpServers: {} }, null, 2));
put(files.codex, 'model = "o4"\n\n[mcp_servers.other]\ncommand = "other"\n');
const vscodeBefore = read(files.vscode);

const port = await new Promise((ok) => { const s = createServer(); s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => ok(p)); }); });
const url = `http://127.0.0.1:${port}/mcp`;
const app = await launch(project({ 'main.py': 'print(1)\n' }), null, { mcp: { enabled: true, port, brave: true } }, { env });
const { page } = app;
const configure = async (id) => JSON.parse(await page.evaluate(`window.pymagicDesktop.mcpConfigure(${JSON.stringify(id)}).then((r) => JSON.stringify(r))`));
const status = async () => JSON.parse(await page.evaluate(`window.pymagicDesktop.mcpStatus().then((r) => JSON.stringify(r))`));
let claudeHere = false;
try { claudeHere = spawnSync('claude', ['--version'], { shell: true, encoding: 'utf8', env: { ...process.env, ...env }, timeout: 60000 }).status === 0; } catch { /* not here */ }

try {
    for (let i = 0; i < 40 && !(await status().catch(() => null))?.running; i++) await sleep(500);
    const before = await status();
    console.log('clients:', before.clients.map((c) => `${c.id}=${c.configured} ${c.file}`).join(' | '));
    for (const c of before.clients) assert.ok(c.file.startsWith(home), `${c.id}'s file is in the test's home: ${c.file}`);

    for (const id of ['cursor', 'claude-desktop', 'gemini', 'windsurf', 'codex', 'vscode']) {
        const r = await configure(id);
        console.log(`configure ${id}:`, r.ok, r.message?.split('\n')[0]);
    }
    // each client's own format, beside its other servers and settings
    const cursor = JSON.parse(read(files.cursor));
    assert.deepEqual(cursor.mcpServers.pymagic, { url });
    assert.deepEqual(cursor.mcpServers.other, { url: 'http://localhost:9/mcp' });
    const desktop = JSON.parse(read(files.claudeDesktop));
    assert.equal(desktop.globalShortcut, 'Ctrl+Space');
    assert.ok(desktop.mcpServers.other && desktop.mcpServers.pymagic?.command?.endsWith('PyMagic.exe'), JSON.stringify(desktop.mcpServers.pymagic));
    assert.deepEqual(desktop.mcpServers.pymagic.args.slice(-1), [String(port)]);
    const gemini = JSON.parse(read(files.gemini));
    assert.equal(gemini.theme, 'Default');
    assert.deepEqual(gemini.mcpServers.pymagic, { httpUrl: url });
    assert.deepEqual(JSON.parse(read(files.windsurf)).mcpServers.pymagic, { serverUrl: url });
    const codex = read(files.codex);
    assert.match(codex, /^model = "o4"/m);
    assert.match(codex, /\[mcp_servers\.other\]/);
    assert.match(codex, /\[mcp_servers\.pymagic\]/);
    // a file with comments is left as it is: its entry is given to copy
    assert.equal(read(files.vscode), vscodeBefore, 'VS Code\'s file with comments is not touched');

    // Claude Code: its own command, then the real CLI connects to the server
    if (!claudeHere) console.log('claude CLI: not here, skipped');
    else {
        const r = await configure('claude-code');
        console.log('configure claude-code:', r.ok, r.message);
        assert.ok(r.ok, r.message);
        assert.ok(existsSync(files.claude), 'Claude Code wrote its file in the test\'s home');
        const entry = JSON.parse(read(files.claude)).mcpServers?.pymagic;
        assert.deepEqual({ type: entry?.type, url: entry?.url }, { type: 'http', url });
        const run = (...args) => spawnSync('claude', args, { shell: true, encoding: 'utf8', env: { ...process.env, ...env }, timeout: 120000, cwd: home });
        const listed = run('mcp', 'list');
        console.log('claude mcp list:', (listed.stdout + listed.stderr).trim().replace(/\n/g, ' | '));
        assert.match(listed.stdout, /pymagic: http:\/\/127\.0\.0\.1:\d+\/mcp \(HTTP\) - .*Connected/);
        const got = run('mcp', 'get', 'pymagic');
        console.log('claude mcp get:', (got.stdout + got.stderr).trim().replace(/\n/g, ' | '));
        assert.match(got.stdout, /Status: .*Connected/);
        assert.equal((await configure('claude-code')).ok, true, 'a second time: it is there already');
        // the stdio entry Configure wrote for Claude Desktop (PyMagic.exe running mcp-stdio.js as node), started by the real client
        const stdio = JSON.parse(read(files.claudeDesktop)).mcpServers.pymagic;
        const q = (s) => `"${s}"`;
        const added = run('mcp', 'add', 'pymagic-stdio', '-s', 'user', ...Object.entries(stdio.env ?? {}).flatMap(([k, v]) => ['-e', `${k}=${v}`]), '--', q(stdio.command), ...stdio.args.map(q));
        console.log('claude mcp add (stdio):', (added.stdout + added.stderr).trim().replace(/\n/g, ' | '));
        assert.equal(added.status, 0);
        const both = run('mcp', 'list');
        console.log('claude mcp list:', (both.stdout + both.stderr).trim().replace(/\n/g, ' | '));
        assert.match(both.stdout, /pymagic-stdio: .*PyMagic\.exe.* - .*Connected/);
    }
    const after = await status();
    console.log('configured:', after.clients.map((c) => `${c.id}=${c.configured}`).join(' '));
    for (const id of ['cursor', 'claude-desktop', 'gemini', 'windsurf', 'codex', ...(claudeHere ? ['claude-code'] : [])])
        assert.ok(after.clients.find((c) => c.id === id)?.configured, `${id} is shown as configured`);
    assert.ok(!after.clients.find((c) => c.id === 'vscode')?.configured);
    console.log('OK');
} finally {
    await app.close();
    rmSync(home, { recursive: true, force: true });
}

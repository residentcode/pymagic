// The MCP server: its protocol, its two transports and their checks, the stdio bridge, and writing the clients' configurations (in folders of the
// test's own: no client's real file is read or written).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync, existsSync, rmSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import http from 'node:http';

const require = createRequire(import.meta.url);
const { createHandler, startServer, allowed } = require('../mcp-server.js');
const { TOOLS, checkArguments } = require('../mcp-tools.js');
const clients = require('../mcp-clients.js');

const calls = [];
const handler = createHandler({
  version: '9.9.9',
  callTool: async (name, args) => {
    calls.push([name, args]);
    if (name === 'get_file_text_by_path' && args.pathInProject === 'boom') throw new Error('it broke');
    return { text: `ran ${name}` };
  },
});

test('initialize answers the version the client asks for when it knows it, else its newest', async () => {
  const a = await handler.handle({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 't' } } });
  assert.equal(a.result.protocolVersion, '2025-03-26');
  assert.equal(a.result.serverInfo.name, 'PyMagic');
  assert.equal(a.result.serverInfo.version, '9.9.9');
  assert.deepEqual(a.result.capabilities, { tools: { listChanged: false } });
  const b = await handler.handle({ jsonrpc: '2.0', id: 2, method: 'initialize', params: { protocolVersion: '1999-01-01' } });
  assert.equal(b.result.protocolVersion, '2025-06-18');
});

test('tools/list gives every tool with a schema; notifications get no answer; an unknown method is an error', async () => {
  const r = await handler.handle({ jsonrpc: '2.0', id: 3, method: 'tools/list' });
  assert.equal(r.result.tools.length, TOOLS.length);
  for (const t of r.result.tools) {
    assert.equal(t.inputSchema.type, 'object');
    assert.ok(t.inputSchema.properties.projectPath, t.name);
    assert.ok(t.description.length > 10, t.name);
  }
  assert.ok(r.result.tools.some((t) => t.name === 'search_in_files_by_regex'));
  assert.equal(await handler.handle({ jsonrpc: '2.0', method: 'notifications/initialized' }), null);
  assert.equal((await handler.handle({ jsonrpc: '2.0', id: 4, method: 'nope' })).error.code, -32601);
  assert.equal((await handler.handle({ id: 5, method: 'ping' })).error.code, -32600);
  assert.deepEqual((await handler.handle({ jsonrpc: '2.0', id: 6, method: 'ping' })).result, {});
});

test('tools/call checks the arguments, and a tool that throws answers its message as an error result', async () => {
  const missing = await handler.handle({ jsonrpc: '2.0', id: 7, method: 'tools/call', params: { name: 'get_file_text_by_path', arguments: {} } });
  assert.equal(missing.result.isError, true);
  assert.match(missing.result.content[0].text, /pathInProject is required/);
  const wrongType = await handler.handle({ jsonrpc: '2.0', id: 8, method: 'tools/call', params: { name: 'list_directory_tree', arguments: { maxDepth: 'two' } } });
  assert.match(wrongType.result.content[0].text, /whole number/);
  const unknownArg = await handler.handle({ jsonrpc: '2.0', id: 9, method: 'tools/call', params: { name: 'get_all_open_file_paths', arguments: { x: 1 } } });
  assert.match(unknownArg.result.content[0].text, /has no argument x/);
  const noTool = await handler.handle({ jsonrpc: '2.0', id: 10, method: 'tools/call', params: { name: 'format_disk' } });
  assert.equal(noTool.error.code, -32602);
  const thrown = await handler.handle({ jsonrpc: '2.0', id: 11, method: 'tools/call', params: { name: 'get_file_text_by_path', arguments: { pathInProject: 'boom' } } });
  assert.deepEqual(thrown.result, { content: [{ type: 'text', text: 'it broke' }], isError: true });
  const fine = await handler.handle({ jsonrpc: '2.0', id: 12, method: 'tools/call', params: { name: 'get_file_text_by_path', arguments: { pathInProject: 'a.py', maxLinesCount: 3 } } });
  assert.deepEqual(fine.result, { content: [{ type: 'text', text: 'ran get_file_text_by_path' }], isError: false });
  assert.equal(checkArguments(TOOLS.find((t) => t.name === 'get_file_text_by_path'), { pathInProject: 'a', truncateMode: 'SIDEWAYS' }), 'truncateMode must be one of START, MIDDLE, END, NONE');
});

test('a batch is answered as a batch; bad JSON is a parse error', async () => {
  const r = await handler.handleBody(JSON.stringify([{ jsonrpc: '2.0', id: 1, method: 'ping' }, { jsonrpc: '2.0', method: 'notifications/initialized' }]));
  assert.equal(r.length, 1);
  assert.equal((await handler.handleBody('{')).error.code, -32700);
  assert.equal(await handler.handleBody(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/cancelled' })), null);
});

test('only this computer reaches it: the Host is its own address, an Origin is a local page', () => {
  assert.ok(allowed({ host: '127.0.0.1:5000' }, 5000));
  assert.ok(allowed({ host: 'localhost:5000', origin: 'http://localhost:6274' }, 5000));
  assert.ok(!allowed({ host: 'evil.example:5000' }, 5000));
  assert.ok(!allowed({ host: '127.0.0.1:5001' }, 5000));
  assert.ok(!allowed({ host: '127.0.0.1:5000', origin: 'https://evil.example' }, 5000));
});

/** A raw request (fetch cannot set Host). */
function request(port, { method = 'POST', path = '/mcp', headers = {}, body = '' }) {
  return new Promise((resolve, reject) => {
    const req = http.request({ host: '127.0.0.1', port, method, path, headers: { 'content-type': 'application/json', ...headers } }, (res) => {
      let text = '';
      res.on('data', (c) => { text += c; });
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, text }));
    });
    req.on('error', reject);
    req.end(body);
  });
}

test('Streamable HTTP: POST /mcp answers JSON, gives a session on initialize, 202 for notifications, 403 for another Host, 405 for GET', async () => {
  const s = await startServer({ port: 0, handler });
  try {
    const init = await request(s.port, { body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18' } }) });
    assert.equal(init.status, 200);
    assert.ok(init.headers['mcp-session-id']);
    assert.equal(JSON.parse(init.text).result.serverInfo.name, 'PyMagic');
    const note = await request(s.port, { body: JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) });
    assert.equal(note.status, 202);
    const call = await request(s.port, { body: JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'get_project_info', arguments: {} } }) });
    assert.equal(JSON.parse(call.text).result.content[0].text, 'ran get_project_info');
    const rebound = await request(s.port, { headers: { host: 'attacker.example' }, body: '{}' });
    assert.equal(rebound.status, 403);
    const fromSite = await request(s.port, { headers: { origin: 'https://attacker.example' }, body: '{}' });
    assert.equal(fromSite.status, 403);
    assert.equal((await request(s.port, { method: 'GET', headers: { accept: 'text/event-stream' } })).status, 405);
    const browser = await request(s.port, { method: 'GET', headers: { accept: 'text/html,*/*' } });
    assert.equal(browser.status, 200);
    assert.match(browser.text, /PyMagic's MCP server is running/);
    assert.match(browser.text, new RegExp(`claude mcp add --transport http --scope user pymagic http://127\\.0\\.0\\.1:${s.port}/mcp`));
  }
  finally { await s.close(); }
});

test('HTTP with SSE: the stream names its endpoint, and a POST there is answered on the stream', async () => {
  const s = await startServer({ port: 0, handler });
  try {
    const events = [];
    let wake = () => {};
    const req = http.get({ host: '127.0.0.1', port: s.port, path: '/sse' }, (res) => {
      res.setEncoding('utf8');
      let buf = '';
      res.on('data', (c) => {
        buf += c;
        let at;
        while ((at = buf.indexOf('\n\n')) >= 0) { events.push(buf.slice(0, at)); buf = buf.slice(at + 2); wake(); }
      });
    });
    const next = async (n) => { while (events.length < n) await new Promise((r) => { wake = r; }); return events[n - 1]; };
    const endpoint = /data: (\S+)/.exec(await next(1))[1];
    assert.match(endpoint, /^\/message\?sessionId=/);
    const posted = await request(s.port, { path: endpoint, body: JSON.stringify({ jsonrpc: '2.0', id: 5, method: 'tools/list' }) });
    assert.equal(posted.status, 202);
    const message = await next(2);
    assert.match(message, /^event: message/);
    assert.equal(JSON.parse(message.split('data: ')[1]).result.tools.length, TOOLS.length);
    assert.equal((await request(s.port, { path: '/message?sessionId=none', body: '{}' })).status, 404);
    req.destroy();
  }
  finally { await s.close(); }
});

test('the stdio bridge posts each line and writes the answers; without a server each request gets an error', async () => {
  const s = await startServer({ port: 0, handler });
  const run = (port, lines) => new Promise((resolve) => {
    const child = spawn(process.execPath, [join(import.meta.dirname, '..', 'mcp-stdio.js'), String(port)], { stdio: ['pipe', 'pipe', 'inherit'] });
    let out = '';
    child.stdout.on('data', (c) => { out += c; });
    child.on('close', () => resolve(out.trim().split('\n').filter(Boolean).map((l) => JSON.parse(l))));
    child.stdin.end(lines.map((l) => JSON.stringify(l)).join('\n') + '\n');
  });
  try {
    const answers = await run(s.port, [
      { jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2024-11-05' } },
      { jsonrpc: '2.0', method: 'notifications/initialized' },
      { jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'get_all_open_file_paths', arguments: {} } },
    ]);
    assert.equal(answers.length, 2);
    assert.equal(answers[0].result.protocolVersion, '2024-11-05');
    assert.equal(answers[1].result.content[0].text, 'ran get_all_open_file_paths');
  }
  finally { await s.close(); }
  const none = await run(1, [{ jsonrpc: '2.0', id: 9, method: 'initialize', params: {} }]);
  assert.equal(none[0].id, 9);
  assert.match(none[0].error.message, /PyMagic is not running/);
});

test('the clients: PyMagic\'s entry added to a new file, merged into one with other servers, refused in one with comments, Codex\'s TOML section added and replaced', () => {
  const home = mkdtempSync(join(tmpdir(), 'pm-mcp-home-'));
  try {
    const ctx = { home, appData: join(home, 'Roaming'), port: 64352, exe: 'C:\\Program Files\\PyMagic\\PyMagic.exe', stdioScript: 'C:\\Program Files\\PyMagic\\resources\\app.asar\\mcp-stdio.js' };
    const io = { read: (f) => readFileSync(f, 'utf8'), exists: existsSync, write: (f, t) => { mkdirSync(dirname(f), { recursive: true }); writeFileSync(f, t); } };
    const list = clients.clients(ctx);
    const by = (id) => list.find((c) => c.id === id);
    assert.deepEqual(list.map((c) => c.id), ['claude-code', 'claude-desktop', 'cursor', 'vscode', 'windsurf', 'gemini', 'codex']);

    const cursor = by('cursor');
    assert.equal(clients.isConfigured(cursor, io.read), false);
    assert.ok(clients.configure(cursor, io).ok);
    assert.deepEqual(JSON.parse(readFileSync(cursor.file, 'utf8')), { mcpServers: { pymagic: { url: 'http://127.0.0.1:64352/mcp' } } });
    assert.equal(clients.isConfigured(cursor, io.read), true);

    const desktop = by('claude-desktop');
    io.write(desktop.file, JSON.stringify({ mcpServers: { other: { command: 'x' } }, theme: 'dark' }));
    assert.ok(clients.configure(desktop, io).ok);
    const d = JSON.parse(readFileSync(desktop.file, 'utf8'));
    assert.deepEqual(d.mcpServers.other, { command: 'x' });
    assert.equal(d.theme, 'dark');
    assert.deepEqual(d.mcpServers.pymagic, { command: ctx.exe, args: [ctx.stdioScript, '64352'], env: { ELECTRON_RUN_AS_NODE: '1' } });

    const vscode = by('vscode');
    const commented = '{\n  // mine\n  "servers": {}\n}\n';
    io.write(vscode.file, commented);
    const refused = clients.configure(vscode, io);
    assert.equal(refused.ok, false);
    assert.match(refused.message, /not plain JSON/);
    assert.match(refused.message, /"servers"/);
    assert.equal(readFileSync(vscode.file, 'utf8'), commented);

    const codex = by('codex');
    io.write(codex.file, 'model = "o4"\n\n[mcp_servers.pymagic]\ncommand = "old"\n\n[profiles.x]\nmodel = "y"\n');
    assert.ok(clients.configure(codex, io).ok);
    const toml = readFileSync(codex.file, 'utf8');
    assert.match(toml, /^model = "o4"/);
    assert.match(toml, /\[mcp_servers\.pymagic\]\ncommand = "C:\\\\Program Files\\\\PyMagic\\\\PyMagic\.exe"\nargs = \["C:\\\\Program Files/);
    assert.ok(!toml.includes('"old"'));
    assert.match(toml, /\[profiles\.x\]\nmodel = "y"/);
    assert.equal(clients.isConfigured(codex, io.read), true);
    rmSync(codex.file);
    assert.ok(clients.configure(codex, io).ok);
    assert.match(readFileSync(codex.file, 'utf8'), /^\[mcp_servers\.pymagic\]/);
  }
  finally { rmSync(home, { recursive: true, force: true }); }
});

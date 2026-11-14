'use strict';

// The MCP server for clients that start a program and talk over its stdin and stdout (Claude Desktop): each JSON-RPC line read is posted to
// PyMagic's MCP server (http://127.0.0.1:<port>/mcp) and its answer written back as a line. Run by PyMagic.exe itself as Node.js:
//   ELECTRON_RUN_AS_NODE=1 PyMagic.exe <this file> <port>
// Settings > MCP Server writes exactly that into the client's configuration.
const http = require('node:http');

const port = Number(process.argv[2]) || 64352;
const url = `http://127.0.0.1:${port}/mcp`;
let session = null;

const write = (msg) => process.stdout.write(JSON.stringify(msg) + '\n');

async function forward(line) {
  let msg;
  try { msg = JSON.parse(line); }
  catch { write({ jsonrpc: '2.0', id: null, error: { code: -32700, message: 'not JSON' } }); return; }
  const isRequest = (m) => m && typeof m === 'object' && m.method && m.id !== undefined && m.id !== null;
  try {
    const headers = { 'content-type': 'application/json', accept: 'application/json, text/event-stream' };
    if (session) headers['mcp-session-id'] = session;
    // a connection per message, closed after it (no keep-alive left open when stdin ends)
    const res = await new Promise((resolve, reject) => {
      const req = http.request(url, { method: 'POST', headers, agent: false }, (r) => {
        let text = '';
        r.setEncoding('utf8');
        r.on('data', (c) => { text += c; });
        r.on('end', () => resolve({ status: r.statusCode, session: r.headers['mcp-session-id'], text }));
        r.on('error', reject);
      });
      req.on('error', reject);
      req.end(line);
    });
    session = res.session ?? session;
    if (res.status === 202) return;
    const answer = JSON.parse(res.text);
    for (const a of Array.isArray(answer) ? answer : [answer]) write(a);
  }
  catch {
    // PyMagic is not running, or its MCP server is off: each request gets an error, so the client says so
    for (const m of Array.isArray(msg) ? msg : [msg])
      if (isRequest(m)) write({ jsonrpc: '2.0', id: m.id, error: { code: -32000, message: `PyMagic is not running, or its MCP server is off (Settings > MCP Server); nothing answers at ${url}` } });
  }
}

let buffer = '';
let chain = Promise.resolve();
process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => {
  buffer += chunk;
  let at;
  while ((at = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, at).trim();
    buffer = buffer.slice(at + 1);
    // in order: initialize is answered before the calls that follow it
    if (line) chain = chain.then(() => forward(line));
  }
});
process.stdin.on('end', () => { void chain.then(() => { process.exitCode = 0; }); });

'use strict';

// PyMagic's MCP server (the Model Context Protocol, as PyCharm's MCP Server): an AI agent on this computer connects to it and uses the IDE's tools
// (desktop/mcp-tools.js). Two transports on one port of 127.0.0.1: Streamable HTTP at /mcp (protocol 2025-06-18 and 2025-03-26: a POST answered with
// JSON) and the older HTTP with SSE at /sse (2024-11-05: the stream, and its POSTs to /message?sessionId=…). A client that only starts programs
// (Claude Desktop) reaches it through desktop/mcp-stdio.js.
// Only this computer reaches it: it listens on 127.0.0.1, takes a request only with Host 127.0.0.1 / localhost of its own port (no DNS rebinding),
// and refuses one a web page of another site sends (its Origin).
const http = require('node:http');
const crypto = require('node:crypto');
const { TOOLS, checkArguments } = require('./mcp-tools');

const VERSIONS = ['2025-06-18', '2025-03-26', '2024-11-05'];
const MAX_BODY = 8 * 1024 * 1024;

/**
 * The protocol, without the transport: `handle(message)` answers one JSON-RPC message (null for a notification or a response).
 * `callTool(name, args)` carries out a tool: resolves to { text, isError? }, or throws (the message goes back as the tool's error).
 */
function createHandler({ callTool, version = '0.1.0', onCall = null }) {
  const byName = new Map(TOOLS.map((t) => [t.name, t]));
  const ok = (id, result) => ({ jsonrpc: '2.0', id, result });
  const fail = (id, code, message) => ({ jsonrpc: '2.0', id: id ?? null, error: { code, message } });

  async function handle(msg) {
    if (!msg || typeof msg !== 'object' || Array.isArray(msg) || msg.jsonrpc !== '2.0') return fail(msg?.id, -32600, 'not a JSON-RPC 2.0 message');
    const isRequest = typeof msg.method === 'string' && msg.id !== undefined && msg.id !== null;
    if (typeof msg.method !== 'string') return null;   // a response to something the server never asks: nothing to do
    if (!isRequest) return null;                       // notifications/initialized, notifications/cancelled, …
    const { id, method } = msg;
    const params = msg.params ?? {};
    switch (method) {
      case 'initialize': {
        const asked = String(params.protocolVersion ?? '');
        return ok(id, {
          protocolVersion: VERSIONS.includes(asked) ? asked : VERSIONS[0],
          capabilities: { tools: { listChanged: false } },
          serverInfo: { name: 'PyMagic', title: 'PyMagic IDE', version },
          instructions: 'PyMagic is a Python IDE. These tools read and change the files of the project open in it, search it, show its problems, '
            + 'rename symbols, format files, and run its run configurations and shell commands. Paths are relative to the project folder.',
        });
      }
      case 'ping': return ok(id, {});
      case 'tools/list': return ok(id, { tools: TOOLS.map(({ name, description, inputSchema, annotations }) => ({ name, description, inputSchema, annotations })) });
      case 'tools/call': {
        const def = byName.get(params.name);
        if (!def) return fail(id, -32602, `no tool ${params.name}`);
        const args = params.arguments ?? {};
        const wrong = checkArguments(def, args);
        if (wrong) return ok(id, { content: [{ type: 'text', text: wrong }], isError: true });
        onCall?.(def.name, args);
        try {
          const r = await callTool(def.name, args);
          return ok(id, { content: [{ type: 'text', text: String(r?.text ?? '') }], isError: !!r?.isError });
        }
        catch (e) {
          return ok(id, { content: [{ type: 'text', text: String(e?.message ?? e) }], isError: true });
        }
      }
      case 'resources/list': return ok(id, { resources: [] });
      case 'resources/templates/list': return ok(id, { resourceTemplates: [] });
      case 'prompts/list': return ok(id, { prompts: [] });
      default: return fail(id, -32601, `no method ${method}`);
    }
  }

  /** A POST's body: one message or a batch. Gives what to answer (an object, an array, or null when there is nothing to answer). */
  async function handleBody(text) {
    let parsed;
    try { parsed = JSON.parse(text); }
    catch { return fail(null, -32700, 'the body is not JSON'); }
    if (Array.isArray(parsed)) {
      if (!parsed.length) return fail(null, -32600, 'an empty batch');
      const answers = (await Promise.all(parsed.map(handle))).filter(Boolean);
      return answers.length ? answers : null;
    }
    return handle(parsed);
  }
  return { handle, handleBody };
}

/** Whether a request may reach the server: its Host is this server's own address, and an Origin, if it has one, is a page of this computer. */
function allowed(headers, port) {
  const host = String(headers.host ?? '').toLowerCase();
  if (![`127.0.0.1:${port}`, `localhost:${port}`, `[::1]:${port}`].includes(host)) return false;
  const origin = headers.origin;
  if (!origin || origin === 'null') return true;
  try { const o = new URL(origin); return ['127.0.0.1', 'localhost', '[::1]'].includes(o.hostname); }
  catch { return false; }
}

/** What a browser shows at the server's address: it runs, and this is not a page but an address for an AI client. */
function helpText(port) {
  return `PyMagic's MCP server is running.\n\nThis address is for an AI client, not for a browser. Add it to the client:\n`
    + `  Streamable HTTP: http://127.0.0.1:${port}/mcp\n  HTTP with SSE:   http://127.0.0.1:${port}/sse\n\n`
    + `Claude Code:  claude mcp add --transport http --scope user pymagic http://127.0.0.1:${port}/mcp\n`
    + `Or in PyMagic: Settings > MCP Server > Clients > Configure.\n`;
}

/** Starts the server on 127.0.0.1:`port`. Resolves to { port, close() } once it listens; rejects when the port is taken. */
function startServer({ port, handler, log = () => {} }) {
  const sseStreams = new Map();   // session id -> the response of its /sse stream
  const sessions = new Set();
  const server = http.createServer((req, res) => {
    const url = new URL(req.url ?? '/', 'http://127.0.0.1');
    if (!allowed(req.headers, server.address()?.port ?? port)) {
      res.writeHead(403, { 'content-type': 'text/plain' }).end('forbidden: only programs of this computer may use PyMagic\'s MCP server');
      return;
    }
    const readBody = () => new Promise((resolve, reject) => {
      const parts = [];
      let size = 0;
      req.on('data', (c) => { size += c.length; if (size > MAX_BODY) { reject(new Error('too large')); req.destroy(); } else parts.push(c); });
      req.on('end', () => resolve(Buffer.concat(parts).toString('utf8')));
      req.on('error', reject);
    });
    const json = (status, body, headers = {}) => res.writeHead(status, { 'content-type': 'application/json', ...headers }).end(JSON.stringify(body));

    if (url.pathname === '/mcp') {
      if (req.method === 'POST') {
        readBody().then(async (text) => {
          const answer = await handler.handleBody(text);
          if (answer === null) { res.writeHead(202).end(); return; }
          const headers = {};
          const initialized = (Array.isArray(answer) ? answer : [answer]).some((a) => a.result?.serverInfo);
          if (initialized) { const sid = crypto.randomUUID(); sessions.add(sid); headers['mcp-session-id'] = sid; }
          json(200, answer, headers);
        }).catch((e) => { if (!res.headersSent) json(413, { jsonrpc: '2.0', id: null, error: { code: -32600, message: String(e.message) } }); });
        return;
      }
      if (req.method === 'DELETE') { sessions.delete(String(req.headers['mcp-session-id'] ?? '')); res.writeHead(200).end(); return; }
      // no stream of the server's own messages: it sends none (a client asking for one is told so, as the protocol says)
      if (String(req.headers.accept ?? '').includes('text/event-stream') || req.method !== 'GET') {
        res.writeHead(405, { allow: 'POST, DELETE' }).end();
        return;
      }
      // the address opened in a browser: what it is and how a client connects
      res.writeHead(200, { 'content-type': 'text/plain; charset=utf-8' }).end(helpText(server.address().port));
      return;
    }
    if (url.pathname === '/sse' && req.method === 'GET') {
      const sid = crypto.randomUUID();
      res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache', connection: 'keep-alive' });
      res.write(`event: endpoint\ndata: /message?sessionId=${sid}\n\n`);
      sseStreams.set(sid, res);
      const beat = setInterval(() => res.write(': keep-alive\n\n'), 15000);
      req.on('close', () => { clearInterval(beat); sseStreams.delete(sid); });
      return;
    }
    if (url.pathname === '/message' && req.method === 'POST') {
      const stream = sseStreams.get(url.searchParams.get('sessionId') ?? '');
      if (!stream) { res.writeHead(404, { 'content-type': 'text/plain' }).end('no such session'); return; }
      readBody().then(async (text) => {
        res.writeHead(202).end();
        const answer = await handler.handleBody(text);
        if (answer !== null) stream.write(`event: message\ndata: ${JSON.stringify(answer)}\n\n`);
      }).catch(() => { if (!res.headersSent) res.writeHead(413).end(); });
      return;
    }
    if (url.pathname === '/' && req.method === 'GET') {
      res.writeHead(200, { 'content-type': 'text/plain; charset=utf-8' }).end(helpText(server.address().port));
      return;
    }
    res.writeHead(404).end();
  });
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, '127.0.0.1', () => {
      server.off('error', reject);
      server.on('error', (e) => log(String(e)));
      resolve({
        port: server.address().port,
        close: () => new Promise((done) => {
          for (const s of sseStreams.values()) s.end();
          sseStreams.clear();
          server.closeAllConnections?.();
          server.close(() => done());
        }),
      });
    });
  });
}

module.exports = { createHandler, startServer, allowed, VERSIONS };

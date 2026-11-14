'use strict';

// PyMagic's MCP server in the app: started and stopped by Settings > MCP Server (kept in the app's settings.json as `mcp`), each tool call sent to
// the page of the project it is for, the shell commands run here, the clients' configurations written. The protocol is in mcp-server.js.
const { spawn, spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createHandler, startServer } = require('./mcp-server');
const { IN_MAIN } = require('./mcp-tools');
const mcpClients = require('./mcp-clients');

const DEFAULT_PORT = 64352;
const lastLines = (text, max) => {
  const lines = text.split(/\r?\n/);
  if (lines.length <= max) return { text, cut: 0 };
  return { text: lines.slice(lines.length - max).join('\n'), cut: lines.length - max };
};
const samePath = (a, b) => path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();

/** `deps`: { windows (the Set of { win, port, project, used }), readSettings, writeSettings, dialog, version } */
function setupMcp(deps) {
  let server = null, error = '', starting = null;
  const calls = [];   // the last tool calls, for Settings > MCP Server
  const config = () => ({ enabled: false, port: DEFAULT_PORT, brave: false, ...(deps.readSettings().mcp ?? {}) });
  const keep = (patch) => { const s = deps.readSettings(); s.mcp = { ...config(), ...patch }; deps.writeSettings(s); };

  const projects = () => [...deps.windows].filter((w) => w.project && w.port && !w.win.isDestroyed());
  function windowFor(projectPath) {
    const open = projects();
    if (!open.length) throw new Error('No project is open in PyMagic.');
    if (projectPath) {
      const w = open.find((o) => samePath(o.project, projectPath));
      if (!w) throw new Error(`${projectPath} is not open in PyMagic; open: ${open.map((o) => o.project).join(', ')}`);
      return w;
    }
    return open.find((o) => o.win.isFocused()) ?? open.sort((a, b) => (b.used ?? 0) - (a.used ?? 0))[0];
  }

  /** Asks the person in the project's window; `brave` (Settings) skips it, and "Don't ask again" sets it. */
  async function confirm(w, title, message, detail) {
    if (config().brave) return true;
    const r = await deps.dialog.showMessageBox(w.win, {
      type: 'question', title, message, detail, buttons: ['Run', 'Don\'t run'], defaultId: 1, cancelId: 1, noLink: true,
      checkboxLabel: 'Don\'t ask again (Settings > MCP Server)',
    });
    if (r.response !== 0) return false;
    if (r.checkboxChecked) keep({ brave: true });
    return true;
  }

  async function interpreterOf(w) {
    try { const r = await fetch(`http://127.0.0.1:${w.port}/api/interpreter`); return r.ok ? await r.json() : {}; } catch { return {}; }
  }

  async function terminalCommand(w, args) {
    const command = String(args.command);
    if (!(await confirm(w, 'MCP: run a command', 'An AI agent connected over MCP wants to run this command in the project folder:', command)))
      return { text: 'The person chose not to run the command.', isError: true };
    const env = { ...process.env, PYTHONIOENCODING: 'utf-8', PYTHONUTF8: '1' };
    delete env.ELECTRON_RUN_AS_NODE;
    const interp = await interpreterOf(w);
    if (interp.path && fs.existsSync(interp.path)) {
      const key = Object.keys(env).find((k) => k.toUpperCase() === 'PATH') ?? 'Path';
      env[key] = path.dirname(interp.path) + path.delimiter + (env[key] ?? '');
      if (interp.isVenv) env.VIRTUAL_ENV = path.dirname(path.dirname(interp.path));
    }
    const timeout = args.timeout ?? 120000, max = args.maxLinesCount ?? 1000;
    return new Promise((resolve) => {
      let out = '';
      let timedOut = false;
      const child = spawn(command, { cwd: w.project, env, shell: true, windowsHide: true });
      const take = (c) => { out += c.toString('utf8'); if (out.length > 4_000_000) out = out.slice(-2_000_000); };
      child.stdout.on('data', take);
      child.stderr.on('data', take);
      const timer = setTimeout(() => {
        timedOut = true;
        if (process.platform === 'win32') spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true });
        else child.kill('SIGKILL');
      }, timeout);
      const done = (code) => {
        clearTimeout(timer);
        const { text, cut } = lastLines(out.replace(/\s+$/, ''), max);
        const head = timedOut ? `Stopped after ${timeout} ms.` : `Exit code: ${code}`;
        resolve({ text: `${head}\n${cut ? `(the first ${cut} lines are left out)\n` : ''}${text}`, isError: timedOut || code !== 0 });
      };
      child.on('error', (e) => { out += String(e.message); done(-1); });
      child.on('close', (code) => done(code));
    });
  }

  async function callTool(name, args) {
    const w = windowFor(args.projectPath);
    const entry = { at: new Date().toISOString(), tool: name, project: w.project, ok: null };
    calls.unshift(entry);
    calls.length = Math.min(calls.length, 50);
    try {
      let r;
      if (name === 'execute_terminal_command') r = await terminalCommand(w, args);
      else if (name === 'get_project_info') {
        const interp = await interpreterOf(w);
        r = { text: JSON.stringify({ projectPath: w.project, name: path.basename(w.project), interpreter: interp.path ? { path: interp.path, version: interp.version, label: interp.label, isVenv: !!interp.isVenv } : null,
          openProjects: projects().map((o) => o.project) }, null, 2) };
      }
      else {
        if (name === 'execute_run_configuration'
          && !(await confirm(w, 'MCP: run a configuration', 'An AI agent connected over MCP wants to run this run configuration:', String(args.configurationName))))
          r = { text: 'The person chose not to run it.', isError: true };
        else {
          const { projectPath, ...rest } = args;
          // a project still opening sets its tools up last: waited for, up to 30 seconds
          const code = `(async () => { for (let i = 0; i < 150 && !(window.pymagic && window.pymagic.mcp); i++) await new Promise((r) => setTimeout(r, 200));
            return window.pymagic && window.pymagic.mcp ? window.pymagic.mcp.call(${JSON.stringify(name)}, ${JSON.stringify(rest)}) : { text: 'The project is still opening; try again.', isError: true }; })()`;
          r = await w.win.webContents.executeJavaScript(code, true);
        }
      }
      entry.ok = !r?.isError;
      return r;
    }
    catch (e) { entry.ok = false; throw e; }
  }

  const handler = createHandler({ callTool, version: deps.version });

  async function apply() {
    if (starting) await starting;
    const c = config();
    const want = c.enabled ? c.port : 0;
    if (server && server.port === want) return;
    if (server) { await server.close(); server = null; }
    error = '';
    if (!want) return;
    starting = startServer({ port: want, handler, log: (t) => { error = t; } })
      .then((s) => { server = s; })
      .catch((e) => { error = e.code === 'EADDRINUSE' ? `Port ${want} is taken by another program; choose another one.` : String(e.message ?? e); })
      .finally(() => { starting = null; });
    await starting;
  }

  const ctx = () => ({
    home: os.homedir(), appData: process.env.APPDATA ?? path.join(os.homedir(), 'AppData', 'Roaming'), port: config().port,
    exe: process.execPath, stdioScript: path.join(__dirname, 'mcp-stdio.js'),
  });
  const io = {
    read: (f) => fs.readFileSync(f, 'utf8'),
    exists: (f) => fs.existsSync(f),
    write: (f, t) => { fs.mkdirSync(path.dirname(f), { recursive: true }); fs.writeFileSync(f, t); },
  };
  function status() {
    const c = config();
    return {
      enabled: c.enabled, port: c.port, brave: c.brave, running: !!server, error,
      url: `http://127.0.0.1:${c.port}/mcp`, sseUrl: `http://127.0.0.1:${c.port}/sse`,
      clients: mcpClients.clients(ctx()).map((k) => ({ id: k.id, name: k.name, file: k.file, configured: mcpClients.isConfigured(k, io.read), snippet: mcpClients.snippet(k), command: k.command?.join(' ') ?? null })),
      calls: calls.slice(0, 20),
    };
  }
  async function set(patch) {
    const p = {};
    if (typeof patch?.enabled === 'boolean') p.enabled = patch.enabled;
    if (typeof patch?.brave === 'boolean') p.brave = patch.brave;
    if (Number.isInteger(patch?.port) && patch.port >= 1024 && patch.port <= 65535) p.port = patch.port;
    keep(p);
    await apply();
    return status();
  }
  function configure(id) {
    const client = mcpClients.clients(ctx()).find((k) => k.id === id);
    if (!client) return { ok: false, message: 'no such client' };
    // Claude Code: its own command when it is installed (it writes its ~/.claude.json itself, which it also writes while it runs)
    if (client.command) {
      const r = spawnSync(client.command[0], client.command.slice(1), { shell: true, windowsHide: true, encoding: 'utf8', timeout: 30000 });
      if (r.status === 0) return { ok: true, message: `Added with: ${client.command.join(' ')}` };
      const said = `${r.stdout ?? ''}${r.stderr ?? ''}`;
      if (/already exists/i.test(said)) return { ok: true, message: 'Claude Code has PyMagic already (claude mcp remove pymagic, then Configure, to write it again).' };
    }
    try { return mcpClients.configure(client, io); }
    catch (e) { return { ok: false, message: String(e.message ?? e) }; }
  }
  return { apply, status, set, configure, callTool };
}

module.exports = { setupMcp, DEFAULT_PORT };

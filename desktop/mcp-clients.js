'use strict';

// Settings > MCP Server's clients: where each AI client keeps its MCP servers, what PyMagic's entry there is, and adding it (PyCharm's
// Auto-Configure). Only PyMagic's own entry ("pymagic") is added or replaced; the rest of the file stays as it is, and a file that is not plain
// JSON (comments) is not touched: its entry is given to copy instead. Kept apart from main.js so desktop/test/mcp-clients.test.mjs can check it
// with folders of its own.
const path = require('node:path');

const NAME = 'pymagic';

/** `ctx`: { home, appData, port, exe, stdioScript }. Each client: id, its name, its file, and the entry PyMagic adds to it. */
function clients(ctx) {
  const url = `http://127.0.0.1:${ctx.port}/mcp`;
  const stdio = { command: ctx.exe, args: [ctx.stdioScript, String(ctx.port)], env: { ELECTRON_RUN_AS_NODE: '1' } };
  return [
    { id: 'claude-code', name: 'Claude Code', file: path.join(ctx.home, '.claude.json'), key: 'mcpServers', entry: { type: 'http', url },
      command: ['claude', 'mcp', 'add', '--transport', 'http', '--scope', 'user', NAME, url] },
    { id: 'claude-desktop', name: 'Claude Desktop', file: path.join(ctx.appData, 'Claude', 'claude_desktop_config.json'), key: 'mcpServers', entry: stdio },
    { id: 'cursor', name: 'Cursor', file: path.join(ctx.home, '.cursor', 'mcp.json'), key: 'mcpServers', entry: { url } },
    { id: 'vscode', name: 'VS Code', file: path.join(ctx.appData, 'Code', 'User', 'mcp.json'), key: 'servers', entry: { type: 'http', url } },
    { id: 'windsurf', name: 'Windsurf', file: path.join(ctx.home, '.codeium', 'windsurf', 'mcp_config.json'), key: 'mcpServers', entry: { serverUrl: url } },
    { id: 'gemini', name: 'Gemini CLI', file: path.join(ctx.home, '.gemini', 'settings.json'), key: 'mcpServers', entry: { httpUrl: url } },
    { id: 'codex', name: 'Codex', file: path.join(ctx.home, '.codex', 'config.toml'), toml: true, entry: stdio },
  ];
}

const tomlString = (s) => '"' + String(s).replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"';
/** Codex's section for PyMagic. */
function tomlSection(entry) {
  return `[mcp_servers.${NAME}]\ncommand = ${tomlString(entry.command)}\nargs = [${entry.args.map(tomlString).join(', ')}]\n`
    + `env = { ${Object.entries(entry.env).map(([k, v]) => `${k} = ${tomlString(v)}`).join(', ')} }\n`;
}

/** What the person copies into the client's file by hand. */
function snippet(client) {
  if (client.toml) return tomlSection(client.entry);
  return JSON.stringify({ [client.key]: { [NAME]: client.entry } }, null, 2);
}

/** Whether the client's file has PyMagic's entry (any; a different port shows as configured too, and Configure writes it again). */
function isConfigured(client, read) {
  let text;
  try { text = read(client.file); } catch { return false; }
  if (client.toml) return new RegExp(`^\\[mcp_servers\\.${NAME}\\]`, 'm').test(text);
  try { return !!JSON.parse(text || '{}')?.[client.key]?.[NAME]; } catch { return false; }
}

/**
 * Adds PyMagic's entry to the client's file (made, with its folders, when it is not there). `io`: { read(file), write(file, text), exists(file) }.
 * Gives { ok, message }.
 */
function configure(client, io) {
  let text = '';
  if (io.exists(client.file)) {
    try { text = io.read(client.file); } catch (e) { return { ok: false, message: `${client.file} cannot be read: ${e.message}` }; }
  }
  if (client.toml) {
    // PyMagic's section replaced (up to the next section), or added at the end
    const lines = text.split(/\r?\n/);
    const start = lines.findIndex((l) => l.trim() === `[mcp_servers.${NAME}]`);
    let kept;
    if (start < 0) kept = text.replace(/\s*$/, '') + (text.trim() ? '\n\n' : '') + tomlSection(client.entry);
    else {
      let end = start + 1;
      while (end < lines.length && !/^\s*\[/.test(lines[end])) end++;
      kept = [...lines.slice(0, start), ...tomlSection(client.entry).trimEnd().split('\n'), ...(end < lines.length ? [''] : []), ...lines.slice(end)].join('\n');
      if (!kept.endsWith('\n')) kept += '\n';
    }
    io.write(client.file, kept);
    return { ok: true, message: `Added to ${client.file}` };
  }
  let data;
  try { data = text.trim() ? JSON.parse(text) : {}; }
  catch { return { ok: false, message: `${client.file} is not plain JSON (it may have comments), so PyMagic leaves it as it is; add this to it:\n${snippet(client)}` }; }
  if (!data || typeof data !== 'object' || Array.isArray(data)) return { ok: false, message: `${client.file} does not hold a JSON object; add this to it:\n${snippet(client)}` };
  const servers = data[client.key] && typeof data[client.key] === 'object' && !Array.isArray(data[client.key]) ? data[client.key] : {};
  data[client.key] = { ...servers, [NAME]: client.entry };
  io.write(client.file, JSON.stringify(data, null, 2) + '\n');
  return { ok: true, message: `Added to ${client.file}` };
}

module.exports = { clients, configure, isConfigured, snippet, tomlSection, NAME };

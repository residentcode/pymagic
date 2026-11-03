// Loads the page, presses the rail, the dock tabs and the command bar, runs a file, and prints every error the page raised (usage: node web/tools/check-page.mjs <url>).
import { spawn, spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { removeTree } from './remove-tree.mjs';

const url = process.argv[2];
const port = 9900 + Math.floor(Math.random() * 90);
const profile = join(tmpdir(), 'pm_check_' + port);
const edge = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  [`--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--headless=new', '--window-size=1440,900', 'about:blank'], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let targets;
for (let i = 0; i < 50; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); if (targets.length) break; } catch { /* not yet */ } await sleep(200); }
const ws = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
await new Promise(r => ws.onopen = r);
let id = 0; const waiting = new Map(); const errors = [];
ws.onmessage = e => {
  const m = JSON.parse(e.data);
  if (m.id && waiting.has(m.id)) { waiting.get(m.id)(m); waiting.delete(m.id); }
  if (m.method === 'Runtime.exceptionThrown') errors.push('exception: ' + (m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text));
  if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') errors.push('console.error: ' + m.params.args.map(a => a.value ?? a.description).join(' '));
  if (m.method === 'Log.entryAdded' && m.params.entry.level === 'error') errors.push('log: ' + m.params.entry.text + ' ' + (m.params.entry.url ?? ''));
};
const call = (method, params = {}) => new Promise(r => { const i = ++id; waiting.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const run = async code => (await call('Runtime.evaluate', { expression: code, awaitPromise: true, returnByValue: true })).result?.result?.value;
await call('Runtime.enable'); await call('Log.enable'); await call('Page.enable');
await call('Page.navigate', { url });
await sleep(3500);
const ids = ['r-search', 'r-git', 'r-tests', 'r-problems', 'r-terminal', 'r-packages', 'r-explorer', 'r-explorer',
  't-output', 't-debug', 't-terminal', 't-tests', 't-coverage', 't-profiler', 't-problems', 't-todo', 't-insp', 't-hier', 't-search', 't-git', 't-packages', 't-routes', 't-django', 't-close',
  'ex-refresh', 'ex-collapse', 'b-format', 'b-imports', 'branch-chip'];
for (const idn of ids) { await run(`document.getElementById(${JSON.stringify(idn)})?.click()`); await sleep(350); }
// run something: a program with output, then the debug label
await run(`[...document.querySelectorAll('#files li')].find(l => l.textContent.includes('main.py'))?.click()`);
await sleep(800);
await run(`document.getElementById('b-run').click()`);
await sleep(2500);
const state = await run(`JSON.stringify({ active: document.querySelector('#tabs button.active')?.id, sidebarHidden: document.getElementById('app').classList.contains('no-side'), debugLabel: document.querySelector('#b-debug .lbl').textContent, project: document.getElementById('project').textContent, branch: document.querySelector('#branch-chip span:last-child').textContent, problemsBadge: document.querySelector('#r-problems .badge').textContent, files: document.querySelectorAll('#files li').length })`);
console.log(state);
console.log(errors.length ? errors.join('\n') : 'no errors');
ws.close();
// Edge and its helper processes (only the tree started here), then its profile, which would otherwise stay in the temp folder
if (edge.exitCode === null) spawnSync('taskkill', ['/PID', String(edge.pid), '/T', '/F'], { stdio: 'ignore' });
removeTree(profile);

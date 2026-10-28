// Takes screenshots of the running PyMagic page at a fixed window size, through the DevTools protocol of Microsoft Edge (Windows).
// usage: node web/tools/shoot.mjs <url> <out dir> <prefix> [width] [height]   (the page is the host of a project that has checkout.py, e.g. examples/demo)
import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const [url, outDir, prefix, w = '1440', h = '900'] = process.argv.slice(2);
mkdirSync(outDir, { recursive: true });
const port = 9333 + Math.floor(Math.random() * 500);
const profile = join(tmpdir(), 'pm_shoot_' + port);
const edge = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  [`--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--headless=new', `--window-size=${w},${h}`, '--hide-scrollbars', '--force-device-scale-factor=1', 'about:blank'], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let targets;
for (let i = 0; i < 50; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); if (targets.length) break; } catch { /* not up yet */ } await sleep(200); }
const page = targets.find(t => t.type === 'page');
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise(r => ws.onopen = r);
let id = 0; const waiting = new Map();
ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id && waiting.has(m.id)) { waiting.get(m.id)(m); waiting.delete(m.id); } };
const call = (method, params = {}) => new Promise(r => { const i = ++id; waiting.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const run = async code => (await call('Runtime.evaluate', { expression: code, awaitPromise: true, returnByValue: true })).result?.result?.value;
const shot = async name => {
  const r = await call('Page.captureScreenshot', { format: 'png' });
  writeFileSync(join(outDir, `${prefix}-${name}.png`), Buffer.from(r.result.data, 'base64'));
  console.log('shot', name);
};
await call('Page.enable');
await call('Emulation.setDeviceMetricsOverride', { width: +w, height: +h, deviceScaleFactor: 1, mobile: false });
await call('Page.navigate', { url });
await sleep(3500);
const click = sel => run(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); if (e) e.click(); return !!e; })()`);
const openFile = name => run(`(() => { const li = [...document.querySelectorAll('#files li')].find(l => l.textContent.includes(${JSON.stringify(name)})); if (li) li.click(); return !!li; })()`);

await openFile('checkout.py');
await sleep(1500);
if (process.env.PROBE) { console.log(await run(process.env.PROBE)); ws.close(); edge.kill(); process.exit(0); }
await shot('1-editor');
await click('#b-tests'); await sleep(800);
await click('#t-problems'); await sleep(2500);
await shot('2-problems');
await click('#t-tests'); await sleep(1200);
await shot('3-tests');
await click('#t-git'); await sleep(1500);
await shot('4-git');
await click('#t-debug'); await sleep(600);
await shot('5-debugger');
await click('#t-terminal'); await sleep(800);
await shot('6-terminal');
await click('#t-packages'); await sleep(2500);
await shot('7-packages');
await click('#t-insp'); await sleep(800);
await shot('8-inspections');
ws.close(); edge.kill();
await sleep(500);
try { rmSync(profile, { recursive: true, force: true }); } catch { /* still in use */ }

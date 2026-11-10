// PyMagic's Node.js runner and debugger: what pm_debug.py is for Python, for JavaScript and TypeScript run by Node.
//
// It runs a file (`node file`) or an npm script (`npm run name`). Run alone (no --port) it only starts the program, with the inspector's own
// lines kept out of the output, and ends with its exit code. Under the debugger it speaks to PyMagic the same JSON lines as pm_debug.py does
// (hello, setBreakpoints, go, stopped, scopes, variables, evaluate, setVariable, continue / next / stepIn / stepOut, ...), and to every Node
// process of the run through Node's inspector (the Chrome DevTools Protocol): a small file preloaded into each process (NODE_OPTIONS
// --require, so the processes an npm script or a framework starts are reached too) opens its inspector, writes where it listens to a file
// this helper reads, and waits until this helper has attached and set the breakpoints. npm's own process is left alone.
//
// One process is shown stopped at a time; another that stops meanwhile is shown when the shown one goes on. Code in node_modules and
// Node's own is skipped when stepping (Debugger.setBlackboxPatterns), as pm_debug.py skips library code.
import { spawn } from 'node:child_process';
import { appendFileSync, existsSync, readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { createConnection } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { loadMap, prepare, generatedFor, originalFor } from './pm_sourcemap.mjs';

// ------------------------------------------------------------ arguments
const argv = process.argv.slice(2);
const opt = { args: [] };
for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--') { opt.args = argv.slice(i + 1); break; }
    if (a === '--port') opt.port = Number(argv[++i]);
    else if (a === '--host') opt.host = argv[++i];
    else if (a === '--token') opt.token = argv[++i];
    else if (a === '--root') opt.root = argv[++i];
    else if (a === '--script') opt.script = argv[++i];
    else if (a === '--npm') opt.npm = argv[++i];
    else if (a === '--all-code') opt.allCode = true;
    else if (a === '--browser') opt.browser = argv[++i];            // a page to debug in Edge or Chrome (JavaScript Debug)
    else if (a === '--browser-path') opt.browserPath = argv[++i];   // the browser to start; empty: Edge, else Chrome
    else if (a === '--web-root') opt.webRoot = argv[++i];           // the folder the page's addresses are files of (a Vite app's)
    else if (a === '--attach') opt.attach = argv[++i];              // host:port of the inspector of a Node process already running
    else if (a === '--attach-pid') opt.attachPid = Number(argv[++i]);   // a Node process of this computer whose inspector is opened first
}
const debugging = Number.isInteger(opt.port) && opt.port > 0;
// run as the helper (not imported by a test of its functions)
const isMain = !!process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url);

// ------------------------------------------------------------ the program
/** npm as Node itself runs it (no shell): node_modules/npm/bin/npm-cli.js beside node; else the npm command through a shell. */
function npmCommand() {
    const cli = join(dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npm-cli.js');
    return existsSync(cli) ? { program: process.execPath, prefix: [cli], shell: false } : { program: 'npm', prefix: [], shell: process.platform === 'win32' };
}

/** The lines Node's inspector writes on stderr, which are the debugger's business, not the program's output. */
const INSPECTOR_LINE = /^(Debugger listening on ws:|For help, see: https:\/\/nodejs\.org\/|Debugger attached\.|Waiting for the debugger to disconnect)/;

let child = null;
function startProgram(env) {
    let program, args, shell = false;
    if (opt.npm) {
        const npm = npmCommand();
        program = npm.program;
        args = [...npm.prefix, 'run', opt.npm, ...(opt.args.length ? ['--', ...opt.args] : [])];
        shell = npm.shell;
    }
    else {
        program = process.execPath;
        args = [opt.script, ...opt.args];
    }
    child = spawn(program, args, { cwd: process.cwd(), env, stdio: ['inherit', 'inherit', 'pipe'], shell, windowsHide: true });
    // stderr as the program writes it, without the inspector's own lines
    let rest = '';
    child.stderr.on('data', (chunk) => {
        const text = rest + chunk.toString('utf8');
        const lines = text.split('\n');
        rest = lines.pop();
        const kept = lines.filter((l) => !INSPECTOR_LINE.test(l.replace(/\r$/, '')));
        if (kept.length) process.stderr.write(kept.join('\n') + '\n');
        if (rest && !INSPECTOR_LINE.test(rest) && !'Debugger listening on ws:'.startsWith(rest) && !'For help, see:'.startsWith(rest)) { process.stderr.write(rest); rest = ''; }
    });
    child.stderr.on('end', () => { if (rest && !INSPECTOR_LINE.test(rest)) process.stderr.write(rest); });
    child.on('error', (e) => { process.stderr.write(`cannot start ${program}: ${e.message}\n`); finish(1); });
    child.on('exit', (code, signal) => finish(code ?? (signal ? 1 : 0)));
}

let finished = false;
function finish(code) {
    if (finished) return;
    finished = true;
    clearInterval(watchTimer);
    for (const s of sessions.values()) s.close();
    try { if (inspectFile) rmSync(dirname(inspectFile), { recursive: true, force: true }); } catch { /* left for the temp folder */ }
    try { if (browserProfile) rmSync(browserProfile, { recursive: true, force: true, maxRetries: 3 }); } catch { /* the browser still holds it: left for the temp folder */ }
    // the last messages go out before the end
    if (channel) channel.end(() => process.exit(code));
    else process.exit(code);
    setTimeout(() => process.exit(code), 1000).unref();
}

// ------------------------------------------------------------ run without the debugger
if (isMain && !debugging) {
    startProgram({ ...process.env, FORCE_COLOR: process.env.FORCE_COLOR ?? '0' });
}

// ------------------------------------------------------------ the debugger: PyMagic's side
let channel = null;
let inspectFile = null;
let watchTimer = 0;
const sessions = new Map();        // pid -> Session
const flat = new Map();            // sessionId -> Session: the targets reached through another's connection (the browser's tabs, their frames and workers)
let firstPageSeen = false;         // the browser's first page is "the page in the browser"; the others are tabs
let childIds = 0;
let shown = null;                  // the Session shown stopped
const waiting = [];                // Sessions stopped while another one is shown
const breakpoints = new Map();     // canonical path -> { file, specs: [{line, condition, hitCondition, logMessage}] }
let exceptionMode = 'uncaught';
let exceptionClasses = [];
let muted = false;
let nextRef = 1;
const refs = new Map();            // ref -> { session, objectId } | { session, scope: {frame, index}, objectId }

const canonical = (p) => (process.platform === 'win32' ? resolve(p).toLowerCase() : resolve(p));

function send(message) {
    if (channel && !channel.destroyed) channel.write(JSON.stringify(message) + '\n');
}

if (isMain && debugging) {
    channel = createConnection({ host: opt.host ?? '127.0.0.1', port: opt.port }, () => send({ type: 'hello', token: opt.token, ...(process.env.PYMAGIC_DEBUG_NAME ? { name: process.env.PYMAGIC_DEBUG_NAME } : {}) }));
    channel.setEncoding('utf8');
    let buffer = '';
    channel.on('data', (data) => {
        buffer += data;
        let nl;
        while ((nl = buffer.indexOf('\n')) >= 0) {
            const line = buffer.slice(0, nl);
            buffer = buffer.slice(nl + 1);
            let msg;
            try { msg = JSON.parse(line); } catch { continue; }
            void handle(msg).catch((e) => { if (msg.id !== undefined) send({ type: answerType(msg.type), id: msg.id, error: String(e?.message ?? e) }); });
        }
    });
    channel.on('error', () => { if (child) child.kill(); process.exit(1); });
    channel.on('close', () => { if (!finished) { if (child) child.kill(); process.exit(0); } });
}

/** Whether a script's map was made from another text of the file than the one on disk now (the file edited and saved since). */
/** (Only a script whose text was the file's when it loaded: a map whose text is a preprocessor's is taken as current.) */
function madeFromOlder(prepared, key) {
    if (!prepared?.sameAsDisk?.has(key)) return false;
    return !sameText(key, prepared.contents.get(key));
}
function sameText(file, text) {
    const lf = (t) => t.replace(/\r\n/g, '\n');
    try { return lf(readFileSync(file, 'utf8')) === lf(text); }
    catch { return false; }
}
const ANSWERS = { scopes: 'scopes', variables: 'variables', evaluate: 'evaluated', setVariable: 'variableSet', values: 'values', table: 'table', jump: 'jumped', setName: 'setName', reprOf: 'reprOf', typeSource: 'typeSource' };
const answerType = (kind) => ANSWERS[kind] ?? 'evaluated';

async function handle(msg) {
    const kind = msg.type;
    switch (kind) {
        case 'setBreakpoints': {
            const file = msg.file ?? '';
            breakpoints.set(canonical(file), { file, specs: (msg.lines ?? []).map((l) => (typeof l === 'object' ? l : { line: l })) });
            if (sessions.size) await scanCompiled().catch(() => {});   // built again since? (only the changed files are read)
            for (const s of sessions.values()) { await s.applyBreakpoints(file); await s.tellBreaks(); }
            return;
        }
        case 'setExceptionBreak':
            exceptionMode = msg.mode === 'raised' ? 'raised' : 'uncaught';
            exceptionClasses = (msg.classes ?? []).map((c) => String(c).trim()).filter(Boolean);
            for (const s of sessions.values()) await s.applyExceptions();
            return;
        case 'mute':
            muted = !!msg.on;
            for (const s of sessions.values()) await s.cdp('Debugger.setBreakpointsActive', { active: !muted }).catch(() => {});
            return;
        case 'setLoadPolicy': case 'setFunctionBreaks': case 'clearDataBreak':
            return;   // what Node's debugger does not have (function breakpoints by name, data breakpoints, values on demand)
        case 'go': {
            if (opt.browser) { await startBrowser(); return; }
            if (opt.attach) { await startAttach(); return; }
            const dir = mkdtempSync(join(tmpdir(), 'pymagic-node-'));
            inspectFile = join(dir, 'inspect.jsonl');
            writeFileSync(inspectFile, '');
            const boot = join(dir, 'boot.cjs');
            writeFileSync(boot, BOOT);
            const preload = `--require ${JSON.stringify(boot)}`;
            const env = { ...process.env, PYMAGIC_NODE_INSPECT: inspectFile, NODE_OPTIONS: [process.env.NODE_OPTIONS, preload].filter(Boolean).join(' '), FORCE_COLOR: process.env.FORCE_COLOR ?? '0' };
            await scanCompiled().catch(() => {});   // the compiled files with maps, before anything runs
            watchTimer = setInterval(readInspectFile, 50);
            startProgram(env);
            return;
        }
        case 'pause':
            for (const s of sessions.values()) await s.cdp('Debugger.pause').catch(() => {});
            return;
        case 'stop': case 'detach':
            // a process PyMagic attached to is let go (its breakpoints gone, running on): it is not PyMagic's to end
            if (opt.attach) {
                for (const s of [...sessions.values()]) { await s.cdp('Debugger.disable').catch(() => {}); s.close(); }
                send({ type: 'output', stream: 'stdout', text: 'Detached: the program goes on.\n' });
                finish(0);
                return;
            }
            if (child) killTree(child.pid);
            if (browserProcess) killTree(browserProcess.pid);
            finish(137);
            return;
        case 'continue': return shown?.resume('Debugger.resume');
        case 'next': return shown?.resume('Debugger.stepOver');
        case 'stepIn': case 'stepInto': return shown?.resume('Debugger.stepInto');
        case 'stepOut': return shown?.resume('Debugger.stepOut');
        case 'runTo': return shown?.runTo(msg.file, Number(msg.line));
        case 'scopes': return send({ type: 'scopes', id: msg.id, scopes: await need().scopes(Number(msg.frameId ?? 0)) });
        case 'variables': return send({ type: 'variables', id: msg.id, ref: msg.ref, items: await variables(Number(msg.ref)) });
        case 'evaluate': return send({ type: 'evaluated', id: msg.id, ...(await need().evaluate(Number(msg.frameId ?? 0), msg.expression ?? '', !!msg.evalOnly, Number(msg.limit) || 1000, !!msg.detail)) });
        case 'setVariable': return send({ type: 'variableSet', id: msg.id, ...(await setVariable(Number(msg.ref), msg.name ?? '', msg.expression ?? '', Number(msg.frameId ?? 0))) });
        case 'setName': return send({ type: 'setName', id: msg.id, ...(await need().setName(Number(msg.frameId ?? 0), msg.name ?? '', msg.expression ?? '')) });
        case 'values': return send({ type: 'values', id: msg.id, values: await need().values(Number(msg.frameId ?? 0), msg.names ?? []) });
        case 'reprOf': return send({ type: 'reprOf', id: msg.id, error: 'The value is shown already.' });
        case 'table': return send({ type: 'table', id: msg.id, error: 'View as table is for Python values (pandas, NumPy, lists of rows); View value shows a JavaScript value whole.' });
        case 'jump': return send({ type: 'jumped', id: msg.id, error: 'Node cannot move the execution point to another line.' });
        case 'typeSource': return send({ type: 'typeSource', id: msg.id, error: 'Jump to type source is for Python classes.' });
        default:
            if (msg.id !== undefined) send({ type: answerType(kind), id: msg.id, error: `${kind} is not there for Node.js` });
    }
}

function need() {
    if (!shown) throw new Error('The program is not stopped.');
    return shown;
}

function killTree(pid) {
    if (!pid) return;
    if (process.platform === 'win32') spawn('taskkill', ['/PID', String(pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
    else try { process.kill(-pid, 'SIGKILL'); } catch { try { process.kill(pid, 'SIGKILL'); } catch { /* gone */ } }
}

// ------------------------------------------------------------ the processes of the run
// written into each Node process by NODE_OPTIONS --require: it opens the inspector, says where, and waits for this helper
const BOOT = `'use strict';
(() => {
  const file = process.env.PYMAGIC_NODE_INSPECT;
  if (!file) return;
  const main = process.argv[1] || '';
  // npm's own process, and npx's, are not the program
  if (/[\\\\/]node_modules[\\\\/]npm[\\\\/]|[\\\\/]npm-cli\\.js$|[\\\\/]npx-cli\\.js$/i.test(main)) return;
  try { if (!require('node:worker_threads').isMainThread) return; } catch (e) { /* no workers */ }
  const inspector = require('node:inspector');
  if (inspector.url()) return;
  try {
    inspector.open(0, '127.0.0.1', false);
    require('node:fs').appendFileSync(file, JSON.stringify({ pid: process.pid, url: inspector.url(), argv: process.argv.slice(1) }) + '\\n');
    // a module with a source map that is about to run (a dev server's freshly built chunk, a .ts file built into dist/): while there are
    // breakpoints, it stops here first, so PyMagic sets them in it through its map before any of its code runs (Node itself gives no pause there)
    const Module = require('node:module');
    const compile = Module.prototype._compile;
    Module.prototype._compile = function (content, filename) {
      if (globalThis.__pymagicBreaks && typeof content === 'string' && !/[\\\\/]node_modules[\\\\/]/.test(filename) && /\\/\\/[#@] sourceMappingURL=/.test(content.slice(-8192))) {
        globalThis.__pymagicCompiling = filename;
        debugger; // PyMagic: the breakpoints of the files this module was made from
        globalThis.__pymagicCompiling = undefined;
      }
      // every argument as given: Node passes more than two (the format of a .ts file, whose types _compile strips)
      return compile.apply(this, arguments);
    };
    inspector.waitForDebugger();
  } catch (e) { /* the program runs without the debugger */ }
})();
`;

// ------------------------------------------------------------ a page in the browser (JavaScript Debug)
// Edge (or Chrome) is started with a profile of its own and its DevTools port; the page's target is debugged as a Node process is, its
// scripts' source maps (Vite's, Next.js's) giving the project's files. Closing the browser ends the run; Stop closes the browser.
let browserProcess = null;
let browserProfile = null;

function findBrowser() {
    if (opt.browserPath) return opt.browserPath;
    const env = process.env;
    const candidates = process.platform === 'win32'
        ? [[env['ProgramFiles(x86)'], 'Microsoft', 'Edge', 'Application', 'msedge.exe'], [env.ProgramFiles, 'Microsoft', 'Edge', 'Application', 'msedge.exe'],
            [env.ProgramFiles, 'Google', 'Chrome', 'Application', 'chrome.exe'], [env['ProgramFiles(x86)'], 'Google', 'Chrome', 'Application', 'chrome.exe'],
            [env.LOCALAPPDATA, 'Google', 'Chrome', 'Application', 'chrome.exe']]
        : process.platform === 'darwin'
            ? [['/Applications', 'Microsoft Edge.app', 'Contents', 'MacOS', 'Microsoft Edge'], ['/Applications', 'Google Chrome.app', 'Contents', 'MacOS', 'Google Chrome']]
            : [['/usr', 'bin', 'microsoft-edge'], ['/usr', 'bin', 'google-chrome'], ['/usr', 'bin', 'chromium']];
    for (const parts of candidates) {
        if (!parts[0]) continue;
        const p = join(...parts);
        if (existsSync(p)) return p;
    }
    return null;
}

async function startBrowser() {
    const program = findBrowser();
    if (!program) { send({ type: 'output', stream: 'stderr', text: 'No Microsoft Edge or Google Chrome was found; name the browser in the run configuration.\n' }); finish(1); return; }
    browserProfile = mkdtempSync(join(tmpdir(), 'pymagic-browser-'));
    browserProcess = spawn(program, [`--remote-debugging-port=0`, `--user-data-dir=${browserProfile}`, '--no-first-run', '--no-default-browser-check', '--disable-features=Translate', 'about:blank'], { stdio: 'ignore', windowsHide: false });
    browserProcess.on('error', (e) => { send({ type: 'output', stream: 'stderr', text: `cannot start ${program}: ${e.message}\n` }); finish(1); });
    send({ type: 'output', stream: 'stdout', text: `${program.replace(/^.*[\\/]/, '')} opens ${opt.browser} under the debugger.\n` });
    // the port the browser listens on is written into its profile
    let port = 0;
    for (let i = 0; i < 300 && !port; i++) {
        await new Promise((r) => setTimeout(r, 100));
        try { port = Number(readFileSync(join(browserProfile, 'DevToolsActivePort'), 'utf8').split('\n')[0]); } catch { /* not yet */ }
    }
    if (!port) { send({ type: 'output', stream: 'stderr', text: 'The browser did not open its DevTools port.\n' }); killTree(browserProcess.pid); finish(1); return; }
    // the browser itself: every page (the first one, the tabs opened later) is reached through its connection, each with its frames of other
    // sites and its workers, waiting until its breakpoints are set
    const version = await fetch(`http://127.0.0.1:${port}/json/version`).then((r) => r.json()).catch(() => null);
    if (!version?.webSocketDebuggerUrl) { send({ type: 'output', stream: 'stderr', text: 'The browser did not answer on its DevTools port.\n' }); killTree(browserProcess.pid); finish(1); return; }
    const b = new Session({ pid: 'browser', url: version.webSocketDebuggerUrl, browserTarget: true, argv: ['the browser'] });
    try { await b.open(); }
    catch (e) { send({ type: 'output', stream: 'stderr', text: `PyMagic could not attach the debugger to the browser: ${e.message}\n` }); killTree(browserProcess.pid); finish(1); return; }
    let page = null;
    for (let i = 0; i < 100 && !page; i++) {
        page = [...sessions.values()].find((s) => s.info.first && s.ready && !s.ended);
        if (!page) await new Promise((r) => setTimeout(r, 100));
    }
    if (!page) { send({ type: 'output', stream: 'stderr', text: 'The browser has no page to debug.\n' }); killTree(browserProcess.pid); finish(1); return; }
    // the page last, once the breakpoints are set: its scripts stop at them from the start
    await page.cdp('Page.navigate', { url: opt.browser }).catch((e) => send({ type: 'output', stream: 'stderr', text: `${opt.browser}: ${e.message}\n` }));
    browserProcess.on('exit', () => finish(0));
}

// ------------------------------------------------------------ a Node process already running (Attach to Node.js)
// Its inspector answers at host:port (node --inspect, --inspect=PORT, --inspect-brk); a Node process of this computer started without it gets
// its inspector opened first (process._debugProcess, as SIGUSR1 does on Linux, on port 9229). Each of its targets is debugged as a process
// PyMagic started is; Stop lets it go on, and its end (or its inspector closing) ends the run.

/** host and port of "host:port" ("[::1]:9229" too). */
function hostPort(text) {
    const m = /^\[?([^\]]*?)\]?:(\d+)$/.exec(String(text ?? '').trim());
    return m ? { host: m[1] || '127.0.0.1', port: Number(m[2]) } : { host: '127.0.0.1', port: 9229 };
}

async function startAttach() {
    const { host, port } = hostPort(opt.attach);
    const where = host.includes(':') ? `[${host}]:${port}` : `${host}:${port}`;
    if (opt.attachPid) {
        try { process._debugProcess(opt.attachPid); }
        catch (e) {
            send({ type: 'output', stream: 'stderr', text: `Process ${opt.attachPid} did not open its inspector (${e.message}): is it a Node.js process of this computer?\n` });
            finish(1);
            return;
        }
        send({ type: 'output', stream: 'stdout', text: `Process ${opt.attachPid} opened its inspector.\n` });
    }
    let targets = [];
    for (let i = 0; i < 100 && !targets.length; i++) {
        const list = await fetch(`http://${where}/json/list`).then((r) => r.json()).catch(() => []);
        targets = (Array.isArray(list) ? list : []).filter((t) => t.webSocketDebuggerUrl && (t.type === 'node' || !t.type));
        if (!targets.length) await new Promise((r) => setTimeout(r, 100));
    }
    if (!targets.length) {
        send({ type: 'output', stream: 'stderr', text: `No Node.js inspector answers at ${where}: start the program with node --inspect (or --inspect=PORT), or give its process ID.\n` });
        finish(1);
        return;
    }
    for (const t of targets) {
        // the inspector names its address as it listens (0.0.0.0, localhost): it is reached where it was asked for
        const url = t.webSocketDebuggerUrl.replace(/^ws:\/\/[^/]+/, `ws://${where}`);
        const info = { pid: opt.attachPid || t.id, url, argv: [t.title || t.url || 'node'], attached: where };
        const s = new Session(info);
        sessions.set(info.pid, s);
        try {
            await s.open();
            send({ type: 'output', stream: 'stdout', text: `Attached to ${s.label()}. Its output stays where it runs; Stop lets it go on.\n` });
        }
        catch (e) {
            sessions.delete(info.pid);
            send({ type: 'output', stream: 'stderr', text: `PyMagic could not attach to ${where}: ${e.message} (another debugger may be attached to it).\n` });
        }
    }
    if (!sessions.size) finish(1);
}

let readSoFar = 0;
function readInspectFile() {
    let text;
    try { text = readFileSync(inspectFile, 'utf8'); } catch { return; }
    if (text.length <= readSoFar) return;
    const fresh = text.slice(readSoFar);
    const done = fresh.lastIndexOf('\n');
    if (done < 0) return;
    readSoFar += done + 1;
    for (const line of fresh.slice(0, done).split('\n')) {
        let info;
        try { info = JSON.parse(line); } catch { continue; }
        if (!sessions.has(info.pid)) void attach(info);
    }
}

async function attach(info) {
    const s = new Session(info);
    sessions.set(info.pid, s);
    try { await s.open(); }
    catch (e) {
        sessions.delete(info.pid);
        send({ type: 'output', stream: 'stderr', text: `PyMagic could not attach the debugger to process ${info.pid}: ${e.message}\n` });
    }
}

// ------------------------------------------------------------ one Node process, through its inspector
class Session {
    constructor(info) {
        this.info = info;
        this.pid = info.pid;
        this.nextId = 1;
        this.pending = new Map();
        this.bpIds = new Map();        // canonical path -> [breakpointId]
        this.bpMeta = new Map();       // breakpointId -> { hitCondition, hits }
        this.scripts = new Map();      // scriptId -> url
        this.maps = new Map();         // scriptId -> prepared source map (pm_sourcemap.mjs), or null for a script without one
        this.mapIds = new Map();       // scriptId -> [breakpointId] set through its map
        this.paused = null;            // the Debugger.paused event while stopped
        this.lastCommand = null;
        this.stepsSkipped = 0;
    }

    /** A script's source map, read the first time it is asked for (a script of node_modules or Node's own is taken as it runs). */
    async mapOf(scriptId) {
        if (this.maps.has(scriptId)) return this.maps.get(scriptId);
        const info = this.parsed?.get(scriptId);
        let prepared = null;
        const file = fileOf(info?.url ?? '');
        if (file && compiled.get(file)?.prepared) prepared = compiled.get(file).prepared;   // looked through before the run already
        else if (info?.sourceMapURL && !/[\\/]node_modules[\\/]/.test(info.url) && !info.url.startsWith('node:')) {
            const loaded = await loadMap(info.sourceMapURL, info.url, (u) => fetch(u).then((r) => r.text())).catch(() => null);
            prepared = prepare(loaded, webRoot(), canonical);
            // the files whose text the script was made from is the one on disk as it loads (a later save makes it an older one)
            if (prepared) prepared.sameAsDisk = new Set([...prepared.contents].filter(([k, text]) => sameText(k, text)).map(([k]) => k));
        }
        this.maps.set(scriptId, prepared);
        return prepared;
    }

    /** The breakpoints of the project's files that a mapped script runs: set where its compiled code has their lines. */
    async applyMapped(scriptId) {
        const prepared = await this.mapOf(scriptId);
        if (!prepared) return;
        for (const id of this.mapIds.get(scriptId) ?? []) { await this.cdp('Debugger.removeBreakpoint', { breakpointId: id }).catch(() => {}); this.bpMeta.delete(id); }
        const ids = [];
        for (const [key, { specs }] of breakpoints) {
            if (!prepared.bySource.has(key)) continue;
            for (const spec of specs) {
                const at = generatedFor(prepared, key, Number(spec.line));
                if (!at) continue;
                try {
                    const r = await this.cdp('Debugger.setBreakpoint', { location: { scriptId, lineNumber: at.line, columnNumber: at.column }, condition: conditionOf(spec) });
                    ids.push(r.breakpointId);
                    if (spec.hitCondition) this.bpMeta.set(r.breakpointId, { hit: hitOf(spec.hitCondition), hits: 0 });
                }
                catch { /* a place the script does not stop at */ }
            }
        }
        this.mapIds.set(scriptId, ids);
    }

    /** Where a paused position is in the project's own files: the map's file and line, or the script's own. */
    placeOf(f) {
        const url = f.url || this.scripts.get(f.location.scriptId) || '';
        const prepared = this.maps.get(f.location.scriptId);
        const orig = prepared ? originalFor(prepared, f.location.lineNumber, f.location.columnNumber ?? 0) : null;
        if (orig) return { file: orig.file, line: orig.line, mapped: true };
        return { file: fileOf(url) ?? servedFile(url), line: f.location.lineNumber + 1, mapped: false, inMap: !!prepared };
    }

    label() {
        if (this.info.browser) {
            // the first page as before; another tab, a frame or a worker by its kind and its address's last part
            const where = String(this.info.url ?? '').replace(/[?#].*$/, '').replace(/^[a-z]+:\/\/[^/]*/i, '') || '/';
            if (this.info.kind === 'page') return this.info.first ? 'the page in the browser' : `tab ${where}`;
            const kind = { iframe: 'frame', worker: 'worker', shared_worker: 'shared worker', service_worker: 'service worker' }[this.info.kind] ?? this.info.kind;
            return `${kind} ${where.replace(/^.*\//, '') || where}`;
        }
        const main = this.info.argv?.[0] ? this.info.argv[0].replace(/^.*[\\/]/, '') : 'node';
        if (this.info.attached) return `${main} (${typeof this.pid === 'number' ? `pid ${this.pid}, ` : ''}${this.info.attached})`;
        return `${main} (pid ${this.pid})`;
    }

    /** The debugger's settings of a process, page, frame or worker, its breakpoints set, then it runs on (it waited for its debugger). */
    async setup() {
        await this.cdp('Runtime.enable');
        await this.cdp('Debugger.enable', { maxScriptsCacheSize: 10e6 });
        // Node's own code and the libraries' are stepped over (not PyMagic's preload: V8 would ignore its `debugger` then; a step that
        // lands in it goes on instead, in onPaused)
        if (!opt.allCode) await this.cdp('Debugger.setBlackboxPatterns', { patterns: ['^node:', '[\\\\/]node_modules[\\\\/]'] }).catch(() => {});
        // a script with a source map (TypeScript built by tsc, a Next.js page built by webpack or Turbopack) stops before it runs,
        // so the breakpoints of the files it was made from are set in it first
        await this.cdp('Debugger.setInstrumentationBreakpoint', { instrumentation: 'beforeScriptWithSourceMapExecution' }).catch(() => {});
        await this.applyExceptions();
        if (muted) await this.cdp('Debugger.setBreakpointsActive', { active: false });
        for (const { file } of breakpoints.values()) await this.applyBreakpoints(file);
        await this.tellBreaks();
        // a page's workers and its frames of other sites are targets of their own, reached through it: each waits until its breakpoints are set
        // (a frame that waits answers it only once it runs: the run is asked for after it, not after its answer; the browser keeps the order)
        const auto = this.info.browser ? this.cdp('Target.setAutoAttach', { autoAttach: true, waitForDebuggerOnStart: true, flatten: true }).catch(() => {}) : null;
        await this.cdp('Runtime.runIfWaitingForDebugger');
        await auto;
    }

    open() {
        // a target reached through another's connection (a tab through the browser's, a worker through its page's)
        if (this.parent) return this.setup();
        return new Promise((ok, fail) => {
            const ws = new WebSocket(this.info.url);
            this.ws = ws;
            ws.onopen = async () => {
                try {
                    // the browser itself: its pages, the first one and the tabs opened later, each waiting until its breakpoints are set
                    if (this.info.browserTarget) await this.cdp('Target.setAutoAttach', { autoAttach: true, waitForDebuggerOnStart: true, flatten: true });
                    else await this.setup();
                    ok();
                }
                catch (e) { fail(e); }
            };
            ws.onerror = () => fail(new Error('the inspector did not answer'));
            // the messages of the targets reached through this connection carry their session's id
            ws.onmessage = (e) => {
                const m = JSON.parse(typeof e.data === 'string' ? e.data : Buffer.from(e.data).toString('utf8'));
                if (m.sessionId) flat.get(m.sessionId)?.onMessage(m);
                else this.onMessage(m);
            };
            ws.onclose = () => this.gone();
        });
    }

    close() { try { this.ws?.close(); } catch { /* gone */ } }

    /** The connection this target's messages go through: its own, or the one of the target it was reached through. */
    get socket() { return this.parent ? this.parent.socket : this.ws; }

    /** A target the browser or a page attached (a tab, a frame of another site, a worker): debugged as the page is, through its connection. */
    async attachChild({ sessionId, targetInfo }) {
        const kind = targetInfo?.type ?? 'other';
        // the browser's own (its extensions' pages and workers, its settings pages) are not the app's
        if (!['page', 'iframe', 'worker', 'shared_worker', 'service_worker'].includes(kind) || /^(chrome-extension|edge|chrome|devtools|extension):/i.test(targetInfo?.url ?? '')) {
            // a target the debugger has nothing for (an extension's): it goes on, let go of
            const ws = this.socket;
            if (ws?.readyState === 1) {
                ws.send(JSON.stringify({ id: 900000000 + (childIds++), method: 'Runtime.runIfWaitingForDebugger', params: {}, sessionId }));
                ws.send(JSON.stringify({ id: 900000000 + (childIds++), method: 'Target.detachFromTarget', params: { sessionId }, ...(this.sessionId ? { sessionId: this.sessionId } : {}) }));
            }
            return;
        }
        const first = kind === 'page' && !firstPageSeen;
        if (first) firstPageSeen = true;
        const s = new Session({ pid: sessionId, browser: true, kind, url: targetInfo.url, first, targetId: targetInfo.targetId });
        s.parent = this;
        s.sessionId = sessionId;
        flat.set(sessionId, s);
        sessions.set(sessionId, s);
        try { await s.open(); s.ready = true; }
        catch (e) {
            flat.delete(sessionId);
            sessions.delete(sessionId);
            if (!/ended|closed|not found/i.test(e.message)) send({ type: 'output', stream: 'stderr', text: `PyMagic could not debug ${s.label()}: ${e.message}\n` });
        }
    }

    /** Tells the process's preload whether to stop before a module with a source map runs (only while there are breakpoints). */
    async tellBreaks() {
        // only Node's preload asks it; a frame or page waiting for its debugger has no context to evaluate in yet, and would wait for it
        if (this.info.browser) return;
        await this.cdp('Runtime.evaluate', { expression: `globalThis.__pymagicBreaks = ${anyBreaks()}`, silent: true }).catch(() => {});
    }

    cdp(method, params = {}) {
        return new Promise((ok, fail) => {
            const ws = this.socket;
            if (!ws || ws.readyState !== 1 || this.ended) { fail(new Error('The process has ended.')); return; }
            const id = this.nextId++;
            this.pending.set(id, { ok, fail });
            ws.send(JSON.stringify(this.sessionId ? { id, method, params, sessionId: this.sessionId } : { id, method, params }));
        });
    }

    onMessage(m) {
        if (m.id !== undefined) {
            const p = this.pending.get(m.id);
            this.pending.delete(m.id);
            if (!p) return;
            if (m.error) p.fail(new Error(m.error.message));
            else p.ok(m.result);
            return;
        }
        if (m.method === 'Debugger.scriptParsed') {
            this.scripts.set(m.params.scriptId, m.params.url);
            if (m.params.sourceMapURL) (this.parsed ??= new Map()).set(m.params.scriptId, { url: m.params.url, sourceMapURL: m.params.sourceMapURL });
        }
        else if (m.method === 'Debugger.paused') void this.onPaused(m.params);
        else if (m.method === 'Target.attachedToTarget') void this.attachChild(m.params);
        else if (m.method === 'Target.detachedFromTarget') flat.get(m.params.sessionId)?.gone();
        else if (m.method === 'Target.targetInfoChanged' && this.info.browserTarget) {
            // a tab that went to another address: its label says the new one
            for (const s of flat.values()) if (s.info.targetId === m.params.targetInfo?.targetId) s.info.url = m.params.targetInfo.url;
        }
        else if (m.method === 'Runtime.executionContextDestroyed' && !this.info.browser) {
            // the program has ended: Node waits for its debugger to let go ("Waiting for the debugger to disconnect"), so it lets go
            if (this.paused) { this.paused = null; if (shown === this) { shown = null; dropRefs(this); } }
            this.close();
        }
    }

    gone() {
        if (this.ended) return;
        this.ended = true;
        for (const p of this.pending.values()) p.fail(new Error('The process has ended.'));
        this.pending.clear();
        sessions.delete(this.pid);
        if (this.sessionId) flat.delete(this.sessionId);
        // the targets reached through it go with it (a closed tab's workers)
        for (const c of [...flat.values()]) if (c.parent === this) c.gone();
        const i = waiting.indexOf(this);
        if (i >= 0) waiting.splice(i, 1);
        if (shown === this) {
            shown = null;
            dropRefs(this);
            showNext();
            if (!shown) send({ type: 'running' });
        }
        // the browser closed, or its last page did: the run ends with it (a worker, a frame or one tab of several ending does not end it)
        const pageLeft = [...sessions.values()].some((s) => s.info.browser && s.info.kind === 'page');
        if (this.info.browserTarget || (this.info.browser && this.info.kind === 'page' && !pageLeft)) { if (browserProcess) killTree(browserProcess.pid); finish(0); }
        // a process attached to has ended (or closed its inspector): the run ends when none is left
        if (this.info.attached && !finished && ![...sessions.values()].some((s) => s.info.attached)) {
            send({ type: 'output', stream: 'stdout', text: `${this.label()} ended, or let its debugger go.\n` });
            finish(0);
        }
    }

    async applyExceptions() {
        await this.cdp('Debugger.setPauseOnExceptions', { state: exceptionMode === 'raised' ? 'all' : 'uncaught' }).catch(() => {});
    }

    async applyBreakpoints(file) {
        const key = canonical(file);
        for (const id of this.bpIds.get(key) ?? []) { await this.cdp('Debugger.removeBreakpoint', { breakpointId: id }).catch(() => {}); this.bpMeta.delete(id); }
        const ids = [];
        for (const spec of breakpoints.get(key)?.specs ?? []) {
            const line = Number(spec.line);
            if (!(line > 0)) continue;
            // a file run as it is: by its path, or in a browser by its address on the dev server (http://localhost:5173/js/a.js); a file the
            // dev server compiles (.ts, .tsx, .jsx, .vue, .svelte) is served under its own name as other lines, and reached only through its map
            const compiledByServer = this.info.browser && /\.(m|c)?tsx?$|\.jsx$|\.vue$|\.svelte$/i.test(file);
            if (!compiledByServer) {
                try {
                    const r = await this.cdp('Debugger.setBreakpointByUrl', { lineNumber: line - 1, urlRegex: this.info.browser ? servedUrlRegex(file) : urlRegexFor(file), condition: conditionOf(spec) });
                    ids.push(r.breakpointId);
                    if (spec.hitCondition) this.bpMeta.set(r.breakpointId, { hit: hitOf(spec.hitCondition), hits: 0 });
                }
                catch { /* a line the script does not have */ }
            }
            // where the project's compiled files run this line (a .ts file built into dist/), by their maps
            for (const at of compiledPlaces(key, line)) {
                try {
                    const r = await this.cdp('Debugger.setBreakpointByUrl', { lineNumber: at.line, columnNumber: at.column, urlRegex: urlRegexFor(at.file), condition: conditionOf(spec) });
                    ids.push(r.breakpointId);
                    if (spec.hitCondition) this.bpMeta.set(r.breakpointId, { hit: hitOf(spec.hitCondition), hits: 0 });
                }
                catch { /* already there: the same place twice */ }
            }
        }
        this.bpIds.set(key, ids);
        // the compiled scripts made from this file: their breakpoints through their maps again
        // (not a script made from an earlier text of the file: a hot reload's old module keeps its breakpoints on the lines of that text)
        for (const [scriptId, prepared] of this.maps) if ((prepared?.bySource.has(key) || this.mapIds.get(scriptId)?.length) && !madeFromOlder(prepared, key)) await this.applyMapped(scriptId);
    }

    async onPaused(params) {
        // before a script with a source map runs: its breakpoints set through the map, and on
        if (params.reason === 'instrumentation') {
            const id = params.data?.scriptId;
            if (id) await this.applyMapped(id).catch(() => {});
            await this.cdp('Debugger.resume').catch(() => {});
            return;
        }
        // the preload's stop before a module with a source map runs: its breakpoints set through its map, and on
        // (told by what it leaves in the process: the file it is about to run)
        const top = params.callFrames?.[0];
        if (top && params.reason === 'other' && !(params.hitBreakpoints ?? []).length) {
            const r = await this.cdp('Debugger.evaluateOnCallFrame', { callFrameId: top.callFrameId, expression: 'globalThis.__pymagicCompiling', returnByValue: true, silent: true }).catch(() => null);
            if (typeof r?.result?.value === 'string') {
                await compiledNow(this, r.result.value).catch(() => {});
                await this.cdp('Debugger.resume').catch(() => {});
                return;
            }
        }
        // a step that lands in PyMagic's preload (Step Into on a require) goes on as it was going
        if (top && this.lastCommand && this.lastCommand !== 'Debugger.resume' && /pymagic-node-[^\\/]*[\\/]boot\.cjs$/.test(fileOf(top.url || '') ?? '') && this.stepsSkipped < 40) {
            this.stepsSkipped++;
            await this.cdp(this.lastCommand === 'Debugger.stepInto' ? 'Debugger.stepOut' : this.lastCommand).catch(() => {});
            return;
        }
        // the maps of the scripts the stop is in, so its frames are told as the files they were made from
        for (const f of params.callFrames ?? []) if (this.parsed?.has(f.location.scriptId)) await this.mapOf(f.location.scriptId).catch(() => null);
        // a step that lands in compiled code with no line of the project's own (a bundler's helper) goes on the same way
        if (this.lastCommand && this.lastCommand !== 'Debugger.resume' && !(params.hitBreakpoints ?? []).length && params.reason === 'other' && this.stepsSkipped < 40) {
            const top = params.callFrames?.[0];
            if (top && this.maps.get(top.location.scriptId) && !this.placeOf(top).mapped) {
                this.stepsSkipped++;
                await this.cdp(this.lastCommand).catch(() => {});
                return;
            }
        }
        this.stepsSkipped = 0;
        // a hit condition is counted here: a stop it does not allow goes on at once
        const hitIds = params.hitBreakpoints ?? [];
        if (hitIds.length) {
            let stop = false, counted = false;
            for (const id of hitIds) {
                const meta = this.bpMeta.get(id);
                if (!meta) { stop = true; continue; }
                counted = true;
                meta.hits++;
                if (!meta.hit || hitMatches(meta.hit, meta.hits)) stop = true;
            }
            if (counted && !stop) { await this.cdp('Debugger.resume').catch(() => {}); return; }
        }
        // an exception whose stack has none of the project's code (Node's own streams reject promises they handle themselves; a library's):
        // as "only my code", the program goes on
        if ((params.reason === 'exception' || params.reason === 'promiseRejection') && !opt.allCode) {
            const mine = (params.callFrames ?? []).some((f) => { const p = this.placeOf(f); return p.file && !(p.inMap && !p.mapped) && !/[\\/]node_modules[\\/]/.test(p.file); });
            if (!mine) { await this.cdp('Debugger.resume').catch(() => {}); return; }
        }
        // a thrown exception of a class not asked for
        if ((params.reason === 'exception' || params.reason === 'promiseRejection') && exceptionMode === 'raised' && exceptionClasses.length) {
            const cls = params.data?.className ?? '';
            if (!exceptionClasses.includes(cls)) { await this.cdp('Debugger.resume').catch(() => {}); return; }
        }
        this.paused = params;
        this.reason = params.reason === 'exception' || params.reason === 'promiseRejection' ? 'exception'
            : hitIds.length ? 'breakpoint' : this.lastCommand && this.lastCommand !== 'Debugger.resume' ? 'step' : params.reason === 'other' ? 'breakpoint' : 'pause';
        this.message = this.reason === 'exception' ? (params.data?.description ?? params.data?.value ?? 'an exception').split('\n')[0] : null;
        this.lastCommand = null;
        await this.refreshAddress();
        if (shown && shown !== this) {
            if (!waiting.includes(this)) waiting.push(this);
            send({ type: 'output', stream: 'stdout', text: `${this.label()} stopped too; it is shown when ${shown.label()} goes on.\n` });
            return;
        }
        this.show();
    }

    /** A tab's or frame's address as it is now (it is attached before it has one, and a tab goes to others): the browser says it. */
    async refreshAddress() {
        if (!this.info.browser || !this.info.targetId) return;
        let root = this;
        while (root.parent) root = root.parent;
        if (!root.info.browserTarget) return;
        const r = await root.cdp('Target.getTargetInfo', { targetId: this.info.targetId }).catch(() => null);
        if (r?.targetInfo?.url) this.info.url = r.targetInfo.url;
    }

    show() {
        shown = this;
        send({ type: 'stopped', reason: this.reason, frames: this.frames(), message: this.message, thread: this.label(), suspended: 'thread', threads: [] });
    }

    /** The frames of the stop, innermost first: the project's and other files' code, not Node's own. */
    frames() {
        const all = this.paused?.callFrames ?? [];
        this.frameMap = [];
        const out = [];
        all.forEach((f, i) => {
            // a compiled script's frame as the file it was made from says it (its source map); a frame of compiled code with no line of the
            // project's own (a bundler's helper, a library inside the bundle) is left out
            const place = this.placeOf(f);
            if (!place.file || (place.inMap && !place.mapped)) return;
            if (!opt.allCode && /[\\/]node_modules[\\/]/.test(place.file) && out.length) return;
            this.frameMap.push(i);
            const first = place.mapped ? place.line : (f.functionLocation?.lineNumber ?? f.location.lineNumber) + 1;
            out.push({ id: out.length, name: f.functionName || (out.length === 0 && i === all.length - 1 ? '(module)' : '(anonymous)'), file: place.file, line: place.line, first });
        });
        if (!out.length && all.length) {
            // stopped in Node's own code only: its frames as they are
            all.forEach((f, i) => { this.frameMap.push(i); out.push({ id: out.length, name: f.functionName || '(anonymous)', file: f.url || '<node>', line: f.location.lineNumber + 1, first: f.location.lineNumber + 1 }); });
        }
        return out;
    }

    callFrame(frameId) {
        const f = this.paused?.callFrames?.[this.frameMap?.[frameId] ?? frameId];
        if (!f) throw new Error('That frame is gone; the program has moved on.');
        return f;
    }

    async resume(command) {
        if (!this.paused) return;
        this.lastCommand = command;
        this.paused = null;
        dropRefs(this);
        shown = null;
        send({ type: 'running' });
        await this.cdp(command).catch(() => {});
        showNext();
    }

    async runTo(file, line) {
        if (!this.paused) return;
        let scriptId = [...this.scripts.entries()].find(([, url]) => { const f = fileOf(url); return f && canonical(f) === canonical(file); })?.[0];
        // a file the program runs compiled: where its compiled script has the line
        let at = null;
        if (!scriptId) {
            for (const [id, prepared] of this.maps) {
                const g = prepared && generatedFor(prepared, canonical(file), line);
                if (g) { scriptId = id; at = g; break; }
            }
        }
        if (!scriptId) return this.resume('Debugger.resume');
        if (at) {
            this.lastCommand = 'Debugger.stepOver';
            this.paused = null;
            dropRefs(this);
            shown = null;
            send({ type: 'running' });
            await this.cdp('Debugger.continueToLocation', { location: { scriptId, lineNumber: at.line, columnNumber: at.column } }).catch(() => this.cdp('Debugger.resume').catch(() => {}));
            return;
        }
        this.lastCommand = 'Debugger.stepOver';
        this.paused = null;
        dropRefs(this);
        shown = null;
        send({ type: 'running' });
        await this.cdp('Debugger.continueToLocation', { location: { scriptId, lineNumber: line - 1 } }).catch(() => this.cdp('Debugger.resume').catch(() => {}));
    }

    async scopes(frameId) {
        const f = this.callFrame(frameId);
        const result = [];
        if (frameId === 0 && f.returnValue) {
            const ref = newRef({ session: this, values: [['(return value)', f.returnValue]] });
            result.push({ name: 'Return values', ref });
        }
        f.scopeChain.forEach((sc, index) => {
            if (!sc.object?.objectId) return;
            const name = sc.type === 'local' ? 'Locals' : sc.type === 'global' ? 'Globals' : sc.type === 'closure' ? `Closure${sc.name ? ` (${sc.name})` : ''}`
                : sc.type === 'block' ? 'Block' : sc.type === 'script' ? 'Script' : sc.type === 'module' ? 'Module' : sc.type === 'catch' ? 'Catch' : sc.type[0].toUpperCase() + sc.type.slice(1);
            result.push({ name, ref: newRef({ session: this, objectId: sc.object.objectId, scope: { frameId, index, callFrameId: f.callFrameId }, isGlobal: sc.type === 'global' }) });
        });
        return result;
    }

    async evaluate(frameId, expression, evalOnly, limit, detail) {
        const f = this.callFrame(frameId);
        const r = await this.cdp('Debugger.evaluateOnCallFrame', { callFrameId: f.callFrameId, expression, generatePreview: true, throwOnSideEffect: evalOnly, silent: evalOnly, objectGroup: 'pymagic' });
        if (r.exceptionDetails) {
            const text = r.exceptionDetails.exception?.description ?? r.exceptionDetails.text ?? 'it failed';
            if (evalOnly && /EvalError: Possible side-effect/.test(text)) return { error: 'This expression may change the program; evaluate it in the Evaluate window (Alt+F8).' };
            return { error: text.split('\n')[0] };
        }
        const v = r.result;
        const out = { result: shortValue(v, limit), valueType: typeOf(v), ref: refFor(this, v), output: '' };
        if (detail) {
            if (v.type === 'string') { out.result = JSON.stringify(v.value); out.text = v.value; }
            else if (v.objectId) {
                const j = await this.cdp('Runtime.callFunctionOn', { objectId: v.objectId, functionDeclaration: 'function () { try { return JSON.stringify(this, (k, x) => typeof x === "bigint" ? String(x) : x, 2); } catch (e) { return undefined; } }', returnByValue: true }).catch(() => null);
                if (typeof j?.result?.value === 'string') { out.json = j.result.value; out.text = j.result.value; }
                out.result = out.text ?? describe(v, 200000);
            }
        }
        return out;
    }

    async setName(frameId, name, expression) {
        if (!/^[A-Za-z_$][\w$]*$/.test(name)) return { error: `${name} is not a name.` };
        const f = this.callFrame(frameId);
        const r = await this.cdp('Debugger.evaluateOnCallFrame', { callFrameId: f.callFrameId, expression: `${name} = (${expression})`, generatePreview: true });
        if (r.exceptionDetails) return { error: (r.exceptionDetails.exception?.description ?? r.exceptionDetails.text).split('\n')[0] };
        return { name, value: shortValue(r.result), valueType: typeOf(r.result) };
    }

    /** The short values of names the frame sees, for the values at the ends of the editor's lines. */
    async values(frameId, names) {
        const f = this.callFrame(frameId);
        const own = new Set();
        for (const sc of f.scopeChain) {
            if (sc.type === 'global' || !sc.object?.objectId) continue;
            const props = (await this.cdp('Runtime.getProperties', { objectId: sc.object.objectId, ownProperties: true }).catch(() => ({ result: [] }))).result;
            for (const p of props) if (names.includes(p.name)) own.add(p.name);
        }
        const fresh = await this.fresh(f.callFrameId, [...own]);
        const found = {};
        for (const [name, v] of fresh) if (v.type !== 'function') found[name] = shortValue(v, 60);
        return found;
    }

    /**
     * The values of names as the frame has them now. The scope objects of a stop are what the names held when it stopped, so a value set
     * since (Set Value) is read through the frame itself: name -> RemoteObject, for the names it has.
     */
    async fresh(callFrameId, names) {
        const ok = names.filter((n) => /^[A-Za-z_$][\w$]*$/.test(n) && n !== 'arguments');
        const out = new Map();
        if (!ok.length) return out;
        const expression = `(() => { const o = Object.create(null); ${ok.map((n) => `try { o[${JSON.stringify(n)}] = ${n}; } catch (e) {}`).join(' ')} return o; })()`;
        const r = await this.cdp('Debugger.evaluateOnCallFrame', { callFrameId, expression, silent: true, objectGroup: 'pymagic' }).catch(() => null);
        if (!r?.result?.objectId) return out;
        const props = (await this.cdp('Runtime.getProperties', { objectId: r.result.objectId, ownProperties: true, generatePreview: true, objectGroup: 'pymagic' }).catch(() => ({ result: [] }))).result;
        for (const p of props) if (p.value) out.set(p.name, p.value);
        return out;
    }
}

function showNext() {
    if (shown) return;
    const next = waiting.shift();
    if (next && next.paused) next.show();
}

// ------------------------------------------------------------ values
function newRef(entry) {
    const ref = nextRef++;
    refs.set(ref, entry);
    return ref;
}
function dropRefs(session) {
    for (const [ref, e] of refs) if (e.session === session) refs.delete(ref);
    session.cdp('Runtime.releaseObjectGroup', { objectGroup: 'pymagic' }).catch(() => {});
}

/** A reference to open a value by: objects and arrays, maps and sets (not functions, whose members are their source's business). */
function refFor(session, v) {
    if (!v || !v.objectId || v.type !== 'object' || v.subtype === 'null') return 0;
    return newRef({ session, objectId: v.objectId });
}

async function variables(ref) {
    const e = refs.get(ref);
    if (!e) return [];
    if (e.values) return e.values.map(([name, v]) => item(e.session, name, v));
    const r = await e.session.cdp('Runtime.getProperties', { objectId: e.objectId, ownProperties: true, generatePreview: true, objectGroup: 'pymagic' });
    // a frame's own names (not the globals) as they are now, a value set since the stop included
    if (e.scope && !e.isGlobal) {
        const now = await e.session.fresh(e.scope.callFrameId, (r.result ?? []).map((p) => p.name)).catch(() => new Map());
        for (const p of r.result ?? []) if (now.has(p.name)) p.value = now.get(p.name);
    }
    const items = [];
    for (const p of r.result ?? []) {
        if (!p.value && !p.get) continue;
        if (e.isGlobal && p.value?.type === 'function') continue;   // the globals without Node's own functions
        if (p.value) items.push(item(e.session, p.name, p.value));
        else items.push({ name: p.name, value: '(getter)', type: 'getter', ref: 0 });
    }
    for (const p of r.internalProperties ?? []) {
        if (!p.value) continue;
        if (p.name === '[[Entries]]' || p.name === '[[PrimitiveValue]]' || p.name === '[[TargetFunction]]' || p.name === '[[Prototype]]' && !e.scope) items.push(item(e.session, p.name, p.value));
    }
    if (e.isGlobal) items.sort((a, b) => a.name.localeCompare(b.name));
    return items.slice(0, 500);
}

function item(session, name, v) {
    return { name, value: shortValue(v), type: typeOf(v), ref: refFor(session, v) };
}

async function setVariable(ref, name, expression, frameId) {
    const e = refs.get(ref);
    if (!e) return { error: 'That value is gone; the program has moved on.' };
    const s = e.session;
    const f = s.callFrame(frameId);
    const v = await s.cdp('Debugger.evaluateOnCallFrame', { callFrameId: (e.scope ? s.callFrame(e.scope.frameId) : f).callFrameId, expression: `(${expression})`, generatePreview: true, objectGroup: 'pymagic' });
    if (v.exceptionDetails) return { error: (v.exceptionDetails.exception?.description ?? v.exceptionDetails.text).split('\n')[0] };
    const arg = v.result.objectId ? { objectId: v.result.objectId } : v.result.unserializableValue ? { unserializableValue: v.result.unserializableValue } : { value: v.result.value };
    if (e.scope && !e.isGlobal) {
        try {
            await s.cdp('Debugger.setVariableValue', { scopeNumber: e.scope.index, variableName: name, newValue: arg, callFrameId: e.scope.callFrameId });
        }
        catch (err) { return { error: err.message }; }
    }
    else {
        const r = await s.cdp('Runtime.callFunctionOn', { objectId: e.objectId, functionDeclaration: 'function (k, v) { this[k] = v; return this[k]; }', arguments: [{ value: name }, arg], generatePreview: true, objectGroup: 'pymagic' });
        if (r.exceptionDetails) return { error: (r.exceptionDetails.exception?.description ?? r.exceptionDetails.text).split('\n')[0] };
    }
    return { name, value: shortValue(v.result), valueType: typeOf(v.result), ref: refFor(s, v.result) };
}

/** The type of a value as the panes show it: the class of an object (Object, Array, Map, Person), else JavaScript's own (number, string). */
function typeOf(v) {
    if (v.type === 'object') return v.subtype === 'null' ? 'null' : v.className ?? 'Object';
    if (v.type === 'function') return 'function';
    return v.type;
}

function shortValue(v, limit = 200) {
    const text = describe(v, limit);
    return text.length <= limit ? text : text.slice(0, limit - 1) + '…';
}

function describe(v, limit) {
    switch (v.type) {
        case 'undefined': return 'undefined';
        case 'string': return JSON.stringify(v.value.length > limit ? v.value.slice(0, limit) : v.value);
        case 'number': case 'boolean': return v.unserializableValue ?? String(v.value);
        case 'bigint': return v.unserializableValue ?? `${v.value}n`;
        case 'symbol': return v.description ?? 'Symbol()';
        case 'function': {
            const head = (v.description ?? 'function').split('\n')[0];
            const m = /^(async\s+)?(function\*?|class)\s*([\w$]*)/.exec(head);
            return m ? `${m[1] ?? ''}${m[2] === 'class' ? 'class' : 'ƒ'} ${m[3] || '(anonymous)'}${m[2] === 'class' ? '' : '()'}` : head.slice(0, 80);
        }
        default:
            if (v.subtype === 'null') return 'null';
            return v.preview ? previewText(v.preview) : v.description ?? 'Object';
    }
}

function previewText(p) {
    // a value inside a preview, as DevTools writes it: an object {…}, an array Array(n), a map Map(n)
    const value = (x) => (x.type === 'string' ? JSON.stringify(x.value)
        : x.type === 'object' && x.subtype !== 'null' ? (x.value === 'Object' || !x.value ? '{…}' : x.value)
            : x.type === 'function' ? 'ƒ' : x.value ?? x.type);
    const more = p.overflow ? ', …' : '';
    if (p.subtype === 'array' || p.subtype === 'typedarray') return `${p.description?.startsWith('Array(') ? '' : (p.description ?? '').replace(/\(\d+\)$/, '') + ' '}[${p.properties.filter((x) => /^\d+$/.test(x.name)).map(value).join(', ')}${more}]`.trim();
    if (p.subtype === 'map' || p.subtype === 'set') {
        const entries = (p.entries ?? []).map((e) => (e.key ? `${value(e.key)} => ${value(e.value)}` : value(e.value)));
        return `${p.description} {${entries.join(', ')}${p.overflow ? ', …' : ''}}`;
    }
    if (p.subtype === 'date' || p.subtype === 'regexp' || p.subtype === 'error' || p.subtype === 'promise' && !p.properties.length) return p.description;
    const props = p.properties.map((x) => `${/^[A-Za-z_$][\w$]*$/.test(x.name) ? x.name : JSON.stringify(x.name)}: ${value(x)}`).join(', ');
    const head = p.description && p.description !== 'Object' ? `${p.description} ` : '';
    return `${head}{${props}${more}}`;
}

// ------------------------------------------------------------ compiled files of the project and their maps
// A .ts file compiled by tsc (or esbuild, Babel) into dist/a.js runs as dist/a.js: its breakpoints are set there, where the map says its lines
// are. Node gives no pause before such a script runs (V8's instrumentation breakpoint is for scripts it runs, and Node runs a module as a
// function), so the project's compiled files are looked through before the program starts, as VS Code's debugger does with its outFiles.
// Files built while the program runs (a dev server's) are reached through their maps when they load (Session.applyMapped).
const compiled = new Map();   // compiled file -> { mtime, prepared }
const SKIP_DIRS = new Set(['node_modules', '.git', '.next', '.nuxt', '.svelte-kit', '.turbo', '.cache', '.pymagic', '.venv', 'venv', '__pycache__']);

async function scanCompiled() {
    const root = opt.root ?? process.cwd();
    const { readdirSync, statSync, openSync, readSync, closeSync } = await import('node:fs');
    let seen = 0;
    const walk = async (dir, depth) => {
        if (depth > 12 || seen > 20000) return;
        let entries;
        try { entries = readdirSync(dir, { withFileTypes: true }); } catch { return; }
        for (const e of entries) {
            const full = join(dir, e.name);
            if (e.isDirectory()) { if (!SKIP_DIRS.has(e.name)) await walk(full, depth + 1); continue; }
            if (!/\.(m|c)?js$/i.test(e.name)) continue;
            seen++;
            let st;
            try { st = statSync(full); } catch { continue; }
            if (st.size > 20e6) continue;
            await readCompiled(full, st, { openSync, readSync, closeSync });
        }
    };
    await walk(root, 0);
}

/** A compiled file's map, read again when the file has changed: the entry of `compiled` for it. */
async function readCompiled(full, st, fs) {
    const known = compiled.get(full);
    if (known && known.mtime === st.mtimeMs) return known;
    // the comment that names its map is at the end of the file
    let tail = '';
    try {
        const fd = fs.openSync(full, 'r');
        const len = Math.min(st.size, 8192);
        const buf = Buffer.alloc(len);
        fs.readSync(fd, buf, 0, len, st.size - len);
        fs.closeSync(fd);
        tail = buf.toString('utf8');
    } catch { return null; }
    let m = /\/\/[#@] sourceMappingURL=(\S+)\s*$/.exec(tail);
    if (!m && !/\/\/[#@] sourceMappingURL=data:/.test(tail)) { const e = { mtime: st.mtimeMs, prepared: null }; compiled.set(full, e); return e; }
    if (!m) {
        // an inline map longer than the end of the file read: the whole file
        try { m = /\/\/[#@] sourceMappingURL=(\S+)\s*$/.exec(readFileSync(full, 'utf8')); } catch { m = null; }
    }
    const loaded = m ? await loadMap(m[1], pathToFileURL(full).href).catch(() => null) : null;
    const entry = { mtime: st.mtimeMs, prepared: prepare(loaded, opt.root ?? process.cwd(), canonical) };
    compiled.set(full, entry);
    return entry;
}

/** One compiled file about to run (the preload's stop in a process): its map read, and the breakpoints of its sources set in it. */
async function compiledNow(session, full) {
    const fs = await import('node:fs');
    let st;
    try { st = fs.statSync(full); } catch { return; }
    const entry = await readCompiled(full, st, fs);
    if (!entry?.prepared) return;
    for (const key of entry.prepared.bySource.keys()) if (breakpoints.has(key)) await session.applyBreakpoints(breakpoints.get(key).file);
}

/** Whether there are breakpoints at all: the processes' preload stops before a module with a source map runs only then. */
const anyBreaks = () => [...breakpoints.values()].some((b) => b.specs.length > 0);

/** The places in compiled files of the project where a source's line runs: [{ file (the compiled one), line, column }] (0-based). */
function compiledPlaces(key, line) {
    const out = [];
    for (const [file, { prepared }] of compiled) {
        if (!prepared?.bySource.has(key)) continue;
        const at = generatedFor(prepared, key, line);
        if (at) out.push({ file, line: at.line, column: at.column });
    }
    return out;
}

// ------------------------------------------------------------ breakpoints
/** A file as the inspector names it, whichever way (C:\a\b.js, file:///C:/a/b.js, file:///c:/a/b%20c.js), Windows paths in any case. */
function urlRegexFor(file) {
    const esc = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const ci = (s) => [...s].map((c) => (c === ' ' ? '(?: |%20)' : /[a-z]/i.test(c) && process.platform === 'win32' ? `[${c.toLowerCase()}${c.toUpperCase()}]` : esc(c))).join('');
    const parts = resolve(file).split(/[\\/]/);
    return `^(?:file:///?)?${parts.map(ci).join('[\\\\/]')}$`;
}

/** The folder the page's addresses and its maps' sources are files of: the web root of a JavaScript Debug run, else the project. */
const webRoot = () => opt.webRoot ?? opt.root ?? process.cwd();

/** A file of the web root a page's address serves as it is (no map): http://localhost:5173/js/a.js -> <web root>/js/a.js; null when it is not there. */
function servedFile(url) {
    if (!/^https?:\/\//.test(url ?? '')) return null;
    try {
        const p = decodeURIComponent(new URL(url).pathname).replace(/^\/+/, '');
        if (!p) return null;
        for (const base of [webRoot(), join(webRoot(), 'public')]) {
            const f = join(base, p);
            if (existsSync(f)) return f;
        }
    } catch { /* not an address */ }
    return null;
}

/** A file of the web root as a dev server's address names it, whatever the host and port, a query (?t=…) or not. */
function servedUrlRegex(file) {
    const rel = canonical(file).startsWith(canonical(webRoot())) ? resolve(file).slice(resolve(webRoot()).length).replace(/^[\\/]+/, '') : null;
    if (!rel) return '^$a';   // outside the web root: no address
    const esc = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const parts = rel.split(/[\\/]/).map(esc).join('/');
    return `^https?://[^/]+/(?:public/)?${parts}(?:[?#].*)?$`;
}

function fileOf(url) {
    if (!url) return null;
    if (url.startsWith('file://')) { try { return fileURLToPath(url); } catch { return null; } }
    if (/^[A-Za-z]:[\\/]/.test(url) || url.startsWith('/')) return url;
    return null;
}

/** A breakpoint's condition for the inspector: its own, and a log message written out without stopping ({expressions} filled in). */
function conditionOf(spec) {
    const cond = spec.condition?.trim();
    if (!spec.logMessage) return cond || undefined;
    const parts = [];
    let rest = spec.logMessage, m;
    const re = /\{([^{}]+)\}/g;
    let last = 0;
    while ((m = re.exec(rest))) {
        parts.push(JSON.stringify(rest.slice(last, m.index)));
        parts.push(`(() => { try { const v = (${m[1]}); return typeof v === 'string' ? v : JSON.stringify(v) ?? String(v); } catch (e) { return '<' + (e && e.name || 'Error') + '>'; } })()`);
        last = m.index + m[0].length;
    }
    parts.push(JSON.stringify(rest.slice(last)));
    const log = `(console.log(${parts.join(' + ')}), false)`;
    return cond ? `(${cond}) && ${log}` : log;
}

function hitOf(text) {
    const m = /^\s*(==|>=|<=|>|<|%)?\s*(\d+)\s*$/.exec(text ?? '');
    return m ? { op: m[1] ?? '==', n: Number(m[2]) } : null;
}

function hitMatches(h, hits) {
    switch (h.op) {
        case '==': return hits === h.n;
        case '>=': return hits >= h.n;
        case '<=': return hits <= h.n;
        case '>': return hits > h.n;
        case '<': return hits < h.n;
        case '%': return h.n > 0 && hits % h.n === 0;
        default: return true;
    }
}

export { urlRegexFor, conditionOf, hitOf, hitMatches, previewText, describe };

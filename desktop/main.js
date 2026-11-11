// PyMagic for Windows: starts the C# host in the background and shows the editor in its own window.
const { app, BrowserWindow, Menu, dialog, shell, ipcMain, safeStorage } = require('electron');
const { spawn, spawnSync } = require('node:child_process');
const fs = require('node:fs');
const net = require('node:net');
const path = require('node:path');
const http = require('node:http');
const os = require('node:os');
const { hostEnvironment } = require('./host-env');
const { remember, existing, forget } = require('./recent-projects');
const { missingWebPackages } = require('./web-packages');
const { cloneAddressOk, repositoryName } = require('./clone-model');
const { engineSettingsFile, customizeOf, withCustomize } = require('./customize-model');
const { ghCommand, ghAccountOf, deviceCodeOf, ghWantsEnter, tokenOk, reposOf } = require('./github-model');
const vcsHosts = require('./vcs-hosts-model');

const repo = path.resolve(__dirname, '..');
const packaged = app.isPackaged;
const icon = path.join(__dirname, 'icon', 'pymagic.ico');
// the id the installer gives the shortcuts (build-config.mjs), so the running app and a pinned shortcut share a taskbar button;
// a run from the repo keeps Electron's own, so it is not taken for an installed copy
if (process.platform === 'win32' && packaged) app.setAppUserModelId('io.github.residentcode.pymagic');
const www = packaged ? path.join(process.resourcesPath, 'www') : path.join(repo, 'web', 'dist');
const demo = packaged ? path.join(process.resourcesPath, 'demo') : path.join(repo, 'examples', 'demo');
const settingsFile = () => path.join(app.getPath('userData'), 'settings.json');

// one window per open project, each with its engine (the C# host) on a port of its own: { win, host, port, project }
const windows = new Set();

function readSettings() {
  try { return JSON.parse(fs.readFileSync(settingsFile(), 'utf8')); } catch { return {}; }
}
function writeSettings(s) {
  try { fs.mkdirSync(path.dirname(settingsFile()), { recursive: true }); fs.writeFileSync(settingsFile(), JSON.stringify(s, null, 2)); } catch { /* not fatal */ }
}

function freePort(start) {
  return new Promise(resolve => {
    const server = net.createServer();
    server.once('error', () => resolve(freePort(start + 1)));
    server.listen(start, '127.0.0.1', () => server.close(() => resolve(start)));
  });
}

/** The typeshed the engine reads: PYMAGIC_TYPESHED, else the one shipped with PyMagic (typeshed/), else PyCharm's. */
function typeshed() {
  if (process.env.PYMAGIC_TYPESHED) return process.env.PYMAGIC_TYPESHED;
  const own = packaged ? path.join(process.resourcesPath, 'typeshed') : path.join(repo, 'typeshed');
  if (fs.existsSync(path.join(own, 'stdlib', 'VERSIONS'))) return own;
  const root = 'C:\\Program Files\\JetBrains';
  try {
    for (const d of fs.readdirSync(root).filter(n => n.startsWith('PyCharm')).sort().reverse()) {
      const p = path.join(root, d, 'plugins', 'python-ce', 'helpers', 'typeshed');
      if (fs.existsSync(p)) return p;
    }
  } catch { /* PyCharm is not installed */ }
  return null;
}

function run(cmd, args, cwd) {
  return spawnSync(cmd, args, { cwd, shell: true, encoding: 'utf8' });
}

/** Builds the page and the host when they are missing or older than their sources; returns the host dll. */
function prepare(report) {
  if (packaged) return path.join(process.resourcesPath, 'host', 'pymagic-web.exe');
  const web = path.join(repo, 'web');
  if (missingWebPackages(web).length) {
    report('Installing the editor packages...');
    if (run('npm', ['install'], web).status !== 0) throw new Error('npm install failed');
  }
  const newest = dir => {
    let t = 0;
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name);
      t = Math.max(t, e.isDirectory() ? newest(p) : fs.statSync(p).mtimeMs);
    }
    return t;
  };
  const main = path.join(www, 'main.js');
  if (!fs.existsSync(main) || newest(path.join(web, 'src')) > fs.statSync(main).mtimeMs) {
    report('Building the editor...');
    if (run('node', ['build.mjs'], web).status !== 0) throw new Error('the editor build failed');
  }
  report('Building the engine...');
  const runs = path.join(process.env.LOCALAPPDATA || app.getPath('userData'), 'PyMagic', 'host');
  const out = path.join(runs, new Date().toISOString().replace(/[-:T.Z]/g, '').slice(0, 14));
  const build = run('dotnet', ['build', `"${path.join(repo, 'src', 'PyMagic.Web', 'PyMagic.Web.csproj')}"`, '-c', 'Release', '-o', `"${out}"`, '--nologo', '-v', 'quiet', '-p:UseAppHost=false'], repo);
  if (build.status !== 0) throw new Error('the engine build failed:\n' + (build.stdout || '').split('\n').filter(l => l.includes('error')).slice(0, 5).join('\n'));
  try {
    for (const d of fs.readdirSync(runs)) if (path.join(runs, d) !== out) fs.rmSync(path.join(runs, d), { recursive: true, force: true });
  } catch { /* a copy still in use stays */ }
  return path.join(out, 'pymagic-web.dll');
}

function waitFor(url, tries = 100) {
  return new Promise((resolve, reject) => {
    const attempt = n => {
      http.get(url, res => { res.resume(); resolve(); }).on('error', () => {
        if (n <= 0) reject(new Error('the engine did not start')); else setTimeout(() => attempt(n - 1), 200);
      });
    };
    attempt(tries);
  });
}

function stopHost(w) {
  if (w.host) { try { w.host.kill(); } catch { /* already gone */ } w.host = null; }
}

async function startHost(w, dll, folder) {
  stopHost(w);
  // a port no other window's engine has taken while starting
  let port = await freePort(5180);
  while ([...windows].some(o => o !== w && o.port === port)) port = await freePort(port + 1);
  w.port = port;
  const args = [dll, '--root', folder, '--www', www, '--port', String(port)];
  const ts = typeshed();
  if (ts) args.push('--typeshed', ts);
  const env = hostEnvironment(os.cpus().length);
  const host = packaged
    ? spawn(dll, args.slice(1), { cwd: path.dirname(dll), windowsHide: true, env })
    : spawn('dotnet', args, { cwd: repo, windowsHide: true, env });
  w.host = host;
  host.on('exit', () => { if (w.host === host) w.host = null; });
  await waitFor(`http://127.0.0.1:${port}/api/info`);
}

let hostDll = null;

const samePath = (a, b) => !!a && !!b && path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();

/** Opens a folder in a window: its engine is started on it and the page loaded. */
async function openProject(w, folder) {
  w.project = folder;
  const s = readSettings();
  s.last = folder;
  delete s.closed;
  s.recent = remember(s.recent, folder);
  writeSettings(s);
  w.win.setTitle(`PyMagic - ${folder}`);
  await startHost(w, hostDll, folder);
  await w.win.loadURL(`http://127.0.0.1:${w.port}/`);
}

/** The Open Project dialog's answer: a project open in a window already brings that one to the front; 'new' opens a window of its own. */
async function openProjectIn(w, folder, where) {
  const open = [...windows].find(o => samePath(o.project, folder));
  if (open) {
    if (open.win.isMinimized()) open.win.restore();
    open.win.focus();
    return;
  }
  if (where === 'new') {
    const other = createWindow();
    await splash(other, 'Opening ' + folder + '...');
    await openProject(other, folder);
    return;
  }
  await openProject(w, folder);
}

/** The welcome screen of a window without a project: the recent projects, Open folder and the demo (welcome.html). */
async function showWelcome(w) {
  stopHost(w);
  w.project = null;
  w.port = 0;
  w.win.setTitle('PyMagic');
  await w.win.loadFile(path.join(__dirname, 'welcome.html'));
}

/** The folder a new project or a clone is suggested beside: the last project, or PyMagicProjects in the home folder. */
const nearFolder = () => existing(readSettings().recent, fs.existsSync, null)[0] ?? path.join(os.homedir(), 'PyMagicProjects', 'project');

/**
 * The welcome screen's New project and Plugins: their dialog alone (the page's welcome-tool.html), on an engine started for it in a folder of
 * PyMagic's own that is never a recent project. New project's location is suggested beside the last project; the project made opens in this
 * window. Plugins is Settings at its Plugins page. Closing either goes back to the welcome screen.
 */
async function welcomeTool(w, tool) {
  const room = path.join(app.getPath('userData'), 'new-project');
  fs.mkdirSync(room, { recursive: true });
  w.project = null;
  w.win.setTitle(tool === 'plugins' ? 'PyMagic - Plugins' : 'PyMagic - New project');
  await startHost(w, hostDll, room);
  await w.win.loadURL(`http://127.0.0.1:${w.port}/welcome-tool.html?tool=${tool}&near=${encodeURIComponent(nearFolder())}`);
}

/**
 * The welcome screen's Get from VCS: `git clone` of an address into a new folder, its progress told to the page as git writes it; the folder
 * opens in this window when the clone is done. One clone at a time per window; Cancel stops it and removes what it had written.
 */
const clones = new Map();   // window -> { child, folder }
function cloneRepository(w, sender, url, folder) {
  return new Promise((resolve) => {
    if (clones.has(w)) { resolve({ error: 'A clone is already running in this window.' }); return; }
    if (!cloneAddressOk(url)) {
      resolve({ error: 'An address of a repository: https://…, ssh://… or git@host:owner/name.git' });
      return;
    }
    if (typeof folder !== 'string' || !path.isAbsolute(folder)) { resolve({ error: 'A full path for the folder it goes in.' }); return; }
    const target = path.resolve(folder);
    if (fs.existsSync(target) && fs.readdirSync(target).length > 0) { resolve({ error: `${target} is there already and not empty.` }); return; }
    // no shell; `--` before the address, so it is never taken for an option of git
    const child = spawn('git', ['clone', '--progress', '--', url.trim(), target], { windowsHide: true });
    clones.set(w, { child, folder: target });
    let tail = '';
    const tell = (chunk) => {
      const text = chunk.toString();
      tail = (tail + text).slice(-2000);
      for (const line of text.split(/[\r\n]+/).map((l) => l.trim()).filter(Boolean))
        if (!sender.isDestroyed()) sender.send('pymagic:clone-progress', line);
    };
    child.stdout.on('data', tell);
    child.stderr.on('data', tell);
    child.on('error', (e) => { clones.delete(w); resolve({ error: e.code === 'ENOENT' ? 'Git is not installed (git was not found on the PATH).' : String(e.message) }); });
    child.on('close', (code, signal) => {
      clones.delete(w);
      if (code === 0) { resolve({ folder: target }); return; }
      if (signal || code === null) {
        try { fs.rmSync(target, { recursive: true, force: true }); } catch { /* left as it is */ }
        resolve({ cancelled: true });
        return;
      }
      const last = tail.split(/[\r\n]+/).map((l) => l.trim()).filter(Boolean).reverse().find((l) => /fatal|error/i.test(l)) ?? `git clone ended with code ${code}`;
      resolve({ error: last.replace(/^fatal:\s*/i, '') });
    });
  });
}
function cancelClone(w) {
  const c = clones.get(w);
  if (c) spawnSync('taskkill', ['/PID', String(c.child.pid), '/T', '/F'], { windowsHide: true });
}

// ---- GitHub for Get from VCS: the GitHub CLI's own sign-in when it is installed, else a token the person pastes (kept encrypted by Windows)
const githubTokenFile = () => path.join(app.getPath('userData'), 'github-token.bin');
const githubApiBase = () => process.env.PYMAGIC_GITHUB_API || 'https://api.github.com';
function storedGithubToken() {
  try { return safeStorage.isEncryptionAvailable() ? safeStorage.decryptString(fs.readFileSync(githubTokenFile())) : null; } catch { return null; }
}
/** Runs the GitHub CLI: { code, out, err }, code -1 and missing when it is not installed. */
function runGh(args, timeoutMs = 60000) {
  return new Promise((resolve) => {
    let out = '', err = '';
    let child;
    try { const c = ghCommand(args); child = spawn(c.program, c.args, { windowsHide: true, env: { ...process.env, GH_PROMPT_DISABLED: '1', NO_COLOR: '1' } }); }
    catch { resolve({ code: -1, out, err, missing: true }); return; }
    const timer = setTimeout(() => { try { child.kill(); } catch { /* gone */ } }, timeoutMs);
    child.stdout.on('data', (d) => { out += d; });
    child.stderr.on('data', (d) => { err += d; });
    child.on('error', (e) => { clearTimeout(timer); resolve({ code: -1, out, err: String(e.message), missing: e.code === 'ENOENT' }); });
    child.on('close', (code) => { clearTimeout(timer); resolve({ code: code ?? -1, out, err }); });
  });
}
async function githubApi(token, apiPath) {
  const res = await fetch(githubApiBase() + apiPath, { headers: { Authorization: 'Bearer ' + token, Accept: 'application/vnd.github+json', 'User-Agent': 'PyMagic', 'X-GitHub-Api-Version': '2022-11-28' } });
  if (!res.ok) throw new Error(res.status === 401 ? 'GitHub did not take the token (401): it may have expired or been revoked.' : 'GitHub answered ' + res.status + ' ' + res.statusText);
  return res.json();
}
/** Who is signed in: { via: 'token' | 'gh' | null, user, gh (installed) }. */
async function githubStatus() {
  const token = storedGithubToken();
  if (token) {
    try { return { via: 'token', user: (await githubApi(token, '/user')).login }; }
    catch (e) { return { via: null, error: String(e.message), tokenKept: true }; }
  }
  const r = await runGh(['auth', 'status', '--hostname', 'github.com'], 30000);
  const user = ghAccountOf(r.out + '\n' + r.err);
  return r.code === 0 && user ? { via: 'gh', user } : { via: null, gh: !r.missing };
}
const ghLogins = new Map();   // window -> the gh auth login running for it
// ---- Get from VCS's other hosts (GitHub Enterprise, GitLab, Bitbucket): a token each, all kept in one file encrypted by Windows
const vcsAccountsFile = () => path.join(app.getPath('userData'), 'vcs-accounts.bin');
function vcsAccounts() {
  try { return safeStorage.isEncryptionAvailable() ? JSON.parse(safeStorage.decryptString(fs.readFileSync(vcsAccountsFile()))) : {}; } catch { return {}; }
}
function keepVcsAccounts(accounts) {
  if (!Object.keys(accounts).length) { try { fs.unlinkSync(vcsAccountsFile()); } catch { /* none */ } return; }
  fs.mkdirSync(path.dirname(vcsAccountsFile()), { recursive: true });
  fs.writeFileSync(vcsAccountsFile(), safeStorage.encryptString(JSON.stringify(accounts)));
}
async function vcsApi(a, apiPath) {
  const label = vcsHosts.KINDS[a.kind].label;
  let res;
  try { res = await fetch(vcsHosts.apiBaseOf(a.kind, a.server) + apiPath, { headers: vcsHosts.headersOf(a.kind, a.token, a.user), signal: AbortSignal.timeout(30000) }); }
  catch (e) { throw new Error(label + ' could not be reached (' + a.server + '): ' + (e.cause?.code ?? e.message)); }
  if (res.status === 401 || res.status === 403) throw new Error(label + ' did not take the token (' + res.status + '): it may be wrong, expired, or lack the read scopes.');
  if (!res.ok) throw new Error(label + ' answered ' + res.status + ' ' + res.statusText);
  return res.json();
}
const REPOS_PATH = '/user/repos?per_page=100&sort=pushed&affiliation=owner,collaborator,organization_member';

/**
 * Close Project, as PyCharm's: with another project window open, this one closes; the last one shows the welcome screen, and so does the
 * next start (until a project is opened).
 */
async function closeProject(w) {
  if (windows.size > 1) { w.win.close(); return; }
  const s = readSettings();
  s.closed = true;
  writeSettings(s);
  await showWelcome(w);
}

async function chooseFolder(w) {
  const r = await dialog.showOpenDialog(w.win, { title: 'Open a Python project', properties: ['openDirectory'], defaultPath: w.project || readSettings().last });
  return r.canceled ? null : r.filePaths[0] ?? null;
}

/** The window a request of a page came from (a tab opened in a window of its own belongs to the window whose engine it uses). */
function windowOf(sender) {
  const own = [...windows].find(w => w.win.webContents === sender);
  if (own) return own;
  let port = 0;
  try { port = Number(new URL(sender.getURL()).port); } catch { /* not an address */ }
  return [...windows].find(w => w.port === port) ?? [...windows][0];
}

/** Runs a command of the page's own in the window in front (the menu's Open Folder asks there how to open it). */
function inPage(code) {
  const w = [...windows].find(o => o.win.isFocused()) ?? [...windows][0];
  w?.win.webContents.executeJavaScript(code).catch(() => {});
}

const windowOptions = (width, height) => ({ width, height, backgroundColor: '#0f111a', title: 'PyMagic', icon, autoHideMenuBar: true, webPreferences: { contextIsolation: true, preload: path.join(__dirname, 'preload.js') } });

function createWindow() {
  // the page has its own menu (the mark at the left of its command bar); the app's menu stays for its keys and shows with Alt
  const w = { win: new BrowserWindow(windowOptions(1400, 900)), host: null, port: 0, project: null };
  windows.add(w);
  // Open Tab in New Window: the page opens itself again (?open=<file>) in a window of PyMagic; any other address goes to the browser
  w.win.webContents.setWindowOpenHandler(({ url }) => {
    let own = false;
    try { const u = new URL(url); own = (u.hostname === '127.0.0.1' || u.hostname === 'localhost') && Number(u.port) === w.port && u.searchParams.has('open'); } catch { /* not an address */ }
    if (own) return { action: 'allow', overrideBrowserWindowOptions: windowOptions(1100, 800) };
    shell.openExternal(url);
    return { action: 'deny' };
  });
  // its engine ends with it
  w.win.on('closed', () => { stopHost(w); windows.delete(w); });
  return w;
}

function menu() {
  Menu.setApplicationMenu(Menu.buildFromTemplate([
    { label: 'File', submenu: [
      { label: 'Open Folder...', accelerator: 'CmdOrCtrl+Shift+O', click: () => inPage('window.pymagic?.projectDesktop()?.openFolder()') },
      { label: 'Open the demo project', click: () => inPage('window.pymagic?.projectDesktop()?.openDemo()') },
      { label: 'Close Project', click: () => inPage('window.pymagic?.closeProject?.()') },
      { type: 'separator' },
      { role: 'quit' },
    ] },
    { label: 'View', submenu: [
      { role: 'reload' }, { role: 'toggleDevTools' }, { type: 'separator' },
      { role: 'resetZoom' }, { role: 'zoomIn', accelerator: 'CmdOrCtrl+=' }, { role: 'zoomOut' }, { role: 'togglefullscreen' },
    ] },
    { label: 'Help', submenu: [{ label: 'PyMagic on GitHub', click: () => shell.openExternal('https://github.com/residentcode/pymagic') }] },
  ]));
}

function splash(w, text) {
  const html = `<body style="margin:0;background:#0f111a;color:#bcc1d6;font:15px Segoe UI;display:flex;align-items:center;justify-content:center;height:100vh"><div id=t></div></body>`;
  return w.win.loadURL('data:text/html;charset=utf-8,' + encodeURIComponent(html))
    .then(() => w.win.webContents.executeJavaScript(`document.getElementById('t').textContent = ${JSON.stringify(text)}`));
}

app.whenReady().then(async () => {
  const first = createWindow();
  ipcMain.handle('pymagic:choose-folder', (e) => chooseFolder(windowOf(e.sender)));
  ipcMain.handle('pymagic:open-project', async (e, folder, where) => {
    if (typeof folder === 'string' && fs.existsSync(folder) && fs.statSync(folder).isDirectory())
      await openProjectIn(windowOf(e.sender), path.resolve(folder), where === 'new' ? 'new' : 'this');
  });
  ipcMain.handle('pymagic:demo-folder', () => demo);
  ipcMain.handle('pymagic:close-project', (e) => closeProject(windowOf(e.sender)));
  // the welcome screen: its New project, and the way back from that dialog when it is cancelled (a window without a project only)
  ipcMain.handle('pymagic:new-project', (e) => { const w = windowOf(e.sender); if (!w.project) return welcomeTool(w, 'newproject'); });
  ipcMain.handle('pymagic:plugins', (e) => { const w = windowOf(e.sender); if (!w.project) return welcomeTool(w, 'plugins'); });
  // Customize: the theme, code colours, font and keymap of the person's settings (the engine's file), read and changed before a project is open;
  // All settings… is Settings itself on an engine of its own, as Plugins is
  ipcMain.handle('pymagic:customize', () => {
    try { return customizeOf(JSON.parse(fs.readFileSync(engineSettingsFile(), 'utf8'))); } catch { return customizeOf({}); }
  });
  ipcMain.handle('pymagic:customize-set', (_e, patch) => {
    const file = engineSettingsFile();
    let stored = {};
    try { stored = JSON.parse(fs.readFileSync(file, 'utf8')); } catch { /* none yet */ }
    try {
      const next = withCustomize(stored, patch);
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, JSON.stringify(next, null, 2));
      return customizeOf(next);
    } catch (err) { return { error: String(err.message ?? err) }; }
  });
  // Get from VCS's GitHub: who is signed in, signing in (the CLI's browser sign-in, its code told to the page; or a token), the repositories
  ipcMain.handle('pymagic:github-status', () => githubStatus());
  ipcMain.handle('pymagic:github-login', (e) => new Promise((resolve) => {
    const w = windowOf(e.sender);
    if (ghLogins.has(w)) { resolve({ error: 'A sign-in is already waiting.' }); return; }
    let child;
    try { const c = ghCommand(['auth', 'login', '--hostname', 'github.com', '--git-protocol', 'https', '--web']); child = spawn(c.program, c.args, { windowsHide: true }); }
    catch { resolve({ error: 'The GitHub CLI (gh) is not installed.' }); return; }
    ghLogins.set(w, child);
    let text = '', told = false, entered = false, cancelled = false;
    child.cancel = () => { cancelled = true; try { child.kill(); } catch { /* gone */ } };
    const read = (d) => {
      text += d;
      const code = deviceCodeOf(text);
      if (code && !told && !e.sender.isDestroyed()) { told = true; e.sender.send('pymagic:github-code', code); }
      // gh waits for Enter, then opens the browser itself
      if (ghWantsEnter(text) && !entered) { entered = true; try { child.stdin.write('\n'); } catch { /* gone */ } }
    };
    child.stdout.on('data', read);
    child.stderr.on('data', read);
    child.on('error', (err) => { ghLogins.delete(w); resolve({ error: err.code === 'ENOENT' ? 'The GitHub CLI (gh) is not installed.' : String(err.message) }); });
    child.on('close', async (code) => {
      ghLogins.delete(w);
      if (cancelled) { resolve({ error: 'The sign-in was cancelled.' }); return; }
      if (code !== 0) { resolve({ error: text.trim().split('\n').pop() || 'gh ended with code ' + code }); return; }
      resolve(await githubStatus());
    });
  }));
  ipcMain.handle('pymagic:github-login-cancel', (e) => { ghLogins.get(windowOf(e.sender))?.cancel(); });
  ipcMain.handle('pymagic:github-token', async (_e, token) => {
    const t = String(token ?? '').trim();
    if (!tokenOk(t)) return { error: 'That is not a GitHub token: it starts with ghp_ or github_pat_.' };
    if (!safeStorage.isEncryptionAvailable()) return { error: 'Windows cannot keep the token encrypted here.' };
    let user;
    try { user = (await githubApi(t, '/user')).login; } catch (err) { return { error: String(err.message) }; }
    fs.mkdirSync(path.dirname(githubTokenFile()), { recursive: true });
    fs.writeFileSync(githubTokenFile(), safeStorage.encryptString(t));
    return { via: 'token', user };
  });
  // forgetting PyMagic's token; the GitHub CLI's own sign-in is the person's, left as it is
  ipcMain.handle('pymagic:github-forget', () => { try { fs.unlinkSync(githubTokenFile()); } catch { /* none */ } return githubStatus(); });
  ipcMain.handle('pymagic:github-repos', async () => {
    try {
      const token = storedGithubToken();
      if (token) {
        const pages = [];
        for (let page = 1; page <= 5; page++) {
          const list = await githubApi(token, REPOS_PATH + '&page=' + page);
          pages.push(list);
          if (!Array.isArray(list) || list.length < 100) break;
        }
        return { repos: reposOf(pages) };
      }
      const r = await runGh(['api', '--paginate', REPOS_PATH.slice(1)], 120000);
      if (r.code !== 0) return { error: (r.err || r.out).trim().split('\n').pop() || 'gh api failed' };
      // --paginate writes one array per page, one after the other
      const pages = JSON.parse('[' + r.out.trim().replace(/\]\s*\[/g, '],[') + ']');
      return { repos: reposOf(pages) };
    } catch (err) { return { error: String(err.message ?? err) }; }
  });
  // the other hosts: the accounts kept (no token reaches the page), signing in to one, forgetting it, its repositories
  ipcMain.handle('pymagic:vcs-accounts', () => ({ accounts: vcsHosts.accountsShown(vcsAccounts()), kinds: vcsHosts.KINDS }));
  ipcMain.handle('pymagic:vcs-sign-in', async (_e, kind, server, user, token) => {
    try {
      const problem = vcsHosts.signInProblem(kind, { token, user });
      if (problem) return { error: problem };
      if (!safeStorage.isEncryptionAvailable()) return { error: 'Windows cannot keep the token encrypted here.' };
      const a = { kind, server: vcsHosts.serverOf(kind, server), user: String(user ?? '').trim(), token: String(token).trim() };
      a.login = vcsHosts.loginOf(kind, await vcsApi(a, '/user'));
      const all = vcsAccounts();
      const key = vcsHosts.accountKey(kind, a.server);
      all[key] = a;
      keepVcsAccounts(all);
      return { key, accounts: vcsHosts.accountsShown(all) };
    } catch (err) { return { error: String(err.message ?? err) }; }
  });
  ipcMain.handle('pymagic:vcs-forget', (_e, key) => { const all = vcsAccounts(); delete all[String(key)]; keepVcsAccounts(all); return { accounts: vcsHosts.accountsShown(all) }; });
  ipcMain.handle('pymagic:vcs-repos', async (_e, key) => {
    const a = vcsAccounts()[String(key)];
    if (!a) return { error: 'That account is not signed in any more.' };
    try {
      const pages = [];
      for (let page = 1; page <= 10; page++) {
        const body = await vcsApi(a, vcsHosts.reposPathOf(a.kind, page));
        pages.push(body);
        if (!vcsHosts.morePages(a.kind, body)) break;
      }
      return { repos: vcsHosts.hostReposOf(a.kind, pages) };
    } catch (err) { return { error: String(err.message ?? err) }; }
  });
  ipcMain.handle('pymagic:all-settings', (e) => { const w = windowOf(e.sender); if (!w.project) return welcomeTool(w, 'settings'); });
  // Get from VCS: where the clone is suggested, the clone itself (its progress as pymagic:clone-progress), and Cancel
  ipcMain.handle('pymagic:clone-near', (_e, url) => path.join(path.dirname(nearFolder()), repositoryName(url) || 'repository'));
  ipcMain.handle('pymagic:clone', async (e, url, folder) => {
    const w = windowOf(e.sender);
    if (w.project) return { error: 'Get from VCS is on the welcome screen.' };
    const r = await cloneRepository(w, e.sender, url, folder);
    if (r.folder) await openProjectIn(w, r.folder, 'this');
    return r;
  });
  ipcMain.handle('pymagic:clone-cancel', (e) => cancelClone(windowOf(e.sender)));
  ipcMain.handle('pymagic:show-welcome', (e) => { const w = windowOf(e.sender); if (!w.project) return showWelcome(w); });
  // the New Project dialog's Browse: a folder's path, which the page sends back to the engine to make the project in
  ipcMain.handle('pymagic:pick-folder', async (e, start) => {
    const r = await dialog.showOpenDialog(windowOf(e.sender).win, { title: 'Where the new project goes', properties: ['openDirectory', 'createDirectory', 'promptToCreate'], defaultPath: typeof start === 'string' && start ? start : undefined });
    return r.canceled ? null : r.filePaths[0] ?? null;
  });
  ipcMain.handle('pymagic:recent', (e) => existing(readSettings().recent, fs.existsSync, windowOf(e.sender).project));
  // Remove from recent projects (a folder) and Clear recent projects (none): the folders open in a window stay
  ipcMain.handle('pymagic:recent-forget', (e, folder) => {
    const s = readSettings();
    s.recent = forget(s.recent, typeof folder === 'string' ? folder : null, [...windows].map((w) => w.project));
    writeSettings(s);
    return existing(s.recent, fs.existsSync, windowOf(e.sender).project);
  });
  // Open In > Explorer: a file or folder of the open project, selected in a window of Explorer
  ipcMain.handle('pymagic:reveal', (e, rel) => {
    const project = windowOf(e.sender).project;
    if (!project || typeof rel !== 'string') return false;
    const base = path.resolve(project), full = path.resolve(base, rel);
    if (full !== base && !full.startsWith(base + path.sep)) return false;
    if (!fs.existsSync(full)) return false;
    shell.showItemInFolder(full);
    return true;
  });
  menu();
  const report = t => first.win.webContents.executeJavaScript(`document.getElementById('t') && (document.getElementById('t').textContent = ${JSON.stringify(t)})`).catch(() => {});
  try {
    await splash(first, 'Starting PyMagic...');
    hostDll = await new Promise((resolve, reject) => setImmediate(() => { try { resolve(prepare(report)); } catch (e) { reject(e); } }));
    let folder = process.argv.slice(packaged ? 1 : 2).find(a => !a.startsWith('--') && fs.existsSync(a));
    // the project was closed last time: the welcome screen, as PyCharm starts
    if (!folder && readSettings().closed) { await showWelcome(first); return; }
    if (!folder) {
      const last = readSettings().last;
      folder = last && fs.existsSync(last) ? last : demo;
    }
    await openProject(first, path.resolve(folder));
  } catch (e) {
    dialog.showErrorBox('PyMagic could not start', String(e.message || e));
    app.quit();
  }
});

// Electron shows no menu of its own on a right-click: a text box or selected text that the page gives no menu of its own (the dialogs'
// fields, the search boxes, the welcome screen) gets Cut, Copy, Paste and Select all, as everywhere else in Windows
app.on('web-contents-created', (_e, contents) => {
  contents.on('context-menu', (_ev, p) => {
    if (!p.isEditable && !p.selectionText) return;
    const f = p.editFlags;
    const items = p.isEditable
      ? [{ role: 'cut', enabled: f.canCut }, { role: 'copy', enabled: f.canCopy }, { role: 'paste', enabled: f.canPaste }, { type: 'separator' }, { role: 'selectAll', enabled: f.canSelectAll }]
      : [{ role: 'copy', enabled: f.canCopy }, { role: 'selectAll' }];
    Menu.buildFromTemplate(items).popup({ window: BrowserWindow.fromWebContents(contents) ?? undefined });
  });
});

app.on('window-all-closed', () => app.quit());
const stopAll = () => { for (const w of windows) stopHost(w); };
app.on('before-quit', stopAll);
process.on('exit', stopAll);

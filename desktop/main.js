// PyMagic for Windows: starts the C# host in the background and shows the editor in its own window.
const { app, BrowserWindow, Menu, dialog, shell, ipcMain } = require('electron');
const { spawn, spawnSync } = require('node:child_process');
const fs = require('node:fs');
const net = require('node:net');
const path = require('node:path');
const http = require('node:http');
const os = require('node:os');
const { hostEnvironment } = require('./host-env');
const { remember, existing } = require('./recent-projects');
const { missingWebPackages } = require('./web-packages');

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
  // the New Project dialog's Browse: a folder's path, which the page sends back to the engine to make the project in
  ipcMain.handle('pymagic:pick-folder', async (e, start) => {
    const r = await dialog.showOpenDialog(windowOf(e.sender).win, { title: 'Where the new project goes', properties: ['openDirectory', 'createDirectory', 'promptToCreate'], defaultPath: typeof start === 'string' && start ? start : undefined });
    return r.canceled ? null : r.filePaths[0] ?? null;
  });
  ipcMain.handle('pymagic:recent', (e) => existing(readSettings().recent, fs.existsSync, windowOf(e.sender).project));
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

app.on('window-all-closed', () => app.quit());
const stopAll = () => { for (const w of windows) stopHost(w); };
app.on('before-quit', stopAll);
process.on('exit', stopAll);

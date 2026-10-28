// PyMagic for Windows: starts the C# host in the background and shows the editor in its own window.
const { app, BrowserWindow, Menu, dialog, shell } = require('electron');
const { spawn, spawnSync } = require('node:child_process');
const fs = require('node:fs');
const net = require('node:net');
const path = require('node:path');
const http = require('node:http');
const os = require('node:os');
const { hostEnvironment } = require('./host-env');

const repo = path.resolve(__dirname, '..');
const packaged = app.isPackaged;
const icon = path.join(__dirname, 'icon', 'pymagic.ico');
// the id the installer gives the shortcuts (build-config.mjs), so the running app and a pinned shortcut share a taskbar button;
// a run from the repo keeps Electron's own, so it is not taken for an installed copy
if (process.platform === 'win32' && packaged) app.setAppUserModelId('io.github.residentcode.pymagic');
const www = packaged ? path.join(process.resourcesPath, 'www') : path.join(repo, 'web', 'dist');
const demo = packaged ? path.join(process.resourcesPath, 'demo') : path.join(repo, 'examples', 'demo');
const settingsFile = () => path.join(app.getPath('userData'), 'settings.json');

let host = null;
let port = 0;
let win = null;
let project = null;

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

function typeshed() {
  if (process.env.PYMAGIC_TYPESHED) return process.env.PYMAGIC_TYPESHED;
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
  if (!fs.existsSync(path.join(web, 'node_modules', 'monaco-editor'))) {
    report('Installing the editor packages (first run only)...');
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

function stopHost() {
  if (host) { try { host.kill(); } catch { /* already gone */ } host = null; }
}

async function startHost(dll, folder) {
  stopHost();
  port = await freePort(5180);
  const args = [dll, '--root', folder, '--www', www, '--port', String(port)];
  const ts = typeshed();
  if (ts) args.push('--typeshed', ts);
  const env = hostEnvironment(os.cpus().length);
  host = packaged
    ? spawn(dll, args.slice(1), { cwd: path.dirname(dll), windowsHide: true, env })
    : spawn('dotnet', args, { cwd: repo, windowsHide: true, env });
  host.on('exit', () => { host = null; });
  await waitFor(`http://127.0.0.1:${port}/api/info`);
}

let hostDll = null;

async function openProject(folder) {
  project = folder;
  const s = readSettings();
  s.last = folder;
  writeSettings(s);
  win.setTitle(`PyMagic - ${folder}`);
  await startHost(hostDll, folder);
  await win.loadURL(`http://127.0.0.1:${port}/`);
}

async function chooseFolder() {
  const r = await dialog.showOpenDialog(win, { title: 'Open a Python project', properties: ['openDirectory'], defaultPath: project || readSettings().last });
  if (!r.canceled && r.filePaths[0]) await openProject(r.filePaths[0]);
}

function menu() {
  Menu.setApplicationMenu(Menu.buildFromTemplate([
    { label: 'File', submenu: [
      { label: 'Open Folder...', accelerator: 'CmdOrCtrl+Shift+O', click: () => chooseFolder() },
      { label: 'Open the demo project', click: () => openProject(demo) },
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

function splash(text) {
  const html = `<body style="margin:0;background:#1e1e1e;color:#ccc;font:15px Segoe UI;display:flex;align-items:center;justify-content:center;height:100vh"><div id=t>${text}</div></body>`;
  return win.loadURL('data:text/html;charset=utf-8,' + encodeURIComponent(html));
}

app.whenReady().then(async () => {
  win = new BrowserWindow({ width: 1400, height: 900, backgroundColor: '#1e1e1e', title: 'PyMagic', icon, autoHideMenuBar: false, webPreferences: { contextIsolation: true } });
  win.webContents.setWindowOpenHandler(({ url }) => { shell.openExternal(url); return { action: 'deny' }; });
  menu();
  const report = t => win.webContents.executeJavaScript(`document.getElementById('t') && (document.getElementById('t').textContent = ${JSON.stringify(t)})`).catch(() => {});
  try {
    await splash('Starting PyMagic...');
    hostDll = await new Promise((resolve, reject) => setImmediate(() => { try { resolve(prepare(report)); } catch (e) { reject(e); } }));
    let folder = process.argv.slice(packaged ? 1 : 2).find(a => !a.startsWith('--') && fs.existsSync(a));
    if (!folder) {
      const last = readSettings().last;
      folder = last && fs.existsSync(last) ? last : demo;
    }
    await openProject(path.resolve(folder));
  } catch (e) {
    dialog.showErrorBox('PyMagic could not start', String(e.message || e));
    app.quit();
  }
});

app.on('window-all-closed', () => app.quit());
app.on('before-quit', stopHost);
process.on('exit', stopHost);

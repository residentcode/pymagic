// Checks of desktop/build-config.mjs: what goes into the app folder and what the installer does
// (`node desktop/test/build-config.test.mjs`; tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { APP_ID, ICON, PRODUCT, installerConfig, packagerOptions } from '../build-config.mjs';

const desktop = resolve(import.meta.dirname, '..');
const opts = packagerOptions(desktop, resolve(desktop, 'stage'));
const kept = p => !opts.ignore.some(re => re.test(p));

test('the installer is a wizard that installs for the current user unless told otherwise', () => {
  const { nsis } = installerConfig();
  assert.equal(nsis.oneClick, false);
  assert.equal(nsis.perMachine, false);
});

test('the installer has a folder page, and a script that only lets it take a folder PyMagic can own', () => {
  // electron-builder keeps a chosen folder as it is when its path contains "PyMagic", and uninstalling deletes it whole
  const { nsis } = installerConfig();
  assert.equal(nsis.allowToChangeInstallationDirectory, true);
  assert.equal(nsis.include, 'installer/installer.nsh');
  const script = readFileSync(resolve(desktop, nsis.include), 'utf8');
  for (const part of ['Function PMCheckFolder', 'Function .onVerifyInstDir', 'SetErrorLevel 2', 'StrCpy $INSTDIR "$1"', 'IfFileExists "$1\\${APP_FILENAME}.exe"'])
    assert.ok(script.includes(part), `installer.nsh no longer has ${part}`);
});

test('the desktop shortcut is a check box of the wizard, made and removed by the script and not by the template', () => {
  const { nsis } = installerConfig();
  assert.equal(nsis.createDesktopShortcut, false);   // the template would make it without asking
  const script = readFileSync(resolve(desktop, nsis.include), 'utf8');
  for (const part of ['${NSD_CreateCheckbox}', 'Page custom PMShortcutPage PMShortcutPageLeave', '!macro customInstall', 'CreateShortCut "$DESKTOP\\${SHORTCUT_NAME}.lnk"',
    '!macro customUnInstall', 'Delete "$DESKTOP\\${SHORTCUT_NAME}.lnk"'])assert.ok(script.includes(part), `installer.nsh no longer has ${part}`);
});

test('the installer makes a Start menu entry named after the app', () => {
  const { nsis } = installerConfig();
  assert.equal(nsis.createStartMenuShortcut, true);
  assert.equal(nsis.shortcutName, PRODUCT);
  assert.equal(nsis.uninstallDisplayName, PRODUCT);
});

test('the installer file carries the version, and nothing is published', () => {
  const c = installerConfig();
  assert.match(c.nsis.artifactName, /^PyMagic-Setup-\$\{version\}\.\$\{ext\}$/);
  assert.equal(c.publish, null);
  assert.equal(c.productName, PRODUCT);
});

test('every icon the app and the installer name exists', () => {
  const c = installerConfig();
  for (const p of [c.win.icon, c.nsis.installerIcon, c.nsis.uninstallerIcon]) assert.ok(existsSync(resolve(desktop, p)), p);
  assert.ok(existsSync(opts.icon), opts.icon);
  assert.equal(opts.icon, resolve(desktop, ICON));
});

test('the installed app uses the same app id as the shortcuts (a run from the repo does not), and the icon for its window', () => {
  const main = readFileSync(resolve(desktop, 'main.js'), 'utf8');
  assert.equal(installerConfig().appId, APP_ID);
  assert.ok(main.includes(`if (process.platform === 'win32' && packaged) app.setAppUserModelId('${APP_ID}');`), 'main.js sets another app id, or sets it for a run from the repo too');
  assert.match(main, /path\.join\(__dirname, 'icon', 'pymagic\.ico'\)/);
  // every window (each has a project) takes the options with the icon
  assert.match(main, /const windowOptions = [^\n]*\bicon\b/);
  assert.match(main, /new BrowserWindow\(windowOptions\(/);
});

test('package.mjs builds the installer from the packaged folder, and the tools are pinned', () => {
  const script = readFileSync(resolve(desktop, 'package.mjs'), 'utf8');
  for (const part of ['packager(packagerOptions(desktop, stage))', 'prepackaged: paths[0]', 'config: installerConfig()', "'--no-installer'"])
    assert.ok(script.includes(part), `package.mjs no longer has ${part}`);
  const pkg = JSON.parse(readFileSync(resolve(desktop, 'package.json'), 'utf8'));
  assert.match(pkg.devDependencies['electron-builder'], /^\d+\.\d+\.\d+$/, 'electron-builder is not pinned to one version');
  assert.ok(pkg.author, 'package.json has no author (the publisher shown in Settings > Apps)');
});

test('the app folder takes the shell and its icon, and leaves out the build tools, tests and icon source', () => {
  for (const p of ['/main.js', '/host-env.js', '/preload.js', '/recent-projects.js', '/package.json', '/icon', '/icon/pymagic.ico']) assert.ok(kept(p), `${p} is left out`);
  for (const p of ['/out', '/out/PyMagic-win32-x64/PyMagic.exe', '/stage/host', '/test/build-config.test.mjs',
    '/package.mjs', '/build-config.mjs', '/installer/installer.nsh', '/icon/pymagic.svg', '/icon/pymagic.ico.sha256', '/icon/make-icon.js']) assert.ok(!kept(p), `${p} is packaged`);
  assert.deepEqual(opts.extraResource.map(p => p.split(/[\\/]/).pop()), ['www', 'host', 'demo', 'typeshed', 'models', 'licenses']);
  assert.equal(opts.name, PRODUCT);
});

test('the wizard shows PyMagic\'s own pictures: 24-bit bitmaps of the sizes NSIS uses, drawn from their SVGs and left out of the app folder', () => {
  const { nsis } = installerConfig();
  for (const [key, w, h] of [['installerSidebar', 164, 314], ['uninstallerSidebar', 164, 314], ['installerHeader', 150, 57]]) {
    const file = resolve(desktop, nsis[key]);
    assert.ok(existsSync(file), `${nsis[key]} is missing (npm run wizard)`);
    const b = readFileSync(file);
    assert.equal(b.toString('ascii', 0, 2), 'BM');
    assert.deepEqual([b.readInt32LE(18), b.readInt32LE(22), b.readUInt16LE(28)], [w, h, 24], key);
    assert.ok(existsSync(file.replace(/\.bmp$/, '.svg')), `${nsis[key]} has no SVG it is drawn from`);
    assert.ok(!kept('/' + nsis[key]), `${nsis[key]} is packaged`);
  }
  assert.ok(!kept('/icon/make-wizard.js'));
});

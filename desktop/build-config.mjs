// What desktop/package.mjs hands to @electron/packager (the app folder) and to electron-builder (the installer).
// Kept apart from the build steps so desktop/test/build-config.test.mjs can check it without building anything.
import { resolve } from 'node:path';

export const PRODUCT = 'PyMagic';
/** Windows groups the taskbar button and the shortcuts by this; main.js sets the same id. */
export const APP_ID = 'io.github.residentcode.pymagic';
export const ICON = 'icon/pymagic.ico';

export function packagerOptions(desktop, stage) {
  return {
    dir: desktop,
    out: resolve(desktop, 'out'),
    name: PRODUCT,
    platform: 'win32',
    arch: 'x64',
    icon: resolve(desktop, ICON),
    win32metadata: { ProductName: PRODUCT, FileDescription: PRODUCT, CompanyName: 'PyMagic contributors' },   // as "author" in package.json
    overwrite: true,
    prune: true,
    // the build tools, the tests and the icon's source stay out of the app; the .ico itself goes in for the window
    ignore: [/^\/out/, /^\/stage/, /^\/test/, /^\/package\.mjs$/, /^\/build-config\.mjs$/, /^\/icon\/(?!pymagic\.ico$)/],
    extraResource: ['www', 'host', 'demo', 'typeshed'].map(d => resolve(stage, d)),
  };
}

/** electron-builder's settings: an NSIS setup wizard made from the folder packagerOptions produces. */
export function installerConfig() {
  return {
    appId: APP_ID,
    productName: PRODUCT,
    directories: { output: 'out' },
    publish: null,
    win: { icon: ICON },
    nsis: {
      oneClick: false,
      perMachine: false,                // a page offers "only for me" (the default, no admin) or "for all users"
      allowElevation: true,             // only needed for "for all users"
      // no folder page: electron-builder uses a chosen folder as it is when its path already says "PyMagic" (any case),
      // and uninstalling or updating then deletes everything in it, a git checkout of PyMagic included
      allowToChangeInstallationDirectory: false,
      createDesktopShortcut: true,
      createStartMenuShortcut: true,
      shortcutName: PRODUCT,
      installerIcon: ICON,
      uninstallerIcon: ICON,
      uninstallDisplayName: PRODUCT,
      runAfterFinish: true,             // the "Run PyMagic" box on the last page
      artifactName: 'PyMagic-Setup-${version}.${ext}',
    },
  };
}

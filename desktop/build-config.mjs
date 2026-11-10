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
    ignore: [/^\/out/, /^\/stage/, /^\/test/, /^\/installer/, /^\/package\.mjs$/, /^\/build-config\.mjs$/, /^\/icon\/(?!pymagic\.ico$)/],
    extraResource: ['www', 'host', 'demo', 'typeshed', 'models', 'phpactor', 'licenses'].map(d => resolve(stage, d)),
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
      // a folder page that only takes a folder PyMagic can own (not there yet, empty, or an earlier PyMagic): electron-builder keeps a chosen folder
      // as it is when its path already says "PyMagic", and uninstalling or updating deletes it whole; the rule is in installer/installer.nsh
      allowToChangeInstallationDirectory: true,
      createDesktopShortcut: false,     // made by installer/installer.nsh instead, on a page with a check box
      include: 'installer/installer.nsh',
      createStartMenuShortcut: true,
      shortcutName: PRODUCT,
      installerIcon: ICON,
      uninstallerIcon: ICON,
      // PyMagic's own pictures (icon/wizard-*.svg, drawn into these by `npm run wizard`): the sidebar of the first and last pages, the header of the others
      installerSidebar: 'icon/wizard-sidebar.bmp',
      uninstallerSidebar: 'icon/wizard-sidebar.bmp',
      installerHeader: 'icon/wizard-header.bmp',
      uninstallDisplayName: PRODUCT,
      runAfterFinish: true,             // the "Run PyMagic" box on the last page
      artifactName: 'PyMagic-Setup-${version}.${ext}',
    },
  };
}

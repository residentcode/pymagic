'use strict';

// What the page may ask of the desktop app: choose a project folder, open a project (in this window or a new one), the demo's folder, the recent
// projects (and remove one from them, or clear them), choose a folder for a new project, show a project file in Explorer. Nothing else of Electron or of the computer reaches the page.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('pymagicDesktop', {
  chooseFolder: () => ipcRenderer.invoke('pymagic:choose-folder'),
  openProject: (folder, where) => ipcRenderer.invoke('pymagic:open-project', String(folder), where === 'new' ? 'new' : 'this'),
  demoFolder: () => ipcRenderer.invoke('pymagic:demo-folder'),
  pickFolder: (start) => ipcRenderer.invoke('pymagic:pick-folder', String(start ?? '')),
  recentProjects: () => ipcRenderer.invoke('pymagic:recent'),
  forgetRecentProject: (folder) => ipcRenderer.invoke('pymagic:recent-forget', folder == null ? null : String(folder)),
  reveal: (rel) => ipcRenderer.invoke('pymagic:reveal', String(rel)),
});

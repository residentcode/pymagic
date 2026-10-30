'use strict';

// What the page may ask of the desktop app: open a folder, open a recent project, show a project file in Explorer. Nothing else of Electron or of the computer reaches the page.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('pymagicDesktop', {
  openFolder: () => ipcRenderer.invoke('pymagic:open-folder'),
  openProject: (folder) => ipcRenderer.invoke('pymagic:open-project', String(folder)),
  openDemo: () => ipcRenderer.invoke('pymagic:open-demo'),
  recentProjects: () => ipcRenderer.invoke('pymagic:recent'),
  reveal: (rel) => ipcRenderer.invoke('pymagic:reveal', String(rel)),
});

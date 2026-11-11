'use strict';

// What the page may ask of the desktop app: choose a project folder, open a project (in this window or a new one), the demo's folder, the recent
// projects (and remove one from them, or clear them), choose a folder for a new project, show a project file in Explorer, close the project. Nothing else of Electron or of the computer reaches the page.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('pymagicDesktop', {
  chooseFolder: () => ipcRenderer.invoke('pymagic:choose-folder'),
  openProject: (folder, where) => ipcRenderer.invoke('pymagic:open-project', String(folder), where === 'new' ? 'new' : 'this'),
  demoFolder: () => ipcRenderer.invoke('pymagic:demo-folder'),
  pickFolder: (start) => ipcRenderer.invoke('pymagic:pick-folder', String(start ?? '')),
  recentProjects: () => ipcRenderer.invoke('pymagic:recent'),
  forgetRecentProject: (folder) => ipcRenderer.invoke('pymagic:recent-forget', folder == null ? null : String(folder)),
  reveal: (rel) => ipcRenderer.invoke('pymagic:reveal', String(rel)),
  // Close Project: the window closes when another one is open, else it shows the welcome screen
  closeProject: () => ipcRenderer.invoke('pymagic:close-project'),
  // the welcome screen's New project, and back to the welcome screen from it
  newProject: () => ipcRenderer.invoke('pymagic:new-project'),
  plugins: () => ipcRenderer.invoke('pymagic:plugins'),
  customize: () => ipcRenderer.invoke('pymagic:customize'),
  setCustomize: (patch) => ipcRenderer.invoke('pymagic:customize-set', patch && typeof patch === 'object' ? { ...patch } : {}),
  allSettings: () => ipcRenderer.invoke('pymagic:all-settings'),
  // Get from VCS: git clone into a new folder, its progress line by line, and Cancel
  cloneNear: (url) => ipcRenderer.invoke('pymagic:clone-near', String(url ?? '')),
  cloneRepository: (url, folder) => ipcRenderer.invoke('pymagic:clone', String(url), String(folder)),
  cancelClone: () => ipcRenderer.invoke('pymagic:clone-cancel'),
  onCloneProgress: (fn) => { const on = (_e, line) => fn(String(line)); ipcRenderer.on('pymagic:clone-progress', on); return () => ipcRenderer.off('pymagic:clone-progress', on); },
  // Get from VCS's GitHub: who is signed in, the GitHub CLI's sign-in (its one-time code comes as onGithubCode), a token, the repositories
  githubStatus: () => ipcRenderer.invoke('pymagic:github-status'),
  githubLogin: () => ipcRenderer.invoke('pymagic:github-login'),
  githubLoginCancel: () => ipcRenderer.invoke('pymagic:github-login-cancel'),
  githubToken: (token) => ipcRenderer.invoke('pymagic:github-token', String(token ?? '')),
  githubForget: () => ipcRenderer.invoke('pymagic:github-forget'),
  githubRepos: () => ipcRenderer.invoke('pymagic:github-repos'),
  onGithubCode: (fn) => { const on = (_e, c) => fn({ code: String(c?.code ?? ''), url: String(c?.url ?? '') }); ipcRenderer.on('pymagic:github-code', on); return () => ipcRenderer.off('pymagic:github-code', on); },
  // Get from VCS's other hosts (GitHub Enterprise, GitLab, Bitbucket): the accounts kept, signing in with a token, forgetting one, its repositories
  vcsAccounts: () => ipcRenderer.invoke('pymagic:vcs-accounts'),
  vcsSignIn: (kind, server, user, token) => ipcRenderer.invoke('pymagic:vcs-sign-in', String(kind), String(server ?? ''), String(user ?? ''), String(token ?? '')),
  vcsForget: (key) => ipcRenderer.invoke('pymagic:vcs-forget', String(key)),
  vcsRepos: (key) => ipcRenderer.invoke('pymagic:vcs-repos', String(key)),
  showWelcome: () => ipcRenderer.invoke('pymagic:show-welcome'),
});

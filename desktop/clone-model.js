'use strict';

// Get from VCS of the welcome screen: which addresses are taken for git clone, and the folder a repository is suggested in.

/** An address git clone takes from the welcome screen: https://, http://, ssh://, git:// or scp-like git@host:owner/name; never one that starts with -. */
function cloneAddressOk(url) {
  const u = typeof url === 'string' ? url.trim() : '';
  return /^(https?:\/\/|ssh:\/\/|git:\/\/)[^\s]+$|^[\w.-]+@[\w.-]+:[^\s]+$/.test(u);
}

/** The folder name of a repository's address: its last part without .git (https://github.com/owner/name.git gives name); '' when there is none. */
function repositoryName(url) {
  const u = (typeof url === 'string' ? url.trim() : '').replace(/[?#].*$/, '').replace(/[\\/]+$/, '');
  const last = u.split(/[/:]/).pop() ?? '';
  return last.replace(/\.git$/i, '').replace(/[<>:"|?*]/g, '');
}

module.exports = { cloneAddressOk, repositoryName };

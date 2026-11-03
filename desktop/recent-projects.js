'use strict';

// The projects opened lately, newest first: kept in the settings file of the desktop app, shown in the File menu of the page, where one can be
// removed from the list or the list cleared.

const MAX = 10;

/** The list with this folder first (once); the same folder in another case or with a closing slash counts as the same. */
function remember(list, folder, max = MAX) {
  const key = (p) => p.replace(/[\\/]+$/, '').toLowerCase();
  const rest = (Array.isArray(list) ? list : []).filter((p) => typeof p === 'string' && key(p) !== key(folder));
  return [folder, ...rest].slice(0, max);
}

/** The folders of the list that are still there, the one that is open left out. */
function existing(list, exists, open = '') {
  const key = (p) => p.replace(/[\\/]+$/, '').toLowerCase();
  return (Array.isArray(list) ? list : []).filter((p) => typeof p === 'string' && key(p) !== key(open) && exists(p));
}

/** The list without this folder, or, for none given (Clear recent projects), without all but the folders open in a window (`open`). */
function forget(list, folder, open = []) {
  const key = (p) => p.replace(/[\\/]+$/, '').toLowerCase();
  const valid = (Array.isArray(list) ? list : []).filter((p) => typeof p === 'string');
  if (typeof folder === 'string' && folder) return valid.filter((p) => key(p) !== key(folder));
  const kept = new Set(open.filter(Boolean).map(key));
  return valid.filter((p) => kept.has(key(p)));
}

module.exports = { remember, existing, forget, MAX };

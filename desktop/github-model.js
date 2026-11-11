// Signing in to GitHub for Get from VCS without Electron: with the GitHub CLI when it is installed (its own sign-in in the browser, the
// one-time code it prints shown here), else with a personal access token the person pastes (kept encrypted by Windows, Electron's
// safeStorage); then the person's repositories (their own, their collaborations and their organizations'), newest first, to clone one.

/** The GitHub CLI's program: PYMAGIC_GH (tests), else gh on the PATH. */
const ghProgram = (env = process.env) => env.PYMAGIC_GH || 'gh';

/** How gh is started with these arguments: { program, args }; a PYMAGIC_GH that is a .js file (a test's stand-in) is run by Node. */
function ghCommand(args, env = process.env) {
  const program = ghProgram(env);
  return /\.m?js$/i.test(program) ? { program: env.PYMAGIC_NODE || 'node', args: [program, ...args] } : { program, args };
}

/** The account `gh auth status` says is signed in to github.com, or null ("Logged in to github.com account NAME" or the older "as NAME"). */
function ghAccountOf(statusText) {
  const m = /Logged in to github\.com (?:account|as) ([A-Za-z0-9-]+)/.exec(String(statusText ?? ''));
  return m ? m[1] : null;
}

/** What `gh auth login --web` prints for the person: its one-time code and the page to type it in, once both are known. */
function deviceCodeOf(text) {
  const code = /one-time code:\s*([A-Z0-9]{4}-[A-Z0-9]{4})/i.exec(String(text ?? ''));
  const url = /(https:\/\/github\.com\/login\/device)/.exec(String(text ?? ''));
  return code ? { code: code[1].toUpperCase(), url: url ? url[1] : 'https://github.com/login/device' } : null;
}

/** Whether gh is waiting for Enter to open the browser (it opens it itself then). */
const ghWantsEnter = (text) => /Press Enter to open/i.test(String(text ?? ''));

/** A token as GitHub writes them (classic ghp_, fine-grained github_pat_, OAuth gho_ …), with nothing around it. */
function tokenOk(token) {
  return /^(gh[pousr]_[A-Za-z0-9]{20,255}|github_pat_[A-Za-z0-9_]{20,255})$/.test(String(token ?? '').trim());
}

/** GitHub's repositories (its API's /user/repos, one page or several) as the welcome screen lists them, the latest pushed first. */
function reposOf(pages) {
  const all = [].concat(...(Array.isArray(pages?.[0]) ? pages : [pages ?? []]));
  const seen = new Set();
  const out = [];
  for (const r of all) {
    if (!r || typeof r.full_name !== 'string' || typeof r.clone_url !== 'string' || seen.has(r.full_name)) continue;
    seen.add(r.full_name);
    out.push({
      name: r.full_name, url: r.clone_url, description: r.description ?? '', private: !!r.private, fork: !!r.fork, archived: !!r.archived,
      pushed: r.pushed_at ?? r.updated_at ?? '', owner: r.owner?.login ?? r.full_name.split('/')[0],
    });
  }
  return out.sort((a, b) => String(b.pushed).localeCompare(String(a.pushed)));
}

/** The repositories that match a filter (owner/name or description, any case); all of them for none. */
function filterRepos(repos, q) {
  const words = String(q ?? '').trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (!words.length) return repos;
  return repos.filter((r) => words.every((w) => r.name.toLowerCase().includes(w) || r.description.toLowerCase().includes(w)));
}

module.exports = { ghProgram, ghCommand, ghAccountOf, deviceCodeOf, ghWantsEnter, tokenOk, reposOf, filterRepos };

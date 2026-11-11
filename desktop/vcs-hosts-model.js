// Get from VCS's other hosts than github.com, without Electron: GitHub Enterprise Server, GitLab (gitlab.com or a server of its own) and
// Bitbucket Cloud. Each is signed in to with a token the person pastes (checked by the host's API, kept encrypted by Windows through
// Electron's safeStorage); then the person's repositories, the latest changed first, as github-model.js's reposOf gives GitHub's.

const KINDS = {
  ghe: { label: 'GitHub Enterprise', server: '', tokenHelp: 'A personal access token of your GitHub Enterprise server (Settings › Developer settings › Personal access tokens), with read access to your repositories.' },
  gitlab: { label: 'GitLab', server: 'https://gitlab.com', tokenHelp: 'A personal access token of GitLab (Preferences › Access tokens) with the read_api and read_repository scopes.' },
  bitbucket: { label: 'Bitbucket', server: 'https://bitbucket.org', tokenHelp: 'An API token of Bitbucket (your Atlassian account › Security › API tokens, with the read:repository and read:user scopes) and your account\'s email.' },
};

/**
 * The server's address as kept: https only (http for this computer itself, a test's stand-in), no path but a GitLab server's own, no slash
 * at the end. Throws with what is wrong.
 */
function serverOf(kind, text) {
  if (!KINDS[kind]) throw new Error('No such host: ' + kind);
  if (kind === 'bitbucket') return KINDS.bitbucket.server;
  let s = String(text ?? '').trim() || KINDS[kind].server;
  if (!s) throw new Error('The server’s address is needed (https://github.example.com).');
  if (!/^[a-z]+:\/\//i.test(s)) s = 'https://' + s;
  let u;
  try { u = new URL(s); } catch { throw new Error('That is not an address: ' + text); }
  const local = /^(localhost|127\.0\.0\.1|\[::1\])$/.test(u.hostname);
  if (u.protocol !== 'https:' && !(u.protocol === 'http:' && local)) throw new Error('The server is reached over https only.');
  if (u.username || u.password || u.search || u.hash) throw new Error('The address is the server’s alone (no name, password or query in it).');
  // a GitHub Enterprise address pasted with its API's path is the server too
  const path = u.pathname.replace(/\/+$/, '').replace(/\/api\/v[34]$/, '');
  if (kind === 'ghe' && u.hostname.toLowerCase() === 'github.com') throw new Error('github.com is the GitHub host itself (choose GitHub).');
  return u.origin + path;
}

/** The host's API under its server (env PYMAGIC_BITBUCKET_API for a test's stand-in of Bitbucket's). */
function apiBaseOf(kind, server, env = process.env) {
  if (kind === 'ghe') return server + '/api/v3';
  if (kind === 'gitlab') return server + '/api/v4';
  return env.PYMAGIC_BITBUCKET_API || 'https://api.bitbucket.org/2.0';
}

/** What a request of the API carries for the token (Bitbucket's: the account's email and the token, as Basic). */
function headersOf(kind, token, user) {
  const h = { 'User-Agent': 'PyMagic', Accept: 'application/json' };
  if (kind === 'ghe') { h.Authorization = 'Bearer ' + token; h.Accept = 'application/vnd.github+json'; }
  else if (kind === 'gitlab') h['PRIVATE-TOKEN'] = token;
  else h.Authorization = 'Basic ' + Buffer.from(String(user) + ':' + token).toString('base64');
  return h;
}

/** Whether the sign-in's fields are there and of the right shape: an error to show, or null. */
function signInProblem(kind, { token, user } = {}) {
  const t = String(token ?? '').trim();
  if (!t) return 'The token is needed.';
  if (/\s/.test(t) || t.length < 10 || t.length > 2000) return 'That is not a token (it has no spaces, and is longer).';
  if (kind === 'bitbucket' && !/^[^\s@]+@[^\s@]+$/.test(String(user ?? '').trim()) && !/^[A-Za-z0-9_-]{1,100}$/.test(String(user ?? '').trim()))
    return 'Bitbucket needs your account’s email with the API token.';
  return null;
}

/** The account the API's /user names. */
function loginOf(kind, body) {
  const name = kind === 'ghe' ? body?.login : kind === 'gitlab' ? body?.username : body?.username ?? body?.nickname ?? body?.display_name;
  if (typeof name !== 'string' || !name) throw new Error('The server did not say whose token it is.');
  return name;
}

/** The API path of the repositories' page `page` (1 on). */
function reposPathOf(kind, page) {
  if (kind === 'ghe') return '/user/repos?per_page=100&sort=pushed&affiliation=owner,collaborator,organization_member&page=' + page;
  if (kind === 'gitlab') return '/projects?membership=true&order_by=last_activity_at&sort=desc&per_page=100&page=' + page;
  return '/repositories?role=member&sort=-updated_on&pagelen=100&page=' + page;
}

/** Whether there is a page after this one. */
function morePages(kind, body) {
  if (kind === 'bitbucket') return typeof body?.next === 'string' && body.next.length > 0;
  return Array.isArray(body) && body.length >= 100;
}

/** A host's repositories (its API's pages) as the welcome screen lists them, the latest changed first, one entry each. */
function hostReposOf(kind, pages) {
  const items = [].concat(...(pages ?? []).map((p) => (kind === 'bitbucket' ? p?.values ?? [] : Array.isArray(p) ? p : [])));
  const seen = new Set();
  const out = [];
  for (const r of items) {
    let e = null;
    if (kind === 'ghe' && typeof r?.full_name === 'string' && typeof r.clone_url === 'string')
      e = { name: r.full_name, url: r.clone_url, description: r.description ?? '', private: !!r.private, fork: !!r.fork, archived: !!r.archived, pushed: r.pushed_at ?? r.updated_at ?? '' };
    else if (kind === 'gitlab' && typeof r?.path_with_namespace === 'string' && typeof r.http_url_to_repo === 'string')
      e = { name: r.path_with_namespace, url: r.http_url_to_repo, description: r.description ?? '', private: r.visibility !== 'public', fork: !!r.forked_from_project, archived: !!r.archived, pushed: r.last_activity_at ?? '' };
    else if (kind === 'bitbucket' && typeof r?.full_name === 'string') {
      const https = (r.links?.clone ?? []).find((c) => c?.name === 'https')?.href;
      // Bitbucket writes the account into the address (https://name@bitbucket.org/…): left out, git asks its credential helper
      if (typeof https === 'string') e = { name: r.full_name, url: https.replace(/^https:\/\/[^@/]+@/, 'https://'), description: r.description ?? '', private: !!r.is_private, fork: !!r.parent, archived: false, pushed: r.updated_on ?? '' };
    }
    if (!e || seen.has(e.name)) continue;
    seen.add(e.name);
    e.owner = e.name.split('/')[0];
    out.push(e);
  }
  return out.sort((a, b) => String(b.pushed).localeCompare(String(a.pushed)));
}

/** The key an account is kept under (one account a host). */
const accountKey = (kind, server) => kind + ' ' + server;

/** The accounts as the page sees them: no token. */
function accountsShown(accounts) {
  return Object.entries(accounts ?? {}).map(([key, a]) => ({ key, kind: a.kind, label: KINDS[a.kind]?.label ?? a.kind, server: a.server, user: a.login }))
    .sort((a, b) => a.key.localeCompare(b.key));
}

module.exports = { KINDS, serverOf, apiBaseOf, headersOf, signInProblem, loginOf, reposPathOf, morePages, hostReposOf, accountKey, accountsShown };

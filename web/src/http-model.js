// The HTTP client's logic that needs no page: the requests of an .http / .rest file (JetBrains' format), their
// {{variables}}, and cURL commands read and written.

const METHODS = ['GET', 'POST', 'PUT', 'DELETE', 'PATCH', 'HEAD', 'OPTIONS', 'TRACE', 'CONNECT'];
const REQUEST_LINE = new RegExp(`^(?:(${METHODS.join('|')})\\s+)?(\\S+?)(?:\\s+(HTTP\\/[\\d.]+))?\\s*$`);
const comment = (line) => /^\s*(#|\/\/)/.test(line);

/**
 * The requests of a file, in order: {line (1-based, of the request line), name, method, url, headers: [[name, value]],
 * body (text or null), bodyFile (the path after `<`, or null), pre (the script of `< {% ... %}` before the request line, or null),
 * handlers ([{script} | {file}]: `> {% ... %}` and `> handler.js` after the body), output ({file, overwrite}: `>> file`, `>>! file`; or null)}.
 * Requests are separated by `###` lines (the rest of the line names the request, as `# @name x` does); comments (`#`, `//`) and
 * `@name = value` lines are not requests; a URL goes on over indented lines that start with `?` or `&`; headers end at the first empty line,
 * the body runs to the next request or to its response handler.
 */
export function parseRequests(text) {
    const lines = text.split(/\r?\n/);
    const requests = [];
    let i = 0;
    // a script between {% and %}, from line k (its first line starts with `<` or `>`): [script, the line after it]
    const scriptAt = (block, k) => {
        const first = block[k].replace(/^[<>]\s*\{%/, '');
        if (first.includes('%}'))
            return [first.slice(0, first.indexOf('%}')), k + 1];
        const parts = [first];
        let m = k + 1;
        for (; m < block.length && !block[m].includes('%}'); m++)
            parts.push(block[m]);
        if (m < block.length)
            parts.push(block[m].slice(0, block[m].indexOf('%}')));
        return [parts.join('\n'), m + 1];
    };
    while (i < lines.length) {
        // one block: up to the next ### line
        let name = null;
        if (/^###/.test(lines[i])) {
            name = lines[i].replace(/^###\s*/, '').trim() || null;
            i++;
        }
        const start = i;
        while (i < lines.length && !/^###/.test(lines[i]))
            i++;
        const block = lines.slice(start, i);
        let j = 0;
        let pre = null;
        // comments, variables, a pre-request script and blank lines before the request line
        while (j < block.length && (!block[j].trim() || comment(block[j]) || /^@\w+\s*=/.test(block[j]) || /^<\s*\{%/.test(block[j]))) {
            if (/^<\s*\{%/.test(block[j])) {
                const [script, next] = scriptAt(block, j);
                pre = (pre ? pre + '\n' : '') + script;
                j = next;
                continue;
            }
            const named = /^\s*(?:#|\/\/)\s*@name\s+(\S+)/.exec(block[j]);
            if (named)
                name = named[1];
            j++;
        }
        if (j >= block.length)
            continue;
        const m = REQUEST_LINE.exec(block[j].trim());
        if (!m)
            continue;
        const request = { line: start + j + 1, name, method: m[1] ?? 'GET', url: m[2], headers: [], body: null, bodyFile: null, pre, handlers: [], output: null };
        j++;
        while (j < block.length && /^\s+[?&]/.test(block[j]))
            request.url += block[j++].trim();
        for (; j < block.length && block[j].trim() && !/^[<>]/.test(block[j]); j++) {
            if (comment(block[j]))
                continue;
            const h = /^([^:\s]+)\s*:\s*(.*)$/.exec(block[j]);
            if (h)
                request.headers.push([h[1], h[2].trim()]);
        }
        const body = block.slice(j < block.length && !block[j].trim() ? j + 1 : j);
        // response handlers and redirects of the response to a file end the body
        const handler = body.findIndex((l) => /^>\s*(\{%|\S)|^>>!?\s/.test(l));
        const kept = (handler >= 0 ? body.slice(0, handler) : body).join('\n').replace(/\s+$/, '');
        if (/^<\s+\S/.test(kept) && !kept.includes('\n'))
            request.bodyFile = kept.replace(/^<\s+/, '').trim();
        else if (kept.trim())
            request.body = kept;
        for (let k = handler; handler >= 0 && k < body.length;) {
            const l = body[k];
            const out = /^>>(!?)\s+(\S.*?)\s*$/.exec(l);
            if (out) {
                request.output = { file: out[2], overwrite: out[1] === '!' };
                k++;
            }
            else if (/^>\s*\{%/.test(l)) {
                const [script, next] = scriptAt(body, k);
                request.handlers.push({ script });
                k = next;
            }
            else if (/^>\s*\S/.test(l)) {
                request.handlers.push({ file: l.replace(/^>\s*/, '').trim() });
                k++;
            }
            else
                k++;
        }
        requests.push(request);
    }
    return requests;
}

/**
 * The parts of a body as they are sent: text, and the files its `< path` lines name (sent as their bytes, as in a multipart body's file
 * part), `<@ path` for a file whose {{variables}} are filled in first. A multipart body (its Content-Type says so) has its lines ended by
 * CRLF, as the format asks. [{text}] | [{file, vars}].
 */
export function bodyParts(body, contentType) {
    if (body === null || body === undefined)
        return [];
    const multipart = /multipart\//i.test(contentType ?? '');
    const eol = multipart ? '\r\n' : '\n';
    const parts = [];
    let text = [];
    const flush = (last) => {
        if (!text.length)
            return;
        parts.push({ text: text.join(eol) + (last ? '' : eol) });
        text = [];
    };
    const lines = String(body).split(/\r?\n/);
    lines.forEach((line, n) => {
        const file = /^<(@?)\s+(\S.*?)\s*$/.exec(line);
        if (file) {
            flush(false);
            parts.push({ file: file[2], vars: file[1] === '@' });
            if (n < lines.length - 1)
                parts.push({ text: eol });
        }
        else
            text.push(line);
    });
    flush(true);
    // a multipart body ends with its closing boundary's line end
    if (multipart && parts.length && parts[parts.length - 1].text !== undefined && !parts[parts.length - 1].text.endsWith('\r\n'))
        parts[parts.length - 1].text += '\r\n';
    return parts.filter((part) => part.file !== undefined || part.text !== '');
}

/** The request whose block holds a line (1-based): the last one starting at or before it. */
export function requestAt(requests, line) {
    let found = null;
    for (const r of requests)
        if (r.line <= line)
            found = r;
    return found ?? requests[0] ?? null;
}

/** The `@name = value` variables of a file (the later wins). */
export function fileVariables(text) {
    const vars = {};
    for (const line of text.split(/\r?\n/)) {
        const m = /^@(\w+)\s*=\s*(.*?)\s*$/.exec(line);
        if (m)
            vars[m[1]] = m[2];
    }
    return vars;
}

/**
 * The variables of an environment: http-client.env.json's, then http-client.private.env.json's over them (secrets kept
 * out of version control), each `{ "dev": { "host": "..." }, "prod": {...} }`; the names of its environments too.
 */
export function environment(publicEnv, privateEnv, name) {
    const names = [...new Set([...Object.keys(publicEnv ?? {}), ...Object.keys(privateEnv ?? {})])].filter((n) => n !== '$shared');
    const pick = (env) => ({ ...(env?.$shared ?? {}), ...(name ? env?.[name] ?? {} : {}) });
    return { names, vars: { ...pick(publicEnv), ...pick(privateEnv) } };
}

/** The name the editor of environments gives `$shared` (the variables of every environment). */
export const SHARED_LABEL = 'Every environment';

/**
 * The two environment files as rows for the editor in the HTTP tab: `{ env, name, value, secret }`, env being an environment's name or '$shared';
 * a variable of the private file is `secret` (when both files have it, the private value is the one used, so it is the one shown). Also the names of
 * the environments, '$shared' first when it has variables.
 */
export function environmentRows(publicEnv, privateEnv) {
    const rows = [];
    const at = new Map();
    const add = (file, secret) => {
        for (const [env, vars] of Object.entries(file ?? {})) {
            if (!vars || typeof vars !== 'object' || Array.isArray(vars)) continue;
            for (const [name, value] of Object.entries(vars)) {
                const row = { env, name, value: value === null || value === undefined ? '' : String(value), secret };
                const key = env + '\u0000' + name;
                if (at.has(key)) rows[at.get(key)] = row;
                else { at.set(key, rows.length); rows.push(row); }
            }
        }
    };
    add(publicEnv, false);
    add(privateEnv, true);
    const envs = [...new Set([...Object.keys(publicEnv ?? {}), ...Object.keys(privateEnv ?? {}), ...rows.map((r) => r.env)])];
    envs.sort((a, b) => (a === '$shared' ? -1 : b === '$shared' ? 1 : 0));
    return { envs, rows };
}

/**
 * The rows back as the two files: `{ public, private }`, each an object of environments (an environment with no variables is kept in the public file,
 * so it can be chosen), or null when that file has nothing. Rows without a name are left out; a name used twice in one environment keeps the last value.
 */
export function environmentFiles(envs, rows) {
    const pub = {}, priv = {};
    for (const env of envs) if (env !== '$shared') pub[env] = {};
    for (const r of rows) {
        const name = String(r.name ?? '').trim();
        const env = String(r.env ?? '').trim();
        if (!name || !env) continue;
        const into = r.secret ? priv : pub;
        (into[env] ??= {})[name] = String(r.value ?? '');
        // the other file must not keep an older value of it
        delete (r.secret ? pub : priv)[env]?.[name];
    }
    for (const file of [pub, priv]) for (const env of Object.keys(file)) if (file === priv && !Object.keys(file[env]).length) delete file[env];
    if (pub.$shared && !Object.keys(pub.$shared).length) delete pub.$shared;
    return { public: Object.keys(pub).length ? pub : null, private: Object.keys(priv).length ? priv : null };
}

let counter = 0;
/** JetBrains' dynamic variables: a new value each time a request is sent. */
function dynamic(name) {
    switch (name) {
        case '$uuid':
        case '$random.uuid':
            return globalThis.crypto?.randomUUID?.() ?? `00000000-0000-4000-8000-${String(++counter).padStart(12, '0')}`;
        case '$timestamp':
            return String(Math.floor(Date.now() / 1000));
        case '$isoTimestamp':
            return new Date().toISOString();
        case '$randomInt':
            return String(Math.floor(Math.random() * 1000));
        default:
            return undefined;
    }
}

/** `{{name}}` replaced by its value; the names without one are listed and left as they are. */
export function substitute(text, vars) {
    const missing = [];
    const out = (text ?? '').replace(/\{\{\s*([$\w.-]+)\s*\}\}/g, (all, name) => {
        const v = Object.prototype.hasOwnProperty.call(vars, name) ? vars[name] : dynamic(name);
        if (v === undefined) {
            missing.push(name);
            return all;
        }
        return typeof v === 'string' ? v : JSON.stringify(v);
    });
    return { text: out, missing };
}

/** A request with every {{variable}} filled in (file variables over the environment's), and the names nothing gave. */
export function resolve(request, vars) {
    const missing = new Set();
    const sub = (t) => { const r = substitute(t, vars); r.missing.forEach((m) => missing.add(m)); return r.text; };
    return {
        request: { ...request, url: sub(request.url), headers: request.headers.map(([k, v]) => [k, sub(v)]), body: request.body === null ? null : sub(request.body), bodyFile: request.bodyFile && sub(request.bodyFile) },
        missing: [...missing],
    };
}

/** The words of a shell command line, as POSIX shells and cmd.exe (`^` at a line's end) split them. */
function shellWords(command) {
    const text = command.replace(/\\\r?\n/g, ' ').replace(/\^\r?\n/g, ' ');
    const words = [];
    let i = 0;
    while (i < text.length) {
        while (i < text.length && /\s/.test(text[i]))
            i++;
        if (i >= text.length)
            break;
        let word = '';
        while (i < text.length && !/\s/.test(text[i])) {
            const c = text[i];
            if (c === "'") {
                const end = text.indexOf("'", i + 1);
                word += text.slice(i + 1, end < 0 ? text.length : end);
                i = end < 0 ? text.length : end + 1;
            }
            else if (c === '"') {
                i++;
                while (i < text.length && text[i] !== '"') {
                    if (text[i] === '\\' && /["\\$`]/.test(text[i + 1] ?? ''))
                        i++;
                    word += text[i++];
                }
                i++;
            }
            else if (c === '$' && text[i + 1] === "'") {
                // $'...' with \n and \' inside
                let j = i + 2;
                while (j < text.length && text[j] !== "'") {
                    if (text[j] === '\\' && j + 1 < text.length) {
                        const n = text[j + 1];
                        word += n === 'n' ? '\n' : n === 't' ? '\t' : n;
                        j += 2;
                    }
                    else
                        word += text[j++];
                }
                i = j + 1;
            }
            else if (c === '\\' && i + 1 < text.length) {
                word += text[i + 1];
                i += 2;
            }
            else
                word += text[i++];
        }
        words.push(word);
    }
    return words;
}

/** A cURL command as a request (method, url, headers, body), or null when it is not one. */
export function parseCurl(command) {
    const words = shellWords(command.trim());
    if (!/^curl(\.exe)?$/i.test(words[0] ?? ''))
        return null;
    const request = { method: null, url: null, headers: [], body: null, bodyFile: null };
    const data = [];
    for (let i = 1; i < words.length; i++) {
        const w = words[i];
        const next = () => words[++i] ?? '';
        if (w === '-X' || w === '--request')
            request.method = next().toUpperCase();
        else if (w === '-H' || w === '--header') {
            const h = /^([^:]+):\s*(.*)$/.exec(next());
            if (h)
                request.headers.push([h[1].trim(), h[2]]);
        }
        else if (['-d', '--data', '--data-raw', '--data-binary', '--data-ascii'].includes(w)) {
            const d = next();
            if (d.startsWith('@') && w !== '--data-raw')
                request.bodyFile = d.slice(1);
            else
                data.push(d);
        }
        else if (w === '--json') {
            data.push(next());
            if (!request.headers.some(([k]) => k.toLowerCase() === 'content-type'))
                request.headers.push(['Content-Type', 'application/json']);
            if (!request.headers.some(([k]) => k.toLowerCase() === 'accept'))
                request.headers.push(['Accept', 'application/json']);
        }
        else if (w === '-u' || w === '--user')
            request.headers.push(['Authorization', 'Basic ' + btoa(next())]);
        else if (w === '-A' || w === '--user-agent')
            request.headers.push(['User-Agent', next()]);
        else if (w === '-e' || w === '--referer')
            request.headers.push(['Referer', next()]);
        else if (w === '-b' || w === '--cookie')
            request.headers.push(['Cookie', next()]);
        else if (w === '--url')
            request.url = next();
        else if (w === '-I' || w === '--head')
            request.method = 'HEAD';
        else if (/^-(o|w|m|-max-time|-connect-timeout|-output|-write-out|-proxy|x|-retry|-cacert|E|-cert|-key)$/.test(w))
            next();   // options with a value that do not change the request
        else if (!w.startsWith('-') && request.url === null)
            request.url = w;
    }
    if (data.length) {
        request.body = data.join('&');
        if (!request.headers.some(([k]) => k.toLowerCase() === 'content-type'))
            request.headers.push(['Content-Type', 'application/x-www-form-urlencoded']);
    }
    request.method ??= data.length || request.bodyFile ? 'POST' : 'GET';
    return request.url ? request : null;
}

/** A request as the lines of an .http file. */
export function formatRequest(request) {
    const lines = [`${request.method} ${request.url}`, ...request.headers.map(([k, v]) => `${k}: ${v}`)];
    if (request.bodyFile)
        lines.push('', `< ${request.bodyFile}`);
    else if (request.body !== null && request.body !== undefined && request.body !== '')
        lines.push('', request.body);
    return lines.join('\n') + '\n';
}

/** A request as a cURL command that a terminal can run (POSIX quoting), one option per line. */
export function toCurl(request) {
    const q = (s) => "'" + String(s).replace(/'/g, "'\\''") + "'";
    const parts = [`curl -X ${request.method} ${q(request.url)}`, ...request.headers.map(([k, v]) => `-H ${q(`${k}: ${v}`)}`)];
    if (request.bodyFile)
        parts.push(`--data-binary ${q('@' + request.bodyFile)}`);
    else if (request.body)
        parts.push(`--data-raw ${q(request.body)}`);
    return parts.join(' \\\n  ');
}

/** A response body shown readably: JSON indented, anything else as it came. */
export function prettyBody(body, contentType) {
    if (/json/i.test(contentType ?? '') || /^\s*[[{]/.test(body ?? '')) {
        try {
            return JSON.stringify(JSON.parse(body), null, 2);
        }
        catch { /* not JSON after all */ }
    }
    return body ?? '';
}

/**
 * Runs a response handler or pre-request script as PyCharm's HTTP client does, with its `client` and `response` (and `request`) objects:
 * client.global.set/get/isEmpty/clear/clearAll (variables kept for the next requests, used as {{name}}), client.test(name, fn),
 * client.assert(condition, message), client.log(...), client.exit(); response.body (parsed when it is JSON), response.headers.valueOf/valuesOf,
 * response.status, response.contentType {mimeType, charset}; request.variables.set/get (for this request, before it is sent),
 * request.environment.get, request.method, request.url; jsonPath(value, "$.path") (see jsonPath below). `input` is {script, response?, request?, globals, environment, variables}.
 * Answers {globals, variables, tests: [{name, passed, error?}], log: [text], error?}. Self-contained: the page runs it in a Worker
 * (its source is this function's text), away from the page and without network.
 */
export function runScript(input) {
    // (inside runScript, which the page runs in a Worker from its own text)
    /**
     * jsonPath(value, path) as the response handlers of PyCharm's HTTP client have it: `$` the value, `.name` or `['name']` a member, `[2]` an
     * item (`[-1]` from the end), `[*]` / `.*` every member or item, `..name` a member at any depth, `[1:3]` a slice, `[0,2]` / `['a','b']` several.
     * A path that picks one place (no wildcard, recursion, slice or list) gives its value, null when it is not there; any other gives the list
     * of the values found. A string that is JSON (a body that was not parsed) is read first. Throws on a path it cannot read.
     */
    function jsonPath(value, path) {
        if (typeof value === 'string') { try { value = JSON.parse(value); } catch { /* a string is a value too */ } }
        const p = String(path ?? '').trim();
        if (!p.startsWith('$')) throw new Error(`a JSON path starts with $: ${p}`);
        let at = 1, many = false;
        const steps = [];
        const name = /^[A-Za-z_$][\w$-]*/;
        const closing = (text, open) => {
            let quote = '';
            for (let i = open + 1; i < text.length; i++) {
                const c = text[i];
                if (quote) { if (c === '\\') i++; else if (c === quote) quote = ''; }
                else if (c === "'" || c === '"') quote = c;
                else if (c === ']') return i;
            }
            throw new Error(`a [ without its ] in ${text}`);
        };
        const split = (text) => {
            const out = [];
            let quote = '', from = 0;
            for (let i = 0; i < text.length; i++) {
                const c = text[i];
                if (quote) { if (c === '\\') i++; else if (c === quote) quote = ''; }
                else if (c === "'" || c === '"') quote = c;
                else if (c === ',') { out.push(text.slice(from, i)); from = i + 1; }
            }
            out.push(text.slice(from));
            return out;
        };
        while (at < p.length) {
            if (p.startsWith('..', at)) {
                at += 2;
                many = true;
                if (p[at] === '*') { steps.push({ kind: 'deep', key: null }); at++; continue; }
                if (p[at] === '[') { steps.push({ kind: 'deep', key: null, then: true }); continue; }
                const m = name.exec(p.slice(at));
                if (!m) throw new Error(`a name after .. in ${p}`);
                steps.push({ kind: 'deep', key: m[0] });
                at += m[0].length;
            }
            else if (p[at] === '.') {
                at++;
                if (p[at] === '*') { steps.push({ kind: 'all' }); many = true; at++; continue; }
                const m = name.exec(p.slice(at));
                if (!m) throw new Error(`a name after . in ${p}`);
                steps.push({ kind: 'keys', keys: [m[0]] });
                at += m[0].length;
            }
            else if (p[at] === '[') {
                const end = closing(p, at);
                const inner = p.slice(at + 1, end).trim();
                at = end + 1;
                if (inner === '*') { steps.push({ kind: 'all' }); many = true; continue; }
                const slice = /^(-?\d*)\s*:\s*(-?\d*)$/.exec(inner);
                if (slice) { steps.push({ kind: 'slice', from: slice[1], to: slice[2] }); many = true; continue; }
                const parts = split(inner).map((s) => s.trim());
                if (parts.length > 1) many = true;
                steps.push({
                    kind: 'keys', keys: parts.map((s) => {
                        if (/^-?\d+$/.test(s)) return Number(s);
                        const q = /^(['"])(.*)\1$/s.exec(s);
                        if (q) return q[2].replace(/\\(['"\\])/g, '$1');
                        throw new Error(`cannot read [${inner}] in ${p}`);
                    }),
                });
            }
            else throw new Error(`cannot read ${p.slice(at)} in ${p}`);
        }
        const isObj = (v) => v !== null && typeof v === 'object';
        const children = (v) => (Array.isArray(v) ? v : isObj(v) ? Object.values(v) : []);
        const descend = (v, out) => { out.push(v); for (const c of children(v)) descend(c, out); return out; };
        let found = [value];
        for (const s of steps) {
            const next = [];
            for (const v of found) {
                if (s.kind === 'all') next.push(...children(v));
                else if (s.kind === 'keys') {
                    for (const k of s.keys) {
                        if (Array.isArray(v) && typeof k === 'number') { const i = k < 0 ? v.length + k : k; if (i >= 0 && i < v.length) next.push(v[i]); }
                        else if (isObj(v) && !Array.isArray(v) && Object.prototype.hasOwnProperty.call(v, String(k))) next.push(v[String(k)]);
                    }
                }
                else if (s.kind === 'slice') {
                    if (!Array.isArray(v)) continue;
                    const n = v.length, f = s.from === '' ? 0 : Number(s.from), t = s.to === '' ? n : Number(s.to);
                    next.push(...v.slice(f < 0 ? Math.max(0, n + f) : f, t < 0 ? n + t : t));
                }
                else {
                    for (const d of descend(v, [])) {
                        if (s.key === null) { if (!s.then) next.push(...children(d)); else next.push(d); }
                        else if (isObj(d) && !Array.isArray(d) && Object.prototype.hasOwnProperty.call(d, s.key)) next.push(d[s.key]);
                    }
                }
            }
            found = next;
        }
        return many ? found : found.length ? found[0] : null;
    }
    if (input.jsonPathOnly) return jsonPath(input.value, input.path);   // for the tests of jsonPath by itself

    const globals = { ...(input.globals ?? {}) };
    const variables = { ...(input.variables ?? {}) };
    const environment = input.environment ?? {};
    const tests = [];
    const log = [];
    const text = (v) => (typeof v === 'string' ? v : (() => { try { return JSON.stringify(v); } catch { return String(v); } })());
    const EXIT = {};
    class AssertionFailed extends Error { }
    let current = null;
    const client = {
        global: {
            set: (name, value) => { globals[String(name)] = value === undefined || value === null ? '' : typeof value === 'string' ? value : text(value); },
            get: (name) => (Object.prototype.hasOwnProperty.call(globals, name) ? globals[name] : null),
            isEmpty: () => Object.keys(globals).length === 0,
            clear: (name) => { delete globals[name]; },
            clearAll: () => { for (const k of Object.keys(globals)) delete globals[k]; },
        },
        test(name, fn) {
            const test = { name: String(name), passed: true };
            tests.push(test);
            current = test;
            try { fn(); }
            catch (e) {
                if (e === EXIT) { current = null; throw e; }
                test.passed = false;
                test.error = e instanceof AssertionFailed ? e.message : `${e?.name ?? 'Error'}: ${e?.message ?? e}`;
            }
            current = null;
        },
        assert(condition, message) {
            if (condition)
                return;
            const why = message === undefined ? 'Assertion failed' : String(message);
            if (current)
                throw new AssertionFailed(why);
            tests.push({ name: why, passed: false, error: why });
        },
        log: (...args) => { log.push(args.map(text).join(' ')); },
        exit: () => { throw EXIT; },
    };
    const r = input.response;
    let response;
    if (r) {
        const headers = Object.entries(r.headers ?? {});
        const type = (headers.find(([k]) => k.toLowerCase() === 'content-type')?.[1] ?? '').split(';');
        const charset = (type.slice(1).find((p) => /charset=/i.test(p)) ?? '').replace(/.*charset=/i, '').trim();
        let body = r.body ?? '';
        if (/json/i.test(type[0])) {
            try { body = JSON.parse(body); } catch { /* the text as it came */ }
        }
        const values = (name) => headers.filter(([k]) => k.toLowerCase() === String(name).toLowerCase()).flatMap(([, v]) => String(v).split(/,\s*/));
        response = {
            status: r.status, body,
            headers: { valueOf: (name) => headers.find(([k]) => k.toLowerCase() === String(name).toLowerCase())?.[1] ?? null, valuesOf: (name) => values(name) },
            contentType: { mimeType: type[0].trim(), charset: charset || 'UTF-8' },
        };
    }
    const q = input.request ?? {};
    const request = {
        method: q.method, url: q.url, body: q.body ?? null,
        headers: { findByName: (name) => (q.headers ?? []).find(([k]) => k.toLowerCase() === String(name).toLowerCase())?.[1] ?? null, all: () => (q.headers ?? []).map(([name, value]) => ({ name, value })) },
        variables: { set: (name, value) => { variables[String(name)] = typeof value === 'string' ? value : text(value); }, get: (name) => variables[name] ?? null },
        environment: { get: (name) => (Object.prototype.hasOwnProperty.call(environment, name) ? environment[name] : null) },
    };
    let error;
    try {
        new Function('client', 'response', 'request', 'jsonPath', `"use strict";\n${input.script}`)(client, response, request, jsonPath);
    }
    catch (e) {
        if (e !== EXIT)
            error = `${e?.name ?? 'Error'}: ${e?.message ?? e}`;
    }
    return { globals, variables, tests, log, ...(error ? { error } : {}) };
}

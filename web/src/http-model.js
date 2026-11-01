// The HTTP client's logic that needs no page: the requests of an .http / .rest file (JetBrains' format), their
// {{variables}}, and cURL commands read and written.

const METHODS = ['GET', 'POST', 'PUT', 'DELETE', 'PATCH', 'HEAD', 'OPTIONS', 'TRACE', 'CONNECT'];
const REQUEST_LINE = new RegExp(`^(?:(${METHODS.join('|')})\\s+)?(\\S+?)(?:\\s+(HTTP\\/[\\d.]+))?\\s*$`);
const comment = (line) => /^\s*(#|\/\/)/.test(line);

/**
 * The requests of a file, in order: {line (1-based, of the request line), name, method, url, headers: [[name, value]],
 * body (text or null), bodyFile (the path after `<`, or null)}. Requests are separated by `###` lines (the rest of the
 * line names the request, as `# @name x` does); comments (`#`, `//`) and `@name = value` lines are not requests; a URL
 * goes on over indented lines that start with `?` or `&`; headers end at the first empty line, the body runs to the
 * next request (response handlers `> {% ... %}` and `>> file` left out).
 */
export function parseRequests(text) {
    const lines = text.split(/\r?\n/);
    const requests = [];
    let i = 0;
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
        // comments, variables and blank lines before the request line
        while (j < block.length && (!block[j].trim() || comment(block[j]) || /^@\w+\s*=/.test(block[j]))) {
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
        const request = { line: start + j + 1, name, method: m[1] ?? 'GET', url: m[2], headers: [], body: null, bodyFile: null };
        j++;
        while (j < block.length && /^\s+[?&]/.test(block[j]))
            request.url += block[j++].trim();
        for (; j < block.length && block[j].trim(); j++) {
            if (comment(block[j]))
                continue;
            const h = /^([^:\s]+)\s*:\s*(.*)$/.exec(block[j]);
            if (h)
                request.headers.push([h[1], h[2].trim()]);
        }
        const body = block.slice(j + 1);
        // response handlers and redirects of the response to a file end the body
        const handler = body.findIndex((l) => /^>\s*(\{%|\S)|^>>!?\s/.test(l));
        const kept = (handler >= 0 ? body.slice(0, handler) : body).join('\n').replace(/\s+$/, '');
        if (/^<\s+\S/.test(kept) && !kept.includes('\n'))
            request.bodyFile = kept.replace(/^<\s+/, '').trim();
        else if (kept.trim())
            request.body = kept;
        requests.push(request);
    }
    return requests;
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

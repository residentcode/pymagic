// The HTTP client: requests written in .http and .rest files (JetBrains' format), each with ▶ Run above it (or Ctrl+Enter),
// {{variables}} from the environments (edited with Environments… in the HTTP tab, kept in http-client.env.json / http-client.private.env.json), the response in the HTTP tab with a history,
// a pasted cURL command turned into a request, and a request copied as cURL. The engine sends them (/api/http). A body's `< file` lines are sent
// as the files' bytes (multipart bodies too), `>> file` keeps the response in a file, and the scripts of `< {% %}` (before the request) and
// `> {% %}` (on its response) run in a Worker, with PyCharm's `client`, `request` and `response` objects; their tests show in the Tests part.
// PyCharm's other kinds of request too: GRAPHQL (the query and its variables POSTed as JSON), WEBSOCKET (messages with `===` between them,
// `=== wait-for-server`; the talk shown as the body, /api/ws) and GRPC (host:port/package.Service/Method with the message as JSON; the
// service described by the server's reflection, /api/grpc).
import * as monaco from 'monaco-editor';
import { showTab } from './panel';
import { toast } from './toast';
import { parseRequests, requestAt, fileVariables, environment, resolve, substitute, parseCurl, formatRequest, toCurl, prettyBody, environmentRows, environmentFiles, SHARED_LABEL, bodyParts, runScript, KINDS, graphqlPayload, websocketMessages, websocketTranscript, grpcTarget } from './http-model';

const $ = (id) => document.getElementById(id);
const HISTORY = '.pymagic/http-history.json';
const KEEP = 50;

monaco.languages.register({ id: 'http', extensions: ['.http', '.rest'], aliases: ['HTTP request'] });
monaco.languages.setMonarchTokensProvider('http', {
    tokenizer: {
        root: [
            // a pre-request or response handler script is JavaScript, up to %}
            [/^[<>]\s*\{%/, { token: 'delimiter', next: '@script', nextEmbedded: 'javascript' }],
            [/^>>!?\s.*$/, 'string'],
            [/^>\s+\S.*$/, 'string'],
            [/^###.*$/, 'comment.doc'],
            [/^\s*(#|\/\/).*$/, 'comment'],
            [/^@\w+/, 'variable', '@assign'],
            [/^(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS|TRACE|CONNECT|GRAPHQL|WEBSOCKET|GRPC)(?=\s)/, 'keyword', '@url'],
            [/^===(\s*wait-for-server)?\s*$/, 'keyword'],
            [/^[\w-]+(?=\s*:)/, 'type.identifier'],
            [/\{\{[^}]*\}\}/, 'variable'],
            [/^<\s.*$/, 'string'],
            [/[{}[\],:]/, 'delimiter'],
            [/"([^"\\]|\\.)*"/, 'string'],
            [/-?\d+(\.\d+)?/, 'number'],
        ],
        script: [[/%\}/, { token: 'delimiter', next: '@pop', nextEmbedded: '@pop' }]],
        url: [[/\{\{[^}]*\}\}/, 'variable'], [/HTTP\/[\d.]+/, 'keyword'], [/\S/, 'string'], [/$/, '', '@pop']],
        assign: [[/\{\{[^}]*\}\}/, 'variable'], [/[^{]+$/, 'string', '@pop'], [/$/, '', '@pop']],
    },
});

export function setupHttpClient(deps) {
    const envBox = $('http-env');
    const list = $('http-history');
    const head = $('http-status');
    const bodyView = $('http-body');
    let history = [];
    let shown = null;      // the history entry shown
    let part = 'body';     // body | headers | request
    const ENV_KEY = 'pymagic.http.env:' + deps.root;

    const readText = (rel) => fetch('/api/file?path=' + encodeURIComponent(rel)).then((r) => (r.ok ? r.text() : null)).catch(() => null);
    const readJson = async (rel) => { const t = await readText(rel); try { return t ? JSON.parse(t) : null; } catch { toast(`${rel} is not valid JSON`); return null; } };
    const folderOf = (rel) => (rel.includes('/') ? rel.slice(0, rel.lastIndexOf('/') + 1) : '');

    /** The environment files beside the .http file, else at the project's root (the first folder that has one); `dir` is that folder, or the file's own when there is none. */
    async function environmentsFor(rel) {
        for (const dir of [...new Set([folderOf(rel), ''])]) {
            const pub = await readJson(dir + 'http-client.env.json');
            const priv = await readJson(dir + 'http-client.private.env.json');
            if (pub || priv)
                return { pub, priv, dir, found: true };
        }
        return { pub: null, priv: null, dir: folderOf(rel), found: false };
    }
    let lastRel = '';      // the .http file the environments were last read for
    async function refreshEnvironments(rel) {
        lastRel = rel;
        const { pub, priv } = await environmentsFor(rel);
        const { names } = environment(pub, priv, null);
        let saved = '';
        try { saved = localStorage.getItem(ENV_KEY) ?? ''; } catch { /* storage unavailable */ }
        envBox.replaceChildren(new Option('No environment', ''), ...names.map((n) => new Option(n, n)));
        envBox.value = names.includes(envBox.value) ? envBox.value : names.includes(saved) ? saved : names[0] ?? '';
        return { pub, priv };
    }
    envBox.onchange = () => { try { localStorage.setItem(ENV_KEY, envBox.value); } catch { /* storage unavailable */ } };

    // ------------------------------------------------------------ the environments, edited here (the two files are written for the person)

    const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; };

    async function editEnvironments() {
        const model = deps.editor?.getModel();
        const open = model && deps.relOf(model);
        const rel = open && /\.(http|rest)$/i.test(open) ? open : lastRel;
        const { pub, priv, dir } = await environmentsFor(rel || '');
        let { envs, rows } = environmentRows(pub, priv);
        if (!envs.length) envs = ['dev'];

        const shade = el('div', 'ask-shade');
        const box = el('div', 'ask-box http-envs');
        const where = dir ? `${dir}http-client.env.json` : 'http-client.env.json at the root of the project';
        box.append(el('div', 'ask-title', 'HTTP environments'),
            el('div', 'ask-hint', `{{name}} in a request takes the value of the chosen environment; "${SHARED_LABEL}" is used by all of them. Private values (tokens, passwords) are kept apart, in http-client.private.env.json, which is not meant to be committed. Kept in ${where}.`));
        const table = el('div', 'http-env-table');
        const error = el('div', 'db-form-error');
        const addVar = el('button', '', 'Add variable');
        const addEnv = el('button', '', 'Add environment…');
        const save = el('button', 'primary', 'Save');
        save.id = 'http-env-save';
        const cancel = el('button', '', 'Cancel');
        const buttons = el('div', 'ask-buttons');
        buttons.append(addVar, addEnv, el('span', 'spacer'), save, cancel);
        box.append(table, error, buttons);
        shade.append(box);
        document.body.append(shade);
        const done = () => shade.remove();
        cancel.onclick = done;
        box.onkeydown = (e) => { if (e.key === 'Escape') done(); e.stopPropagation(); };

        const label = (env) => (env === '$shared' ? SHARED_LABEL : env);
        function paint() {
            table.replaceChildren();
            const head = el('div', 'http-env-row head');
            head.append(el('span', '', 'Environment'), el('span', '', 'Variable'), el('span', '', 'Value'), el('span', '', 'Private'), el('span', ''));
            table.append(head);
            if (!rows.length) table.append(el('div', 'http-env-none', 'No variables yet: Add variable makes one.'));
            rows.forEach((r, i) => {
                const line = el('div', 'http-env-row');
                const env = el('select');
                for (const e of [...new Set(['$shared', ...envs])]) env.add(new Option(label(e), e));
                env.value = r.env;
                env.onchange = () => { r.env = env.value; };
                const name = el('input');
                name.value = r.name;
                name.placeholder = 'host';
                name.oninput = () => { r.name = name.value; };
                const value = el('input');
                value.value = r.value;
                value.type = r.secret ? 'password' : 'text';
                value.oninput = () => { r.value = value.value; };
                const secret = el('input');
                secret.type = 'checkbox';
                secret.checked = r.secret;
                secret.title = 'Kept in http-client.private.env.json';
                secret.onchange = () => { r.secret = secret.checked; value.type = r.secret ? 'password' : 'text'; };
                const remove = el('button', 'mini', '✕');
                remove.title = 'Remove the variable';
                remove.onclick = () => { rows.splice(i, 1); paint(); };
                line.append(env, name, value, secret, remove);
                table.append(line);
            });
        }
        addVar.onclick = () => {
            rows.push({ env: envBox.value && envs.includes(envBox.value) ? envBox.value : envs.find((e) => e !== '$shared') ?? '$shared', name: '', value: '', secret: false });
            paint();
            table.querySelector('.http-env-row:last-child input')?.focus();
        };
        addEnv.onclick = async () => {
            const name = (await deps.askText('New environment', 'staging', 'A name such as dev, staging or prod'))?.trim();
            if (!name) return;
            if (name.startsWith('$')) { error.textContent = 'A name of an environment cannot start with $.'; return; }
            if (!envs.includes(name)) envs.push(name);
            rows.push({ env: name, name: '', value: '', secret: false });
            paint();
        };
        save.onclick = async () => {
            error.textContent = '';
            const files = environmentFiles(envs, rows);
            const write = async (file, data, existed) => {
                // a file that had something and has nothing now is emptied rather than left with old values
                if (!data && !existed) return true;
                const res = await fetch('/api/file?path=' + encodeURIComponent(dir + file), { method: 'PUT', body: JSON.stringify(data ?? {}, null, 2) + '\n' });
                return res.ok;
            };
            const ok = await write('http-client.env.json', files.public, !!pub) && await write('http-client.private.env.json', files.private, !!priv);
            if (!ok) { error.textContent = 'The environments could not be written.'; return; }
            done();
            await refreshEnvironments(rel || '');
            toast('HTTP environments saved');
        };
        paint();
    }
    $('http-env-edit').onclick = () => void editEnvironments();

    // ------------------------------------------------------------ the scripts: in a Worker, away from the page, without network
    // client.global's variables: kept for the next requests, and for the next start of PyMagic too (.pymagic/http-globals.json, out of version
    // control: they often hold a token a login request gave)
    const globals = {};
    void fetch('/api/http-globals').then((r) => (r.ok ? r.json() : null)).catch(() => null).then((kept) => {
        if (kept && typeof kept === 'object' && !Array.isArray(kept))
            for (const [k, v] of Object.entries(kept)) if (typeof v === 'string' && !(k in globals)) globals[k] = v;
    });
    let globalsTimer = 0;
    function keepGlobals() {
        clearTimeout(globalsTimer);
        globalsTimer = setTimeout(() => {
            void fetch('/api/http-globals', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(globals) }).catch(() => { /* kept next time */ });
        }, 300);
    }
    let sandboxUrl = null;
    function sandboxed(input, ms = 5000) {
        sandboxUrl ??= URL.createObjectURL(new Blob([
            `const runScript = ${runScript.toString()};\n`
            + "for (const k of ['fetch', 'XMLHttpRequest', 'WebSocket', 'EventSource', 'importScripts', 'indexedDB', 'caches', 'BroadcastChannel', 'Worker', 'SharedWorker'])\n"
            + '    try { Object.defineProperty(self, k, { value: undefined, writable: false, configurable: false }); } catch { /* not there */ }\n'
            + 'onmessage = (e) => postMessage(runScript(e.data));\n'], { type: 'text/javascript' }));
        return new Promise((done) => {
            const failed = (error) => ({ globals: input.globals, variables: input.variables, tests: [], log: [], error });
            let worker;
            try { worker = new Worker(sandboxUrl); }
            catch (e) { done(failed(`The script could not be started: ${e}`)); return; }
            const timer = setTimeout(() => { worker.terminate(); done(failed(`The script ran for more than ${ms / 1000} seconds and was stopped.`)); }, ms);
            worker.onmessage = (e) => { clearTimeout(timer); worker.terminate(); done(e.data); };
            worker.onerror = (e) => { e.preventDefault?.(); clearTimeout(timer); worker.terminate(); done(failed(e.message || 'The script failed.')); };
            worker.postMessage(input);
        });
    }
    // a file a request names (`< body.json`, `> handler.js`, `>> out.json`): from the .http file's folder, or from the project's root with a leading /
    const pathFrom = (rel, path) => { const p = path.replace(/^\.\//, ''); return p.startsWith('/') ? p.slice(1) : folderOf(rel) + p; };

    // ------------------------------------------------------------ sending
    async function send(model, line) {
        const rel = deps.relOf(model);
        if (!rel)
            return;
        const text = model.getValue();
        const request = requestAt(parseRequests(text), line);
        if (!request) {
            toast('No request here: write one as `GET https://example.com`.');
            return;
        }
        await sendRequest(rel, text, request, undefined, true);
    }
    // ------------------------------------------------------------ a WebSocket or a streaming gRPC call kept open (LiveSockets.cs, GrpcLive.cs)
    const liveBar = $('http-live'), liveText = $('http-live-text');
    let liveWs = null;
    /**
     * Opens a live connection and shows its talk as it goes; resolves when it has closed, with the answer as the one-shot request's.
     * `mode`: 'socket' (a WebSocket, messages typed), 'subscription' (GraphQL's, Stop), 'grpc' (a gRPC call: typed JSON messages for a
     * client-streaming or bidirectional method, Close ends what is sent; a unary method's answer comes once).
     */
    function liveSocket(start, label, mode) {
        liveWs?.close();
        const subscription = mode === 'subscription', grpc = mode === 'grpc';
        return new Promise((done) => {
            const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
            const ws = new WebSocket(`${scheme}://${location.host}/api/${grpc ? 'grpc' : 'ws'}/live`);
            liveWs = ws;
            const transcript = [];
            let headers = {}, opened = false, closed = null, ended = false, ms = 0, call = null;
            const paint = () => { bodyView.textContent = websocketTranscript(transcript, closed); bodyView.scrollTop = bodyView.scrollHeight; };
            const finish = (answer) => {
                if (ended)
                    return;
                ended = true;
                if (liveWs === ws)
                    liveWs = null;
                liveBar.hidden = true;
                done(answer);
            };
            ws.onopen = () => ws.send(JSON.stringify(start));
            ws.onmessage = (e) => {
                const m = JSON.parse(e.data);
                ms = m.ms ?? ms;
                if (m.type === 'error') {
                    finish({ error: m.error });
                    return;
                }
                if (m.type === 'result') {
                    finish({ ...m, size: (m.body ?? '').length });
                    return;
                }
                if (m.type === 'open' && grpc) {
                    opened = true;
                    const typing = m.streaming !== 'server';
                    head.className = 'ok';
                    head.textContent = `gRPC · open (${m.streaming === 'bidi' ? 'bidirectional' : m.streaming + ' streaming'}) · ${label}`;
                    liveBar.hidden = false;
                    liveText.hidden = !typing;
                    liveText.placeholder = `A message of ${m.input} as JSON: Enter sends it`;
                    $('http-live-send').hidden = !typing;
                    $('http-live-close').textContent = typing ? 'Close' : 'Stop';
                    $('http-live-close').title = typing ? 'End what is sent: the server then finishes the call' : 'Cancel the call';
                    if (typing)
                        liveText.focus();
                }
                else if (m.type === 'open') {
                    opened = true;
                    liveText.placeholder = 'A message to the server: Enter sends it';
                    $('http-live-close').title = 'Close the connection (a subscription is stopped first)';
                    headers = m.headers ?? {};
                    head.className = 'ok';
                    head.textContent = `101 Switching Protocols · open${m.protocol ? ` (${m.protocol})` : ''} · ${label}`;
                    liveBar.hidden = false;
                    liveText.hidden = subscription;
                    $('http-live-send').hidden = subscription;
                    $('http-live-close').textContent = subscription ? 'Stop' : 'Close';
                    if (!subscription)
                        liveText.focus();
                }
                else if (m.type === 'sent' || m.type === 'received' || m.type === 'note') {
                    transcript.push({ dir: m.type, text: m.text, ms: m.ms });
                    paint();
                }
                else if (m.type === 'closed' && grpc) {
                    call = m;
                    headers = m.headers ?? {};
                    closed = { status: `${m.status} ${m.reason}`, description: m.message ?? '' };
                    paint();
                }
                else if (m.type === 'closed') {
                    closed = m.byServer ? { status: m.status, description: m.description } : null;
                    paint();
                }
            };
            ws.onclose = () => finish(grpc && opened
                ? (call ? { status: call.status, reason: call.reason, message: call.message, headers, ms, body: websocketTranscript(transcript, closed), transcript, size: transcript.reduce((n, t) => n + t.text.length, 0) }
                    : { error: 'the call ended without a status' })
                : opened
                ? { status: 101, reason: 'Switching Protocols', headers, ms, body: websocketTranscript(transcript, closed), transcript, size: transcript.reduce((n, t) => n + t.text.length, 0) }
                : { error: 'the connection to PyMagic closed' });
        });
    }
    function sendLive() {
        if (!liveWs || !liveText.value)
            return;
        liveWs.send(JSON.stringify({ type: 'send', text: liveText.value }));
        liveText.value = '';
    }
    $('http-live-send').onclick = sendLive;
    liveText.onkeydown = (e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendLive(); } };
    $('http-live-close').onclick = () => liveWs?.readyState === WebSocket.OPEN && liveWs.send(JSON.stringify({ type: 'close' }));
    /** The requests of a file kept as a run configuration: the one named (or on that line), or all of them in turn; `env` the environment to use. */
    async function runFile(rel, which, env) {
        const text = await readText(rel);
        if (text === null) {
            toast(`There is no file ${rel} in the project.`);
            return;
        }
        const requests = parseRequests(text);
        const chosen = !which ? requests : requests.filter((r) => r.name === which || `${r.method} ${r.url}` === which || String(r.line) === String(which));
        if (!chosen.length) {
            toast(`${rel} has no request ${which}.`);
            return;
        }
        for (const r of chosen)
            if (!(await sendRequest(rel, text, r, env)))
                break;
    }
    /** Sends one request of a file; false when it could not be sent (a variable without a value, a file that is not there). */
    async function sendRequest(rel, text, request, env, live = false) {
        const { pub, priv } = await refreshEnvironments(rel);
        if (env !== undefined && [...envBox.options].some((o) => o.value === env))
            envBox.value = env;
        const envVars = environment(pub, priv, envBox.value || null).vars;
        let vars = { ...envVars, ...globals, ...fileVariables(text) };
        // the pre-request script: request.variables.set(...) gives this request's variables
        let preResult = null;
        if (request.pre) {
            preResult = await sandboxed({ script: request.pre, request: { method: request.method, url: request.url, headers: request.headers, body: request.body }, globals, environment: envVars, variables: {} });
            Object.assign(globals, preResult.globals);
            for (const k of Object.keys(globals)) if (!(k in preResult.globals)) delete globals[k];
            keepGlobals();
            vars = { ...envVars, ...globals, ...fileVariables(text), ...preResult.variables };
        }
        const { request: r, missing } = resolve(request, vars);
        if (missing.length) {
            toast(`No value for ${missing.map((m) => `{{${m}}}`).join(', ')}` + (envBox.value ? ` in the environment ${envBox.value}: Environments… in the HTTP tab adds it` : ': choose an environment in the HTTP tab, or add it with Environments…'));
            showTab('httpclient');
            return false;
        }
        const type = r.headers.find(([k]) => k.toLowerCase() === 'content-type')?.[1];
        // the body's parts: its text, the files of its `< path` lines as their bytes, `<@ path` files with their {{variables}} filled in
        let parts = r.bodyFile ? [{ file: r.bodyFile, vars: false }] : bodyParts(r.body, type);
        for (const [n, part] of parts.entries()) {
            if (part.file === undefined)
                continue;
            const path = pathFrom(rel, part.file);
            if (part.vars) {
                const content = await readText(path);
                if (content === null) {
                    toast(`The file ${part.file} of the body is not in the project.`);
                    return false;
                }
                parts[n] = { text: substitute(content, vars).text };
            }
            else
                parts[n] = { file: path };
        }
        parts = parts.length ? parts : null;
        const shownBody = r.bodyFile ? `< ${r.bodyFile}` : r.body;
        showTab('httpclient');
        head.className = '';
        head.textContent = `${r.method} ${r.url} …`;
        bodyView.textContent = '';
        const saveTo = r.output ? { file: pathFrom(rel, substitute(r.output.file, vars).text), overwrite: r.output.overwrite } : undefined;
        const post = (path, payload) => fetch(path, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(payload) })
            .then(async (res) => (res.ok ? res.json() : { error: (await res.json().catch(() => null))?.error ?? `HTTP ${res.status}` })).catch((e) => ({ error: String(e) }));
        const kind = KINDS[r.method] ?? 'http';
        let answer;
        // a GraphQL subscription (a ws:// address, or a subscription operation) goes over a WebSocket kept open, its events shown as they come
        const subscription = kind === 'graphql' && (/^wss?:\/\//i.test(r.url) || /^\s*subscription\b/.test(graphqlPayload(r.body).query));
        if (subscription)
            answer = await liveSocket({ url: r.url, headers: Object.fromEntries(r.headers), graphql: graphqlPayload(r.body), fromFile: true }, `${r.method} ${r.url}`, 'subscription');
        // a WebSocket request sent from the editor stays open: what is typed below the status goes to the server until Close
        else if (kind === 'websocket' && live)
            answer = await liveSocket({ url: r.url, headers: Object.fromEntries(r.headers), messages: websocketMessages(r.body), fromFile: true }, `${r.method} ${r.url}`, 'socket');
        else if (kind === 'graphql') {
            // POSTed as JSON, {query, variables}, as PyCharm sends it; the request's own Content-Type stays when it has one
            const headers = Object.fromEntries(r.headers);
            if (!Object.keys(headers).some((k) => k.toLowerCase() === 'content-type'))
                headers['Content-Type'] = 'application/json';
            answer = await post('/api/http', { method: 'POST', url: r.url, headers, parts: [{ text: JSON.stringify(graphqlPayload(r.body)) }], saveTo, fromFile: true });
        }
        else if (kind === 'websocket') {
            const ws = await post('/api/ws', { url: r.url, headers: Object.fromEntries(r.headers), messages: websocketMessages(r.body), fromFile: true });
            answer = ws.error ? ws : { status: 101, reason: 'Switching Protocols', headers: ws.headers ?? {}, ms: ws.ms, body: websocketTranscript(ws.transcript, ws.closed), transcript: ws.transcript, size: (ws.transcript ?? []).reduce((n, t) => n + t.text.length, 0) };
        }
        // a gRPC request sent from the editor: a streaming call stays open, what is typed below the status sent as it is typed
        else if (kind === 'grpc' && live && grpcTarget(r.url))
            answer = await liveSocket({ ...grpcTarget(r.url), metadata: Object.fromEntries(r.headers), body: r.body ?? '', fromFile: true }, `${r.method} ${r.url}`, 'grpc');
        else if (kind === 'grpc') {
            const target = grpcTarget(r.url);
            answer = !target ? { error: 'a gRPC address: host:port/package.Service/Method' }
                : await post('/api/grpc', { ...target, metadata: Object.fromEntries(r.headers), body: r.body ?? '{}', fromFile: true });
            if (!answer.error)
                answer = { ...answer, size: (answer.body ?? '').length };
        }
        else
            answer = await post('/api/http', { method: r.method, url: r.url, headers: Object.fromEntries(r.headers), parts, saveTo, fromFile: true });
        // the response handlers, in order: `> {% script %}` and `> handler.js`
        const tests = [...(preResult?.tests ?? [])];
        const log = [...(preResult?.log ?? [])];
        const scriptErrors = preResult?.error ? [`Pre-request script: ${preResult.error}`] : [];
        if (!answer.error)
            for (const h of r.handlers ?? []) {
                const script = h.script ?? await readText(pathFrom(rel, h.file));
                if (script === null) {
                    scriptErrors.push(`The response handler ${h.file} is not in the project.`);
                    continue;
                }
                const out = await sandboxed({
                    script, globals, environment: envVars, variables: preResult?.variables ?? {},
                    request: { method: r.method, url: r.url, headers: r.headers, body: shownBody ?? null },
                    response: { status: answer.status, headers: answer.headers, body: answer.body ?? '' },
                });
                for (const k of Object.keys(globals)) if (!(k in out.globals)) delete globals[k];
                Object.assign(globals, out.globals);
                keepGlobals();
                tests.push(...out.tests);
                log.push(...out.log);
                if (out.error)
                    scriptErrors.push(`Response handler: ${out.error}`);
            }
        const entry = {
            when: new Date().toISOString(), file: rel, line: request.line, name: request.name, method: r.method, url: r.url,
            request: { headers: r.headers, body: shownBody ?? null },
            ...(kind !== 'http' ? { kind } : {}),
            ...(answer.error ? { error: answer.error } : { status: answer.status, reason: answer.reason, ms: answer.ms, size: answer.size, headers: answer.headers, body: (answer.body ?? '').slice(0, 64 * 1024), truncated: answer.truncated || (answer.body ?? '').length > 64 * 1024 }),
            ...(tests.length || log.length || scriptErrors.length ? { tests, log, scriptErrors } : {}),
            ...(answer.savedTo ? { savedTo: answer.savedTo } : {}),
        };
        history = [entry, ...history].slice(0, KEEP);
        void fetch('/api/file?path=' + encodeURIComponent(HISTORY), { method: 'PUT', body: JSON.stringify(history) }).catch(() => { /* kept for this session */ });
        if (entry.tests && (scriptErrors.length || tests.some((x) => !x.passed)))
            part = 'tests';
        show(entry);
        paintHistory();
        if (answer.savedTo)
            toast(`Response saved to ${answer.savedTo}`);
        return !answer.error;
    }
    window.addEventListener('pymagic:runHttp', (e) => void runFile(e.detail.file, e.detail.request, e.detail.environment));

    // ------------------------------------------------------------ the HTTP tab
    const sizeText = (n) => (n >= 1024 * 1024 ? `${(n / 1048576).toFixed(1)} MB` : n >= 1024 ? `${(n / 1024).toFixed(1)} KB` : `${n} B`);
    // a gRPC answer's status is gRPC's code (0 OK, 5 NOT_FOUND, ...), an HTTP one's 4xx and 5xx are failures
    const failed = (entry) => (entry.kind === 'grpc' ? entry.status !== 0 : entry.status >= 400);
    function show(entry) {
        shown = entry;
        head.className = entry.error ? 'err' : failed(entry) ? 'bad' : 'ok';
        const passed = (entry.tests ?? []).filter((x) => x.passed).length;
        const testText = entry.tests?.length || entry.scriptErrors?.length
            ? ` · tests ${passed}/${entry.tests.length}${entry.scriptErrors?.length ? ', script failed' : ''}` : '';
        head.textContent = entry.error ? `${entry.method} ${entry.url}: ${entry.error}`
            : `${entry.kind === 'grpc' ? 'gRPC ' : ''}${entry.status} ${entry.reason ?? ''} · ${entry.ms} ms · ${sizeText(entry.size ?? 0)}${testText} · ${entry.method} ${entry.url}`;
        if (!entry.error && (entry.scriptErrors?.length || entry.tests?.some((x) => !x.passed)))
            head.className = 'bad';
        paintPart();
        document.querySelectorAll('#http-history .http-item').forEach((el, i) => el.classList.toggle('sel', history[i] === entry));
    }
    function paintPart() {
        document.querySelectorAll('#http-parts button').forEach((b) => b.classList.toggle('active', b.dataset.part === part));
        if (!shown || shown.error) {
            bodyView.textContent = '';
            return;
        }
        const type = Object.entries(shown.headers ?? {}).find(([k]) => k.toLowerCase() === 'content-type')?.[1];
        bodyView.textContent = part === 'tests' ? testsText(shown)
            : part === 'headers' ? Object.entries(shown.headers ?? {}).map(([k, v]) => `${k}: ${v}`).join('\n')
            : part === 'request' ? formatRequest({ method: shown.method, url: shown.url, headers: shown.request?.headers ?? [], body: shown.request?.body ?? null })
                : prettyBody(shown.body, type) + (shown.truncated ? '\n… (cut: only the start of the body is kept)' : '');
    }
    document.querySelectorAll('#http-parts button').forEach((b) => { b.onclick = () => { part = b.dataset.part; paintPart(); }; });
    /** What the scripts of a request said: each test passed or failed (with why), the script errors, client.log's lines, and where the response was saved. */
    function testsText(entry) {
        const lines = [];
        for (const x of entry.tests ?? [])
            lines.push(x.passed ? `✓ ${x.name}` : `✗ ${x.name}${x.error && x.error !== x.name ? `: ${x.error}` : ''}`);
        for (const e of entry.scriptErrors ?? [])
            lines.push(`✗ ${e}`);
        if (entry.log?.length)
            lines.push('', 'Log:', ...entry.log);
        if (entry.savedTo)
            lines.push('', `The response was saved to ${entry.savedTo}`);
        return lines.length ? lines.join('\n') : 'No response handler: write one after the request, as\n> {%\n    client.test("Status 200", () => client.assert(response.status === 200));\n%}';
    }
    function paintHistory() {
        list.textContent = '';
        for (const entry of history) {
            const row = document.createElement('div');
            row.className = 'http-item' + (entry === shown ? ' sel' : '');
            const status = document.createElement('span');
            status.className = 'http-code ' + (entry.error ? 'err' : failed(entry) ? 'bad' : 'ok');
            status.textContent = entry.error ? '✕' : String(entry.status);
            const what = document.createElement('span');
            what.className = 'http-what';
            what.textContent = `${entry.method} ${entry.url.replace(/^https?:\/\//, '')}`;
            const when = document.createElement('span');
            when.className = 'http-when';
            when.textContent = new Date(entry.when).toLocaleTimeString();
            row.append(status, what, when);
            row.title = `${entry.file}:${entry.line}${entry.name ? ` (${entry.name})` : ''} · double-click to open the request`;
            row.onclick = () => show(entry);
            row.ondblclick = () => void deps.openFile(entry.file, new monaco.Range(entry.line, 1, entry.line, 1));
            list.appendChild(row);
        }
        if (!history.length)
            list.innerHTML = '<div class="http-empty">No request sent yet. Open or make an .http file and click ▶ Run above a request.</div>';
    }
    $('http-copy').onclick = () => {
        if (shown && !shown.error)
            void navigator.clipboard.writeText(shown.body ?? '').then(() => toast('Body copied'), () => toast('Could not copy'));
    };
    $('http-clear').onclick = () => {
        history = [];
        shown = null;
        void fetch('/api/file?path=' + encodeURIComponent(HISTORY), { method: 'PUT', body: '[]' });
        head.textContent = '';
        paintPart();
        paintHistory();
    };
    $('http-new').onclick = async () => {
        const name = await deps.askText('New HTTP request file', 'requests.http', 'A file of the project; ▶ Run above each request sends it');
        if (!name?.trim())
            return;
        const rel = name.trim().endsWith('.http') || name.trim().endsWith('.rest') ? name.trim() : name.trim() + '.http';
        if ((await readText(rel)) === null)
            await fetch('/api/file?path=' + encodeURIComponent(rel), { method: 'PUT', body: '### An example\nGET https://httpbin.org/get\nAccept: application/json\n' });
        await deps.openFile(rel);
    };
    readJson(HISTORY).then((h) => { history = Array.isArray(h) ? h : []; paintHistory(); });
    $('t-httpclient').onclick = () => { showTab('httpclient'); const m = deps.editor.getModel(); if (m && deps.relOf(m)) void refreshEnvironments(deps.relOf(m)); };

    // ------------------------------------------------------------ in the editor: ▶ Run, Copy as cURL, Ctrl+Enter, pasted cURL
    monaco.editor.registerCommand('pymagic.http.run', (_, uri, line) => {
        const model = monaco.editor.getModel(monaco.Uri.parse(uri));
        if (model)
            void send(model, line);
    });
    monaco.editor.registerCommand('pymagic.http.curl', async (_, uri, line) => {
        const model = monaco.editor.getModel(monaco.Uri.parse(uri));
        const rel = model && deps.relOf(model);
        if (!rel)
            return;
        const request = requestAt(parseRequests(model.getValue()), line);
        const { pub, priv } = await refreshEnvironments(rel);
        const { request: r } = resolve(request, { ...environment(pub, priv, envBox.value || null).vars, ...fileVariables(model.getValue()) });
        void navigator.clipboard.writeText(toCurl(r)).then(() => toast('Copied as cURL'), () => toast('Could not copy'));
    });
    monaco.languages.registerCodeLensProvider('http', {
        provideCodeLenses(model) {
            const lenses = parseRequests(model.getValue()).flatMap((r) => {
                const range = new monaco.Range(r.line, 1, r.line, 1);
                return [
                    { range, command: { id: 'pymagic.http.run', title: '▶ Run', arguments: [model.uri.toString(), r.line] } },
                    { range, command: { id: 'pymagic.http.curl', title: 'Copy as cURL', arguments: [model.uri.toString(), r.line] } },
                ];
            });
            return { lenses, dispose() { } };
        },
    });
    function addHttpActions(ed) {
        ed.addAction({ id: 'pymagic.http.runAtCursor', label: 'Run HTTP request', keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter],
            precondition: 'editorLangId == http', contextMenuGroupId: 'navigation', contextMenuOrder: 0,
            run: () => { const m = ed.getModel(), p = ed.getPosition(); if (m && p) void send(m, p.lineNumber); } });
        // the request at the cursor kept as a run configuration, chosen, to run again with Run (Shift+F10) or in a compound
        ed.addAction({ id: 'pymagic.http.saveRunConfig', label: 'Save HTTP request as run configuration', precondition: 'editorLangId == http',
            contextMenuGroupId: 'navigation', contextMenuOrder: 1,
            run: () => {
                const m = ed.getModel(), p = ed.getPosition();
                const rel = m && deps.relOf(m);
                const r = rel && p ? requestAt(parseRequests(m.getValue()), p.lineNumber) : null;
                if (!r) { toast('No request here.'); return; }
                const request = r.name ?? `${r.method} ${r.url}`;
                window.dispatchEvent(new CustomEvent('pymagic:addRunConfig', { detail: { kind: 'http', name: r.name ?? `${r.method} ${r.url.replace(/^https?:\/\//, '').slice(0, 40)}`, script: rel, request, environment: envBox.value } }));
                toast(`Kept as a run configuration: Run sends ${request}`);
            } });
        // a cURL command pasted into an .http file becomes a request
        ed.onDidPaste((e) => {
            const model = ed.getModel();
            if (!model || model.getLanguageId() !== 'http')
                return;
            const request = parseCurl(model.getValueInRange(e.range));
            if (!request)
                return;
            ed.executeEdits('curl', [{ range: e.range, text: formatRequest(request) }]);
            toast('The cURL command became a request');
        });
    }
    monaco.editor.getEditors().forEach(addHttpActions);
    monaco.editor.onDidCreateEditor(addHttpActions);
    return { send, runFile };
}

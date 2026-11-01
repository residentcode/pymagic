// The HTTP client: requests written in .http and .rest files (JetBrains' format), each with ▶ Run above it (or Ctrl+Enter),
// {{variables}} from http-client.env.json / http-client.private.env.json, the response in the HTTP tab with a history,
// a pasted cURL command turned into a request, and a request copied as cURL. The engine sends them (/api/http).
import * as monaco from 'monaco-editor';
import { showTab } from './panel';
import { toast } from './toast';
import { parseRequests, requestAt, fileVariables, environment, resolve, parseCurl, formatRequest, toCurl, prettyBody } from './http-model';

const $ = (id) => document.getElementById(id);
const HISTORY = '.pymagic/http-history.json';
const KEEP = 50;

monaco.languages.register({ id: 'http', extensions: ['.http', '.rest'], aliases: ['HTTP request'] });
monaco.languages.setMonarchTokensProvider('http', {
    tokenizer: {
        root: [
            [/^###.*$/, 'comment.doc'],
            [/^\s*(#|\/\/).*$/, 'comment'],
            [/^@\w+/, 'variable', '@assign'],
            [/^(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS|TRACE|CONNECT)(?=\s)/, 'keyword', '@url'],
            [/^[\w-]+(?=\s*:)/, 'type.identifier'],
            [/\{\{[^}]*\}\}/, 'variable'],
            [/^<\s.*$/, 'string'],
            [/[{}[\],:]/, 'delimiter'],
            [/"([^"\\]|\\.)*"/, 'string'],
            [/-?\d+(\.\d+)?/, 'number'],
        ],
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

    /** The environment files beside the .http file, else at the project's root (the first folder that has one). */
    async function environmentsFor(rel) {
        for (const dir of [...new Set([folderOf(rel), ''])]) {
            const pub = await readJson(dir + 'http-client.env.json');
            const priv = await readJson(dir + 'http-client.private.env.json');
            if (pub || priv)
                return { pub, priv };
        }
        return { pub: null, priv: null };
    }
    async function refreshEnvironments(rel) {
        const { pub, priv } = await environmentsFor(rel);
        const { names } = environment(pub, priv, null);
        let saved = '';
        try { saved = localStorage.getItem(ENV_KEY) ?? ''; } catch { /* storage unavailable */ }
        envBox.replaceChildren(new Option('No environment', ''), ...names.map((n) => new Option(n, n)));
        envBox.value = names.includes(envBox.value) ? envBox.value : names.includes(saved) ? saved : names[0] ?? '';
        return { pub, priv };
    }
    envBox.onchange = () => { try { localStorage.setItem(ENV_KEY, envBox.value); } catch { /* storage unavailable */ } };

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
        const { pub, priv } = await refreshEnvironments(rel);
        const vars = { ...environment(pub, priv, envBox.value || null).vars, ...fileVariables(text) };
        const { request: r, missing } = resolve(request, vars);
        if (missing.length) {
            toast(`No value for ${missing.map((m) => `{{${m}}}`).join(', ')}` + (envBox.value ? ` in the environment ${envBox.value}` : ': choose an environment in the HTTP tab, or add it to http-client.env.json'));
            showTab('httpclient');
            return;
        }
        let body = r.body;
        if (r.bodyFile) {
            const path = r.bodyFile.replace(/^\.\//, '');
            body = await readText(path.startsWith('/') ? path.slice(1) : folderOf(rel) + path);
            if (body === null) {
                toast(`The body file ${r.bodyFile} is not in the project.`);
                return;
            }
        }
        showTab('httpclient');
        head.className = '';
        head.textContent = `${r.method} ${r.url} …`;
        bodyView.textContent = '';
        const answer = await fetch('/api/http', {
            method: 'POST', headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ method: r.method, url: r.url, headers: Object.fromEntries(r.headers), body, fromFile: true }),
        }).then(async (res) => (res.ok ? res.json() : { error: (await res.json().catch(() => null))?.error ?? `HTTP ${res.status}` })).catch((e) => ({ error: String(e) }));
        const entry = {
            when: new Date().toISOString(), file: rel, line: request.line, name: request.name, method: r.method, url: r.url,
            request: { headers: r.headers, body: body ?? null },
            ...(answer.error ? { error: answer.error } : { status: answer.status, reason: answer.reason, ms: answer.ms, size: answer.size, headers: answer.headers, body: (answer.body ?? '').slice(0, 64 * 1024), truncated: answer.truncated || (answer.body ?? '').length > 64 * 1024 }),
        };
        history = [entry, ...history].slice(0, KEEP);
        void fetch('/api/file?path=' + encodeURIComponent(HISTORY), { method: 'PUT', body: JSON.stringify(history) }).catch(() => { /* kept for this session */ });
        show(entry);
        paintHistory();
    }

    // ------------------------------------------------------------ the HTTP tab
    const sizeText = (n) => (n >= 1024 * 1024 ? `${(n / 1048576).toFixed(1)} MB` : n >= 1024 ? `${(n / 1024).toFixed(1)} KB` : `${n} B`);
    function show(entry) {
        shown = entry;
        head.className = entry.error ? 'err' : entry.status >= 400 ? 'bad' : 'ok';
        head.textContent = entry.error ? `${entry.method} ${entry.url}: ${entry.error}`
            : `${entry.status} ${entry.reason ?? ''} · ${entry.ms} ms · ${sizeText(entry.size ?? 0)} · ${entry.method} ${entry.url}`;
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
        bodyView.textContent = part === 'headers' ? Object.entries(shown.headers ?? {}).map(([k, v]) => `${k}: ${v}`).join('\n')
            : part === 'request' ? formatRequest({ method: shown.method, url: shown.url, headers: shown.request?.headers ?? [], body: shown.request?.body ?? null })
                : prettyBody(shown.body, type) + (shown.truncated ? '\n… (cut: only the start of the body is kept)' : '');
    }
    document.querySelectorAll('#http-parts button').forEach((b) => { b.onclick = () => { part = b.dataset.part; paintPart(); }; });
    function paintHistory() {
        list.textContent = '';
        for (const entry of history) {
            const row = document.createElement('div');
            row.className = 'http-item' + (entry === shown ? ' sel' : '');
            const status = document.createElement('span');
            status.className = 'http-code ' + (entry.error ? 'err' : entry.status >= 400 ? 'bad' : 'ok');
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
    return { send };
}

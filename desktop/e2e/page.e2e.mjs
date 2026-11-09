// The page in a real browser: PyMagic.exe started on a project of its own and driven as a user would, over the DevTools
// protocol (app.mjs). Run after building the exe:
//   node desktop/package.mjs --no-installer
//   node --test desktop/e2e/page.e2e.mjs
// Each check is one a change has broken or could break unseen by the Node tests of the page's pure modules.
import { after, afterEach, before, describe, test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { createServer } from 'node:http';
import { createHash } from 'node:crypto';
import { basename, dirname, join } from 'node:path';
import { launch, project, removeTree, sleep } from './app.mjs';

const FILES = {
    'calc.py': 'import json\n\n\ndef total(prices):\n    """The sum of the prices."""\n    return sum(prices)\n\n\nprint(json.dumps({"total": total([1, 2, 3])}))\nprint(undefined_name)\n',
    'loop.py': 'import time\n\n\ndef spin():\n    count = 0\n    while count < 2000:\n        count += 1\n        time.sleep(0.05)\n\n\nprint("started", flush=True)\nspin()\n',
    'fmt.py': 'x=1\nif x :\n  y=[1,2 ,3]\n',
    'quotes.py': 'url = "/shop/about/"\nname = "pen"\nsaid = "it\'s"\n',
    'queries.sql': "CREATE TABLE note (id INTEGER PRIMARY KEY, body TEXT);\nINSERT INTO note (body) VALUES ('one; two'), ('three');\nSELECT body FROM note ORDER BY id;\n",
    'more_items.csv': 'name,price\nlamp,30\nmug,\n',
    'busy.py': 'def inner(n):\n    return sum(i * i for i in range(n))\n\n\ndef outer():\n    return [inner(3000) for _ in range(40)]\n\n\nouter()\n',
    '.pymagic/schemas/package.json': '{"type":"object","properties":{"name":{"type":"string","pattern":"^[a-z][a-z0-9-]*$"},"scripts":{"type":"object","additionalProperties":{"type":"string"}}}}',
    '.pymagic/schemas/pyproject.json': '{"type":"object","properties":{"project":{"type":"object","properties":{"name":{"type":"string"}}}}}',
    '.pymagic/schemas/workflow.json': '{"type":"object","properties":{"jobs":{"type":"object","additionalProperties":{"type":"object","properties":{"runs-on":{"type":"string"}}}}}}',
    '.github/workflows/ci.yml': 'name: CI\njobs:\n  test:\n    runs-on: 5\n',
    '.pymagic/schemas/compose.json': '{"type":"object","properties":{"services":{"type":"object","additionalProperties":{"type":"object","properties":{"image":{"type":"string"}},"unevaluatedProperties":false}}}}',
    'docker-compose.yml': 'services:\n  web:\n    image: 5\n    extra: 1\n',
    '.pymagic/schemas/gitlab.json': '{"type":"object","properties":{"stages":{"type":"array"}}}',
    '.gitlab-ci.yml': 'stages: build\n',
    '.pymagic/schemas/precommit.json': '{"type":"object","properties":{"fail_fast":{"type":"boolean"}}}',
    '.pre-commit-config.yaml': 'fail_fast: sometimes\n',
    '.pymagic/schemas/mkdocs.json': '{"type":"object","properties":{"site_name":{"type":"string"}}}',
    'mkdocs.yml': 'site_name: 5\n',
    '.pymagic/schemas/tox.json': '{"type":"object","properties":{"env_list":{"type":"array"}}}',
    'tox.toml': 'env_list = "py312"\n',
    '.pymagic/schemas/renovate.json': '{"type":"object","properties":{"labels":{"type":"array"}}}',
    'renovate.json': '{\n  "labels": 5\n}\n',
    '.pymagic/schemas/openapi.json': '{"type":"object","required":["info"],"properties":{"info":{"type":"object","required":["version"]}}}',
    'openapi.yaml': 'openapi: 3.0.3\ninfo:\n  title: t\n',
    'openapi31.yaml': 'openapi: 3.1.0\ninfo:\n  title: t\n',
    '.pymagic/schemas/k8s-service-v1.json': '{"type":"object","properties":{"spec":{"type":["object","null"],"properties":{"ports":{"type":"array","items":{"type":"object","properties":{"port":{"type":"integer"}}}}}}}}',
    // Express's view engines and a script known by its #! line
    'express/views/index.hjs': '<h1>{{ title }}</h1>\n',
    'express/views/index.ejs': '<a href="<%= url %>"><%- title %></a>\n<%# a note %>\n',
    'express/bin/www': '#!/usr/bin/env node\nvar app = require("../app");\n',
    // Emmet: an abbreviation on the first line; the second is a tag with the abbreviation inside it, where nothing is offered
    'emmet/page.html': 'ul>li*2\n<div div.x class="a"></div>\n',
    'emmet/site.css': 'a {\n  m10\n}\n',
    'emmet/page.hjs': 'ul>li*2\n',
    'tags/page.html': '<h1>{{ title }}</h1>\n<p>Welcome to {{ title }}</p>\n',
    'fullline/config.py': 'import json\n\n\ndef load_config(path):\n    with open(path) as f:\n        config = \n    return config\n',
    'tw/input.css': '@import "tailwindcss" source(none);\n@plugin "flowbite/plugin";\n@source "../tw/**/*.html";\n@theme { --color-brand: #3366ff; }\n',
    'node_modules/flowbite/plugin.js': 'module.exports = function () {};\n',
    'tw/page.html': '<div class="md:flex items-center">\n    <div class="ml-au"></div>\n    <p class="bg-bra"></p>\n    <p class="bg-acc"></p>\n</div>\n',
    'tags/App.jsx': 'export const App = () => (\n  <section className="a"><Item key={1} /></section>\n);\n',
    'emmet/App.vue': 'ul>li*2\n',
    'emmet/App.jsx': 'export const App = () => (\n  div.card>h2\n);\n',
    'emmet/server.js': "var express = require('express');\ndiv.card\n",
    // checked against the schemas built into PyMagic (no stand-in kept for these)
    '.github/dependabot.yml': 'version: 3\nupdates: []\n',
    'infra/deploy.yaml': 'apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: web\nspec:\n  replicas: two\n  selector:\n    matchLabels:\n      app: web\n  template:\n    metadata:\n      labels:\n        app: web\n    spec:\n      containers:\n        - name: web\n          image: nginx\n',
    'k8s.yaml': 'apiVersion: v1\nkind: Service\nspec:\n  ports:\n    - port: 80\n---\napiVersion: v1\nkind: Service\nspec:\n  ports:\n    - port: http\n',
    'package.json': '{\n  "name": "Bad Name",\n  "scripts": {\n    "t": 1\n  }\n}\n',
    'pyproject.toml': '[project]\nname = 5\n',
    'broken.toml':'[tool]\nname = "x"\nname = "y"\nflag = True\n',
    'broken.yml': 'a: 1\nb: 2\na: 3\n',
    'notes.md': '# Notes\n\nSee [the guide](docs/nope.md) and [here](#notes).\n\n[g](docs/guide.md#nowhere) [h](docs/guide.md#setup)\n',
    'docs/guide.md': '# Guide\n\n## Setup\n',
    'shop_lib.py': 'def total(prices):\n    return sum(prices)\n',
    'inj.py': 'import re\n\n# language=json\nCONFIG = \'{"a": 1}\'\nQUERY = "SELECT id, name FROM item WHERE price > 1"\nHALF = "SELECT  FROM item"\nPATTERN = re.compile(r"^(\\d+)-[a-z]{2}$")\n',
    'typo.py': '# Recieve the mesage here\n\n\ndef fetch_mesage():\n    return 1\n',
    'demo.md': '# Demo\n\nA [guide](docs/guide.md) and [setup](docs/guide.md#setup) and **bold**.\n\n| Name | Qty |\n|:--|--:|\n| pen | 2 |\n| notebook | 12 |\n\n- [ ] todo\n- [x] done\n\n```mermaid\ngraph TD\n  A[Start] --> B{Ok?}\n  B -->|yes| C[Done]\n```\n\n## Second\n\ntext\n',
    'demo.rst': 'Title\n=====\n\nA *word*.\n',
    'macro.py': 'x = 1\n',
    'which.py': 'import sys\nprint("exe", sys.prefix)\n',
    'gram.md': 'This are a test of the grammar checker.\n\nI has a apple at home today.\n',
    'hist.py': 'def f():\n    return 0\n',
    'intent.py': "name = 'x'\nprint('Hello ' + name + '!')\nvalue = 255\n",
    'rx.py': 'import re\nPATTERN = re.compile(r"[aa]\\-x")\nN = re.compile(f"{PATTERN.pattern}[cc]")\n',
    'api.http':'### the items\nGET {{host}}/items\n    ?page=2\nAccept: application/json\n\n###\nPOST {{host}}/items\nContent-Type: application/json\nAuthorization: Bearer {{token}}\n\n{"name": "pen"}\n',
    'tb_boom.py': 'def explode():\n    raise ValueError("boom")\n\n\nexplode()\n',
    'remote_job.py':'def work(n):\n    total = n * 2\n    return total\n\n\nprint("job", work(21), flush=True)\n',
    'tests/__init__.py': '',
    'tests/test_calc.py': 'import unittest\n\n\nclass Calc(unittest.TestCase):\n    def test_ok(self):\n        self.assertEqual(1 + 1, 2)\n\n    def test_bad(self):\n        self.assertEqual(1 + 1, 3)\n',
};

// a container that is already running, for the last checks (made there when Docker and the python:3.12-slim image are on this PC)
const CONTAINER = 'pymagic-e2e-ctr-' + process.pid;
const hasDocker = spawnSync('docker', ['image', 'inspect', 'python:3.12-slim'], { encoding: 'utf8' }).status === 0;
// a notebook: a title, two code cells, a sub-heading and a cell under it
FILES['nb_demo.ipynb'] = JSON.stringify({
    cells: [
        { cell_type: 'markdown', metadata: {}, source: ['# Intro'] },
        { cell_type: 'code', execution_count: null, metadata: {}, outputs: [], source: ['x = 5\n', 'y = [1, 2]\n', 'z = x * 2'] },
        { cell_type: 'code', execution_count: null, metadata: {}, outputs: [], source: ['print(z)'] },
        { cell_type: 'markdown', metadata: {}, source: ['## Part'] },
        { cell_type: 'code', execution_count: null, metadata: {}, outputs: [], source: ['w = 1'] },
    ],
    metadata: {}, nbformat: 4, nbformat_minor: 5,
}, null, 1) + '\n';

// a second notebook, for the checks of the last block: objects to open, a cell to debug, a text cell
FILES['nb_more.ipynb'] = JSON.stringify({
    cells: [
        { cell_type: 'code', execution_count: null, metadata: {}, outputs: [], source: ['class Point:\n', '    def __init__(self):\n', '        self.x = 3\n', "        self.tags = ['a', 'b']\n", '\n', '\n', 'p = Point()\n', "d = {'one': [1, 2], 'two': 7}"] },
        { cell_type: 'code', execution_count: null, metadata: {}, outputs: [], source: ['a = 4\n', 'b = a * 2\n', 'c = b + 1\n', "print('c =', c)"] },
        { cell_type: 'markdown', metadata: {}, source: ['# Report\n', '\n', 'Some *words* and a formula $x_1^2$.'] },
    ],
    metadata: {}, nbformat: 4, nbformat_minor: 5,
}, null, 1) + '\n';

let app, page, stubSite;
const $ =(id) => `document.getElementById(${JSON.stringify(id)})`;
// the file stays in the editor: on a fresh start PyMagic opens a file of its own, which can land after this one
const open = async (rel) => {
    const shown = `window.pymagic.editor.getModel()?.uri.path.endsWith(${JSON.stringify('/' + rel)})`;
    for (let i = 0; i < 4; i++) {
        await page.evaluate(`window.pymagic.openFile(${JSON.stringify(rel)})`);
        await page.waitFor(shown, 15000, rel);
        await sleep(400);
        if (await page.evaluate(shown))
            return;
    }
    throw new Error(`${rel} did not stay open`);
};
const output = () => page.evaluate(`${$('output')}.textContent`);
/** A click in the editor's margin beside a line, as a breakpoint is set; gives whether the line has one afterwards. */
async function clickMargin(line) {
    const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.revealLine(${line}); const box = ed.getContainerDomNode().getBoundingClientRect();
        return { x: box.left + ed.getLayoutInfo().glyphMarginLeft + 5, y: box.top + ed.getTopForLineNumber(${line}) - ed.getScrollTop() + 8 }; })()`);
    await page.click(at.x, at.y);
    await sleep(200);
    return page.evaluate(`window.pymagic.editor.getModel().getLineDecorations(${line}).some((d) => d.options.glyphMarginClassName?.startsWith('bp-glyph'))`);
}

let echo;
let forge;
let altPython = '';
// the folders this run makes besides its project (scratches, environments, remotes): removed when it ends, so the temp folder does not fill up
const madeDirs = [];
const tempDir = (prefix) => { const dir = mkdtempSync(join(tmpdir(), prefix)); madeDirs.push(dir); return dir; };
let projectRoot = '';
before(async () => {
    // the scratch files of this run, not the user's own
    process.env.PYMAGIC_SCRATCHES = tempDir('pymagic-e2e-scratches-');
    const root = project(FILES);
    // an SQLite file for the Database panel, made with the standard sqlite3 module
    const made = spawnSync('python', ['-c', "import sqlite3,sys; db=sqlite3.connect(sys.argv[1]); db.executescript(\"CREATE TABLE item (id INTEGER PRIMARY KEY, name TEXT, price REAL); INSERT INTO item (name, price) VALUES ('pen', 2), ('book', 12.5), ('cup', 4);\"); db.commit()", join(root, 'shop.sqlite3')]);
    assert.equal(made.status, 0, String(made.stderr));
    // a server for the HTTP client: it answers with what it was sent
    echo = createServer((req, res) => {
        let body = '';
        req.on('data', (c) => { body += c; });
        req.on('end', () => {
            res.writeHead(req.url.startsWith('/missing') ? 404 : 200, { 'Content-Type': 'application/json', 'X-Echo': 'yes' });
            res.end(JSON.stringify({ method: req.method, path: req.url, auth: req.headers.authorization ?? null, body }));
        });
    });
    await new Promise((ok) => echo.listen(0, '127.0.0.1', ok));
    writeFileSync(join(root, 'http-client.env.json'), JSON.stringify({ dev: { host: `http://127.0.0.1:${echo.address().port}` }, prod: { host: 'https://example.invalid' } }));
    writeFileSync(join(root, 'http-client.private.env.json'), JSON.stringify({ dev: { token: 'secret' } }));
    // a second Python (a virtual environment made here) for the run configuration that names an interpreter of its own
    altPython = join(root, '..', 'pymagic-e2e-altenv-' + Date.now(), 'Scripts', 'python.exe');
    madeDirs.push(join(altPython, '..', '..'));
    const venv = spawnSync('python', ['-m', 'venv', join(altPython, '..', '..')], { encoding: 'utf8' });
    assert.equal(venv.status, 0, String(venv.stderr));
    mkdirSync(join(root, '.pymagic'), { recursive: true });
    writeFileSync(join(root, '.pymagic', 'run-configurations.json'), JSON.stringify({ selected: null, configurations: [
        { name: 'which python', kind: 'script', script: 'which.py', interpreter: altPython },
        { name: 'missing python', kind: 'script', script: 'which.py', interpreter: join(root, 'no', 'such', 'python.exe') },
        { name: 'loop a', kind: 'script', script: 'loop.py' },
        { name: 'loop b', kind: 'script', script: 'loop.py' },
        { name: 'loop pair', kind: 'compound', members: 'loop a\nloop b' },
        { name: 'dbg server', kind: 'debugserver', host: '127.0.0.1', port: '0' },
        { name: 'plain which', kind: 'script', script: 'which.py' },
        { name: 'docker boom', kind: 'script', script: 'tb_boom.py', interpreter: 'docker:python:3.12-slim|python' },
        { name: 'container boom', kind: 'script', script: 'tb_boom.py', interpreter: `docker:container=${CONTAINER}|python` },
    ] }));
    projectRoot = root;
    // a GitHub of this run's own (a server on this computer): the engine is told to use it instead of api.github.com, which is never contacted
    forge = await startFakeForge();
    process.env.PYMAGIC_GITHUB_API = forge.base;
    // no Jupyter is installed for the checks: a package of this run's own called jupyter_server (on the app's PYTHONPATH) stands in for it
    stubSite = tempDir('pymagic-e2e-jupyter-');
    mkdirSync(join(stubSite, 'jupyter_server'));
    writeFileSync(join(stubSite, 'jupyter_server', '__init__.py'), '');
    writeFileSync(join(stubSite, 'jupyter_server', '__main__.py'), String.raw`import sys, os, json
from http.server import BaseHTTPRequestHandler, HTTPServer
port = 0
for a in sys.argv[1:]:
    if a.startswith("--port="):
        port = int(a.split("=", 1)[1])
token = os.environ.get("JUPYTER_TOKEN", "")
class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass
    def do_GET(self):
        if self.headers.get("Authorization") != "token " + token:
            self.send_response(403)
            self.end_headers()
            return
        if self.path == "/api/kernelspecs":
            body = json.dumps({"default": "python3", "kernelspecs": {"python3": {}}}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(body)
            return
        self.send_response(404)
        self.end_headers()
HTTPServer(("127.0.0.1", port), H).serve_forever()
`);
    process.env.PYTHONPATH = stubSite;
    app = await launch(root);
    page = app.page;
});
after(async () => {
    await app?.close(); echo?.close(); forge?.close();
    for (const dir of madeDirs) { try { removeTree(dir); } catch { /* still held by a closing process: the next run's temp folder is not the worse for one */ } }
});
// a test that failed half way leaves no program running for the next one
afterEach(async () => {
    if (page && !(await page.evaluate(`${$('b-stop')}.disabled`))) {
        await page.evaluate(`${$('b-stop')}.click()`);
        await page.waitFor(`${$('b-stop')}.disabled`, 15000, 'the end of the program').catch(() => { });
    }
});

describe('the editor and the language server', () => {
    test('a file opens with its text, and an unknown name is marked as a problem', async () => {
        await open('calc.py');
        assert.match(await page.evaluate('window.pymagic.editor.getValue()'), /def total\(prices\)/);
        const markers = await page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); const found = window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }); return found.length ? found.map((x) => x.startLineNumber + ': ' + x.message) : null; })()`, 30000, 'the problems of calc.py');
        assert.ok(markers.some((m) => m.startsWith('10: ') && /undefined_name/.test(m)), markers.join('\n'));
    });

    test('hover over a call shows the function and its docstring', async () => {
        await open('calc.py');
        // asked again every few seconds: the first hover of a session can come before the language server has the file
        let text = null;
        for (let i = 0; i < 6 && !text; i++) {
            await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 9, column: 30 }); ed.focus(); ed.trigger('e2e', 'editor.action.showHover', {}); })()`);
            text = await page.waitFor(`[...document.querySelectorAll('.monaco-hover-content')].map((h) => h.textContent).join(' ').trim() || null`, 4000, 'the hover').catch(() => null);
        }
        assert.ok(text, 'no hover in 24 seconds');
        assert.match(text, /total/);
        assert.match(text, /The sum of the prices/);
        await page.evaluate(`window.pymagic.editor.trigger('e2e', 'hideHover', {})`);
    });

    test('completion after a module name offers its members', async () => {
        await open('calc.py');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; const m = ed.getModel(); const end = m.getLineCount(); ed.setPosition({ lineNumber: end, column: m.getLineMaxColumn(end) }); ed.trigger('e2e', 'type', { text: '\\njson.du' }); ed.trigger('e2e', 'editor.action.triggerSuggest', {}); })()`);
        const labels = await page.waitFor(`[...document.querySelectorAll('.suggest-widget .monaco-list-row')].map((r) => r.getAttribute('aria-label')).filter(Boolean).join(' | ') || null`, 15000, 'the suggestions');
        assert.match(labels, /dumps/);
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.trigger('e2e', 'hideSuggestWidget', {}); ed.trigger('e2e', 'undo', {}); })()`);
        assert.doesNotMatch(await page.evaluate('window.pymagic.editor.getValue()'), /json\.du\b/);
    });

    test('Format Document lays the file out as black does', async () => {
        await open('fmt.py');
        await page.evaluate(`window.pymagic.editor.getAction('editor.action.formatDocument').run()`);
        const text = await page.waitFor(`(() => { const t = window.pymagic.editor.getValue(); return t.startsWith('x = 1') ? t : null; })()`, 15000, 'the formatted text');
        assert.equal(text, 'x = 1\nif x:\n    y = [1, 2, 3]\n');
    });

    test('a quote typed over a selected string changes its quotes, and Format gives single quotes by default', async () => {
        await open('quotes.py');
        // select "/shop/about/" with its quotes and type '
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setSelection(new window.pymagic.monaco.Selection(1, 7, 1, 21)); ed.focus(); })()`);
        await page.send('Input.dispatchKeyEvent', { type: 'keyDown', key: "'", code: 'Quote', text: "'", windowsVirtualKeyCode: 222 });
        await page.send('Input.dispatchKeyEvent', { type: 'keyUp', key: "'", code: 'Quote', windowsVirtualKeyCode: 222 });
        assert.equal(await page.evaluate('window.pymagic.editor.getModel().getLineContent(1)'), "url = '/shop/about/'");
        await page.evaluate(`window.pymagic.editor.getAction('editor.action.formatDocument').run()`);
        const text = await page.waitFor(`(() => { const t = window.pymagic.editor.getValue(); return t.includes("name = 'pen'") ? t : null; })()`, 15000, 'the formatted text');
        assert.equal(text, "url = '/shop/about/'\nname = 'pen'\nsaid = \"it's\"\n");
    });
});

describe('Deployment', () => {
    const share = tempDir('pymagic-e2e-share-');
    const fill = (id, value) => page.evaluate(`(() => { const e = document.getElementById(${JSON.stringify(id)}); e.value = ${JSON.stringify(value)}; e.dispatchEvent(new Event('change', { bubbles: true })); })()`);

    test('a folder server is added, a file uploaded, compared with the server\'s, and the list shows the mapping', async () => {
        await page.evaluate(`${$('t-deployment')}.click()`);
        await page.evaluate(`${$('dep-add')}.click()`);
        await page.waitFor(`document.getElementById('dep-form-type')`, 5000, 'the form');
        await fill('dep-form-type', 'folder');
        await fill('dep-form-root', share.replace(/\\/g, '/'));
        await fill('dep-form-name', 'the share');
        await page.evaluate(`[...document.querySelectorAll('.dep-form button')].find((b) => b.textContent === 'Test and save').click()`);
        await page.waitFor(`[...document.querySelectorAll('#dep-server option')].some((o) => o.textContent === 'the share')`, 10000, 'the server in the list');
        await open('shop_lib.py');
        await page.evaluate(`${$('dep-upload')}.click()`);
        await page.waitFor(`document.getElementById('toast')?.textContent.includes('Uploaded 1 file')`, 10000, 'the upload to finish');
        assert.equal(readFileSync(join(share, 'shop_lib.py'), 'utf8'), 'def total(prices):\n    return sum(prices)\n');
        await page.waitFor(`[...document.querySelectorAll('#dep-list .dep-name')].some((n) => n.textContent === 'shop_lib.py')`, 10000, 'the file in the server list');
        assert.equal(await page.evaluate(`[...document.querySelectorAll('#dep-list .dep-row')].find((r) => r.querySelector('.dep-name').textContent === 'shop_lib.py').querySelector('.dep-local').textContent`), '↔ shop_lib.py');
        // changed on the server: the comparison shows both
        writeFileSync(join(share, 'shop_lib.py'), 'def total(prices):\n    return 0\n');
        await page.evaluate(`${$('dep-compare')}.click()`);
        await page.waitFor(`document.querySelector('.hist-diff-title')?.textContent.includes('the server')`, 10000, 'the comparison');
        await page.evaluate(`[...document.querySelectorAll('.hist-diff-head button')].find((b) => b.textContent === 'Close').click()`);
    });

    test('Sync lists what differs and writes it the way chosen', async () => {
        writeFileSync(join(share, 'from_server.txt'), 'only there');
        await page.evaluate(`${$('dep-sync')}.click()`);
        await page.waitFor(`document.querySelector('.ask-box input')`, 5000, 'the question');
        await page.evaluate(`document.querySelector('.ask-box input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))`);
        await page.waitFor(`document.querySelector('.dep-sync-row')`, 15000, 'the plan');
        const states = await page.evaluate(`Object.fromEntries([...document.querySelectorAll('.dep-sync-row')].map((r) => [r.querySelector('.dep-name').textContent, r.querySelector('.dep-state').textContent]))`);
        assert.equal(states['from_server.txt'], 'only on the server');
        assert.equal(states['calc.py'], 'only here');
        assert.ok(!('.pymagic/macros.json' in states) && !Object.keys(states).some((k) => k.startsWith('.git/') || k.startsWith('.pymagic/')), 'what never goes is not offered');
        await page.evaluate(`[...document.querySelectorAll('.dep-sync .hist-diff-head button')].find((b) => b.textContent === 'Apply').click()`);
        await page.waitFor(`!document.querySelector('.dep-sync')`, 30000, 'the sync to finish');
        assert.ok(existsSync(join(share, 'calc.py')), 'a local file went up');
        assert.equal(await page.evaluate(`fetch('/api/file?path=from_server.txt').then((r) => r.text())`), 'only there');
    });

    test('a server that takes what is saved gets the file on save', async () => {
        await page.evaluate(`${$('dep-edit')}.click()`);
        await page.waitFor(`document.getElementById('dep-form-uploadOnSave')`, 5000, 'the form');
        await page.evaluate(`(() => { const c = document.getElementById('dep-form-uploadOnSave'); c.checked = true; })()`);
        await page.evaluate(`[...document.querySelectorAll('.dep-form button')].find((b) => b.textContent === 'Test and save').click()`);
        await page.waitFor(`document.getElementById('dep-info')?.textContent.includes('uploads on save')`, 10000, 'the setting');
        assert.equal(await page.evaluate(`fetch('/api/file?path=shop_lib.py', { method: 'PUT', body: 'def total(prices):\\n    return sum(prices) + 1\\n' }).then((r) => r.status)`), 204);
        for (let i = 0; i < 30 && !readFileSync(join(share, 'shop_lib.py'), 'utf8').includes('+ 1'); i++) await sleep(300);
        assert.equal(readFileSync(join(share, 'shop_lib.py'), 'utf8'), 'def total(prices):\n    return sum(prices) + 1\n');
        await page.evaluate(`fetch('/api/file?path=shop_lib.py', { method: 'PUT', body: 'def total(prices):\\n    return sum(prices)\\n' })`);
        await sleep(800);
    });
});

describe('Services and Run Anything', () => {
    const rows = () => page.evaluate(`[...document.querySelectorAll('#services-list .svc-row')].map((r) => r.querySelector('.svc-name').textContent + '|' + r.querySelector('.svc-state').textContent.split(' ')[0])`);
    const button = (label) => page.evaluate(`[...document.querySelectorAll('#services-list .svc-row button')].find((b) => b.textContent === ${JSON.stringify(label)}).click()`);

    test('a running program is listed, restarted and stopped from the Services window', async () => {
        await open('loop.py');
        await page.evaluate(`${$('b-run')}.click()`);
        await page.waitFor(`${$('output')}.textContent.includes('started')`, 20000, 'the program to start');
        await page.evaluate(`${$('t-services')}.click()`);
        await page.waitFor(`[...document.querySelectorAll('#services-list .svc-name')].some((n) => n.textContent === 'loop.py')`, 10000, 'the program in the list');
        assert.ok((await rows()).includes('loop.py|running'), (await rows()).join(','));
        assert.equal(await page.evaluate(`document.getElementById('services-count').textContent`), '1');
        // restarted: the output is made again and the same program runs
        await page.evaluate(`${$('output')}.textContent = ''`);
        await button('Restart');
        await page.waitFor(`${$('output')}.textContent.includes('started')`, 20000, 'the program to start again');
        await page.waitFor(`[...document.querySelectorAll('#services-list .svc-name')].some((n) => n.textContent === 'loop.py')`, 10000, 'it in the list again');
        await button('Stop');
        await page.waitFor(`![...document.querySelectorAll('#services-list .svc-name')].some((n) => n.textContent === 'loop.py')`, 15000, 'the program to leave the list');
    });

    test('Control twice opens Run Anything, which runs a script and a module', async () => {
        const tap = async () => {
            await page.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', key: 'Control', code: 'ControlLeft', windowsVirtualKeyCode: 17, modifiers: 2 });
            await page.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Control', code: 'ControlLeft', windowsVirtualKeyCode: 17 });
        };
        await open('calc.py');
        await tap();
        await tap();
        await page.waitFor(`document.querySelector('.ra-box input')`, 10000, 'the box');
        await page.evaluate(`(() => { const i = document.querySelector('.ra-box input'); i.value = 'calc'; i.dispatchEvent(new Event('input')); })()`);
        await page.waitFor(`[...document.querySelectorAll('.ra-row .ra-label')].some((l) => l.textContent === 'calc.py')`, 10000, 'calc.py suggested');
        await page.evaluate(`document.querySelector('.ra-box input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))`);
        await page.waitFor(`${$('output')}.textContent.includes('"total": 6')`, 30000, 'the script run');
        await page.waitFor(`!${$('b-stop')}.disabled === false`, 20000, 'the run to end');
        // a module, typed whole
        await page.evaluate(`window.dispatchEvent(new KeyboardEvent('keydown', { key: 'A', ctrlKey: true, shiftKey: true, bubbles: true }))`);
        await page.evaluate(`window.pymagic.editor.getAction('pymagic.runAnything').run()`);
        await page.waitFor(`document.querySelector('.ra-box input')`, 10000, 'the box again');
        await page.evaluate(`(() => { const i = document.querySelector('.ra-box input'); i.value = 'python -m calendar 2026 1'; i.dispatchEvent(new Event('input')); i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
        await page.waitFor(`${$('output')}.textContent.includes('January 2026')`, 30000, 'the module run');
    });
});

describe('macros', () => {
    const press = async (key, { text, code, vk, modifiers = 0 } = {}) => {
        await page.send('Input.dispatchKeyEvent', { type: text ? 'keyDown' : 'rawKeyDown', key, code, windowsVirtualKeyCode: vk, text, modifiers });
        await page.send('Input.dispatchKeyEvent', { type: 'keyUp', key, code, windowsVirtualKeyCode: vk, modifiers });
    };
    const typeText = async (s) => { for (const ch of s) await press(ch, { text: ch, code: 'Key' + ch.toUpperCase(), vk: ch.toUpperCase().charCodeAt(0) }); };
    const action = (id) => page.evaluate(`window.pymagic.editor.getAction(${JSON.stringify(id)}).run()`);
    const content = () => page.evaluate(`window.pymagic.editor.getModel().getValue()`);

    test('typing and editing keys are recorded, kept under a name and played back', async () => {
        await open('macro.py');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 2, column: 1 }); ed.focus(); })()`);
        await action('pymagic.macro.toggle');
        await page.waitFor(`document.getElementById('macro-state') && !document.getElementById('macro-state').hidden`, 5000, 'the recording mark');
        await typeText('hi');
        await press('Enter', { text: '\r', code: 'Enter', vk: 13 });
        await press('Backspace', { code: 'Backspace', vk: 8 });
        await action('pymagic.macro.toggle');                                    // stops, and asks for a name
        await page.waitFor(`document.querySelector('.ask-box input')`, 5000, 'the question for the name');
        await page.evaluate(`(() => { const i = document.querySelector('.ask-box input'); i.value = 'greet'; i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
        await page.waitFor(`fetch('/api/macros').then((r) => r.json()).then((m) => m.length === 1)`, 5000, 'the saved macro');
        assert.deepEqual(await page.evaluate(`fetch('/api/macros').then((r) => r.json())`), [{ name: 'greet', key: '', steps: [{ k: 'text', text: 'hi\n' }, { k: 'cmd', id: 'deleteLeft' }] }]);
        assert.equal(await content(), 'x = 1\nhi');
        // played back at the end of that line
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 2, column: 3 }); ed.focus(); })()`);
        await action('pymagic.macro.playLast');
        assert.equal(await content(), 'x = 1\nhihi');
    });

    test('a saved macro is given a key and runs from it', async () => {
        await open('macro.py');
        await action('pymagic.macro.manage');
        await page.waitFor(`document.querySelector('.macro-key')`, 5000, 'the dialog');
        await page.evaluate(`document.querySelector('.macro-key').click()`);
        await press('1', { text: undefined, code: 'Digit1', vk: 49, modifiers: 2 | 1 });
        await page.waitFor(`document.querySelector('.macro-key')?.textContent === 'Ctrl+Alt+1'`, 5000, 'the key shown');
        assert.equal((await page.evaluate(`fetch('/api/macros').then((r) => r.json())`))[0].key, 'Ctrl+Alt+1');
        await page.key(27, 'Escape');
        await page.waitFor(`!document.querySelector('.macro-box')`, 5000, 'the dialog to close');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 2, column: 5 }); ed.focus(); })()`);
        await press('1', { code: 'Digit1', vk: 49, modifiers: 2 | 1 });
        await page.waitFor(`window.pymagic.editor.getModel().getValue() === 'x = 1\\nhihihi'`, 5000, 'the macro played from its key');
    });

    test('the macro played many times, and deleted', async () => {
        await open('macro.py');
        await action('pymagic.macro.manage');
        await page.waitFor(`document.querySelector('.macro-row')`, 5000, 'the dialog');
        await page.evaluate(`[...document.querySelectorAll('.macro-row button')].find((b) => b.textContent === 'Play N times…').click()`);
        await page.waitFor(`document.querySelector('.ask-box input')`, 5000, 'the question');
        await page.evaluate(`(() => { const i = document.querySelector('.ask-box input'); i.value = '3'; i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
        await page.waitFor(`window.pymagic.editor.getModel().getValue() === 'x = 1\\nhihihihihihi'`, 5000, 'three more');
        await page.evaluate(`window.pymagic.editor.trigger('e2e', 'undo', {})`);
        await action('pymagic.macro.manage');
        await page.waitFor(`document.querySelector('.macro-row')`, 5000, 'the dialog');
        await page.evaluate(`[...document.querySelectorAll('.macro-row button')].find((b) => b.textContent === 'Delete').click()`);
        await page.waitFor(`fetch('/api/macros').then((r) => r.json()).then((m) => m.length === 0)`, 5000, 'the macro gone');
        await page.key(27, 'Escape');
    });
});

describe('Markdown and reStructuredText', () => {
    const preview = (js) => page.evaluate(`(() => { const b = document.querySelector('.md-body'); return ${js}; })()`);

    test('the preview shows headings, a table, task boxes, a link and a Mermaid flowchart, and follows the typing', async () => {
        await open('demo.md');
        await page.evaluate(`${$('md-toggle')}.click()`);
        await page.waitFor(`document.querySelector('.md-pane:not([hidden]) .md-body h1')`, 15000, 'the preview');
        assert.equal(await preview(`b.querySelector('h1').textContent`), 'Demo');
        assert.deepEqual(await preview(`[...b.querySelectorAll('tr')].map((r) => [...r.children].map((c) => c.textContent).join('|'))`), ['Name|Qty', 'pen|2', 'notebook|12']);
        assert.equal(await preview(`b.querySelector('td:nth-child(2)').style.textAlign`), 'right');
        assert.deepEqual(await preview(`[...b.querySelectorAll('li.md-task input')].map((i) => i.checked)`), [false, true]);
        assert.deepEqual(await preview(`[...b.querySelectorAll('svg.md-diagram text')].map((t) => t.textContent)`), ['yes', 'Start', 'Ok?', 'Done']);
        assert.equal(await preview(`b.querySelectorAll('svg.md-diagram .md-node').length`), 3);
        await page.evaluate(`window.pymagic.editor.executeEdits('e2e', [{ range: { startLineNumber: 3, startColumn: 1, endLineNumber: 3, endColumn: 1 }, text: 'Typed just now.\\\\n\\\\n' }])`);
        await page.waitFor(`document.querySelector('.md-body')?.textContent.includes('Typed just now.')`, 5000, 'the preview to follow the typing');
        await page.evaluate(`window.pymagic.editor.trigger('e2e', 'undo', {})`);
    });

    test('a link in the preview opens the file in the editor', async () => {
        await open('demo.md');
        await page.waitFor(`document.querySelector('.md-body a[data-href="docs/guide.md"]')`, 10000, 'the link');
        await page.evaluate(`document.querySelector('.md-body a[data-href="docs/guide.md"]').click()`);
        await page.waitFor(`window.pymagic.editor.getModel().getValue().startsWith('# Guide')`, 10000, 'guide.md in the editor');
    });

    test('the Structure view lists the headings of a Markdown file', async () => {
        await open('demo.md');
        await page.evaluate(`${$('r-structure')}.click()`);
        await page.waitFor(`[...document.querySelectorAll('#structure li')].map((l) => l.textContent).join('|').includes('Second')`, 10000, 'the headings');
        assert.match(await page.evaluate(`[...document.querySelectorAll('#structure li')].map((l) => l.textContent).join('|')`), /Demo.*Second/);
        await page.evaluate(`document.getElementById('r-files')?.click() ?? document.querySelector('#rail button').click()`);
    });

    test('links are completed with the project\'s files and the headings of the target', async () => {
        await open('demo.md');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; const m = ed.getModel(); const end = m.getLineCount(); ed.executeEdits('e2e', [{ range: { startLineNumber: end, startColumn: m.getLineMaxColumn(end), endLineNumber: end, endColumn: m.getLineMaxColumn(end) }, text: '\\\\n[x](docs/gui' }]); ed.setPosition({ lineNumber: end + 1, column: '[x](docs/gui'.length + 1 }); ed.focus(); ed.trigger('e2e', 'editor.action.triggerSuggest', {}); })()`);
        await page.waitFor(`[...document.querySelectorAll('.suggest-widget .monaco-list-row')].some((r) => r.textContent.includes('docs/guide.md'))`, 15000, 'the file offered');
        await page.key(27, 'Escape');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; const m = ed.getModel(); const end = m.getLineCount(); ed.executeEdits('e2e', [{ range: { startLineNumber: end, startColumn: 1, endLineNumber: end, endColumn: m.getLineMaxColumn(end) }, text: '[x](docs/guide.md#' }]); ed.setPosition({ lineNumber: end, column: '[x](docs/guide.md#'.length + 1 }); ed.trigger('e2e', 'editor.action.triggerSuggest', {}); })()`);
        await page.waitFor(`[...document.querySelectorAll('.suggest-widget .monaco-list-row')].some((r) => r.textContent.includes('setup'))`, 15000, 'the heading offered');
        await page.key(27, 'Escape');
        await page.evaluate(`window.pymagic.editor.trigger('e2e', 'undo', {}); window.pymagic.editor.trigger('e2e', 'undo', {})`);
    });

    test('Format Tables lines the pipes up', async () => {
        await open('demo.md');
        await page.evaluate(`window.pymagic.editor.getAction('pymagic.markdown.formatTables').run()`);
        const lines = await page.evaluate(`window.pymagic.editor.getModel().getLinesContent().slice(4, 8)`);
        assert.deepEqual(lines, ['| Name     | Qty |', '| :------- | --: |', '| pen      |   2 |', '| notebook |  12 |']);
        await page.evaluate(`window.pymagic.editor.trigger('e2e', 'undo', {})`);
    });

    test('Export to HTML writes a page of its own next to the document', async () => {
        await open('demo.md');
        await page.evaluate(`window.pymagic.editor.getAction('pymagic.markdown.export').run()`);
        await page.waitFor(`document.querySelector('.ask-box input')`, 10000, 'the question');
        await page.evaluate(`(() => { const i = document.querySelector('.ask-box input'); i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
        await page.waitFor(`false || true`, 500);
        const written = join(app.root, 'demo.html');
        for (let i = 0; i < 20 && !existsSync(written); i++) await sleep(300);
        const html = readFileSync(written, 'utf8');
        assert.match(html, /<h1 id="demo">Demo<\/h1>/);
        assert.match(html, /<table>/);
        assert.match(html, /<svg class="md-diagram"/);
        assert.ok(!html.includes('data-line'));
    });

    test('a reStructuredText file is previewed through docutils, or says what is missing', async () => {
        await open('demo.rst');
        await page.waitFor(`document.querySelector('.md-body')?.textContent.length > 0`, 15000, 'the preview');
        const shown = await preview(`b.textContent`);
        assert.ok(/pip install docutils/.test(shown) || /Title/.test(shown), shown);
        if (/pip install docutils/.test(shown)) assert.ok(shown.includes('A *word*.'), 'the text is shown as it is');
    });
});

describe('grammar', { skip: !process.env.PYMAGIC_LANGUAGETOOL && 'LanguageTool is not installed (PYMAGIC_LANGUAGETOOL)' }, () => {
    const marks = () => page.evaluate(`(() => { const m = window.pymagic.editor.getModel(); return window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).filter((x) => x.source === 'Grammar').map((x) => x.startLineNumber + ':' + x.message); })()`);

    test('sentences of a Markdown file are checked by LanguageTool once the setting is on', async () => {
        await open('gram.md');
        await page.evaluate(`window.pymagic.changeSetting({ grammar: true })`);
        const found = await page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); const f = window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).filter((x) => x.source === 'Grammar'); return f.length >= 2 ? f.map((x) => x.startLineNumber + ':' + x.code) : null; })()`, 90000, 'LanguageTool to start and answer');
        assert.ok(found.some((f) => f.startsWith('1:')) && found.some((f) => f.startsWith('3:')), found.join(','));
    });

    test('Alt+Enter offers the suggestion, which replaces the words; a rule can be ignored for the project', async () => {
        await open('gram.md');
        const before = await marks();
        assert.ok(before.length >= 2, before.join('|'));
        const menu = async (line, column, choose) => {
            await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: ${line}, column: ${column} }); ed.focus(); ed.getAction('editor.action.quickFix').run(); })()`);
            await page.waitFor(`[...document.querySelectorAll('.action-widget .monaco-list-row')].some((r) => r.textContent.includes(${JSON.stringify(choose)}))`, 15000, 'the menu with ' + choose).catch(async (e) => { console.log('ROWS', JSON.stringify(await page.evaluate(`[...document.querySelectorAll('.action-widget .monaco-list-row')].map((r) => r.textContent)`)), 'at', '+line+', '+column+'); throw e; });
            const at = await page.evaluate(`(() => { const r = [...document.querySelectorAll('.action-widget .monaco-list-row')].find((x) => x.textContent.includes(${JSON.stringify(choose)})); const b = r.getBoundingClientRect(); return { x: b.left + 20, y: b.top + b.height / 2 }; })()`);
            await page.click(at.x, at.y);
            await sleep(700);
        };
        await menu(1, 7, "Grammar: replace with 'is'");
        assert.match(await page.evaluate(`window.pymagic.editor.getModel().getLineContent(1)`), /^This is a test/);
        // the rule of the other sentence is switched off for the project
        const rule = (await marks()).find((m) => m.startsWith('3:'));
        assert.ok(rule);
        const target = await page.evaluate(`(() => { const m = window.pymagic.editor.getModel(); const x = window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).find((x) => x.source === 'Grammar' && x.startLineNumber === 3); return { column: x.startColumn + 1, code: x.code }; })()`);
        const column = target.column;
        await menu(3, column, 'Ignore the rule');
        for (let i = 0; i < 30 && !existsSync(join(app.root, '.pymagic', 'grammar-ignore.txt')); i++) await sleep(300);
        assert.ok(readFileSync(join(app.root, '.pymagic', 'grammar-ignore.txt'), 'utf8').trim().length > 3);
        await page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); return !window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).some((x) => x.source === 'Grammar' && x.code === ${JSON.stringify(target.code)}); })()`, 30000, 'that rule to go');
        assert.ok(readFileSync(join(app.root, '.pymagic', 'grammar-ignore.txt'), 'utf8').includes(target.code));
        await page.evaluate(`window.pymagic.changeSetting({ grammar: false })`);
    });
});

describe('spelling', () => {
    const typos = () => page.evaluate(`(() => { const m = window.pymagic.editor.getModel(); return window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).filter((x) => x.source === 'Spelling').map((x) => x.startLineNumber + ':' + x.message); })()`);
    const fix = async (line, column, choose) => {
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: ${line}, column: ${column} }); ed.focus(); ed.getAction('editor.action.quickFix').run(); })()`);
        await page.waitFor(`[...document.querySelectorAll('.action-widget .monaco-list-row')].some((r) => r.textContent.includes(${JSON.stringify(choose)}))`, 15000, 'the menu with ' + choose);
        const at = await page.evaluate(`(() => { const r = [...document.querySelectorAll('.action-widget .monaco-list-row')].find((x) => x.textContent.includes(${JSON.stringify(choose)})); const b = r.getBoundingClientRect(); return { x: b.left + 20, y: b.top + b.height / 2 }; })()`);
        await page.click(at.x, at.y);
        await sleep(600);
    };

    test('typos in a comment and in a name are marked by Windows\' spell checker', async () => {
        await open('typo.py');
        const found = await page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); const f = window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).filter((x) => x.source === 'Spelling').map((x) => x.startLineNumber + ':' + x.message); return f.length >= 3 ? f : null; })()`, 30000, 'the typos');
        assert.deepEqual(found.slice().sort(), ["1:Typo: In word 'Recieve'", "1:Typo: In word 'mesage'", "4:Typo: In word 'mesage'"].sort());
    });

    test('Alt+Enter on a typo offers the right word, which replaces it', async () => {
        await open('typo.py');
        await fix(1, 4, "replace 'Recieve' with 'Receive'");
        assert.equal(await page.evaluate(`window.pymagic.editor.getModel().getLineContent(1)`), '# Receive the mesage here');
    });

    test('a word can be saved to the project\'s dictionary, and is no typo anywhere after that', async () => {
        await open('typo.py');
        await fix(1, 15, "Save 'mesage' to the project's dictionary");
        await page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); return !window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).some((x) => x.source === 'Spelling'); })()`, 20000, 'the typos to go');
        assert.match(readFileSync(join(app.root, '.pymagic', 'dictionary.txt'), 'utf8'), /^mesage$/m);
    });
});

describe('language injections', () => {
    const classes = () => page.evaluate(`(() => { const m = window.pymagic.editor.getModel(); const out = {}; for (const d of m.getAllDecorations()) for (const c of (d.options.inlineClassName ?? '').split(' ')) if (c.startsWith('inj-')) out[c] = (out[c] ?? 0) + 1; return out; })()`);

    test('SQL, JSON and a regular expression in strings are coloured by their own languages', async () => {
        await open('inj.py');
        const found = await page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); const out = {}; for (const d of m.getAllDecorations()) for (const c of (d.options.inlineClassName ?? '').split(' ')) if (c.startsWith('inj-')) out[c] = (out[c] ?? 0) + 1; return out['inj-kw'] >= 3 && out['inj-re-quantifier'] ? out : null; })()`, 20000, 'the injected languages');
        assert.ok(found['inj-sql'] >= 2, JSON.stringify(found));      // QUERY and HALF, by their keywords
        assert.ok(found['inj-json'] === 1, JSON.stringify(found));    // by the comment above
        assert.ok(found['inj-regex'] === 1 && found['inj-re-group'] >= 2 && found['inj-re-class'] === 1 && found['inj-re-anchor'] === 2, JSON.stringify(found));
    });

    test('SQL in a string is completed with the columns of the data source', async () => {
        await open('inj.py');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; const line = ed.getModel().getLinesContent().findIndex((l) => l.startsWith('HALF')) + 1; ed.setPosition({ lineNumber: line, column: 'HALF = "SELECT '.length + 1 }); ed.focus(); ed.trigger('e2e', 'editor.action.triggerSuggest', {}); })()`);
        await page.waitFor(`[...document.querySelectorAll('.suggest-widget .monaco-list-row')].some((r) => r.textContent.includes('price'))`, 20000, 'the columns of item');
        await page.key(27, 'Escape');
    });

    test('Edit Injected Fragment opens the SQL in a window of its own and writes it back with its quotes', async () => {
        await open('inj.py');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; const line = ed.getModel().getLinesContent().findIndex((l) => l.startsWith('QUERY')) + 1; ed.setPosition({ lineNumber: line, column: 14 }); ed.focus(); ed.getAction('pymagic.injection.edit').run(); })()`);
        await page.waitFor(`document.querySelector('.inj-window .monaco-editor')`, 10000, 'the window');
        assert.match(await page.evaluate(`document.querySelector('.inj-window .hist-diff-title').textContent`), /SQL fragment/);
        await page.evaluate(`(() => { const inner = window.pymagic.monaco.editor.getEditors().find((e) => document.querySelector('.inj-window').contains(e.getDomNode())); inner.setValue('SELECT "id", name\\nFROM item'); })()`);
        await page.evaluate(`[...document.querySelectorAll('.inj-window button')].find((b) => b.textContent === 'OK').click()`);
        await page.waitFor(`!document.querySelector('.inj-window')`, 5000, 'the window to close');
        const line = await page.evaluate(`window.pymagic.editor.getModel().getLinesContent().find((l) => l.startsWith('QUERY'))`);
        assert.equal(line, 'QUERY = "SELECT \\"id\\", name\\nFROM item"');
        await page.evaluate(`window.pymagic.editor.trigger('e2e', 'undo', {})`);
    });
});

describe('scratch files', () => {
    test("a scratch is made outside the project, listed, run with the project's interpreter and its imports", async () => {
        await page.evaluate(`${$('scratch-new')}.click()`);
        await page.waitFor(`document.querySelector('.ask-box input')`, 5000, 'the question');
        await page.evaluate(`(() => { const i = document.querySelector('.ask-box input'); i.value = 'try.py'; i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
        await page.waitFor(`window.pymagic.editor.getModel()?.getLanguageId() === 'python' && window.pymagic.editor.getValue() === '# scratch\\n'`, 15000, 'the new scratch in the editor');
        assert.ok((await page.evaluate(`[...document.querySelectorAll('#scratch-list .scratch-name')].map((n) => n.textContent)`)).includes('try.py'));
        await page.evaluate(`window.pymagic.editor.setValue('import shop_lib\\nprint("total", shop_lib.total([1, 2, 3]))\\n')`);
        await page.evaluate(`${$('b-run')}.click()`);
        await page.waitFor(`${$('output')}.textContent.includes('total 6')`, 30000, 'the scratch run, importing a module of the project');
        // the project has no trace of it, the scratch folder has the file
        const tree = await page.evaluate(`fetch('/api/tree').then((r) => r.json()).then((t) => t.files.map((f) => f.path))`);
        assert.ok(!tree.some((p) => p.includes('try.py') || p.includes('scratch')), tree.join(', '));
        assert.ok(existsSync(join(process.env.PYMAGIC_SCRATCHES, 'try.py')));
        assert.match(readFileSync(join(process.env.PYMAGIC_SCRATCHES, 'try.py'), 'utf8'), /shop_lib\.total/);
    });

    test('Ctrl+Alt+Shift+Insert asks for another one, and a name that is taken gets a number', async () => {
        await page.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', windowsVirtualKeyCode: 45, key: 'Insert', code: 'Insert', modifiers: 2 | 1 | 8 });
        await page.send('Input.dispatchKeyEvent', { type: 'keyUp', windowsVirtualKeyCode: 45, key: 'Insert', code: 'Insert', modifiers: 2 | 1 | 8 });
        await page.waitFor(`document.querySelector('.ask-box input')`, 5000, 'the question');
        await page.evaluate(`(() => { const i = document.querySelector('.ask-box input'); i.value = 'try.py'; i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
        await page.waitFor(`[...document.querySelectorAll('#scratch-list .scratch-name')].some((n) => n.textContent === 'try_1.py')`, 10000, 'try_1.py');
    });
});

describe('run and debug', () => {
    test('Run prints the program\'s output and how it ended', async () => {
        await open('calc.py');
        await page.evaluate(`${$('b-run')}.click()`);
        await page.waitFor(`${$('output')}.textContent.includes('Process finished')`, 30000, 'the end of the run');
        const text = await output();
        assert.match(text, /\{"total": 6\}/);
        assert.match(text, /NameError: name 'undefined_name' is not defined/);
        assert.match(text, /exit code 1/);
    });

    test('Debug stops at a breakpoint with the frame\'s variables, and steps', async () => {
        await open('calc.py');
        assert.equal(await clickMargin(6), true, 'a breakpoint on `return sum(prices)`');
        await page.evaluate(`${$('b-debug')}.click()`);
        assert.equal(await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 30000, 'the stop'), 'total calc.py:6');
        const vars = await page.waitFor(`${$('vars')}.textContent.includes('prices') && ${$('vars')}.textContent`, 10000, 'the variables');
        assert.match(vars, /prices = \[1, 2, 3\]/);
        await page.evaluate(`${$('b-out')}.click()`);
        assert.match(await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent.startsWith('<module>') && document.querySelector('#frames div.sel').textContent`, 10000, 'the step out'), /^<module> calc\.py:9/);
        await page.evaluate(`${$('b-stop')}.click()`);
        await page.waitFor(`${$('b-stop')}.disabled`, 15000, 'the end of the session');
        assert.equal(await clickMargin(6), false, 'a second click takes the breakpoint away');
    });

    test('Pause stops a program that is running, in its loop', async () => {
        await open('loop.py');
        await page.evaluate(`${$('b-debug')}.click()`);
        await page.waitFor(`${$('output')}.textContent.includes('started')`, 30000, 'the program to start').catch(async (e) => { throw new Error(e.message + '; the Run panel says: ' + await output()); });
        await sleep(300);
        await page.evaluate(`${$('b-pause')}.click()`);
        assert.match(await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 10000, 'the pause'), /^spin loop\.py:[678]$/);
        assert.match(await output(), /Paused/);
        await page.evaluate(`${$('b-stop')}.click()`);
        await page.waitFor(`${$('b-stop')}.disabled`, 15000, 'the end of the session');
    });
});

describe('the window\'s layout at different widths', () => {
    const resize = (width, height) => page.send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
    const visible = (id) => page.evaluate(`(() => { const e = document.getElementById(${JSON.stringify(id)}); const r = e.getBoundingClientRect(); return !!e.offsetParent && r.right <= innerWidth && r.width > 0; })()`);
    after(() => page.send('Emulation.clearDeviceMetricsOverride'));

    test('the command bar fits any width: what does not fit goes into ⋯, the interpreter and the settings stay', async () => {
        for (const [width, height] of [[1920, 900], [1400, 860], [1000, 640], [900, 600]]) {
            await resize(width, height);
            await page.waitFor(`(() => { const t = document.getElementById('toolbar'); return t.scrollWidth <= t.clientWidth + 1; })()`, 5000, `the bar fitting at ${width}`);
            for (const id of ['b-run', 'b-debug', 'interp', 'b-gear']) assert.equal(await visible(id), true, `${id} at ${width}`);
            const status = await page.evaluate(`(() => { const s = document.getElementById('status'); return s.scrollWidth <= s.clientWidth + 1; })()`);
            assert.equal(status, true, `the status bar at ${width}`);
        }
        // at 900 some commands are hidden, and ⋯ offers them
        assert.equal(await visible('b-more'), true, '⋯ at 900');
        await page.evaluate(`${$('b-more')}.click()`);
        const offered = await page.waitFor(`(() => { const r = [...document.querySelectorAll('.ctx-menu .ctx-item')].map((d) => d.textContent.trim()); return r.length && r; })()`, 5000, 'the ⋯ menu');
        assert.ok(offered.some((t) => t.startsWith('Remove unused imports')), offered.join(' | '));
        await page.evaluate(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))`);
        // wide again: nothing hidden, no ⋯
        await resize(2400, 900);
        await page.waitFor(`!document.getElementById('b-more').offsetParent`, 5000, '⋯ gone when everything fits');
    });

    test('the tool-window tabs keep the open one and the close button in view when they do not fit', async () => {
        await resize(900, 600);
        await page.evaluate(`${$('t-packages')}.click()`);
        await page.waitFor(`(() => { const t = document.getElementById('tabs'), a = document.getElementById('t-packages'), c = document.getElementById('t-close');
            const tr = t.getBoundingClientRect(), ar = a.getBoundingClientRect(), cr = c.getBoundingClientRect();
            return t.scrollWidth > t.clientWidth && ar.left >= tr.left && ar.right <= cr.left + 1 && cr.right <= tr.right + 1; })()`, 5000, 'the open tab and the close button in view');
        await page.evaluate(`${$('t-output')}.click()`);
        await page.send('Emulation.clearDeviceMetricsOverride');
    });
});

describe('the project\'s Python environment in the file tree', () => {
    test('an environment folder is shown, excluded, and read a level at a time when it is opened', async () => {
        // a stand-in environment: pyvenv.cfg makes it one, whatever its name
        const env = join(projectRoot, 'tool_env');
        mkdirSync(join(env, 'Lib', 'site-packages', 'pkg'), { recursive: true });
        writeFileSync(join(env, 'pyvenv.cfg'), 'home = C:\\\\Python313\n');
        writeFileSync(join(env, 'Lib', 'site-packages', 'pkg', '__init__.py'), 'X = 1\n');
        const rowOf = (path) => `document.querySelector('#files li[data-path="${path}"]')`;
        try {
            await page.evaluate(`document.getElementById('r-explorer').classList.contains('active') || document.getElementById('r-explorer').click(), true`);
            await page.evaluate(`${$('ex-refresh')}.click()`);
            await page.waitFor(rowOf('tool_env'), 10000, 'the environment in the tree');
            assert.ok(await page.evaluate(`${rowOf('tool_env')}.classList.contains('mark-excluded')`), 'shown as excluded');
            assert.match(await page.evaluate(`${rowOf('tool_env')}.title`), /Python environment/);
            // nothing of it is read before it is opened
            assert.equal(await page.evaluate(`!!${rowOf('tool_env/pyvenv.cfg')}`), false);
            await page.evaluate(`${rowOf('tool_env')}.click()`);
            await page.waitFor(`${rowOf('tool_env/pyvenv.cfg')} && ${rowOf('tool_env/Lib')}`, 10000, 'its first level');
            assert.ok(await page.evaluate(`${rowOf('tool_env/pyvenv.cfg')}.classList.contains('in-excluded')`), 'its files dim');
            assert.equal(await page.evaluate(`!!${rowOf('tool_env/Lib/site-packages')}`), false, 'the next level waits until Lib is opened');
            await page.evaluate(`${rowOf('tool_env/Lib')}.click()`);
            await page.waitFor(rowOf('tool_env/Lib/site-packages'), 10000, 'the next level');
            // closed again for the tests after this one
            await page.evaluate(`${rowOf('tool_env')}.click()`);
        }
        finally { removeTree(env); await page.evaluate(`${$('ex-refresh')}.click()`); }
    });
});

describe('code colours', () => {
    // the colour a word of the editor is drawn in, as "r,g,b"
    const colourOf = (word) => page.evaluate(`(() => {
        // Monaco draws spaces as no-break spaces
        const spans = [...document.querySelectorAll('#editor .view-line span span')].map((s) => [s, s.textContent.replace(/\\u00a0/g, ' ')]);
        const span = (spans.find(([, t]) => t === ${JSON.stringify(word)}) ?? spans.find(([, t]) => t.includes(${JSON.stringify(word)})))?.[0];
        return span ? getComputedStyle(span).color.replace(/[^0-9,]/g, '') : null;
    })()`);
    after(() => page.evaluate(`window.pymagic.changeSetting({ codeColors: 'vscode' })`));

    test('the code is coloured as VS Code colours it, and PyMagic\'s own palette is a setting', async () => {
        await open('calc.py');
        await page.waitFor(`[...document.querySelectorAll('#editor .view-line span span')].some((s) => s.textContent === 'return')`, 15000, 'the tokens');
        // Dark Modern: def blue, return and import purple, a string orange
        assert.equal(await colourOf('def'), '86,156,214');
        assert.equal(await colourOf('return'), '197,134,192');
        assert.equal(await colourOf('import'), '197,134,192');
        assert.equal(await colourOf('The sum of the prices'), '206,145,120');
        // a string's quotes are the string's colour, as VS Code draws them (Monaco's grammar called them escapes)
        assert.equal(await colourOf('"'), '206,145,120');
        await page.evaluate(`window.pymagic.changeSetting({ codeColors: 'pymagic' })`);
        await page.waitFor(`(() => { const s = [...document.querySelectorAll('#editor .view-line span span')].find((x) => x.textContent === 'def'); return s && getComputedStyle(s).color === 'rgb(142, 168, 255)'; })()`, 5000, "PyMagic's own colour of def");
        await page.evaluate(`window.pymagic.changeSetting({ codeColors: 'vscode' })`);
        await page.waitFor(`(() => { const s = [...document.querySelectorAll('#editor .view-line span span')].find((x) => x.textContent === 'def'); return s && getComputedStyle(s).color === 'rgb(86, 156, 214)'; })()`, 5000, "VS Code's colour of def again");
    });
});

describe('tests', () => {
    test('the Tests panel finds the unittest tests and shows their outcomes', async () => {
        await page.evaluate(`(() => { const s = ${$('tests-fw')}; s.value = 'unittest'; s.dispatchEvent(new Event('change')); ${$('t-tests')}.click(); ${$('tests-all')}.click(); })()`);
        await page.waitFor(`!${$('tests-all')}.disabled && ${$('tests-console')}.textContent.includes('finished')`, 60000, 'the test run');
        for (let i = 0; i < 6; i++)
            await page.evaluate(`[...document.querySelectorAll('#tests-tree .trow .caret')].find((c) => c.textContent === '▸')?.click()`);
        const rows = await page.evaluate(`Object.fromEntries([...document.querySelectorAll('#tests-tree .trow')].map((r) => [r.querySelector('.tname').textContent, r.querySelector('.ticon').textContent]))`);
        assert.equal(rows.test_ok, '✔');
        assert.equal(rows.test_bad, '✖');
    });
});

describe('local history', () => {
    const put = (text) => page.evaluate(`fetch('/api/file?path=hist.py', { method: 'PUT', body: ${JSON.stringify(text)} }).then((r) => r.status)`);
    const disk = () => page.evaluate(`fetch('/api/file?path=hist.py').then((r) => r.text())`);
    const labels = () => page.evaluate(`fetch('/api/history/labels').then((r) => r.json())`);
    const rows = () => page.evaluate(`[...document.querySelectorAll('#history-list .hist-row')].map((r) => r.textContent)`);

    test('a test run you ask for puts a label first, and a label put by hand shows among the versions', async () => {
        await page.evaluate(`${$('tests-all')}.click()`);
        await page.waitFor(`!${$('tests-all')}.disabled && ${$('tests-console')}.textContent.includes('finished')`, 60000, 'the test run');
        assert.ok((await labels()).some((l) => l.auto && l.name === 'Before running the tests'), JSON.stringify(await labels()));
        await open('hist.py');
        for (const text of ['def f():\n    return 1\n', 'def f():\n    return 2\n']) assert.equal(await put(text), 204);
        await page.evaluate(`window.confirm = () => true; ${$('t-history')}.click()`);
        await page.waitFor(`document.querySelectorAll('#history-list .hist-row:not(.hist-label)').length >= 2`, 10000, 'the versions');
        // the label by hand, through the form
        await page.evaluate(`${$('hist-label')}.click()`);
        await page.waitFor(`document.querySelector('.ask-box input')`, 5000, 'the question');
        await page.evaluate(`(() => { const i = document.querySelector('.ask-box input'); i.value = 'two returns'; i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
        await page.waitFor(`[...document.querySelectorAll('#history-list .hist-label')].some((r) => r.textContent.includes('two returns'))`, 10000, 'the label in the list');
    });

    test('the history of the selected lines shows only the versions where they differed', async () => {
        await put('# a header\ndef f():\n    return 3\n');
        await open('hist.py');
        await page.waitFor(`window.pymagic.editor.getModel().getLineCount() >= 3 && window.pymagic.editor.getModel().getValue().includes('return 3')`, 10000, 'the editor to read the file again');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setSelection({ startLineNumber: 3, startColumn: 1, endLineNumber: 3, endColumn: 15 }); ed.focus(); ed.getAction('pymagic.history.selection').run(); })()`);
        await page.waitFor(`document.getElementById('history-title').textContent.includes('lines 3–3')`, 10000, 'the selection history');
        const found = await rows();
        assert.ok(found.length >= 2 && found.length <= 3, found.join('\n'));
        assert.ok(found.some((r) => r.includes('return 2')) && found.some((r) => r.includes('return 1')), found.join('\n'));
        // a click opens the lines then and now
        await page.evaluate(`document.querySelector('#history-list .hist-row').click()`);
        await page.waitFor(`document.querySelector('.hist-diff')`, 5000, 'the comparison');
        await page.evaluate(`[...document.querySelectorAll('.hist-diff-head button')].find((b) => b.textContent === 'Close').click()`);
        await page.evaluate(`${$('hist-selection-back')}.click()`);
    });

    test('two versions compared with each other, and the project as a label had it written back', async () => {
        await page.waitFor(`document.querySelectorAll('#history-list .hist-row:not(.hist-label)').length >= 3`, 10000, 'the versions');
        await page.waitFor(`document.getElementById('history-title').textContent === 'Local history of hist.py' && document.querySelectorAll('#history-list .hist-row .hist-size').length >= 3`, 10000, 'the versions of the file');
        for (const i of [0, 2])   // the list is drawn again after each click
            await page.evaluate(`[...document.querySelectorAll('#history-list .hist-row:not(.hist-label)')][${i}].dispatchEvent(new MouseEvent('click', { bubbles: true, ctrlKey: true }))`);
        await page.waitFor(`!${$('hist-two')}.disabled`, 5000, 'Compare two');
        await page.evaluate(`${$('hist-two')}.click()`);
        await page.waitFor(`document.querySelector('.hist-diff-title')?.textContent.includes('and the version of')`, 5000, 'the two versions');
        await page.evaluate(`[...document.querySelectorAll('.hist-diff-head button')].find((b) => b.textContent === 'Close').click()`);
        // go back to the label put by hand: hist.py as it was then
        assert.equal(await put('changed after the label\n'), 204);
        await page.evaluate(`document.querySelector('#history-list .hist-label:not(.auto) button').click()`);
        await page.waitFor(`false || true`, 1000);
        const back = await page.waitFor(`fetch('/api/file?path=hist.py').then((r) => r.text()).then((t) => (t.includes('return 2') ? t : null))`, 15000, 'the file as it was at the label');
        assert.equal(back, 'def f():\n    return 2\n');
        assert.ok((await labels()).some((l) => l.auto && l.name.startsWith('Before going back to')), 'a label of the present was put first');
    });

    test('the project view lists the versions of every file, and the deleted ones can be filtered', async () => {
        await page.evaluate(`${$('hist-project')}.click()`);
        await page.waitFor(`document.getElementById('history-title').textContent.includes('Recent changes')`, 5000, 'the project history');
        await page.waitFor(`[...document.querySelectorAll('#history-list .hist-tag')].some((t) => t.textContent === 'hist.py')`, 10000, 'the file in the list');
        await page.evaluate(`(() => { const c = ${$('hist-deleted')}; c.checked = true; c.dispatchEvent(new Event('change')); })()`);
        await sleep(600);
        assert.equal(await page.evaluate(`[...document.querySelectorAll('#history-list .hist-tag')].filter((t) => t.textContent === 'hist.py').length`), 0);
        await page.evaluate(`(() => { const c = ${$('hist-deleted')}; c.checked = false; c.dispatchEvent(new Event('change')); })()`);
    });
});

describe('database', () => {
    const consoleEditor = `window.pymagic.monaco.editor.getEditors().find((e) => document.getElementById('db-sql').contains(e.getDomNode()))`;
    const grid = () => page.evaluate(`[...document.querySelectorAll('#db-grid tr')].map((tr) => [...tr.children].map((c) => c.textContent).join('|'))`);

    test('the project\'s SQLite file is listed with its tables, and a table\'s rows are shown and changed', async () => {
        await page.evaluate(`document.getElementById('t-database').click()`);
        await page.waitFor(`[...document.querySelectorAll('#db-tree .db-node')].some((n) => n.textContent.startsWith('▾shop.sqlite3'))`, 15000, 'the data source');
        await page.waitFor(`[...document.querySelectorAll('#db-tree .db-table')].some((n) => n.querySelector('.db-name').textContent === 'item')`, 15000, 'its table');
        await page.evaluate(`[...document.querySelectorAll('#db-tree .db-table')].find((n) => n.querySelector('.db-name').textContent === 'item').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))`);
        await page.waitFor(`document.getElementById('db-status').textContent.startsWith('item: rows 1–3 of 3')`, 15000, 'the rows');
        assert.deepEqual(await grid(), ['|id|name|price', '1|1|pen|2.0', '2|2|book|12.5', '3|3|cup|4.0']);
        // the price of the pen, changed and submitted
        await page.evaluate(`document.querySelectorAll('#db-grid tbody tr')[0].cells[3].dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))`);
        await page.evaluate(`(() => { const i = document.querySelector('#db-grid input.db-edit'); i.value = '9'; i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
        assert.match(await page.evaluate(`document.getElementById('db-status').textContent`), /1 changed, 0 added, 0 deleted \(not submitted\)/);
        await page.evaluate(`document.getElementById('db-submit').click()`);
        await page.waitFor(`document.querySelectorAll('#db-grid tbody tr')[0]?.cells[3].textContent === '9.0'`, 15000, 'the submitted value');
    });

    test('the console runs the statement at the cursor and completes the columns of its table', async () => {
        await page.evaluate(`document.getElementById('db-mode-console').click()`);
        await page.waitFor(`!!${consoleEditor}`, 10000, 'the console');
        await page.evaluate(`(() => { const ed = ${consoleEditor}; ed.setValue('SELECT 1;\\nSELECT name, price FROM item ORDER BY price DESC;'); ed.setPosition({ lineNumber: 2, column: 10 }); ed.focus(); })()`);
        await page.evaluate(`document.getElementById('db-run').click()`);
        await page.waitFor(`/^3 rows in/.test(document.getElementById('db-status').textContent)`, 15000, 'the result');
        assert.deepEqual(await grid(), ['|name|price', '1|book|12.5', '2|pen|9.0', '3|cup|4.0']);
        await page.evaluate(`(() => { const ed = ${consoleEditor}; ed.setValue('SELECT * FROM item WHERE pr'); ed.setPosition({ lineNumber: 1, column: 29 }); ed.focus(); ed.trigger('e2e', 'editor.action.triggerSuggest', {}); })()`);
        const labels = await page.waitFor(`[...document.querySelectorAll('#db-sql .suggest-widget .monaco-list-row')].map((r) => r.getAttribute('aria-label')).filter(Boolean).join(' | ') || null`, 10000, 'the suggestions');
        assert.match(labels, /price/);
        await page.evaluate(`(${consoleEditor}).trigger('e2e', 'hideSuggestWidget', {})`);
    });

    /** Answers the page's text prompt (the one askText opens) with `text`. */
    const answer = (text) => page.waitFor(`(() => { const box = document.querySelector('.ask-box input'); if (!box) return false; box.value = ${JSON.stringify(text)};
        box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); return true; })()`, 10000, 'the prompt');

    test('a .sql file of the project runs against the data source, statement by statement or whole', async () => {
        await open('queries.sql');
        assert.equal(await page.evaluate('window.pymagic.editor.getModel().getLanguageId()'), 'sql');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 1, column: 3 }); ed.focus(); return ed.getAction('pymagic.sql.runFile').run(); })()`);
        await page.waitFor(`/^3 statements, 2 rows, 2 changed in/.test(document.getElementById('db-status').textContent)`, 15000, 'the file run');
        assert.deepEqual(await grid(), ['|body', '1|one; two', '2|three']);
        // the table it made is in the tree now; a statement alone, at the cursor
        await page.waitFor(`[...document.querySelectorAll('#db-tree .db-table .db-name')].some((n) => n.textContent === 'note')`, 10000, 'the new table');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 3, column: 5 }); return ed.getAction('pymagic.sql.runStatement').run(); })()`);
        await page.waitFor(`/^2 rows in/.test(document.getElementById('db-status').textContent)`, 15000, 'the statement');
    });

    test('a CSV file is imported into the open table, and the grid is exported as a file', async () => {
        await page.evaluate(`document.getElementById('db-mode-data').click()`);
        await page.evaluate(`[...document.querySelectorAll('#db-tree .db-table')].find((n) => n.querySelector('.db-name').textContent === 'item').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))`);
        await page.waitFor(`document.getElementById('db-status').textContent.startsWith('item: rows 1–3 of 3')`, 15000, 'the rows');
        await page.evaluate(`document.getElementById('db-import').click()`);
        await answer('more_items.csv');
        await page.waitFor(`document.getElementById('db-status').textContent.startsWith('item: rows 1–5 of 5')`, 15000, 'the imported rows');
        assert.deepEqual((await grid()).slice(4), ['4|4|lamp|30.0', '5|5|mug|NULL']);
        // Export ▾ → Save as JSON…
        await page.evaluate(`document.getElementById('db-export').click()`);
        await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.trim().startsWith('Save as JSON')).click()`);
        await answer('export/items.json');
        const file = join(app.root, 'export', 'items.json');
        for (let i = 0; i < 50 && !existsSync(file); i++)
            await sleep(100);
        const saved = JSON.parse(readFileSync(file, 'utf8'));
        assert.equal(saved.length, 5);
        assert.deepEqual(saved[4], { id: 5, name: 'mug', price: null });
        assert.deepEqual(saved[0], { id: 1, name: 'pen', price: 9 });
    });
});

describe('HTTP client', () => {
    const status = () => page.evaluate(`document.getElementById('http-status').textContent`);
    const body = () => page.evaluate(`document.getElementById('http-body').textContent`);

    test('▶ Run above a request sends it with the environment\'s variables, and shows the response', async () => {
        await open('api.http');
        assert.equal(await page.evaluate('window.pymagic.editor.getModel().getLanguageId()'), 'http');
        await page.waitFor(`[...document.querySelectorAll('.codelens-decoration a')].filter((a) => a.textContent.includes('▶ Run')).length === 2`, 15000, 'the run marks');
        // a real click: Monaco runs a lens's command from its own mouse handling
        const at = await page.evaluate(`(() => { const r = [...document.querySelectorAll('.codelens-decoration a')].find((a) => a.textContent.includes('▶ Run')).getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
        await page.click(at.x, at.y);
        await page.waitFor(`/^200 OK · \\d+ ms/.test(document.getElementById('http-status').textContent)`, 15000, 'the response').catch(async (e) => { throw new Error(e.message + ': ' + await status() + ' / ' + await page.evaluate(`document.querySelector('.toast, #toast')?.textContent ?? ''`)); });
        assert.equal(await page.evaluate(`document.getElementById('http-env').value`), 'dev');
        const answer = JSON.parse(await body());
        assert.deepEqual(answer, { method: 'GET', path: '/items?page=2', auth: null, body: '' });
        assert.match(await body(), /^\{\n {2}"method"/);   // indented
        await page.evaluate(`document.querySelector('#http-parts button[data-part=headers]').click()`);
        assert.match(await body(), /X-Echo: yes/);
        await page.evaluate(`document.querySelector('#http-parts button[data-part=body]').click()`);
    });

    test('Ctrl+Enter runs the request at the cursor; the private environment\'s secrets fill it; the history keeps it', async () => {
        await open('api.http');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 11, column: 2 }); ed.focus(); return ed.getAction('pymagic.http.runAtCursor').run(); })()`);
        await page.waitFor(`document.getElementById('http-status').textContent.includes('POST')`, 15000, 'the response');
        assert.deepEqual(JSON.parse(await body()), { method: 'POST', path: '/items', auth: 'Bearer secret', body: '{"name": "pen"}' });
        assert.equal(await page.evaluate(`document.querySelectorAll('#http-history .http-item').length`), 2);
        const saved = JSON.parse(readFileSync(join(app.root, '.pymagic', 'http-history.json'), 'utf8'));
        assert.deepEqual(saved.map((h) => `${h.method} ${h.status}`), ['POST 200', 'GET 200']);
    });

    test('a pasted cURL command becomes a request', async () => {
        await open('api.http');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; const m = ed.getModel(); const end = m.getLineCount();
            ed.setPosition({ lineNumber: end, column: m.getLineMaxColumn(end) }); ed.trigger('keyboard', 'type', { text: '\\n###\\n' });
            ed.trigger('keyboard', 'paste', { text: "curl 'http://127.0.0.1/missing' -H 'Accept: text/plain' --data-raw 'a=1'" }); })()`);
        const text = await page.evaluate('window.pymagic.editor.getValue()');
        assert.match(text, /###\nPOST http:\/\/127\.0\.0\.1\/missing\nAccept: text\/plain\nContent-Type: application\/x-www-form-urlencoded\n\na=1\n$/);
        await page.evaluate(`(() => { const ed = window.pymagic.editor; while (ed.getValue().includes('/missing')) ed.trigger('e2e', 'undo', {}); })()`);
    });

    test('the environments are edited in the HTTP tab: a private variable is added and a value changed, and both files are written', async () => {
        const pubFile = join(app.root, 'http-client.env.json'), privFile = join(app.root, 'http-client.private.env.json');
        const before = [readFileSync(pubFile, 'utf8'), readFileSync(privFile, 'utf8')];
        try {
            await open('api.http');
            await page.evaluate(`${$('t-httpclient')}.click()`);
            await page.evaluate(`${$('http-env-edit')}.click()`);
            await page.waitFor(`document.querySelectorAll('.http-envs .http-env-row:not(.head)').length === 3`, 10000, 'the variables');
            const rows = () => page.evaluate(`[...document.querySelectorAll('.http-envs .http-env-row:not(.head)')].map((r) => { const [env, name, value, secret] = r.querySelectorAll('select, input'); return env.value + '.' + name.value + '=' + value.value + (secret.checked ? ' private ' + value.type : ''); })`);
            const shown = await rows();
            assert.ok(shown.includes('dev.token=secret private password'), shown.join(' | '));   // a secret is kept apart and not shown
            assert.ok(shown.some((r) => r.startsWith('prod.host=https://example.invalid')), shown.join(' | '));
            // the value of prod's host changed, and a private variable added to dev
            const set = (row, index, value) => page.evaluate(`(() => { const r = [...document.querySelectorAll('.http-envs .http-env-row:not(.head)')][${row}]; const e = r.querySelectorAll('select, input')[${index}];
                if (e.type === 'checkbox') { e.checked = ${JSON.stringify(value)}; e.dispatchEvent(new Event('change')); } else { e.value = ${JSON.stringify(value)}; e.dispatchEvent(new Event(e.tagName === 'SELECT' ? 'change' : 'input')); } return true; })()`);
            const prodRow = shown.findIndex((r) => r.startsWith('prod.host'));
            await set(prodRow, 2, 'https://prod.example.invalid');
            await page.evaluate(`[...document.querySelectorAll('.http-envs button')].find((b) => b.textContent === 'Add variable').click()`);
            await set(3, 0, 'dev');
            await set(3, 1, 'apiKey');
            await set(3, 2, 'k-1');
            await set(3, 3, true);
            await page.evaluate(`${$('http-env-save')}.click()`);
            await page.waitFor(`!document.querySelector('.http-envs')`, 5000, 'the dialog closed');
            const pub = JSON.parse(readFileSync(pubFile, 'utf8')), priv = JSON.parse(readFileSync(privFile, 'utf8'));
            assert.equal(pub.prod.host, 'https://prod.example.invalid');
            assert.ok(pub.dev.host.startsWith('http://127.0.0.1:'));
            assert.deepEqual(priv, { dev: { token: 'secret', apiKey: 'k-1' } });
            assert.ok(!('apiKey' in pub.dev) && !('token' in pub.dev), 'secrets only in the private file');
            assert.deepEqual(await page.evaluate(`[...document.getElementById('http-env').options].map((o) => o.value)`), ['', 'dev', 'prod']);
        }
        finally {
            writeFileSync(pubFile, before[0]);
            writeFileSync(privFile, before[1]);
            await page.evaluate(`document.querySelectorAll('.ask-shade').forEach((s) => s.remove())`);
        }
    });
});

describe('keyboard shortcuts in Settings > Keymap', () => {
    // a real key press in the focused page element (Chrome's own event, as a person's)
    const press = async (vk, key, code, modifiers) => {
        await page.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', windowsVirtualKeyCode: vk, key, code, modifiers });
        await page.send('Input.dispatchKeyEvent', { type: 'keyUp', windowsVirtualKeyCode: vk, key, code, modifiers });
    };
    const CTRL = 2, ALT = 1, SHIFT = 8;
    const openKeymap = async () => {
        await page.evaluate(`${$('b-gear')}.click()`);
        await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((d) => d.textContent.trim().startsWith('Settings…'))`, 5000, 'the gear\'s menu');
        await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.trim().startsWith('Settings…')).click()`);
        await page.waitFor(`document.querySelector('.settings-nav')`, 5000, 'the Settings window');
        await page.evaluate(`[...document.querySelectorAll('.settings-nav button')].find((b) => b.textContent === 'Keymap').click()`);
        await page.waitFor(`document.querySelectorAll('#keymap-list .keymap-row').length > 50`, 5000, 'the commands');
    };
    const rowOf = (id) => `document.querySelector('#keymap-list .keymap-row[data-id="${id}"]')`;
    const keysOf = (id) => page.evaluate(`[...${rowOf(id)}.querySelectorAll('.keymap-keys kbd')].map((k) => k.textContent).join(' ') || ${rowOf(id)}.querySelector('.keymap-keys').textContent`);
    const clickIn = (id, label) => page.evaluate(`[...${rowOf(id)}.querySelectorAll('button')].find((b) => b.textContent === ${JSON.stringify(label)}).click()`);
    const closeSettings = () => page.evaluate(`[...document.querySelectorAll('.settings-foot button')].find((b) => b.textContent === 'Done').click()`);
    after(() => page.evaluate(`window.pymagic.changeSetting({ keybindings: {} })`));

    test('every command is listed with its key; Ctrl+D is given to another command, and works there', async () => {
        await openKeymap();
        // Monaco's own Ctrl+D, found by a search
        await page.evaluate(`(() => { const s = document.getElementById('keymap-search'); s.value = 'next find match'; s.dispatchEvent(new Event('input')); })()`);
        await page.waitFor(rowOf('editor.action.addSelectionToNextFindMatch'), 5000, 'Ctrl+D\'s command');
        assert.equal(await keysOf('editor.action.addSelectionToNextFindMatch'), 'Ctrl+D');
        // PyMagic's own commands are there with their keys
        await page.evaluate(`(() => { const s = document.getElementById('keymap-search'); s.value = ''; s.dispatchEvent(new Event('input')); })()`);
        assert.equal(await keysOf('pymagic.run.debug'), 'F5');
        assert.equal(await keysOf('pymagic.run.toggleBreakpoint'), 'F9');
        // Copy line down gets Ctrl+D: the clash is said, and the key given to it
        await clickIn('editor.action.copyLinesDownAction', 'Change');
        await page.waitFor(`${rowOf('editor.action.copyLinesDownAction')}.textContent.includes('Press the new keys')`, 5000, 'waiting for the keys');
        await press(68, 'd', 'KeyD', CTRL);
        await page.waitFor(`document.querySelector('.keymap-clash')?.textContent.includes('Ctrl+D is the key of')`, 5000, 'the clash');
        await page.evaluate(`[...document.querySelectorAll('.keymap-clash button')].find((b) => b.textContent === 'Give it to this one').click()`);
        await page.waitFor(`${rowOf('editor.action.copyLinesDownAction')}?.textContent.includes('Ctrl+D')`, 5000, 'the new key shown');
        assert.equal(await keysOf('editor.action.addSelectionToNextFindMatch'), 'none');
        // kept in the settings (written a moment after the change)
        const kept = await page.waitFor(`fetch('/api/settings').then((r) => r.json()).then((s) => Object.keys(s.keybindings ?? {}).length === 2 && s.keybindings)`, 5000, 'the keys kept');
        assert.deepEqual(kept, { 'editor.action.copyLinesDownAction': 'Ctrl+D', 'editor.action.addSelectionToNextFindMatch': '' });
        // PyMagic's Matching brace from Ctrl+Shift+M to Alt+M
        await clickIn('pymagic.nav.matchingBrace', 'Change');
        await press(77, 'm', 'KeyM', ALT);
        await page.waitFor(`${rowOf('pymagic.nav.matchingBrace')}.querySelector('kbd')?.textContent === 'Alt+M'`, 5000, 'Alt+M shown');
        await closeSettings();

        // in the editor: Ctrl+D copies the line now
        await open('calc.py');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 1, column: 1 }); ed.focus(); return true; })()`);
        const lines = () => page.evaluate(`window.pymagic.editor.getModel().getLineCount()`);
        const before = await lines();
        await press(68, 'd', 'KeyD', CTRL);
        await page.waitFor(`window.pymagic.editor.getModel().getLineCount() === ${before + 1}`, 5000, 'the line copied by Ctrl+D');
        assert.equal(await page.evaluate(`window.pymagic.editor.getModel().getLineContent(2)`), 'import json');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.trigger('e2e', 'undo', {}); return true; })()`);
        // Alt+M goes to the matching bracket; Ctrl+Shift+M no longer does
        const line = await page.evaluate(`window.pymagic.editor.getModel().getLinesContent().findIndex((l) => l.startsWith('def total(')) + 1`);
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: ${line}, column: 10 }); ed.focus(); return true; })()`);
        await press(77, 'm', 'KeyM', CTRL | SHIFT);
        await sleep(300);
        assert.equal(await page.evaluate(`window.pymagic.editor.getPosition().column`), 10, 'the old key does nothing now');
        await press(77, 'm', 'KeyM', ALT);
        await page.waitFor(`window.pymagic.editor.getPosition().column !== 10`, 5000, 'Alt+M at the matching bracket');

        // an action PyMagic gave the editor (Surround With…, Ctrl+Alt+T) moved to Ctrl+Alt+Y: the old key does nothing, the new one opens it
        const keys = await page.evaluate(`fetch('/api/settings').then((r) => r.json()).then((s) => s.keybindings)`);
        await page.evaluate(`window.pymagic.changeSetting({ keybindings: { ...${JSON.stringify(keys)}, 'pymagic.surroundWith': 'Ctrl+Alt+Y' } })`);
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 1, column: 1 }); ed.focus(); return true; })()`);
        await press(84, 't', 'KeyT', CTRL | ALT);
        await sleep(400);
        assert.equal(await page.evaluate(`!!document.querySelector('.ctx-menu')`), false, 'Ctrl+Alt+T no longer opens Surround With');
        await press(89, 'y', 'KeyY', CTRL | ALT);
        await page.waitFor(`document.querySelector('.ctx-menu')`, 5000, 'Surround With on Ctrl+Alt+Y');
        await page.evaluate(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))`);
        await page.evaluate(`document.querySelectorAll('.ctx-menu').forEach((m) => m.remove())`);

        // Reset: back to the keys they start with
        await openKeymap();
        for (const id of ['editor.action.copyLinesDownAction', 'editor.action.addSelectionToNextFindMatch', 'pymagic.nav.matchingBrace', 'pymagic.surroundWith']) await clickIn(id, 'Reset');
        assert.equal(await keysOf('pymagic.surroundWith'), 'Ctrl+Alt+T');
        assert.equal(await keysOf('editor.action.addSelectionToNextFindMatch'), 'Ctrl+D');
        assert.equal(await keysOf('pymagic.nav.matchingBrace'), 'Ctrl+Shift+M');
        await page.waitFor(`fetch('/api/settings').then((r) => r.json()).then((s) => Object.keys(s.keybindings ?? {}).length === 0)`, 5000, 'no change kept');
        // the keys of the window are listed too, and can be changed like the others
        assert.equal(await keysOf('pymagic.window.settings'), 'Ctrl+,');
        assert.equal(await keysOf('pymagic.window.terminal'), 'Ctrl+`');
        assert.equal(await keysOf('pymagic.tabs.switcher'), 'Ctrl+Tab');
        await closeSettings();
    });

    test('the window\'s own keys change too: Settings on Ctrl+Alt+S, the Terminal on Alt+F12, and the menus show the new keys', async () => {
        await page.evaluate(`window.pymagic.changeSetting({ keybindings: { 'pymagic.window.settings': 'Ctrl+Alt+S', 'pymagic.window.terminal': 'Alt+F12' } })`);
        let panesBefore = Infinity;
        try {
            // the gear's menu names the new key of Settings…
            await page.evaluate(`${$('b-gear')}.click()`);
            const row = await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.includes('Settings…'))?.textContent`, 5000, 'the gear\'s menu');
            assert.ok(row.includes('Ctrl+Alt+S') && !row.includes('Ctrl+,'), row);
            await page.evaluate(`document.querySelectorAll('.ctx-menu').forEach((m) => m.remove())`);
            // outside the editors: the old key does nothing, the new one opens Settings
            await page.evaluate(`(document.activeElement?.blur?.(), document.body.focus(), true)`);
            await press(188, ',', 'Comma', CTRL);
            await sleep(300);
            assert.equal(await page.evaluate(`!!document.querySelector('.settings-nav')`), false, 'Ctrl+, no longer opens Settings');
            await press(83, 's', 'KeyS', CTRL | ALT);
            await page.waitFor(`document.querySelector('.settings-nav')`, 5000, 'Settings on Ctrl+Alt+S');
            await closeSettings();
            // Alt+F12 opens the Terminal panel (a terminal it starts is closed again below: the Services window lists running terminals)
            panesBefore = await page.evaluate(`document.querySelectorAll('.term-pane').length`);
            await page.evaluate(`${$('t-output')}.click()`);
            await page.evaluate(`(document.activeElement?.blur?.(), document.body.focus(), true)`);
            await press(123, 'F12', 'F12', ALT);
            await page.waitFor(`document.getElementById('t-terminal').classList.contains('active')`, 5000, 'the Terminal on Alt+F12');
        }
        finally {
            await page.evaluate(`window.pymagic.changeSetting({ keybindings: {} })`);
            await page.evaluate(`document.querySelectorAll('.ctx-menu, .settings-shade').forEach((m) => m.remove())`);
            // the terminal Alt+F12 started is stopped where the Services window lists it (the ✕ of a tab's only pane keeps it)
            if (panesBefore === 0) {
                await page.evaluate(`${$('t-services')}.click()`);
                await sleep(400);
                await page.evaluate(`[...document.querySelectorAll('#services-list .svc-row')].filter((r) => r.textContent.startsWith('Terminal')).forEach((r) => [...r.querySelectorAll('button')].find((b) => b.textContent === 'Stop')?.click())`);
                await page.waitFor(`![...document.querySelectorAll('#services-list .svc-row')].some((r) => r.textContent.startsWith('Terminal'))`, 5000, 'the terminal stopped').catch(() => { });
            }
            await page.evaluate(`${$('t-output')}.click()`);
        }
    });
});

describe('settings that live in the app', () => {
    test('an ignored import is listed in Settings > Inspections and reported again from there', async () => {
        const file = join(app.root, '.pymagic', 'ignored-imports.json');
        mkdirSync(join(app.root, '.pymagic'), { recursive: true });
        writeFileSync(file, JSON.stringify(['vendored_e2e_tool']));
        // the gear's menu, then Settings… in it
        await page.evaluate(`${$('b-gear')}.click()`);
        await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((d) => d.textContent.trim().startsWith('Settings…'))`, 5000, 'the gear\'s menu');
        await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.trim().startsWith('Settings…')).click()`);
        await page.waitFor(`document.querySelector('.settings-nav')`, 5000, 'the Settings window');
        await page.evaluate(`[...document.querySelectorAll('.settings-nav button')].find((b) => b.textContent === 'Inspections').click()`);
        await page.waitFor(`document.querySelector('#set-ignored-imports code')?.textContent === 'vendored_e2e_tool'`, 10000, 'the ignored import listed');
        await page.evaluate(`[...document.querySelectorAll('#set-ignored-imports button')].find((b) => b.textContent === 'Report again').click()`);
        await page.waitFor(`document.getElementById('set-ignored-imports').textContent === 'None'`, 10000, 'the list empty');
        assert.equal(existsSync(file), false, 'the file is gone with the last one');
        await page.evaluate(`[...document.querySelectorAll('.settings-foot button')].find((b) => b.textContent === 'Done').click()`);
    });
});

describe('intention actions', () => {
    /** The quick-fix menu at a place, as titles; one of them can be chosen with a real click. */
    async function menuAt(line, column, choose) {
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: ${line}, column: ${column} }); ed.focus(); ed.getAction('editor.action.quickFix').run(); })()`);
        const titles = await page.waitFor(`(() => { const rows = [...document.querySelectorAll('.action-widget .monaco-list-row')]; return rows.length ? rows.map((r) => r.textContent.trim()) : null; })()`, 15000, 'the quick-fix menu');
        if (choose) {
            const at = await page.evaluate(`(() => { const r = [...document.querySelectorAll('.action-widget .monaco-list-row')].find((x) => x.textContent.includes(${JSON.stringify(choose)})); const b = r.getBoundingClientRect(); return { x: b.left + 20, y: b.top + b.height / 2 }; })()`);
            await page.click(at.x, at.y);
            await sleep(500);
        } else {
            await page.key(27, 'Escape');
        }
        return titles;
    }

    test('Alt+Enter at a concatenation offers an f-string and applies it', async () => {
        await open('intent.py');
        const titles = await menuAt(2, 16, 'Convert to an f-string');
        assert.ok(titles.some((t) => t.includes('Convert to an f-string')), titles.join(' | '));
        assert.equal(await page.evaluate(`window.pymagic.editor.getModel().getLineContent(2)`), "print(f'Hello {name}!')");
        await page.evaluate(`window.pymagic.editor.trigger('e2e', 'undo', {})`);
    });

    test('a number is offered in other bases', async () => {
        await open('intent.py');
        const titles = await menuAt(3, 10);
        assert.ok(titles.some((t) => t.includes('Convert to hexadecimal (0xFF)')), titles.join(' | '));
    });
});

describe('file checks', () => {
    const problems = (file) => open(file).then(() => page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); const found = window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).filter((x) => x.source === 'PyMagic' && /^(toml|yaml|md|re)-/.test(x.code)); return found.length ? found.map((x) => x.code + '@' + x.startLineNumber + ':' + x.startColumn) : null; })()`, 20000, 'the problems of ' + file));

    test('TOML and YAML syntax problems are marked', async () => {
        assert.deepEqual(await problems('broken.toml'), ['toml-duplicate-key@3:1', 'toml-value@4:8']);
        assert.deepEqual(await problems('broken.yml'), ['yaml-duplicate-key@3:1']);
    });

    test('package.json, pyproject.toml and a GitHub workflow are checked against the project copies of their schemas', async () => {
        const schema = (file) => open(file).then(() => page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); const found = window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).filter((x) => x.code === 'schema'); return found.length ? found.map((x) => x.startLineNumber + ':' + x.startColumn + ' ' + x.message) : null; })()`, 20000, 'the schema problems of ' + file));
        assert.deepEqual(await schema('package.json'), ['2:3 Should match ^[a-z][a-z0-9-]*$', '4:5 Should be string, not integer']);
        assert.deepEqual(await schema('pyproject.toml'), ['2:1 Should be string, not integer']);
        assert.deepEqual(await schema('.github/workflows/ci.yml'), ['4:5 Should be string, not integer']);
        assert.deepEqual(await schema('docker-compose.yml'), ['3:5 Should be string, not integer', '4:5 The property extra is not allowed here']);
        assert.deepEqual(await schema('.gitlab-ci.yml'), ['1:1 Should be array, not string']);
        assert.deepEqual(await schema('.pre-commit-config.yaml'), ['1:1 Should be boolean, not string']);
        assert.deepEqual(await schema('mkdocs.yml'), ['1:1 Should be string, not integer']);
        assert.deepEqual(await schema('tox.toml'), ['1:1 Should be array, not string']);
    });

    test('Renovate, OpenAPI and a Kubernetes manifest (the second document of a file) are checked against the project copies of their schemas', async () => {
        const schema = (file) => open(file).then(() => page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); const found = window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).filter((x) => x.code === 'schema'); return found.length ? found.map((x) => x.startLineNumber + ':' + x.startColumn + ' ' + x.message) : null; })()`, 20000, 'the schema problems of ' + file));
        assert.deepEqual(await schema('renovate.json'), ['2:3 Should be array, not integer']);
        assert.deepEqual(await schema('openapi.yaml'), ['2:1 The property version is required']);
        assert.deepEqual(await schema('k8s.yaml'), ['10:3 Should be integer, not string']);
        // OpenAPI 3.1 is not checked (its schema needs $dynamicRef)
        await open('openapi31.yaml');
        await new Promise((r) => setTimeout(r, 2500));
        assert.deepEqual(await page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ resource: window.pymagic.editor.getModel().uri }).filter((x) => x.code === 'schema').length`), 0);
    });

    test('without a kept copy, a file is checked against the schema built into PyMagic, and nothing is asked or downloaded', async () => {
        await page.evaluate(`window.__confirms = 0; window.confirm = () => { window.__confirms++; return false; }`);
        const schema = (file) => open(file).then(() => page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); const found = window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).filter((x) => x.code === 'schema'); return found.length ? found.map((x) => x.startLineNumber + ' ' + x.message) : null; })()`, 20000, 'the schema problems of ' + file));
        const dependabot = await schema('.github/dependabot.yml');
        assert.equal(dependabot.length, 1);
        assert.match(dependabot[0], /^1 /);
        // a Kubernetes kind with no schema of its own in the project: its definition from the built-in ones
        assert.deepEqual(await schema('infra/deploy.yaml'), ['6 Should be integer, not string']);
        assert.equal(await page.evaluate('window.__confirms'), 0);
        assert.ok(!existsSync(join(projectRoot, '.pymagic', 'schemas', 'dependabot.json')) && !existsSync(join(projectRoot, '.pymagic', 'schemas', 'k8s-deployment-apps-v1.json')), 'nothing downloaded into the project');
    });

    test('a Markdown link to a missing file is marked, an anchor that exists is not', async () => {
        assert.deepEqual(await problems('notes.md'), ['md-link@3:17', 'md-anchor@5:5']);
    });

    test('a redundant escape and a repeated class character in re.compile are marked', async () => {
        assert.deepEqual(await problems('rx.py'), ['re-class-repeat@2:26', 're-escape@2:28', 're-class-repeat@3:37']);
    });
});

describe('profiler', () => {
    test('Profile lists each function with what it called, draws the call graph and keeps the run', async () => {
        await open('busy.py');
        await page.evaluate(`document.getElementById('b-profile').click()`);
        await page.waitFor(`document.getElementById('profiler-tab').classList.contains('active') && document.getElementById('prof-summary').textContent.startsWith('busy.py')`, 30000, 'the profile');
        await page.evaluate(`[...document.querySelectorAll('#prof-list .prof-row')].find((r) => r.querySelector('.prof-name')?.textContent === 'outer').click()`);
        const links = await page.waitFor(`document.querySelector('#prof-list .prof-links')?.textContent`, 5000, 'the links');
        assert.match(links, /called from \(1\):<module>/);
        assert.match(links, /calls \(\d+\):.*inner.*40×/);
        await page.evaluate(`document.getElementById('prof-graph').click()`);
        const nodes = await page.evaluate(`[...document.querySelectorAll('#prof-list svg .prof-node-name')].map((t) => t.textContent)`);
        assert.ok(nodes.includes('outer') && nodes.includes('inner') && nodes.includes('<module>'), nodes.join(', '));
        // a click on inner's box: the graph goes to it
        await page.evaluate(`[...document.querySelectorAll('#prof-list svg .prof-node')].find((g) => g.querySelector('.prof-node-name').textContent === 'inner').dispatchEvent(new MouseEvent('click', { bubbles: true }))`);
        assert.equal(await page.evaluate(`document.querySelector('#prof-list svg .prof-node.center .prof-node-name').textContent`), 'inner');
        await page.evaluate(`document.getElementById('prof-graph').click()`);
        const kept = await page.waitFor(`document.getElementById('prof-snapshot').options.length > 1 && document.getElementById('prof-snapshot').options[1].textContent`, 5000, 'the snapshot');
        assert.match(kept, / · busy$/);
    });
});

// last: choosing a configuration (Run Anything does) stays chosen for the runs that follow
describe('an interpreter of its own for a run configuration', () => {
    const runAnything = async (text) => {
        await page.evaluate(`window.pymagic.editor.getAction('pymagic.runAnything').run()`);
        await page.waitFor(`document.querySelector('.ra-box input')`, 10000, 'the box');
        await page.evaluate(`(() => { const i = document.querySelector('.ra-box input'); i.value = ${JSON.stringify(text)}; i.dispatchEvent(new Event('input')); i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
    };

    test('the run uses the interpreter the configuration names, not the project\'s', async () => {
        await open('which.py');
        await runAnything('which python');
        await page.waitFor(`${$('output')}.textContent.includes('exe ')`, 30000, 'the program\'s output');
        const out = await page.evaluate(`${$('output')}.textContent`);
        assert.ok(out.toLowerCase().includes('altenv'), out);
        await page.waitFor(`${$('b-stop')}.disabled`, 20000, 'the run to end');
    });

    test('an interpreter that is not there is told, and nothing runs', async () => {
        await runAnything('missing python');
        await page.waitFor(`${$('output')}.textContent.includes('The interpreter of this configuration is not there')`, 20000, 'the message');
        assert.ok(await page.evaluate(`${$('b-stop')}.disabled`));
    });

    test('the configuration\'s form offers the interpreters of the project', async () => {
        const options = await page.evaluate(`fetch('/api/interpreters').then((r) => r.json()).then((l) => l.length)`);
        assert.ok(options >= 1);
    });
});

// last: this chooses the compound configuration, which stays chosen
describe('a compound run configuration in the Services window', () => {
    const rows = () => page.evaluate(`[...document.querySelectorAll('#services-list .svc-row')].map((r) => r.querySelector('.svc-name').textContent + '|' + r.querySelector('.svc-state').textContent.split(' ')[0])`);
    const head = (label) => page.evaluate(`[...document.querySelectorAll('#services-list .svc-group button')].find((b) => b.textContent === ${JSON.stringify(label)}).click()`);
    const rowButton = (name, label) => page.evaluate(`[...document.querySelectorAll('#services-list .svc-row')].find((r) => r.querySelector('.svc-name').textContent === ${JSON.stringify(name)}).querySelectorAll('button').forEach((b) => b.textContent === ${JSON.stringify(label)} && b.click())`);
    const states = async () => (await rows()).join(',');

    test('Run starts every member, each its own row; one is stopped alone, then all, then all are started again', async () => {
        await page.evaluate(`window.pymagic.editor.getAction('pymagic.runAnything').run()`);
        await page.waitFor(`document.querySelector('.ra-box input')`, 10000, 'the box');
        await page.evaluate(`(() => { const i = document.querySelector('.ra-box input'); i.value = 'loop pair'; i.dispatchEvent(new Event('input')); i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
        await page.waitFor(`${$('output')}.textContent.includes('[loop a] started') && ${$('output')}.textContent.includes('[loop b] started')`, 30000, 'both members to print');
        await page.evaluate(`${$('t-services')}.click()`);
        await page.waitFor(`document.querySelectorAll('#services-list .svc-row').length === 2`, 10000, 'two rows');
        assert.deepEqual((await rows()).sort(), ['loop a|running', 'loop b|running']);
        assert.equal(await page.evaluate(`document.getElementById('services-count').textContent`), '2');
        assert.ok(await page.evaluate(`document.querySelector('#services-list .svc-group').textContent.includes('loop pair')`));
        // one member stopped by itself: the other goes on
        await rowButton('loop a', 'Stop');
        await page.waitFor(`[...document.querySelectorAll('#services-list .svc-row')].some((r) => r.querySelector('.svc-name').textContent === 'loop a' && r.querySelector('.svc-state').textContent.startsWith('finished'))`, 20000, 'loop a to finish');
        assert.ok((await rows()).includes('loop b|running'), await states());
        assert.equal(await page.evaluate(`document.getElementById('services-count').textContent`), '1');
        // all stopped, all started again
        await head('Stop all');
        await page.waitFor(`[...document.querySelectorAll('#services-list .svc-state')].every((s) => s.textContent.startsWith('finished'))`, 20000, 'all to finish');
        await page.evaluate(`${$('output')}.textContent = ''`);
        await head('Start all');
        await page.waitFor(`${$('output')}.textContent.includes('[loop a] started') && ${$('output')}.textContent.includes('[loop b] started')`, 30000, 'both started again');
        await page.waitFor(`[...document.querySelectorAll('#services-list .svc-state')].length === 2 && [...document.querySelectorAll('#services-list .svc-state')].every((s) => s.textContent.startsWith('running'))`, 10000, 'both rows running');
        // a restart of one
        await page.evaluate(`${$('output')}.textContent = ''`);
        await rowButton('loop b', 'Restart');
        await page.waitFor(`${$('output')}.textContent.includes('[loop b] started')`, 30000, 'loop b to start again');
        assert.ok(!(await page.evaluate(`${$('output')}.textContent`)).includes('[loop a] started'));
        // the Stop button of the toolbar stops them all; Close takes the group away
        await page.evaluate(`${$('b-stop')}.click()`);
        await page.waitFor(`[...document.querySelectorAll('#services-list .svc-state')].every((s) => s.textContent.startsWith('finished'))`, 20000, 'all to finish again');
        await head('Close');
        await page.waitFor(`document.querySelectorAll('#services-list .svc-row').length === 0`, 10000, 'the group to be gone');
    });

    test('Debug puts every member under the debugger: both stop at a breakpoint, one stop at a time by its name, and Stop ends them all', async () => {
        await open('loop.py');
        assert.equal(await clickMargin(7), true, 'a breakpoint on `count += 1`');
        await page.evaluate(`${$('output')}.textContent = ''`);
        await page.evaluate(`${$('b-debug')}.click()`);   // the compound is the chosen configuration
        await page.waitFor(`${$('output')}.textContent.includes('every member runs under the debugger')`, 30000, 'the debug server of the compound');
        await page.waitFor(`/connected from loop a/.test(${$('output')}.textContent) && /connected from loop b/.test(${$('output')}.textContent)`, 30000, 'both members connected by name');
        assert.equal(await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 30000, 'a stop'), 'spin loop.py:7');
        await page.evaluate(`${$('t-services')}.click()`);
        // the members' rows (the Run panel's debug server is a row of its own)
        const members = async () => (await rows()).filter((r) => r.startsWith('loop ')).sort();
        for (let i = 0; i < 40 && (await members()).length < 2; i++) await sleep(250);
        assert.deepEqual(await members(), ['loop a|debugging', 'loop b|debugging']);
        // the other member waits at the breakpoint too: with the breakpoint gone, Resume brings its stop up
        await open('loop.py');
        assert.equal(await clickMargin(7), false, 'the breakpoint taken away');
        await page.evaluate(`${$('output')}.textContent = ''`);
        await page.evaluate(`${$('b-debug')}.click()`);   // Resume
        await page.waitFor(`/Program \\d \\(loop [ab]\\) is stopped/.test(${$('output')}.textContent)`, 20000, "the other member's stop, by its name");
        assert.equal(await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 20000, 'the second stop'), 'spin loop.py:7');
        await page.evaluate(`${$('b-debug')}.click()`);   // Resume: both run on
        await page.waitFor(`${$('b-over')}.disabled`, 10000, 'both running');
        // Stop ends the debugger and every member, as for a compound that runs
        await page.evaluate(`${$('b-stop')}.click()`);
        await page.waitFor(`${$('output')}.textContent.includes('The debugger of loop pair is stopped')`, 20000, 'the debugger stopped');
        await page.evaluate(`${$('t-services')}.click()`);
        for (let i = 0; i < 80 && (await members()).join() !== 'loop a|finished,loop b|finished'; i++) await sleep(250);
        assert.deepEqual(await members(), ['loop a|finished', 'loop b|finished']);
        await head('Close');
    });
});

// last: this chooses the debug server configuration, which stays chosen
describe('a debug server a program started elsewhere connects back to', () => {
    test('the snippet is shown; a program that runs it stops at the IDE\'s breakpoint, steps, shows its variables and runs on after Resume', async () => {
        await open('remote_job.py');
        assert.equal(await clickMargin(2), true, 'a breakpoint on `total = n * 2`');
        await page.evaluate(`window.pymagic.editor.getAction('pymagic.runAnything').run()`);
        await page.waitFor(`document.querySelector('.ra-box input')`, 10000, 'the box');
        await page.evaluate(`(() => { const i = document.querySelector('.ra-box input'); i.value = 'dbg server'; i.dispatchEvent(new Event('input')); i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
        const snippet = await page.waitFor(`document.getElementById('debugserver-snippet')?.textContent`, 20000, 'the snippet');
        assert.match(snippet, /import pm_debug\r?\npm_debug\.connect\('127\.0\.0\.1', \d+, '[0-9a-f]{24}', root=/);
        const helper = /copy pm_debug\.py from (.+?) to that machine/.exec(await page.evaluate(`document.querySelector('.debugserver-info .dim').textContent`))?.[1];
        assert.ok(helper, 'the folder of pm_debug.py is named');
        assert.match(await output(), /Debug server listening on 127\.0\.0\.1:\d+/);
        assert.equal(await page.evaluate(`${$('runstate')}.textContent`), 'waiting for a program…');
        await page.evaluate(`document.querySelector('#sigdialog button.primary').click()`);

        // the program, on its own: the snippet, then the project's file
        const dir = tempDir('pymagic-e2e-remote-');
        writeFileSync(join(dir, 'launcher.py'), `import sys\nsys.path.insert(0, ${JSON.stringify(helper)})\n${snippet}\nimport runpy\nrunpy.run_path(${JSON.stringify(join(projectRoot, 'remote_job.py'))}, run_name='__main__')\n`);
        const program = spawn('python', ['-u', 'launcher.py'], { cwd: dir });
        let printed = '';
        program.stdout.on('data', (d) => { printed += d; });
        const exited = new Promise((ok) => program.on('exit', ok));
        try {
            assert.equal(await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 30000, 'the stop'), 'work remote_job.py:2');
            // the console names the program (numbered, as the server holds several at once)
            await page.waitFor(`/(A program|Program 1) connected from 127\\.0\\.0\\.1/.test(${$('output')}.textContent)`, 10000, 'the connect line');
            assert.match(await page.waitFor(`${$('vars')}.textContent.includes('n = 21') && ${$('vars')}.textContent`, 10000, 'the variables'), /n = 21/);
            await page.evaluate(`${$('b-over')}.click()`);
            assert.equal(await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent.startsWith('work') && document.querySelector('#frames div.sel').textContent.endsWith(':3') && document.querySelector('#frames div.sel').textContent`, 10000, 'the step'), 'work remote_job.py:3');
            assert.match(await page.waitFor(`${$('vars')}.textContent.includes('total = 42') && ${$('vars')}.textContent`, 10000, 'the new variable'), /total = 42/);
            await page.evaluate(`${$('b-debug')}.click()`);   // Resume
            await Promise.race([exited, sleep(20000)]);
            assert.match(printed, /job 42/);
            await page.waitFor(`${$('output')}.textContent.includes('The program disconnected')`, 15000, 'the disconnect');
            assert.equal(await page.evaluate(`${$('runstate')}.textContent`), 'waiting for a program…');
            // Stop ends the listening
            await page.evaluate(`${$('b-stop')}.click()`);
            await page.waitFor(`${$('b-stop')}.disabled`, 15000, 'the end of the debug server');
            assert.match(await output(), /Debug server stopped/);
        } finally {
            program.kill();
        }
        assert.equal(await clickMargin(2), false, 'a second click takes the breakpoint away');
    });
});

// last: the consoles use the project's interpreter, which the check above sets to a container when Docker is here, so this runs before it
describe('the Python console: several consoles, a queue of commands, IPython', () => {
    const type = (code) => page.evaluate(`(() => { const i = document.getElementById('pyc-input'); i.value = ${JSON.stringify(code)}; i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
    const shown = () => page.evaluate(`[...document.querySelectorAll('.pyc-out')].filter((o) => !o.hidden).map((o) => o.textContent).join('')`);
    const vars = () => page.evaluate(`[...document.querySelectorAll('#pyc-vars .pyc-name')].map((n) => n.textContent).join(',')`);

    test('a command given while another runs waits in the queue, can be taken out, and the rest run in order', async () => {
        await page.evaluate(`document.getElementById('t-pyconsole').click()`);
        await page.waitFor(`[...document.querySelectorAll('.pyc-out')].some((o) => !o.hidden && o.textContent.includes('Python 3'))`, 30000, 'the console banner');
        await type('import time; time.sleep(4)');
        await sleep(600);
        await type('first = 1');
        await type('second = 2');
        await page.waitFor(`document.querySelectorAll('#pyc-queue .pyc-wait').length === 2`, 5000, 'two waiting commands');
        assert.equal(await page.evaluate(`document.getElementById('pyc-queue').hidden`), false);
        assert.match(await page.evaluate(`document.getElementById('pyc-queue').textContent`), /Running import time; time\.sleep\(4\)/);
        // the first waiting one is taken out
        await page.evaluate(`document.querySelector('#pyc-queue .pyc-wait button').click()`);
        await page.waitFor(`document.querySelectorAll('#pyc-queue .pyc-wait').length === 1`, 5000, 'one waiting command');
        await page.waitFor(`document.getElementById('pyc-queue').hidden`, 30000, 'the queue to empty');
        await page.waitFor(`[...document.querySelectorAll('#pyc-vars .pyc-name')].some((n) => n.textContent === 'second')`, 15000, 'the second command to run');
        assert.equal(await vars(), 'second');
        const text = await shown();
        assert.ok(text.includes('>>> second = 2') && !text.includes('first = 1'), text);
    });

    test('another console has names of its own; the tabs switch between them', async () => {
        await page.evaluate(`document.getElementById('pyc-new').click()`);
        await page.waitFor(`document.querySelectorAll('#pyc-tabs .ttab').length === 2 && [...document.querySelectorAll('.pyc-out')].some((o) => !o.hidden && o.textContent.includes('Python 3'))`, 30000, 'the second console');
        assert.equal(await page.evaluate(`[...document.querySelectorAll('#pyc-tabs .ttab-name')].map((n) => n.textContent).join('|')`), 'Python console|Python console (2)');
        assert.equal(await vars(), '');
        await type('other = 3');
        await page.waitFor(`[...document.querySelectorAll('#pyc-vars .pyc-name')].some((n) => n.textContent === 'other')`, 15000, 'the name in the second console');
        await page.evaluate(`document.querySelector('#pyc-tabs .ttab').click()`);
        await page.waitFor(`[...document.querySelectorAll('#pyc-vars .pyc-name')].map((n) => n.textContent).join(',') === 'second'`, 5000, 'the first console\'s names again');
        assert.ok((await shown()).includes('second = 2'));
        // closing the second one leaves the first
        await page.evaluate(`document.querySelectorAll('#pyc-tabs .ttab-close')[1].click()`);
        await page.waitFor(`document.querySelectorAll('#pyc-tabs .ttab').length === 1`, 5000, 'one tab');
        assert.equal(await vars(), 'second');
    });

    test('IPython ticked: the console starts again and says so when IPython is not in the project\'s Python', async () => {
        const has = spawnSync('python', ['-c', 'import IPython'], { encoding: 'utf8' }).status === 0;
        await page.evaluate(`document.getElementById('pyc-ipython').click()`);
        await page.waitFor(`[...document.querySelectorAll('.pyc-out')].some((o) => !o.hidden && o.textContent.includes(${JSON.stringify(has ? 'IPython 8' : 'IPython is not installed')}))`, 30000, 'the IPython note');
        assert.equal(await vars(), '');
        await type('v = 6 * 7');
        await type('v');
        await page.waitFor(`[...document.querySelectorAll('.pyc-out')].some((o) => !o.hidden && o.textContent.includes('42'))`, 15000, 'the value');
        await page.evaluate(`document.getElementById('pyc-ipython').click()`);
        await page.waitFor(`!document.getElementById('pyc-ipython').checked`, 5000, 'the box unticked');
    });
});

// last: the project's interpreter becomes a running container
describe('Linux paths of a remote interpreter, a running container, and a terminal inside it', () => {
    const skip = hasDocker ? false : 'Docker or the python:3.12-slim image is not on this PC';
    const runAnything = async (text) => {
        await page.evaluate(`window.pymagic.editor.getAction('pymagic.runAnything').run()`);
        await page.waitFor(`document.querySelector('.ra-box input')`, 10000, 'the box');
        await page.evaluate(`(() => { const i = document.querySelector('.ra-box input'); i.value = ${JSON.stringify(text)}; i.dispatchEvent(new Event('input')); i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
    };
    before(() => {
        if (hasDocker) {
            const made = spawnSync('docker', ['run', '-d', '--name', CONTAINER, '-v', `${projectRoot}:/workspace`, 'python:3.12-slim', 'sleep', '900'], { encoding: 'utf8' });
            assert.equal(made.status, 0, String(made.stderr));
        }
    });
    after(() => { if (hasDocker) spawnSync('docker', ['rm', '-f', CONTAINER]); });

    // the traceback names /workspace/tb_boom.py; the link opens the project's file at that line
    const linkOpens = async (config) => {
        await open('calc.py');
        await page.evaluate(`${$('output')}.textContent = ''`);
        await runAnything(config);
        await page.waitFor(`${$('output')}.textContent.includes('ValueError: boom')`, 90000, 'the traceback');
        await page.waitFor(`${$('b-stop')}.disabled`, 30000, 'the run to end');
        assert.match(await output(), /File "\/workspace\/tb_boom\.py", line 2, in explode/);
        const links = await page.evaluate(`[...document.querySelectorAll('#output .tb-link')].map((l) => l.dataset.rel + ':' + l.dataset.line)`);
        assert.deepEqual(links, ['tb_boom.py:5', 'tb_boom.py:2']);
        await page.evaluate(`[...document.querySelectorAll('#output .tb-link')].pop().click()`);
        await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('/tb_boom.py') && window.pymagic.editor.getPosition().lineNumber === 2`, 15000, 'the file at line 2');
    };

    test('the traceback of a Docker image run links /workspace paths back to the project', { skip }, async () => {
        await linkOpens('docker boom');
    });

    test('a container that is already running is the interpreter of a configuration (docker exec), with the same links', { skip }, async () => {
        await linkOpens('container boom');
    });

    test('the project can use the running container: a program runs in it and the Terminal offers a shell inside it', { skip }, async () => {
        const added = await page.evaluate(`fetch('/api/interpreter/remote', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ kind: 'container', name: ${JSON.stringify(CONTAINER)}, python: 'python' }) }).then((r) => r.json())`);
        assert.match(added.label, new RegExp(`Docker container: ${CONTAINER}`), JSON.stringify(added));
        assert.equal(added.target, 'container');
        const shells = await page.evaluate(`fetch('/api/shells').then((r) => r.json())`);
        assert.equal(shells.interpreter, `Docker container: ${CONTAINER}`);
        // the shell: a real terminal in the container, starting in the project's place there
        const got = await page.evaluate(`new Promise((ok) => {
            const ws = new WebSocket((location.protocol === 'https:' ? 'wss' : 'ws') + '://' + location.host + '/console?cols=100&rows=30&shell=interpreter');
            let got = '';
            const done = () => { ws.close(); ok(got); };
            ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.type === 'output') { got += m.data; if (got.includes('mark-42') && got.includes('tb_boom.py')) done(); } else if (m.type === 'error') done(); };
            ws.onopen = () => setTimeout(() => ws.send(JSON.stringify({ type: 'input', data: 'echo mark-$((6*7)); pwd; ls\\r' })), 2500);
            setTimeout(done, 30000);
        })`);
        assert.ok(got.includes('mark-42'), got);
        assert.ok(got.includes('/workspace'), got);
        assert.ok(got.includes('tb_boom.py'), 'ls shows the project\'s files from inside the container: ' + got);
        // the menu of the Terminal's arrow offers it
        await page.evaluate(`document.getElementById('term-pick').click()`);
        await page.waitFor(`[...document.querySelectorAll('body *')].some((e) => e.children.length === 0 && e.textContent.startsWith('In the interpreter: Docker container'))`, 10000, 'the menu entry');
        await page.evaluate(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))`);
        // a run with the project's own interpreter goes through docker exec too
        await open('which.py');
        await page.evaluate(`${$('output')}.textContent = ''`);
        await runAnything('plain which');
        await page.waitFor(`${$('output')}.textContent.includes('exe ')`, 60000, 'the program\'s output');
        assert.match(await output(), /exe \/usr\/local/);
    });

    test('in the running container the debugger stops at a breakpoint and the Tests panel runs the tests (their helper scripts copied in)', { skip }, async () => {
        // the container is still the project's interpreter (the test above): Debug runs pm_debug.py in it through docker exec.
        // The test above chose a run configuration; Debug is to start the file in the editor
        await page.evaluate(`${$('run-config')}.click()`);
        await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((d) => d.textContent.trim().endsWith('Current file'))`, 5000, 'the configuration menu');
        await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.trim().endsWith('Current file')).click()`);
        await open('calc.py');
        assert.equal(await clickMargin(6), true, 'a breakpoint on `return sum(prices)`');
        await page.evaluate(`${$('output')}.textContent = ''`);
        await page.evaluate(`${$('b-debug')}.click()`);
        assert.equal(await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 90000, 'the stop in the container')
            .catch(async (e) => { throw new Error(e.message + '; the Run panel says: ' + await output()); }), 'total calc.py:6');
        assert.match(await page.waitFor(`${$('vars')}.textContent.includes('prices') && ${$('vars')}.textContent`, 10000, 'the variables'), /prices = \[1, 2, 3\]/);
        await page.evaluate(`${$('b-stop')}.click()`);
        await page.waitFor(`${$('b-stop')}.disabled`, 15000, 'the end of the session');
        assert.equal(await clickMargin(6), false, 'the breakpoint taken away');
        // the helper folder is in the container where the run form would mount it
        const listed = spawnSync('docker', ['exec', CONTAINER, 'sh', '-c', 'ls /pymagic/*/pm_debug.py'], { encoding: 'utf8' });
        assert.match(listed.stdout, /\/pymagic\/\d+\/pm_debug\.py/, listed.stderr);

        await page.evaluate(`(() => { const s = ${$('tests-fw')}; s.value = 'unittest'; s.dispatchEvent(new Event('change')); ${$('t-tests')}.click(); ${$('tests-all')}.click(); })()`);
        await page.waitFor(`!${$('tests-all')}.disabled && ${$('tests-console')}.textContent.includes('finished')`, 90000, 'the test run in the container')
            .catch(async (e) => { throw new Error(e.message + '; ' + await page.evaluate(`${$('tests-console')}.textContent.slice(-600)`)); });
        for (let i = 0; i < 6; i++)
            await page.evaluate(`[...document.querySelectorAll('#tests-tree .trow .caret')].find((c) => c.textContent === '▸')?.click()`);
        const rows = await page.evaluate(`Object.fromEntries([...document.querySelectorAll('#tests-tree .trow')].map((r) => [r.querySelector('.tname').textContent, r.querySelector('.ticon').textContent]))`);
        assert.equal(rows.test_ok, '✔');
        assert.equal(rows.test_bad, '✖');
    });
});

// last again: this selects a throwaway environment as the project's interpreter
describe('packages: index settings, versions, installing from a path, the dependency tree, lock files', () => {
    let wheels = '';
    let envPython = '';
    let index;
    let indexUrl = '';
    const MAKE_WHEEL = [
        'import sys, zipfile',
        'out, name, version, requires = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]',
        'info = f"{name}-{version}.dist-info"',
        'z = zipfile.ZipFile(out, "w")',
        'z.writestr(f"{name}/__init__.py", f"VERSION = {version!r}\\n")',
        'meta = f"Metadata-Version: 2.1\\nName: {name}\\nVersion: {version}\\nSummary: made for the e2e test\\n"',
        'meta += "".join(f"Requires-Dist: {r}\\n" for r in requires.split(";;") if r)',
        'z.writestr(f"{info}/METADATA", meta)',
        'z.writestr(f"{info}/WHEEL", "Wheel-Version: 1.0\\nGenerator: test\\nRoot-Is-Purelib: true\\nTag: py3-none-any\\n")',
        'z.writestr(f"{info}/RECORD", f"{name}/__init__.py,,\\n{info}/METADATA,,\\n{info}/WHEEL,,\\n{info}/RECORD,,\\n")',
        'z.close()',
    ].join('\n');
    const rows = () => page.evaluate(`[...document.querySelectorAll('#pk-table .pkrow:not(.head) > span:first-child')].map((s) => s.textContent)`);
    const logDone = (what) => page.waitFor(`/Done\\.|exited with code/.test(${$('pk-log')}.textContent) && ${$('pk-log')}.textContent`, 120000, what);
    const setValue = (id, value) => page.evaluate(`(() => { const e = ${$(id)}; e.value = ${JSON.stringify(value)}; e.dispatchEvent(new Event('input')); })()`);
    const wsRequest = (message) => page.evaluate(`new Promise((ok) => { const ws = new WebSocket(location.origin.replace('http', 'ws') + '/run'); const seen = []; ws.onopen = () => ws.send(${JSON.stringify(JSON.stringify(message))}); ws.onmessage = (e) => { const m = JSON.parse(e.data); seen.push(m); if (m.type === 'error' || m.type === 'exited') { ws.close(); ok(m); } }; setTimeout(() => ok({ type: 'timeout', seen }), 20000); })`);

    before(async () => {
        wheels = tempDir('pymagic-e2e-wheels-');
        writeFileSync(join(wheels, 'make_wheel.py'), MAKE_WHEEL);
        const make = (name, version, requires = '') => {
            const r = spawnSync('python', [join(wheels, 'make_wheel.py'), join(wheels, `${name}-${version}-py3-none-any.whl`), name, version, requires], { encoding: 'utf8' });
            assert.equal(r.status, 0, String(r.stderr));
        };
        make('e2elib', '1.0');
        make('e2elib', '2.0');
        make('e2eapp', '1.0', 'e2elib>=1;;e2eextra>=1; extra == "more"');
        // an index of this test: the two releases of e2elib
        index = createServer((req, res) => {
            if (req.url.startsWith('/simple/e2elib')) {
                res.writeHead(200, { 'Content-Type': 'text/html' });
                res.end('<html><body><a href="/files/e2elib-1.0-py3-none-any.whl">e2elib-1.0-py3-none-any.whl</a><a href="/files/e2elib-2.0-py3-none-any.whl">e2elib-2.0-py3-none-any.whl</a></body></html>');
            }
            else if (req.url.startsWith('/files/') && existsSync(join(wheels, req.url.slice(7)))) {
                res.writeHead(200, { 'Content-Type': 'application/octet-stream' });
                res.end(readFileSync(join(wheels, req.url.slice(7))));
            }
            else {
                res.writeHead(404);
                res.end();
            }
        });
        await new Promise((ok) => index.listen(0, '127.0.0.1', ok));
        indexUrl = `http://127.0.0.1:${index.address().port}/simple`;
        // a throwaway environment becomes the project's interpreter
        const env = tempDir('pymagic-e2e-pkgenv-');
        const venv = spawnSync('python', ['-m', 'venv', env], { encoding: 'utf8' });
        assert.equal(venv.status, 0, String(venv.stderr));
        envPython = join(env, 'Scripts', 'python.exe');
        const chosen = await page.evaluate(`fetch('/api/interpreter', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path: ${JSON.stringify(envPython)} }) }).then((r) => r.ok)`);
        assert.ok(chosen, 'the environment can be selected');
        await page.evaluate(`${$('t-packages')}.click()`);
        await page.waitFor(`document.querySelectorAll('#pk-table .pkrow').length > 1`, 60000, 'the package list');
    });
    after(() => { index?.close(); });

    test('index settings are checked, kept and shown again', async () => {
        await page.evaluate(`${$('pk-config-open')}.click()`);
        await page.waitFor(`!${$('pk-config')}.hidden`, 5000, 'the form');
        await setValue('pk-index-url', '--find-links');
        await page.evaluate(`${$('pk-config-save')}.click()`);
        await page.waitFor(`${$('pk-config-msg')}.className === 'err' && ${$('pk-config-msg')}.textContent`, 10000, 'the refusal');
        await setValue('pk-index-url', indexUrl);
        await setValue('pk-trusted', '127.0.0.1:' + new URL(indexUrl).port);
        await setValue('pk-proxy', '');
        await page.evaluate(`${$('pk-config-save')}.click()`);
        assert.match(await page.waitFor(`${$('pk-config-msg')}.textContent.startsWith('Saved: index') && ${$('pk-config-msg')}.textContent`, 10000, 'the saved message'), /index http:\/\/127\.0\.0\.1:\d+\/simple/);
        const kept = await page.evaluate(`fetch('/api/packages/settings').then((r) => r.json())`);
        assert.equal(kept.indexUrl, indexUrl);
        assert.deepEqual(kept.trustedHosts, ['127.0.0.1:' + new URL(indexUrl).port]);
        await page.evaluate(`${$('pk-config-close')}.click()`);
    });

    test('a path is checked and has to be confirmed; an install without the confirmation is refused', async () => {
        const lib1 = join(wheels, 'e2elib-1.0-py3-none-any.whl');
        // the host refuses an install of a source that was not confirmed
        const forged = await wsRequest({ type: 'startPip', op: 'installSource', packages: [], source: lib1, token: 'forged' });
        assert.equal(forged.type, 'error');
        assert.match(forged.message, /not confirmed/);
        assert.match((await wsRequest({ type: 'startPip', op: 'install', packages: ['--index-url=http://evil'] })).message, /not allowed/);
        // the box does not install a path: it asks
        await setValue('pk-install', lib1);
        await page.evaluate(`${$('pk-go')}.click()`);
        await page.waitFor(`!${$('pk-confirm')}.hidden && ${$('pk-confirm')}.textContent.includes('Install from a file')`, 15000, 'the confirmation');
        assert.match(await page.evaluate(`${$('pk-confirm')}.textContent`), /wheel is installed as it is/);
        assert.ok(!(await rows()).includes('e2elib'), 'nothing was installed yet');
        await page.evaluate(`${$('pk-source-install')}.click()`);
        await logDone('the install of the wheel');
        await page.waitFor(`[...document.querySelectorAll('#pk-table .pkrow span:first-child')].some((s) => s.textContent === 'e2elib')`, 30000, 'e2elib in the list');
        // a folder with nothing to build is explained
        await page.evaluate(`${$('pk-source-open')}.click()`);
        await page.waitFor(`!${$('pk-source')}.hidden`, 5000, 'the source form');
        await setValue('pk-source-text', wheels);
        await page.evaluate(`${$('pk-source-check')}.click()`);
        await page.waitFor(`${$('pk-confirm')}.textContent.includes('no pyproject.toml')`, 10000, 'the explanation');
        // e2eapp needs e2elib, which is there now: no index is asked
        await setValue('pk-source-text', join(wheels, 'e2eapp-1.0-py3-none-any.whl'));
        await page.evaluate(`${$('pk-source-check')}.click()`);
        await page.waitFor(`document.getElementById('pk-source-install')`, 10000, 'the second confirmation');
        await page.evaluate(`${$('pk-source-install')}.click()`);
        await logDone('the install of the app');
        await page.waitFor(`[...document.querySelectorAll('#pk-table .pkrow span:first-child')].some((s) => s.textContent === 'e2eapp')`, 30000, 'e2eapp in the list');
    });

    test('the dependency tree of the environment, and of one package both ways', async () => {
        await page.evaluate(`${$('pk-tree')}.click()`);
        await page.waitFor(`document.querySelector('#pk-table .pktree')`, 30000, 'the tree');
        const texts = () => page.evaluate(`[...document.querySelectorAll('#pk-table .pktree')].map((d) => d.textContent.replace(/^[▾▸]?/, '').trim())`);
        let shown = await texts();
        assert.ok(shown.includes('e2eapp 1.0'), shown.join(' | '));
        assert.ok(shown.some((t) => t.startsWith('e2elib 1.0') && t.endsWith('>=1')), 'e2eapp\'s requirement is listed below it: ' + shown.join(' | '));
        // the requirement that belongs to an extra is not one here
        assert.ok(!shown.some((t) => t.startsWith('e2eextra')));
        await page.evaluate(`${$('pk-tree-back')}.click()`);
        await page.waitFor(`document.querySelectorAll('#pk-table .pkrow').length > 1`, 5000, 'the list again');
        // who needs e2elib
        await page.evaluate(`[...document.querySelectorAll('#pk-table .pkrow')].find((r) => r.firstChild.textContent === 'e2elib').click()`);
        await page.waitFor(`[...document.querySelectorAll('#pk-detail button')].some((b) => b.textContent === 'Required by tree')`, 15000, 'the detail buttons');
        await page.evaluate(`[...document.querySelectorAll('#pk-detail button')].find((b) => b.textContent === 'Required by tree').click()`);
        await page.waitFor(`document.querySelector('#pk-table .pktree') && document.getElementById('pk-treebar').textContent.includes('What needs e2elib')`, 30000, 'the reverse tree');
        shown = await texts();
        assert.deepEqual(shown.slice(0, 2).map((t) => t.replace(/\s+/g, ' ')), ['e2elib 1.0', 'e2eapp 1.0 >=1']);
        await page.evaluate(`${$('pk-tree-back')}.click()`);
    });

    test('a version is chosen from the index named in the settings and installed', async () => {
        await page.evaluate(`[...document.querySelectorAll('#pk-detail button')].find((b) => b.textContent === 'Versions…').click()`);
        await page.waitFor(`document.getElementById('pk-version-select')?.options.length >= 2`, 90000, 'the versions of e2elib');
        const labels = await page.evaluate(`[...document.getElementById('pk-version-select').options].map((o) => o.textContent)`);
        assert.deepEqual(labels, ['2.0', '1.0 (installed)']);
        await page.evaluate(`document.getElementById('pk-version-select').value = '2.0'`);
        await page.evaluate(`document.getElementById('pk-version-install').click()`);
        await logDone('the install of the chosen version');
        await page.waitFor(`[...document.querySelectorAll('#pk-table .pkrow')].some((r) => r.children[0].textContent === 'e2elib' && r.children[1].textContent === '2.0')`, 30000, 'e2elib 2.0 in the list');
    });

    test('lock files are listed with their sync commands, and only a detected one can be synced', async () => {
        writeFileSync(join(projectRoot, 'uv.lock'), 'version = 1\n');
        writeFileSync(join(projectRoot, 'poetry.lock'), '# poetry\n');
        try {
            await page.evaluate(`${$('pk-refresh')}.click()`);
            await page.waitFor(`document.querySelectorAll('#pk-locks .lockrow').length >= 2`, 15000, 'the lock files');
            const found = await page.evaluate(`[...document.querySelectorAll('#pk-locks .lockrow')].map((r) => [r.dataset.file, r.querySelector('code').textContent, r.querySelector('button').disabled])`);
            const api = await page.evaluate(`fetch('/api/packages/locks').then((r) => r.json())`);
            assert.deepEqual(found.map((f) => f.slice(0, 2)), [['uv.lock', 'uv sync --frozen'], ['poetry.lock', 'poetry install --sync --no-interaction']]);
            assert.deepEqual(found.map((f) => !f[2]), api.map((l) => l.available), 'a sync is offered exactly when the tool is there');
            // the host only runs what it detected
            const refused = await wsRequest({ type: 'startPip', op: 'lockSync', packages: [], file: 'nope.lock' });
            assert.equal(refused.type, 'error');
            assert.match(refused.message, /not a lock file of this project/);
        }
        finally {
            rmSync(join(projectRoot, 'uv.lock'), { force: true });
            rmSync(join(projectRoot, 'poetry.lock'), { force: true });
        }
    });

    test('a password in the index address is shown masked; requirements.in is compiled through it, and an out-of-date requirements.txt is told', { skip: !spawnSync('uv', ['--version']).stdout?.length && 'uv is not installed' }, async () => {
        // a made-up password, put together here so that a secret scanner does not take it for a real one
        const pw = ['e2e', 'fake', 'pass'].join('-');
        const secretUrl = indexUrl.replace('http://', `http://e2euser:${pw}@`);
        await page.evaluate(`${$('pk-config-open')}.click()`);
        await page.waitFor(`!${$('pk-config')}.hidden`, 5000, 'the form');
        await setValue('pk-index-url', secretUrl);
        await page.evaluate(`${$('pk-config-save')}.click()`);
        const saved = await page.waitFor(`${$('pk-config-msg')}.textContent.startsWith('Saved: index') && ${$('pk-config-msg')}.textContent`, 10000, 'the saved message');
        assert.ok(saved.includes('e2euser:***@') && !saved.includes(pw), saved);
        // the page never gets the password back, and packages.json does not have it
        assert.equal((await page.evaluate(`fetch('/api/packages/settings').then((r) => r.json())`)).indexUrl, indexUrl.replace('http://', 'http://e2euser:***@'));
        assert.ok(!readFileSync(join(projectRoot, '.pymagic', 'packages.json'), 'utf8').includes(pw));
        await page.evaluate(`${$('pk-config-close')}.click()`);
        await page.evaluate(`${$('pk-config-open')}.click()`);
        await page.waitFor(`${$('pk-index-url')}.value.includes('***')`, 5000, 'the masked address in the form');

        writeFileSync(join(projectRoot, 'requirements.in'), 'e2elib<2\n');
        writeFileSync(join(projectRoot, 'requirements.txt'), '# This file was autogenerated by uv via the following command:\n#    uv pip compile requirements.in -o requirements.txt\ne2elib==3.0\n    # via -r requirements.in\n');
        try {
            await page.evaluate(`${$('pk-refresh')}.click()`);
            const row = `document.querySelector('#pk-locks .lockrow[data-file="requirements.txt"]')`;
            assert.match(await page.waitFor(`${row}?.querySelector('.lockstate.stale')?.textContent`, 30000, 'the out-of-date note'), /requirements\.in asks for e2elib<2, requirements\.txt has 3\.0/);
            // Compile needs a second click; then requirements.txt is made again through the index (with the password, which the file does not get)
            await page.evaluate(`${row}.querySelector('.lockcompile').click()`);
            await page.waitFor(`${row}.querySelector('.lockcompile').classList.contains('armed')`, 5000, 'the second-click warning');
            await page.evaluate(`${row}.querySelector('.lockcompile').click()`);
            await logDone('the compile');
            const txt = readFileSync(join(projectRoot, 'requirements.txt'), 'utf8');
            assert.match(txt, /^e2elib==1\.0$/m);
            assert.ok(!txt.includes(pw) && !txt.includes('e2euser'), txt);
            await page.evaluate(`${$('pk-refresh')}.click()`);
            await page.waitFor(`${row}?.querySelector('.lockstate.fresh')`, 30000, 'up to date after the compile');
            // the .in changes: out of date again, with the reason
            writeFileSync(join(projectRoot, 'requirements.in'), 'e2elib>=2\n');
            await page.evaluate(`${$('pk-refresh')}.click()`);
            assert.equal(await page.waitFor(`${row}?.querySelector('.lockstate.stale')?.textContent.trim()`, 30000, 'out of date again'),
                'out of date: requirements.in asks for e2elib>=2, requirements.txt has 1.0');
        }
        finally {
            rmSync(join(projectRoot, 'requirements.in'), { force: true });
            rmSync(join(projectRoot, 'requirements.txt'), { force: true });
            await setValue('pk-index-url', indexUrl);
            await page.evaluate(`${$('pk-config-save')}.click()`);
            await page.evaluate(`${$('pk-config-close')}.click()`);
        }
    });

    test('a .venv made by uv: chosen at the new-environment button, and its packages installed and listed through uv pip', { skip: (!spawnSync('uv', ['--version']).stdout?.length && 'uv is not installed') || (existsSync(join(projectRoot, '.venv')) && 'the project has a .venv already') }, async () => {
        const venv = join(projectRoot, '.venv');
        try {
            await page.evaluate(`${$('b-venv')}.click()`);
            await page.waitFor(`document.querySelector('#venv-ask button[data-tool="uv"]')`, 10000, 'the choice of tool');
            await page.evaluate(`document.querySelector('#venv-ask button[data-tool="uv"]').click()`);
            // made by uv, and selected
            await page.waitFor(`(async () => (await (await fetch('/api/packages')).json()).manager === 'uv')()`, 90000, 'the uv environment selected');
            assert.match(readFileSync(join(venv, 'pyvenv.cfg'), 'utf8'), /^uv = /m);
            await page.evaluate(`${$('pk-refresh')}.click()`);
            await page.waitFor(`${$('pk-banner')}.className === 'info' && ${$('pk-banner')}.textContent.includes('uv pip')`, 30000, 'the uv note');
            // no "pip is not installed" for it, and an install goes through uv pip (from the test's index)
            await setValue('pk-install', 'e2elib==1.0');
            await page.evaluate(`${$('pk-go')}.click()`);
            await logDone('the install through uv pip');
            assert.match(await page.evaluate(`${$('pk-log')}.textContent`), /uv|Installed|e2elib/i);
            await page.evaluate(`${$('pk-refresh')}.click()`);
            await page.waitFor(`[...document.querySelectorAll('#pk-table .pkrow')].some((r) => r.children[0].textContent === 'e2elib' && r.children[1].textContent === '1.0')`, 30000, 'e2elib in the uv environment');
            assert.ok(!existsSync(join(venv, 'Scripts', 'pip.exe')), 'uv made it without pip');
        }
        finally {
            await page.evaluate(`fetch('/api/interpreter', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path: ${JSON.stringify(envPython)} }) })`);
            await sleep(500);
            removeTree(venv);
        }
    });

    test('pipenv: Pipfile.lock made with Lock, synced into the selected environment, and told out of date when the Pipfile changes', { skip: !spawnSync('pipenv', ['--version'], { shell: true }).stdout?.length && 'pipenv is not installed' }, async () => {
        const wheel = join(wheels, 'e2elib-1.0-py3-none-any.whl').replace(/\\/g, '/');
        writeFileSync(join(projectRoot, 'Pipfile'), `[packages]\ne2elib = {file = "${wheel}"}\n\n[dev-packages]\n`);
        writeFileSync(join(projectRoot, 'Pipfile.lock'), '{"_meta": {"hash": {"sha256": "0"}}, "default": {}, "develop": {}}');
        const row = `document.querySelector('#pk-locks .lockrow[data-file="Pipfile.lock"]')`;
        const twice = async (button, what) => {
            await page.evaluate(`${row}.querySelector('${button}').click()`);
            await page.waitFor(`${row}.querySelector('${button}').classList.contains('armed')`, 5000, 'the second-click warning of ' + what);
            await page.evaluate(`${row}.querySelector('${button}').click()`);
            await logDone(what);
        };
        try {
            await page.evaluate(`${$('pk-refresh')}.click()`);
            assert.match(await page.waitFor(`${row}?.querySelector('.lockstate.stale')?.textContent`, 60000, 'the made-up lock out of date'), /Pipfile changed/);
            assert.equal(await page.evaluate(`${row}.querySelector('.lockcompile').textContent`), 'Lock');
            await twice('.lockcompile', 'pipenv lock');
            assert.match(readFileSync(join(projectRoot, 'Pipfile.lock'), 'utf8'), /"e2elib"/);
            await page.evaluate(`${$('pk-refresh')}.click()`);
            await page.waitFor(`${row}?.querySelector('.lockstate.fresh')`, 60000, 'up to date after Lock');
            await twice('.locksync', 'pipenv sync');
            await page.evaluate(`${$('pk-refresh')}.click()`);
            await page.waitFor(`[...document.querySelectorAll('#pk-table .pkrow')].some((r) => r.children[0].textContent === 'e2elib' && r.children[1].textContent === '1.0')`, 30000, 'e2elib 1.0 synced');
            writeFileSync(join(projectRoot, 'Pipfile'), readFileSync(join(projectRoot, 'Pipfile'), 'utf8') + '\n[requires]\npython_version = "3.99"\n');
            await page.evaluate(`${$('pk-refresh')}.click()`);
            assert.equal(await page.waitFor(`${row}?.querySelector('.lockstate.stale')?.textContent.trim()`, 60000, 'out of date again'),
                'out of date: the Pipfile changed since Pipfile.lock was made: run pipenv lock');
        }
        finally {
            rmSync(join(projectRoot, 'Pipfile'), { force: true });
            rmSync(join(projectRoot, 'Pipfile.lock'), { force: true });
        }
    });

    // PYMAGIC_TEST_CONDA names conda.exe (conda downloads Python from its channels the first time)
    test('conda: a .conda made at the new-environment button, and a package installed into it with conda', { skip: (!process.env.PYMAGIC_TEST_CONDA && 'PYMAGIC_TEST_CONDA is not set') || (existsSync(join(projectRoot, '.conda')) && 'the project has a .conda already') }, async () => {
        const env = join(projectRoot, '.conda');
        try {
            await page.evaluate(`${$('b-venv')}.click()`);
            await page.waitFor(`document.querySelector('#venv-ask button[data-tool="conda"]')`, 10000, 'the conda choice');
            await page.evaluate(`document.querySelector('#venv-ask button[data-tool="conda"]').click()`);
            // the environment made and selected, or the reason it was not (the note beside the interpreter)
            const outcome = await page.waitFor(`(async () => (await (await fetch('/api/packages')).json()).manager === 'conda' ? 'conda' : document.getElementById('interp-note').className === 'bad' && document.getElementById('interp-note').textContent)()`, 600000, 'the conda environment selected');
            assert.equal(outcome, 'conda');
            assert.ok(existsSync(join(env, 'conda-meta')) && existsSync(join(env, 'python.exe')));
            await page.evaluate(`${$('pk-refresh')}.click()`);
            await page.waitFor(`${$('pk-banner')}.className === 'info' && ${$('pk-banner')}.textContent.includes('conda environment')`, 30000, 'the conda note');
            // the list is conda's: python is in it, with the version conda gave
            await page.waitFor(`[...document.querySelectorAll('#pk-table .pkrow')].some((r) => r.children[0].textContent === 'python')`, 60000, 'python in the list');
            await setValue('pk-install', 'six');
            await page.evaluate(`${$('pk-go')}.click()`);
            await logDone('conda install six');
            await page.evaluate(`${$('pk-refresh')}.click()`);
            await page.waitFor(`[...document.querySelectorAll('#pk-table .pkrow')].some((r) => r.children[0].textContent === 'six')`, 60000, 'six installed by conda');
            const listed = JSON.parse(spawnSync(process.env.PYMAGIC_TEST_CONDA, ['list', '-p', env, 'six', '--json'], { encoding: 'utf8' }).stdout);
            assert.equal(listed[0]?.channel, 'conda-forge', JSON.stringify(listed));
        }
        finally {
            await page.evaluate(`fetch('/api/interpreter', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path: ${JSON.stringify(envPython)} }) })`);
            await sleep(500);
            if (existsSync(env)) spawnSync(process.env.PYMAGIC_TEST_CONDA, ['env', 'remove', '-p', env, '-y', '--quiet'], { encoding: 'utf8' });
            removeTree(env);
        }
    });
});

// last: the notebook edits cells of the file and leaves it changed
describe('a notebook: merge and split, tags, Run Section, the Variables window and the headings in Structure', () => {
    const nbState = (expr) => page.evaluate(`(() => { const s = window.pymagic.notebook.stateOf('nb_demo.ipynb'); return ${expr}; })()`);
    const cellsText = () => nbState(`s.nb.cells.map((c) => (Array.isArray(c.source) ? c.source.join('') : c.source))`);

    test('a split cuts a cell at the cursor and a merge joins it again', async () => {
        await open('nb_demo.ipynb');
        await page.waitFor(`document.querySelector('#notebook.active .nb-cell')`, 15000, 'the notebook');
        // the editor of the cell is made when it is in view; make it and put the cursor after the second line
        await nbState(`(s.views[1].el.__create(), 1)`);
        await page.waitFor(`!!window.pymagic.notebook.stateOf('nb_demo.ipynb').views[1].editor`, 10000, 'the cell editor');
        await nbState(`(s.views[1].editor.setPosition({ lineNumber: 2, column: 11 }), 1)`);
        await nbState(`(s.views[1].el.querySelector('.nb-split').click(), 1)`);
        await page.waitFor(`window.pymagic.notebook.stateOf('nb_demo.ipynb').nb.cells.length === 6`, 10000, 'the split');
        assert.deepEqual((await cellsText()).slice(1, 3), ['x = 5\ny = [1, 2]', 'z = x * 2']);
        await nbState(`(s.views[1].el.querySelector('.nb-merge').click(), 1)`);
        await page.waitFor(`window.pymagic.notebook.stateOf('nb_demo.ipynb').nb.cells.length === 5`, 10000, 'the merge');
        assert.equal((await cellsText())[1], 'x = 5\ny = [1, 2]\n\nz = x * 2');
        // put the cell back the way it was for the run below (the merge drew the cells again: the cell's editor is made again first)
        await nbState(`(s.views[1].el.__create?.(), 1)`);
        await page.waitFor(`!!window.pymagic.notebook.stateOf('nb_demo.ipynb').views[1].editor`, 10000, 'the cell editor after the merge');
        await nbState(`(s.views[1].editor.executeEdits('t', [{ range: s.views[1].editor.getModel().getFullModelRange(), text: 'x = 5\\ny = [1, 2]\\nz = x * 2' }]), 1)`);
        assert.equal((await cellsText())[1], 'x = 5\ny = [1, 2]\nz = x * 2');
    });

    test('a tag is added in the cell and written to its metadata, and taken away again', async () => {
        await nbState(`(s.views[2].el.querySelector('.nb-tag-btn').click(), 1)`);
        await page.waitFor(`window.pymagic.notebook.stateOf('nb_demo.ipynb').views[2].el.querySelector('.nb-tag-input')`, 5000, 'the tag field');
        await page.evaluate(`(() => { const i = window.pymagic.notebook.stateOf('nb_demo.ipynb').views[2].el.querySelector('.nb-tag-input'); i.value = 'slow, keep'; i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
        assert.deepEqual(await nbState(`s.nb.cells[2].metadata.tags`), ['slow', 'keep']);
        assert.deepEqual(await nbState(`[...s.views[2].el.querySelectorAll('.nb-tag-name')].map((e) => e.textContent)`), ['slow', 'keep']);
        await nbState(`(s.views[2].el.querySelector('.nb-tag-x').click(), 1)`);
        assert.deepEqual(await nbState(`s.nb.cells[2].metadata.tags`), ['keep']);
        await nbState(`(s.views[2].el.querySelector('.nb-tag-x').click(), 1)`);
        assert.equal(await nbState(`'tags' in s.nb.cells[2].metadata`), false, 'no tags: no key');
    });

    test('Run Section runs the heading cell\'s section and the Variables window lists what the kernel holds', async () => {
        await nbState(`(s.views[0].el.querySelector('.nb-run-section').click(), 1)`);
        await page.waitFor(`window.pymagic.notebook.stateOf('nb_demo.ipynb').nb.cells[4].execution_count`, 60000, 'the last cell of the section');
        assert.equal(await nbState(`s.nb.cells[2].outputs[0].text.join('')`), '10\n');
        const text = await page.waitFor(`(() => { const t = window.pymagic.notebook.stateOf('nb_demo.ipynb').varsEl.textContent; return t.includes('Variables (') && t.includes('w') && t; })()`, 15000, 'the variables');
        assert.match(text, /Variables \(4\)/);
        assert.deepEqual(await nbState(`[...s.varsEl.querySelectorAll('tr')].slice(1).map((r) => [...r.children].map((c) => c.textContent).join('|'))`),
            ['w|int||1', 'x|int||5', 'y|list|2|[1, 2]', 'z|int||10']);
    });

    test('the headings of the text cells are the Structure window, and a click goes to the cell', async () => {
        await page.evaluate(`window.dispatchEvent(new CustomEvent('pymagic:sidebar', { detail: 'structure' }))`);
        const names = await page.waitFor(`(() => { const r = [...document.querySelectorAll('#structure li .fname')].map((e) => e.textContent); return r.length === 2 && r; })()`, 10000, 'the headings');
        assert.deepEqual(names, ['Intro', 'Part']);
        await page.evaluate(`[...document.querySelectorAll('#structure li')].find((li) => li.textContent.includes('Part')).click()`);
        assert.equal(await nbState(`s.selected`), 3);
    });
});

// last: this turns the project folder into a Git repository
describe('branches: comparing, rebasing onto another, and pushing tags', () => {
    const git = (...args) => {
        const r = spawnSync('git', args, { cwd: projectRoot, encoding: 'utf8' });
        assert.equal(r.status, 0, `git ${args.join(' ')}: ${r.stderr}`);
        return r.stdout;
    };
    test('the Branches & tags window compares two branches, rebases onto one and pushes a tag', async () => {
        git('init', '-q', '-b', 'main');
        git('config', 'user.name', 'Tester');
        git('config', 'user.email', 'tester@example.com');
        git('config', 'core.autocrlf', 'false');
        // PyMagic's own state (recent files, the index) changes while the app runs: it is not part of this repository
        writeFileSync(join(projectRoot, '.git', 'info', 'exclude'), '.pymagic/' + String.fromCharCode(10) + 'nb_demo.ipynb' + String.fromCharCode(10));
        git('add', '-A');
        git('commit', '-q', '-m', 'start');
        git('switch', '-q', '-c', 'other');
        writeFileSync(join(projectRoot, 'other_only.txt'), 'other\n');
        git('add', '-A');
        git('commit', '-q', '-m', 'only on other');
        git('switch', '-q', 'main');
        writeFileSync(join(projectRoot, 'main_only.txt'), 'main\n');
        git('add', '-A');
        git('commit', '-q', '-m', 'only on main');
        const remote = tempDir('pymagic-e2e-remote-git-');
        assert.equal(spawnSync('git', ['init', '-q', '--bare', '-b', 'main'], { cwd: remote }).status, 0);
        git('remote', 'add', 'origin', remote);
        git('push', '-q', '-u', 'origin', 'main');
        git('tag', 'v1');

        await page.evaluate(`window.confirm = () => true`);
        const openManage = async () => {
            await page.evaluate(`${$('t-git')}.click()`);
            await page.evaluate(`${$('git-manage')}.click()`);
            await page.waitFor(`document.querySelector('.ask-box .gs-row')`, 15000, 'the Branches & tags window');
        };
        const clickIn = (rowText, label) => page.evaluate(`(() => {
            const row = [...document.querySelectorAll('.ask-box .gs-row')].find((r) => r.textContent.includes(${JSON.stringify(rowText)}));
            const b = [...row.querySelectorAll('button')].find((x) => x.textContent === ${JSON.stringify(label)});
            b.click(); return true; })()`);

        // compare
        await openManage();
        await clickIn('other', 'Compare');
        await page.waitFor(`document.querySelectorAll('.ask-box').length === 2 && document.body.textContent.includes('Only in main (1)')`, 15000, 'the comparison');
        const text = await page.evaluate(`document.body.textContent`);
        assert.match(text, /only on main/);
        assert.match(text, /only on other/);
        assert.match(text, /other_only\.txt/);
        assert.match(text, /main_only\.txt/);
        await page.evaluate(`[...document.querySelectorAll('.ask-box .gs-row')].find((r) => r.textContent.includes('other_only.txt')).click()`);
        await page.waitFor(`${$('git-diff')}.classList.contains('open')`, 10000, 'the diff');
        assert.match(await page.evaluate(`${$('git-diff-title')}.textContent`), /other_only\.txt .{1,3} main \(left\) against other \(right\)/);
        await page.evaluate(`${$('git-diff-close')}.click()`);

        // the tag goes to the remote
        await openManage();
        await clickIn('v1', 'Push');
        await page.waitFor(`document.body.textContent.includes('Pushed v1') || true`, 2000).catch(() => { });
        for (let i = 0; i < 30 && !git('ls-remote', '--tags', 'origin').includes('refs/tags/v1'); i++) await sleep(300);
        assert.match(git('ls-remote', '--tags', 'origin'), /refs\/tags\/v1/);

        // rebase main onto other
        await page.evaluate(`document.querySelectorAll('.ask-shade').forEach((s) => s.remove())`);
        await openManage();
        await clickIn('other', 'Rebase current onto');
        for (let i = 0; i < 40 && !git('log', '--format=%s').includes('only on other'); i++) await sleep(300);
        assert.equal(git('log', '--format=%s').trim().split('\n').join(' | '), 'only on main | only on other | start');
    });

    test('a rebase stopped at edit: Amend and continue puts the change and the new message into that commit', async () => {
        // the last commit marked edit, as the interactive rebase window would (git's own sed, through the sequence editor)
        const stop = spawnSync('git', ['rebase', '-i', 'HEAD~1'], { cwd: projectRoot, encoding: 'utf8', env: { ...process.env, GIT_SEQUENCE_EDITOR: 'sed -i 1s/^pick/edit/' } });
        assert.equal(stop.status, 0, stop.stderr);
        assert.ok(existsSync(join(projectRoot, '.git', 'rebase-merge', 'amend')), 'stopped at edit');
        await page.evaluate(`document.querySelectorAll('.ask-shade').forEach((s) => s.remove())`);
        await page.evaluate(`${$('t-git')}.click()`);
        await page.evaluate(`${$('git-refresh')}.click()`);
        await page.waitFor(`${$('git-op-amend')}.style.display === '' && ${$('git-op-text')}.textContent.includes('only on main')`, 15000, 'the edit stop and its button');
        writeFileSync(join(projectRoot, 'main_only.txt'), 'main, edited\n');
        await page.evaluate(`(() => { const e = ${$('git-message')}; e.value = 'only on main, edited'; e.dispatchEvent(new Event('input')); })()`);
        await page.evaluate(`${$('git-op-amend')}.click()`);
        for (let i = 0; i < 40 && existsSync(join(projectRoot, '.git', 'rebase-merge')); i++) await sleep(300);
        assert.ok(!existsSync(join(projectRoot, '.git', 'rebase-merge')), 'the rebase finished');
        assert.equal(git('log', '--format=%s').trim().split('\n').join(' | '), 'only on main, edited | only on other | start');
        assert.equal(git('show', 'HEAD:main_only.txt'), 'main, edited\n');
        await page.waitFor(`${$('git-op')}.style.display === 'none'`, 15000, 'the bar gone');
    });

    test('in the interactive rebase window a commit is dragged to another place, and the rebase replays them in that order', async () => {
        await page.evaluate(`${$('t-git')}.click()`);
        await page.evaluate(`${$('git-refresh')}.click()`);
        const subjectRow = (subject) => `[...document.querySelectorAll('#git-log .gl')].find((r) => r.querySelector('.subject')?.textContent === ${JSON.stringify(subject)})`;
        await page.waitFor(subjectRow('only on other'), 20000, 'the commit in the log');
        await page.evaluate(`${subjectRow('only on other')}.click()`);
        await page.waitFor(`[...document.querySelectorAll('#git-detail .git-acts button')].some((b) => b.textContent === 'Rebase from here…')`, 10000, 'the actions');
        await page.evaluate(`[...document.querySelectorAll('#git-detail .git-acts button')].find((b) => b.textContent === 'Rebase from here…').click()`);
        const rowsNow = `[...document.querySelectorAll('.rebase-box .rebase-row .rebase-subject')].map((s) => s.textContent).join(' | ')`;
        await page.waitFor(`${rowsNow} === 'only on other | only on main, edited'`, 10000, 'the rebase window');
        // the second row's grip dragged over the top half of the first: the line shows above it, and the drop puts it there
        const drag = (type, rowIndex, part) => page.evaluate(`(() => {
            const rows = document.querySelectorAll('.rebase-box .rebase-row');
            const target = ${JSON.stringify(type)} === 'dragstart' ? rows[${rowIndex}].querySelector('.rebase-grip') : rows[${rowIndex}];
            const r = rows[${rowIndex}].getBoundingClientRect();
            const e = new DragEvent(${JSON.stringify(type)}, { bubbles: true, cancelable: true, clientY: r.top + r.height * ${part}, dataTransfer: new DataTransfer() });
            target.dispatchEvent(e);
            return rows[${rowIndex}].className;
        })()`);
        await drag('dragstart', 1, 0.5);
        assert.match(await drag('dragover', 0, 0.2), /drop-before/);
        assert.match(await drag('dragover', 0, 0.8), /drop-after/);
        await drag('dragover', 0, 0.2);
        await drag('drop', 0, 0.2);
        assert.equal(await page.evaluate(rowsNow), 'only on main, edited | only on other');
        assert.equal(await page.evaluate(`document.querySelector('.rebase-foot').textContent`), '2 moved');
        await page.evaluate(`[...document.querySelectorAll('.rebase-box button')].find((b) => b.textContent === 'Start rebasing').click()`);
        for (let i = 0; i < 40 && !git('log', '--format=%s').startsWith('only on other'); i++) await sleep(300);
        assert.equal(git('log', '--format=%s').trim().split('\n').join(' | '), 'only on other | only on main, edited | start');
    });

    test('a merge conflict settled in three editors: single lines picked from both sides, and a side taken with its arrow', async () => {
        const nl = String.fromCharCode(10);
        const file = (lines) => writeFileSync(join(projectRoot, 'merge_demo.txt'), lines.join(nl) + nl);
        const commitAll = (message) => { git('add', '-A'); git('commit', '-q', '-m', message); };
        file(['top', 'one', 'keep 1', 'keep 2', 'keep 3', 'keep 4', 'two', 'end']);
        commitAll('merge demo');
        git('switch', '-q', '-c', 'merge-side');
        file(['top', 't1', 't2', 'keep 1', 'keep 2', 'keep 3', 'keep 4', 'q', 'end']);
        commitAll('theirs');
        git('switch', '-q', 'main');
        file(['top', 'o1', 'o2', 'o3', 'keep 1', 'keep 2', 'keep 3', 'keep 4', 'p', 'end']);
        commitAll('ours');
        assert.notEqual(spawnSync('git', ['merge', 'merge-side'], { cwd: projectRoot, encoding: 'utf8' }).status, 0, 'the merge stops on conflicts');
        try {
            await page.evaluate(`${$('t-git')}.click()`);
            await page.evaluate(`${$('git-refresh')}.click()`);
            await page.waitFor(`[...document.querySelectorAll('#git-conflicts .gs-row')].some((r) => r.textContent.includes('merge_demo.txt'))`, 15000, 'the conflicted file');
            await page.evaluate(`[...[...document.querySelectorAll('#git-conflicts .gs-row')].find((r) => r.textContent.includes('merge_demo.txt')).querySelectorAll('button')].find((b) => b.textContent === 'Resolve…').click()`);
            await page.waitFor(`document.querySelectorAll('.merge-box .merge-card').length === 2 && document.querySelectorAll('.merge-ours .merge-pick').length === 4`, 15000, 'the three editors');
            const modelIn = (cls) => `window.pymagic.monaco.editor.getEditors().find((e) => e.getContainerDomNode().closest(${JSON.stringify('.' + cls)})).getModel().getValue()`;
            // each side is the file with the conflicts taken its way; the result keeps the markers
            assert.equal(await page.evaluate(modelIn('merge-ours')), ['top', 'o1', 'o2', 'o3', 'keep 1', 'keep 2', 'keep 3', 'keep 4', 'p', 'end', ''].join(nl));
            assert.equal(await page.evaluate(modelIn('merge-theirs')), ['top', 't1', 't2', 'keep 1', 'keep 2', 'keep 3', 'keep 4', 'q', 'end', ''].join(nl));
            assert.match(await page.evaluate(modelIn('merge-result')), /^<{7} HEAD$/m);

            // a real click on a box picks a line: o3 and o1 from ours, t2 from theirs (the boxes are in the order of the lines)
            const clickIn = async (selector, nth) => {
                const at = await page.evaluate(`(() => { const r = [...document.querySelectorAll(${JSON.stringify(selector)})].map((e) => e.getBoundingClientRect()).sort((a, b) => a.top - b.top)[${nth}]; return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
                await page.click(at.x, at.y);
            };
            await clickIn('.merge-ours .merge-pick', 2);
            await page.waitFor(`document.querySelectorAll('.merge-ours .merge-pick.on').length === 1`, 5000, 'the first pick');
            await clickIn('.merge-ours .merge-pick', 0);
            await clickIn('.merge-theirs .merge-pick', 1);
            await page.waitFor(`document.querySelector('.merge-card .merge-take-picked')?.textContent === 'Take chosen lines (3)'`, 5000, 'three lines picked');
            await page.evaluate(`document.querySelector('.merge-card .merge-take-picked').click()`);
            await page.waitFor(`document.querySelectorAll('.merge-box .merge-card').length === 1`, 5000, 'the first conflict settled');
            // the second, with the arrow on theirs (Monaco draws the margin again on its next frame: the click is tried until the arrow is where it is drawn)
            const settled = `document.querySelector('.merge-box .hist-diff-title')?.textContent.includes('every conflict is settled')`;
            for (let i = 0; i < 5 && !(await page.evaluate(settled)); i++) {
                await sleep(300);
                await clickIn('.merge-theirs .merge-take-theirs', 0);
                await page.waitFor(settled, 1500, 'nothing left').catch(() => { });
            }
            await page.waitFor(settled, 2000, 'nothing left');
            const merged = ['top', 'o1', 'o3', 't2', 'keep 1', 'keep 2', 'keep 3', 'keep 4', 'q', 'end', ''].join(nl);
            assert.equal(await page.evaluate(modelIn('merge-result')), merged);
            // with nothing left, the sides are the result
            assert.equal(await page.evaluate(modelIn('merge-ours')), merged);
            await page.evaluate(`[...document.querySelectorAll('.merge-box button')].find((b) => b.textContent === 'Apply').click()`);
            for (let i = 0; i < 30 && git('diff', '--name-only', '--diff-filter=U').trim(); i++) await sleep(300);
            assert.equal(git('diff', '--name-only', '--diff-filter=U').trim(), '', 'marked resolved');
            assert.equal(readFileSync(join(projectRoot, 'merge_demo.txt'), 'utf8'), merged);
            git('commit', '-q', '--no-edit');
        } catch (e) {
            // the tests after this one need the window closed and no merge going on
            await page.evaluate(`document.querySelectorAll('.settings-shade').forEach((s) => s.remove())`);
            spawnSync('git', ['merge', '--abort'], { cwd: projectRoot });
            throw e;
        }
    });
});

// A GitHub of the test's own: the few calls the Pull Requests window makes, answered from memory, and everything it was sent written down.
// The engine reaches it through PYMAGIC_GITHUB_API; the real github.com is never contacted, and the token below is made up for this run.
const FORGE_TOKEN = 'e2e-fake-token-4711';
function startFakeForge() {
    const state = { requests: [], head: 'abc123def4567890', merged: false, base: '', many: false };
    const send = (res, code, body) => { res.writeHead(code, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)); };
    const pr = () => ({
        number: 7, title: 'Add the cart', user: { login: 'ada' }, state: state.merged ? 'closed' : 'open', merged: state.merged, draft: false,
        head: { ref: 'cart', sha: state.head }, base: { ref: 'main' }, html_url: 'https://github.com/acme/widgets/pull/7', updated_at: '2026-10-30T10:00:00Z',
        body: 'Adds a cart to the shop.', mergeable: true,
    });
    const server = createServer((req, res) => {
        let body = '';
        req.on('data', (c) => { body += c; });
        req.on('end', () => {
            const path = req.url.split('?')[0];
            state.requests.push({ method: req.method, path, body, auth: req.headers.authorization ?? '' });
            if (req.headers.authorization !== 'Bearer ' + FORGE_TOKEN) return send(res, 401, { message: 'Bad credentials' });
            const repo = '/repos/acme/widgets';
            const json = body ? JSON.parse(body) : {};
            if (req.method === 'GET' && path === repo + '/pulls') {
                if (!state.many) return send(res, 200, [pr()]);
                // a busy repository: 30 on the first page, 2 on the second
                const second = new URL(req.url, 'http://x').searchParams.get('page') === '2';
                const first = second ? 130 : 100, count = second ? 2 : 30;
                return send(res, 200, Array.from({ length: count }, (_, i) => ({ ...pr(), number: first + i, title: 'Request ' + (first + i) })));
            }
            if (req.method === 'GET' && path === repo + '/pulls/7') return send(res, 200, pr());
            if (req.method === 'GET' && path === repo + '/pulls/7/commits') return send(res, 200, [{ sha: 'c1c1c1c1c1', commit: { message: 'cart: the model\n\nmore', author: { name: 'Ada', date: '2026-10-29T09:00:00Z' } } }]);
            if (req.method === 'GET' && path === repo + '/pulls/7/files') return send(res, 200, [{ filename: 'cart.py', status: 'modified', additions: 2, deletions: 1, patch: '@@ -1,2 +1,3 @@\n def total():\n-    return 0\n+    return len(items)\n+    # counted\n' }]);
            if (req.method === 'GET' && path === repo + '/issues/7/comments') return send(res, 200, [{ user: { login: 'bob' }, body: 'Looks fine to me', created_at: '2026-10-30T11:00:00Z' }]);
            if (req.method === 'GET' && path === repo + '/pulls/7/comments') return send(res, 200, []);
            if (req.method === 'GET' && path === repo + '/commits/' + state.head + '/check-runs') return send(res, 200, { check_runs: [{ name: 'tests', status: 'completed', conclusion: 'success' }] });
            if (req.method === 'GET' && path === repo + '/commits/' + state.head + '/status') return send(res, 200, { state: 'pending', statuses: [] });
            if (req.method === 'POST' && path === repo + '/issues/7/comments') return send(res, 201, { id: 1, body: json.body });
            if (req.method === 'POST' && path === repo + '/pulls/7/reviews') return json.commit_id === state.head ? send(res, 200, { id: 31, state: json.event === 'APPROVE' ? 'APPROVED' : 'COMMENTED' }) : send(res, 422, { message: 'commit_id is not the head' });
            if (req.method === 'POST' && path === repo + '/pulls/7/comments') return send(res, 201, { id: 32, path: json.path, line: json.line, body: json.body });
            if (req.method === 'POST' && path === repo + '/pulls') return send(res, 201, { ...pr(), number: 8, title: json.title });
            if (req.method === 'PUT' && path === repo + '/pulls/7/merge') {
                if (json.sha !== state.head) return send(res, 409, { message: 'Head branch was modified' });
                state.merged = true;
                return send(res, 200, { merged: true, sha: 'merged-sha' });
            }
            send(res, 404, { message: 'Not Found' });
        });
    });
    return new Promise((ok) => server.listen(0, '127.0.0.1', () => {
        state.base = `http://127.0.0.1:${server.address().port}`;
        ok({ base: state.base, state, writes: () => state.requests.filter((r) => r.method !== 'GET'), close: () => server.close() });
    }));
}

describe('pull requests: listing, reading, and the three outward actions behind a confirmation', () => {
    const git = (...args) => {
        const r = spawnSync('git', args, { cwd: projectRoot, encoding: 'utf8' });
        assert.equal(r.status, 0, `git ${args.join(' ')}: ${r.stderr}`);
        return r.stdout;
    };
    const click = (id) => page.evaluate(`${$(id)}.click()`);
    const setValue = (id, value) => page.evaluate(`(() => { const e = document.getElementById(${JSON.stringify(id)}); e.value = ${JSON.stringify(value)}; e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    const confirmText = () => page.evaluate(`document.querySelector('.pr-confirm')?.textContent ?? ''`);
    const api = (url, body) => page.evaluate(`fetch(${JSON.stringify(url)}, ${body ? `{ method: 'POST', body: ${JSON.stringify(JSON.stringify(body))} }` : '{}'}).then((r) => r.json())`);

    test('the remote is found, a token is asked for once and kept in the private file, and the open requests are listed', async () => {
        // an address on github.com is only recorded: nothing here ever fetches from it, and the engine's calls go to the fake above
        git('remote', 'add', 'forge', 'https://github.com/acme/widgets.git');
        git('switch', '-q', '-c', 'cart');
        writeFileSync(join(projectRoot, 'cart.txt'), 'cart\n');
        git('add', '-A');
        git('commit', '-q', '-m', 'Add the cart');
        git('push', '-q', '-u', 'origin', 'cart');
        await click('t-prs');
        await page.waitFor(`document.getElementById('pr-remote').textContent === 'github.com/acme/widgets'`, 15000, 'the remote');
        await page.waitFor(`document.getElementById('pr-list').textContent.includes('Type a personal access token')`, 10000, 'the question for a token');
        assert.equal(forge.state.requests.length, 0, 'nothing is asked of the forge without a token');
        await setValue('pr-token', FORGE_TOKEN);
        await click('pr-token-save');
        await page.waitFor(`document.querySelector('#pr-list .pr-row')`, 15000, 'the list');
        const row = await page.evaluate(`document.querySelector('#pr-list .pr-row').textContent`);
        assert.match(row, /#7/);
        assert.match(row, /Add the cart/);
        assert.match(row, /cart → main/);
        assert.match(row, /ada/);
        assert.equal(await page.evaluate(`document.getElementById('pr-token').value`), '', 'the field is emptied');
        assert.ok(forge.state.requests.every((r) => r.method === 'GET' && r.auth === 'Bearer ' + FORGE_TOKEN));
        // the token is in the private file, which git is told to ignore, and in no other file of the project's .pymagic folder
        const priv = join(projectRoot, '.pymagic', 'pullrequests.private.json');
        assert.ok(readFileSync(priv, 'utf8').includes(FORGE_TOKEN));
        assert.ok(readFileSync(join(projectRoot, '.pymagic', '.gitignore'), 'utf8').split('\n').includes('pullrequests.private.json'));
        assert.ok(!(await api('/api/pr/info').then((i) => JSON.stringify(i))).includes(FORGE_TOKEN), 'the page is never told the token');
        assert.ok(!git('status', '--porcelain').includes('pullrequests'), 'the private file is not offered to git');
    });

    test('a long list is read a page at a time, with Load more for the next', async () => {
        forge.state.many = true;
        try {
            await click('pr-refresh');
            await page.waitFor(`document.querySelectorAll('#pr-list .pr-row').length === 30 && document.getElementById('pr-more')`, 20000, 'the first page and its button');
            await click('pr-more');
            await page.waitFor(`document.querySelectorAll('#pr-list .pr-row').length === 32 && !document.getElementById('pr-more')`, 20000, 'the second page, and no button after it');
            assert.match(await page.evaluate(`[...document.querySelectorAll('#pr-list .pr-row')].pop()?.textContent ?? ''`), /Request 131|Request 130/);
        } finally {
            forge.state.many = false;
            await click('pr-refresh');
            await page.waitFor(`document.querySelectorAll('#pr-list .pr-row').length === 1`, 20000, 'the short list again');
        }
    });

    test('one request is shown in full, and a changed file opens as a side-by-side diff', async () => {
        await page.evaluate(`document.querySelector('#pr-list .pr-row').click()`);
        await page.waitFor(`document.getElementById('pr-detail').textContent.includes('Looks fine to me')`, 15000, 'the detail');
        const text = await page.evaluate(`document.getElementById('pr-detail').textContent`);
        assert.match(text, /Adds a cart to the shop\./);
        assert.match(text, /cart: the model/);
        assert.match(text, /cart\.py/);
        assert.match(text, /\+2 −1/);
        assert.match(text, /ada wants to merge cart into main/);
        await page.evaluate(`document.querySelector('#pr-detail .pr-file').click()`);
        await page.waitFor(`document.querySelector('.pr-diff-shade .monaco-diff-editor')`, 15000, 'the diff');
        const sides = await page.waitFor(`(() => { const models = window.pymagic.monaco.editor.getModels().map((m) => m.getValue()); return models.some((t) => t.includes('return 0')) && models.some((t) => t.includes('return len(items)')) ? models : null; })()`, 10000, 'both sides');
        assert.ok(sides.some((t) => t.includes('# counted')));
        await page.evaluate(`document.querySelector('.pr-diff-shade .hist-diff-head button').click()`);
        await page.waitFor(`!document.querySelector('.pr-diff-shade')`, 5000, 'the diff to close');
    });

    test('a comment shows the exact request first, sends nothing on Cancel, and is sent once on Send', async () => {
        await click('pr-comment');
        await page.waitFor(`document.getElementById('pr-comment-text')`, 5000, 'the form');
        await setValue('pr-comment-text', 'Ship it, please.');
        await page.evaluate(`[...document.querySelectorAll('.pr-form button')].find((b) => b.textContent.startsWith('Review')).click()`);
        await page.waitFor(`document.querySelector('.pr-confirm')`, 10000, 'the confirmation');
        const shown = await confirmText();
        assert.match(shown, /POST .*\/repos\/acme\/widgets\/issues\/7\/comments/);
        assert.match(shown, /"body": "Ship it, please\."/);
        assert.equal(forge.writes().length, 0, 'nothing has been sent yet');
        await page.evaluate(`document.querySelector('.pr-confirm-cancel').click()`);
        await sleep(500);
        assert.equal(forge.writes().length, 0, 'Cancel sends nothing');
        // again, and Send
        await page.evaluate(`[...document.querySelectorAll('.pr-form button')].find((b) => b.textContent.startsWith('Review')).click()`);
        await page.waitFor(`document.querySelector('.pr-confirm-send')`, 10000, 'the confirmation again');
        await page.evaluate(`document.querySelector('.pr-confirm-send').click()`);
        await page.waitFor(`!document.querySelector('.pr-confirm') && !document.querySelector('.pr-form')`, 10000, 'the windows to close');
        const sent = forge.writes();
        assert.equal(sent.length, 1);
        assert.equal(sent[0].path, '/repos/acme/widgets/issues/7/comments');
        assert.deepEqual(JSON.parse(sent[0].body), { body: 'Ship it, please.' });
    });

    test('a review shows the exact request first, needs words to ask for changes, and approves the head that was looked at', async () => {
        const before = forge.writes().length;
        const okButton = `[...document.querySelectorAll('.pr-form button')].find((b) => b.textContent.startsWith('Review')).click()`;
        await click('pr-review');
        await page.waitFor(`document.getElementById('pr-review-verdict')`, 5000, 'the form');
        assert.equal(await page.evaluate(`[...document.getElementById('pr-review-verdict').options].map((o) => o.value).join()`), 'approve,request_changes,comment');
        // asking for changes without a word is refused before anything is prepared
        await setValue('pr-review-verdict', 'request_changes');
        await page.evaluate(okButton);
        assert.match(await page.evaluate(`document.querySelector('.pr-form .db-form-error').textContent`), /few words/);
        assert.equal(await page.evaluate(`!!document.querySelector('.pr-confirm')`), false);
        await setValue('pr-review-verdict', 'approve');
        await page.evaluate(okButton);
        await page.waitFor(`document.querySelector('.pr-confirm')`, 10000, 'the confirmation');
        const shown = await confirmText();
        assert.match(shown, /POST .*\/repos\/acme\/widgets\/pulls\/7\/reviews/);
        assert.match(shown, /"event": "APPROVE"/);
        assert.match(shown, /"commit_id": "abc123def4567890"/);
        assert.equal(forge.writes().length, before, 'nothing has been sent yet');
        await page.evaluate(`document.querySelector('.pr-confirm-send').click()`);
        await page.waitFor(`!document.querySelector('.pr-confirm') && !document.querySelector('.pr-form')`, 10000, 'the windows to close');
        const sent = forge.writes().slice(before);
        assert.equal(sent.length, 1);
        assert.equal(sent[0].path, '/repos/acme/widgets/pulls/7/reviews');
        assert.deepEqual(JSON.parse(sent[0].body), { event: 'APPROVE', commit_id: 'abc123def4567890' });
    });

    test('a comment on a line of a file lists the lines with their numbers and sends the path and the line that were chosen', async () => {
        const before = forge.writes().length;
        await page.evaluate(`document.querySelector('#pr-detail .pr-lines').click()`);
        await page.waitFor(`document.querySelector('.pr-lines-shade .pr-patch-row')`, 10000, 'the lines');
        // the lines of the changes with their real numbers: a plus on those that take a comment, none on the removed line
        const rows = await page.evaluate(`[...document.querySelectorAll('.pr-lines-shade .pr-patch-row')].map((r) => r.className.replace('pr-patch-row ', '') + ':' + (r.querySelector('.pr-patch-plus')?.dataset.line ?? '-')).join()`);
        assert.equal(rows, 'pr-patch-hunk:-,pr-patch-ctx:1,pr-patch-del:-,pr-patch-add:2,pr-patch-add:3');
        await page.evaluate(`document.querySelector('.pr-lines-shade .pr-patch-plus[data-line="3"]').click()`);
        await page.waitFor(`document.getElementById('pr-inline-text')`, 5000, 'the form');
        await setValue('pr-inline-text', 'Is this the count we want?');
        await page.evaluate(`[...document.querySelectorAll('.pr-form button')].find((b) => b.textContent.startsWith('Review')).click()`);
        await page.waitFor(`document.querySelector('.pr-confirm')`, 10000, 'the confirmation');
        const shown = await confirmText();
        assert.match(shown, /POST .*\/repos\/acme\/widgets\/pulls\/7\/comments/);
        assert.match(shown, /"path": "cart\.py"/);
        assert.match(shown, /"line": 3/);
        assert.match(shown, /"side": "RIGHT"/);
        assert.equal(forge.writes().length, before, 'nothing has been sent yet');
        await page.evaluate(`document.querySelector('.pr-confirm-send').click()`);
        await page.waitFor(`!document.querySelector('.pr-confirm') && !document.querySelector('.pr-form') && !document.querySelector('.pr-lines-shade')`, 10000, 'the windows to close');
        const sent = forge.writes().slice(before);
        assert.equal(sent.length, 1);
        assert.equal(sent[0].path, '/repos/acme/widgets/pulls/7/comments');
        assert.deepEqual(JSON.parse(sent[0].body), { body: 'Is this the count we want?', commit_id: 'abc123def4567890', path: 'cart.py', line: 3, side: 'RIGHT' });
    });

    test('a request is created from the current branch, only after the exact request was shown', async () => {
        await click('pr-new');
        await page.waitFor(`document.getElementById('pr-new-title')`, 10000, 'the form');
        assert.equal(await page.evaluate(`document.getElementById('pr-new-title').value`), 'Add the cart', 'the last commit is the starting title');
        assert.ok((await page.evaluate(`[...document.querySelectorAll('#pr-new-base option')].map((o) => o.value)`)).includes('main'));
        await setValue('pr-new-title', 'Cart for the shop');
        await setValue('pr-new-base', 'main');
        await setValue('pr-new-body', 'Adds the cart.');
        const before = forge.writes().length;
        await page.evaluate(`[...document.querySelectorAll('.pr-form button')].find((b) => b.textContent.startsWith('Review')).click()`);
        await page.waitFor(`document.querySelector('.pr-confirm')`, 10000, 'the confirmation');
        const shown = await confirmText();
        assert.match(shown, /POST .*\/repos\/acme\/widgets\/pulls/);
        assert.match(shown, /"head": "cart"/);
        assert.match(shown, /"base": "main"/);
        assert.match(shown, /"title": "Cart for the shop"/);
        assert.equal(forge.writes().length, before, 'shown, not yet sent');
        await page.evaluate(`document.querySelector('.pr-confirm-send').click()`);
        await page.waitFor(`!document.querySelector('.pr-confirm')`, 10000, 'the window to close');
        const made = forge.writes().at(-1);
        assert.equal(made.path, '/repos/acme/widgets/pulls');
        assert.deepEqual(JSON.parse(made.body), { title: 'Cart for the shop', body: 'Adds the cart.', head: 'cart', base: 'main', draft: false });
    });

    test('a merge is pinned to the commit that was looked at, and happens only on Send', async () => {
        await page.evaluate(`document.querySelector('#pr-list .pr-row').click()`);
        await page.waitFor(`document.getElementById('pr-merge') && !document.getElementById('pr-merge').disabled`, 15000, 'the Merge button');
        await setValue('pr-method', 'squash');
        await click('pr-merge');
        await page.waitFor(`document.querySelector('.pr-confirm')`, 10000, 'the confirmation');
        const shown = await confirmText();
        assert.match(shown, /PUT .*\/repos\/acme\/widgets\/pulls\/7\/merge/);
        assert.match(shown, /"merge_method": "squash"/);
        assert.match(shown, new RegExp('"sha": "' + forge.state.head + '"'));
        assert.ok(!forge.state.merged);
        await page.evaluate(`document.querySelector('.pr-confirm-cancel').click()`);
        await sleep(500);
        assert.ok(!forge.state.merged, 'Cancel merges nothing');
        await click('pr-merge');
        await page.waitFor(`document.querySelector('.pr-confirm-send')`, 10000, 'the confirmation again');
        await page.evaluate(`document.querySelector('.pr-confirm-send').click()`);
        await page.waitFor(`!document.querySelector('.pr-confirm')`, 10000, 'the window to close');
        assert.ok(forge.state.merged);
        const put = forge.writes().at(-1);
        assert.deepEqual(JSON.parse(put.body), { merge_method: 'squash', sha: forge.state.head });
    });

    test('the engine itself refuses what was not shown: an unknown token, and one used twice', async () => {
        const before = forge.writes().length;
        const bogus = await api('/api/pr/confirm', { token: 'ffffffffffffffffffffffffffffffff' });
        assert.match(bogus.error, /not valid any more/);
        const empty = await api('/api/pr/confirm', {});
        assert.match(empty.error, /not valid any more/);
        const prepared = await api('/api/pr/prepare', { action: 'comment', number: 7, body: 'Once only.' });
        assert.ok(prepared.token, JSON.stringify(prepared));
        assert.equal(forge.writes().length, before, 'preparing sends nothing');
        const first = await api('/api/pr/confirm', { token: prepared.token });
        assert.equal(first.ok, true);
        const second = await api('/api/pr/confirm', { token: prepared.token });
        assert.match(second.error, /not valid any more/);
        assert.equal(forge.writes().length, before + 1, 'once');
        // nothing was ever sent anywhere but the fake: every call it saw carried the made-up token
        assert.ok(forge.state.requests.every((r) => r.auth === 'Bearer ' + FORGE_TOKEN));
    });
});

// A Jupyter server for the checks below, written here: the REST calls and the WebSocket the notebook uses, nothing more. It has never been
// compared with a real Jupyter server.
function fakeJupyter(token) {
    const calls = [];
    const server = createServer((req, res) => {
        const path = req.url.split('?')[0];
        const ok = req.headers.authorization === 'token ' + token;
        calls.push(`${req.method} ${path} ${ok ? 'ok' : 'denied'}`);
        if (!ok) { res.writeHead(403).end(); return; }
        if (req.method === 'GET' && path === '/api/kernelspecs') { res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({ default: 'python3', kernelspecs: { python3: {} } })); return; }
        if (req.method === 'POST' && path === '/api/kernels') { res.writeHead(201, { 'content-type': 'application/json' }).end(JSON.stringify({ id: 'k1', name: 'python3' })); return; }
        if (req.method === 'DELETE') { res.writeHead(204).end(); return; }
        res.writeHead(404).end();
    });
    server.on('upgrade', (req, socket) => {
        if (req.headers.authorization !== 'token ' + token) { socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return; }
        calls.push('WS ' + req.url.split('?')[0]);
        const accept = createHash('sha1').update(req.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
        socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
        const send = (obj) => {
            const data = Buffer.from(JSON.stringify(obj));
            const head = data.length < 126 ? Buffer.from([0x81, data.length]) : data.length < 65536 ? Buffer.from([0x81, 126, data.length >> 8, data.length & 255]) : null;
            socket.write(Buffer.concat([head, data]));
        };
        const msg = (type, parent, content, channel) => ({ header: { msg_id: Math.random().toString(16).slice(2), msg_type: type, session: 's', username: 'u', version: '5.3' }, parent_header: { msg_id: parent }, metadata: {}, content, channel, buffers: [] });
        let count = 0;
        const handle = (m) => {
            const id = m.header.msg_id;
            if (m.header.msg_type === 'kernel_info_request') send(msg('kernel_info_reply', id, { status: 'ok', language_info: { name: 'python', version: '3.99.9' } }, 'shell'));
            if (m.header.msg_type === 'execute_request') {
                calls.push('execute ' + JSON.stringify(m.content.code));
                send(msg('status', id, { execution_state: 'busy' }, 'iopub'));
                send(msg('stream', id, { name: 'stdout', text: 'from the fake Jupyter server\n' }, 'iopub'));
                send(msg('execute_reply', id, { status: 'ok', execution_count: ++count }, 'shell'));
                send(msg('status', id, { execution_state: 'idle' }, 'iopub'));
            }
        };
        let buf = Buffer.alloc(0);
        socket.on('data', (chunk) => {
            buf = Buffer.concat([buf, chunk]);
            for (;;) {
                if (buf.length < 2) return;
                const opcode = buf[0] & 15;
                let len = buf[1] & 127, at = 2;
                if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); at = 4; }
                else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); at = 10; }
                if (buf.length < at + 4 + len) return;
                const mask = buf.subarray(at, at + 4);
                const payload = Buffer.from(buf.subarray(at + 4, at + 4 + len)).map((b, i) => b ^ mask[i % 4]);
                buf = buf.subarray(at + 4 + len);
                if (opcode === 8) { socket.end(); return; }
                if (opcode === 1) handle(JSON.parse(payload.toString('utf8')));
            }
        });
        socket.on('error', () => { });
    });
    return { server, calls };
}

// last: the notebook of the Open work list, which turns the file into a kernel session, debugs it and exports it
describe('a notebook: objects in the Variables window, Debug Cell, the console, Open in browser and the Jupyter servers', () => {
    const REL = 'nb_more.ipynb';
    const nb = (expr) => page.evaluate(`(() => { const s = window.pymagic.notebook.stateOf(${JSON.stringify(REL)}); return ${expr}; })()`);
    const runCell = async (index) => {
        await nb(`(s.views[${index}].el.querySelector('.nb-tools button').click(), 1)`);
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).nb.cells[${index}].execution_count`, 60000, `cell ${index} run`);
    };
    const rows = () => nb(`[...s.varsEl.querySelectorAll('tr[data-path]')].map((r) => r.querySelector('.nb-var-label').textContent + '|' + r.children[3].textContent)`);
    const caretOf = (name) => nb(`(([...s.varsEl.querySelectorAll('tr[data-path]')].find((r) => r.querySelector('.nb-var-label').textContent === ${JSON.stringify(name)})?.querySelector('button.nb-var-caret'))?.click(), 1)`);

    test('an object, a dict and a list open one level at a time, from the kernel', async () => {
        await open(REL);
        await page.waitFor(`document.querySelector('#notebook.active .nb-cell')`, 15000, 'the notebook');
        await runCell(0);
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).varsEl.textContent.includes('Variables (3)')`, 15000, 'the variables');
        const first = await rows();
        assert.ok(first.includes("d|{'one': [1, 2], 'two': 7}"), first.join(''));
        assert.ok(first.some((r) => r.startsWith('p|<')), first.join(''));
        await caretOf('d');
        const open1 = await page.waitFor(`(() => { const r = window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).varsEl.querySelectorAll('tr[data-path]'); return r.length === 5 && r.length; })()`, 15000, 'the members of d');
        assert.equal(open1, 5);
        assert.deepEqual((await rows()).slice(2, 4), ["'one'|[1, 2]", "'two'|7"]);
        await caretOf("'one'");
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).varsEl.querySelectorAll('tr[data-path]').length === 7`, 15000, 'the members of the list');
        assert.deepEqual((await rows()).slice(2, 6), ["'one'|[1, 2]", '0|1', '1|2', "'two'|7"]);
        await caretOf('p');
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).varsEl.textContent.includes('tags')`, 15000, 'the attributes of p');
        const all = await rows();
        assert.ok(all.includes('x|3') && all.includes("tags|['a', 'b']"), all.join('\n'));
        // a run asks again for what is open: the list changed in a cell shows its new length
        await nb(`(s.views[0].editor ?? (s.views[0].el.__create(), s.views[0].editor), 1)`);
        await nb(`(s.views[0].editor.executeEdits('t', [{ range: s.views[0].editor.getModel().getFullModelRange(), text: s.views[0].editor.getValue() + "\\nd['one'].append(9)" }]), 1)`);
        await runCell(0);
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).varsEl.textContent.includes('[1, 2, 9]')`, 20000, 'the refreshed members');
        assert.ok((await rows()).includes('2|9'), (await rows()).join('\n'));
    });

    test('Debug Cell stops at a breakpoint in the margin of the cell, shows it in the cell, steps, and runs on', async () => {
        await nb(`(s.views[1].el.__create(), 1)`);
        await page.waitFor(`!!window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).views[1].editor`, 10000, 'the editor of the cell');
        // F9 in the cell: a breakpoint on line 2 (the cell's glyph margin)
        await nb(`(s.views[1].editor.setPosition({ lineNumber: 2, column: 1 }), s.views[1].editor.focus(), 1)`);
        await page.key(120, 'F9');
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).views[1].editor.getModel().getLineDecorations(2).some((d) => d.options.glyphMarginClassName?.startsWith('bp-glyph'))`, 5000, 'the breakpoint');
        await nb(`(s.views[1].el.querySelector('.nb-debug-btn').click(), 1)`);
        await page.waitFor(`${$('runstate')}.textContent === 'paused'`, 60000, 'the stop in the cell');
        // the stop is drawn in the cell, on the line of the breakpoint
        assert.equal(await nb(`s.views[1].editor.getModel().getAllDecorations().filter((d) => d.options.className === 'debug-line').map((d) => d.range.startLineNumber).join()`), '2');
        assert.match(await page.evaluate(`${$('frames')}.textContent`), /nb_more_ipynb\.cell2\.py/);
        const vars = await page.waitFor(`(() => { const t = ${$('vars')}.textContent; return t.includes('a') && t.includes('4') && t; })()`, 15000, 'the variables of the stop');
        assert.match(vars, /a/);
        // step over to line 3; the cell is still not done
        await page.evaluate(`${$('b-over')}.click()`);
        await page.waitFor(`${$('runstate')}.textContent === 'paused' && window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).views[1].editor.getModel().getAllDecorations().some((d) => d.options.className === 'debug-line' && d.range.startLineNumber === 3)`, 20000, 'the step');
        assert.equal(await nb(`s.nb.cells[1].execution_count`), null);
        // remove the breakpoint and resume: the cell runs to its end, with its output in the cell
        await nb(`(s.views[1].editor.setPosition({ lineNumber: 2, column: 1 }), s.views[1].editor.focus(), 1)`);
        await page.key(120, 'F9');
        await page.evaluate(`${$('b-debug')}.click()`);
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).nb.cells[1].execution_count`, 60000, 'the end of the cell');
        assert.equal(await nb(`s.nb.cells[1].outputs[0].text.join('')`), 'c = 9\n');
        await page.waitFor(`${$('b-stop')}.disabled`, 15000, 'the end of the debug session');
        // the cell's names are the notebook's: the next cell sees them, and runs without the debugger
        assert.equal(await nb(`s.views[1].editor.getModel().getAllDecorations().filter((d) => d.options.className === 'debug-line').length`), 0);
    });

    test('the console runs a line in the namespace of the cells and the Variables window follows', async () => {
        await nb(`(s.consoleBtn.click(), 1)`);
        await page.waitFor(`!!window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).consoleInput`, 5000, 'the console');
        const type = (text) => page.evaluate(`(() => { const i = window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).consoleInput; i.value = ${JSON.stringify(text)};
            i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); return true; })()`);
        await type('a + 100');
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).consoleEl.querySelector('.nb-console-out')?.textContent.includes('104')`, 30000, 'the value');
        await type('late = [a, b, c]');
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).varsEl.textContent.includes('late')`, 30000, 'the new name in the Variables window');
        await type('print(late)');
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).consoleEl.textContent.includes('[4, 8, 9]')`, 30000, 'the printed list');
        // a line that is not finished (an open bracket) waits for more instead of running
        assert.equal(await page.evaluate(`(() => { const s = window.pymagic.notebook.stateOf(${JSON.stringify(REL)}); const n = s.consoleEl.querySelectorAll('.nb-console-entry').length;
            s.consoleInput.value = 'x = ['; s.consoleInput.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
            return s.consoleEl.querySelectorAll('.nb-console-entry').length - n; })()`), 0);
        await page.evaluate(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).consoleInput.value = ''`);
        // Up recalls the line
        await page.evaluate(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).consoleInput.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true, cancelable: true }))`);
        assert.equal(await nb(`s.consoleInput.value`), 'print(late)');
        // Tab finishes the name under the cursor with the kernel's words; when several fit, they are listed and a click picks one
        const tab = (text) => page.evaluate(`(() => { const i = window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).consoleInput; i.value = ${JSON.stringify(text)}; i.selectionStart = i.selectionEnd = i.value.length;
            i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true })); return true; })()`);
        await tab('print(lat');
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).consoleInput.value === 'print(late'`, 15000, 'one word: late');
        await tab('late.app');
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).consoleInput.value === 'late.append'`, 15000, 'an attribute of the list');
        await tab('late.re');
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).consoleHints.querySelectorAll('button').length === 2`, 15000, 'two words listed');
        assert.equal(await nb(`[...s.consoleHints.querySelectorAll('button')].map((b) => b.textContent).join()`), 'remove,reverse');
        await nb(`(s.consoleHints.querySelector('button').click(), 1)`);
        assert.equal(await nb(`s.consoleInput.value`), 'late.remove');
        assert.equal(await nb(`s.consoleHints.childElementCount`), 0, 'the list closes after a pick');
        await nb(`(s.consoleBtn.click(), s.consoleInput.value = '', 1)`);
    });

    test('Open in browser makes a page of the notebook that no script can run in', async () => {
        await page.evaluate(`window.__opened = []; window.open = (u) => { window.__opened.push(u); return null; }`);
        await nb(`(s.barEl.querySelector('button[title^="Show the notebook as a page"]').click(), 1)`);
        const url = await page.waitFor(`window.__opened[0]`, 15000, 'the page being opened');
        assert.match(url, /\/api\/notebook\/view\?id=[0-9a-f]{24}$/);
        const got = await page.evaluate(`fetch(${JSON.stringify(url)}).then(async (r) => ({ status: r.status, type: r.headers.get('content-type'), csp: r.headers.get('content-security-policy'), text: await r.text() }))`);
        assert.equal(got.status, 200);
        assert.match(got.type, /text\/html/);
        assert.match(got.csp, /sandbox/);
        assert.match(got.csp, /script-src 'none'/);
        assert.ok(got.text.includes('<title>nb_more.ipynb</title>') && got.text.includes('<h1>Report</h1>') && got.text.includes('c = 9'), got.text.slice(0, 600));
        assert.ok(!got.text.includes('<script'), 'there is no script in the page');
        // the code is coloured and the formula is MathML
        assert.ok(got.text.includes('<span class="tok-k">class</span>'), 'keywords are marked');
        assert.ok(got.text.includes('<msubsup><mi>x</mi><mn>1</mn><mn>2</mn></msubsup>'), 'the formula is MathML');
        // and the browser (Chromium here) really draws them: a keyword in its own colour and a formula with a size
        const drawn = await page.evaluate(`(async () => { const f = document.createElement('iframe'); f.style.cssText = 'position:fixed;left:0;top:0;width:900px;height:900px'; f.srcdoc = ${JSON.stringify(got.text)};
            const loaded = new Promise((ok) => { f.onload = ok; }); document.body.append(f); await loaded; const d = f.contentDocument;
            const out = { kw: getComputedStyle(d.querySelector('.tok-k')).color, body: getComputedStyle(d.body).color, w: d.querySelector('math').getBoundingClientRect().width, h: d.querySelector('math').getBoundingClientRect().height };
            f.remove(); return out; })()`);
        assert.notEqual(drawn.kw, drawn.body, 'a keyword has a colour of its own');
        assert.ok(drawn.w > 8 && drawn.h > 8, `the formula has a size: ${JSON.stringify(drawn)}`);
        // the address is not a way to read anything else
        assert.equal(await page.evaluate(`fetch('/api/notebook/view?id=0000').then((r) => r.status)`), 404);
    });

    test('the servers window starts a Jupyter server on this computer, with a token, and stops it', async () => {
        await nb(`(s.serverBtn.click(), 1)`);
        await page.waitFor(`document.getElementById('nb-server-local-start')`, 5000, 'the servers window');
        await page.evaluate(`document.getElementById('nb-server-local-start').click()`);
        await page.waitFor(`/runs on this computer \\(http:\\/\\/127\\.0\\.0\\.1:\\d+\\)/.test(document.querySelector('.nb-server-msg')?.textContent ?? '')`, 60000, 'the server to start');
        assert.match(await page.evaluate(`document.querySelector('.nb-servers').textContent`), /This computer \(Jupyter\)/);
        const status = await page.evaluate(`fetch('/api/jupyter/local').then((r) => r.json())`);
        assert.equal(status.running, true);
        assert.match(status.url, /^http:\/\/127\.0\.0\.1:\d+$/);
        // the entry that was made is a server the window can test: it answers with the token that was made for it
        await page.evaluate(`[...document.querySelectorAll('.nb-server-row')].find((r) => r.textContent.includes('This computer')).querySelector('button').click()`);
        await page.waitFor(`/Connected/.test(document.querySelector('.nb-server-msg')?.textContent ?? '')`, 20000, 'the test of the new server');
        await page.evaluate(`document.getElementById('nb-server-local-stop').click()`);
        await page.waitFor(`/was stopped/.test(document.querySelector('.nb-server-msg')?.textContent ?? '')`, 10000, 'the stop');
        assert.equal((await page.evaluate(`fetch('/api/jupyter/local').then((r) => r.json())`)).running, false);
        // the entry goes: a stopped server with its one-time token is of no use
        await page.evaluate(`[...document.querySelectorAll('.nb-server-row')].find((r) => r.textContent.includes('This computer')).querySelectorAll('button')[1].click()`);
        await page.evaluate(`[...document.querySelectorAll('.ask-buttons button')].find((b) => b.textContent === 'Close').click()`);
    });

    test('the server selector adds a Jupyter server by address and token and runs the notebook on it', async () => {
        const jupyter = fakeJupyter('tok-123');
        await new Promise((ok) => jupyter.server.listen(0, '127.0.0.1', ok));
        const url = `http://127.0.0.1:${jupyter.server.address().port}`;
        try {
            await nb(`(s.serverBtn.click(), 1)`);
            await page.waitFor(`document.querySelector('.nb-servers #nb-server-url')`, 5000, 'the servers window');
            assert.match(await page.evaluate(`document.querySelector('.nb-servers').textContent`), /Built-in kernel/);
            // the address as Jupyter prints it, token and all; a wrong token is refused when tested
            await page.evaluate(`(() => { const set = (id, v) => { const e = document.getElementById(id); e.value = v; }; set('nb-server-url', ${JSON.stringify(url + '/lab?token=wrong')}); set('nb-server-name', 'Fake'); })()`);
            await page.evaluate(`[...document.querySelectorAll('.nb-servers button')].find((b) => b.textContent === 'Test').click()`);
            await page.waitFor(`document.querySelector('.nb-server-msg.bad')?.textContent.includes('refused the token')`, 15000, 'the refusal');
            await page.evaluate(`document.getElementById('nb-server-token').value = 'tok-123'`);
            await page.evaluate(`[...document.querySelectorAll('.nb-servers button')].find((b) => b.textContent === 'Test').click()`);
            await page.waitFor(`document.querySelector('.nb-server-msg:not(.bad)')?.textContent.includes('python3')`, 15000, 'the connection');
            await page.evaluate(`document.getElementById('nb-server-add').click()`);
            await page.evaluate(`document.getElementById('nb-server-use').click()`);
            await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).serverBtn.textContent.includes('Fake')`, 5000, 'the selector');
            // the token is not in the saved address
            assert.equal(await page.evaluate(`JSON.parse(localStorage.getItem('pm.jupyterServers'))[0].url`), url);
            // a cell now runs on the server, over its WebSocket, and its output is the server's
            await nb(`(s.views[1].el.querySelector('.nb-tools button').click(), 1)`);
            await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).nb.cells[1].outputs[0]?.text.join('').includes('fake Jupyter')`, 30000, 'the output of the server');
            assert.match(await nb(`s.statusEl.textContent`), /3\.99\.9/);
            assert.ok(jupyter.calls.includes('POST /api/kernels ok') && jupyter.calls.includes('WS /api/kernels/k1/channels'), jupyter.calls.join('\n'));
            assert.ok(jupyter.calls.some((c) => c.startsWith('execute "a = 4')), jupyter.calls.join('\n'));
            assert.ok(!jupyter.calls.some((c) => c.endsWith('denied') && !c.startsWith('GET /api/kernelspecs')), 'the token was sent on every call');
            // this stand-in server cannot be asked what the cells left (it is no IPython kernel), so the Variables window says it asks and waits; Debug Cell needs the built-in kernel
            assert.match(await nb(`s.varsEl.textContent`), /asked of the Jupyter server/);
            // back to the built-in kernel: the server's kernel is deleted
            await nb(`(s.serverBtn.click(), 1)`);
            await page.waitFor(`document.querySelector('.nb-servers #nb-server-use')`, 5000, 'the servers window');
            await page.evaluate(`document.querySelector('.nb-servers input[type=radio]').click()`);
            await page.evaluate(`document.getElementById('nb-server-use').click()`);
            await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).serverBtn.textContent.includes('Built-in')`, 5000, 'the built-in kernel');
            for (let i = 0; i < 30 && !jupyter.calls.includes('DELETE /api/kernels/k1 ok'); i++) await sleep(200);
            assert.ok(jupyter.calls.includes('DELETE /api/kernels/k1 ok'), jupyter.calls.join('\n'));
        } finally { jupyter.server.close(); jupyter.server.closeAllConnections?.(); }
    });
});

// a repository of its own for a test: the project as it is, committed as 'start' on main (PyMagic's own files, which change while it runs, stay out)
function freshRepo() {
    const git = (...args) => {
        const r = spawnSync('git', args, { cwd: projectRoot, encoding: 'utf8' });
        assert.equal(r.status, 0, `git ${args.join(' ')}: ${r.stderr}`);
    };
    rmSync(join(projectRoot, '.git'), { recursive: true, force: true });
    git('init', '-q', '-b', 'main');
    git('config', 'user.name', 'Tester');
    git('config', 'user.email', 'tester@example.com');
    git('config', 'core.autocrlf', 'false');
    writeFileSync(join(projectRoot, '.git', 'info', 'exclude'), ['.pymagic/', 'nb_demo.ipynb', ''].join(String.fromCharCode(10)));
    git('add', '-A');
    git('commit', '-q', '-m', 'start');
}

describe('git: the Log graph, its filters and actions, and the toolbar on a change marker', () => {
    const git = (...args) => {
        const r = spawnSync('git', args, { cwd: projectRoot, encoding: 'utf8' });
        assert.equal(r.status, 0, `git ${args.join(' ')}: ${r.stderr}`);
        return r.stdout;
    };
    const setField = (id, value) => page.evaluate(`(() => { const e = ${$(id)}; e.value = ${JSON.stringify(value)}; e.dispatchEvent(new Event(e.tagName === 'SELECT' || e.type === 'date' ? 'change' : 'input')); return true; })()`);
    const rowCount = `document.querySelectorAll('#git-log .gl[data-hash]').length`;
    test('the log draws a graph with merge lines and labels, filters, and acts on a commit', async () => {
        freshRepo();
        git('config', 'user.name', 'Tester');
        git('config', 'user.email', 'tester@example.com');
        git('switch', '-q', 'main');
        git('switch', '-q', '-c', 'topic');
        writeFileSync(join(projectRoot, 'topic.txt'), 'topic\n');
        git('add', '-A');
        git('commit', '-q', '-m', 'topic work', '--author=Zed Other <zed@example.com>');
        git('switch', '-q', 'main');
        writeFileSync(join(projectRoot, 'main2.txt'), 'main\n');
        git('add', '-A');
        git('commit', '-q', '-m', 'main work');
        git('merge', '-q', '--no-ff', 'topic', '-m', 'merge topic');
        git('tag', 'v2');
        await page.evaluate(`window.confirm = () => false; window.prompt = () => 'from-log'`);
        await page.evaluate(`${$('t-git')}.click()`);
        await page.evaluate(`${$('git-refresh')}.click()`);
        await page.waitFor(`document.querySelectorAll('#git-log .gl .graph circle').length >= 4`, 20000, 'the graph').catch(async (e) => { throw new Error(e.message + ' ' + (await page.evaluate(`(async () => document.getElementById('git-log').outerHTML.slice(0, 1500) + document.body.innerText.slice(-300) + await (await fetch('/api/git/log?limit=5&skip=0')).text() + await (await fetch('/api/git/status')).text())()`))); });
        // every row has its dot, and the merge made a second lane: some line is not straight
        assert.equal(await page.evaluate(`${rowCount} === document.querySelectorAll('#git-log .gl .graph circle').length`), true);
        assert.equal(await page.evaluate(`[...document.querySelectorAll('#git-log .gl .graph path')].some((p) => /C/.test(p.getAttribute('d')))`), true);
        assert.equal(await page.evaluate(`[...document.querySelectorAll('#git-log .gl .ref.tag')].some((r) => r.textContent === 'v2')`), true);
        assert.equal(await page.evaluate(`document.querySelector('#git-log .gl .ref.current')?.textContent`), 'main');

        // filters: the author, then a path, then the words, then a branch; a filtered list has its graph too
        await setField('git-log-author', 'zed');
        await page.waitFor(`${rowCount} === 1`, 10000, 'the author filter');
        assert.equal(await page.evaluate(`document.querySelectorAll('#git-log .gl .graph circle').length`), 1);
        await page.evaluate(`${$('git-log-clear')}.click()`);
        await page.waitFor(`${rowCount} >= 4`, 10000, 'the cleared filter');
        await setField('git-log-paths', 'main2.txt');
        await page.waitFor(`${rowCount} === 1`, 10000, 'the path filter');
        assert.match(await page.evaluate(`document.querySelector('#git-log .gl .subject').textContent`), /main work/);
        assert.equal(await page.evaluate(`document.querySelectorAll('#git-log .gl .graph circle').length`), 1);
        await setField('git-log-author', 'zed');
        await page.waitFor(`${rowCount} === 0`, 10000, 'the path and the author together');
        await page.evaluate(`${$('git-log-clear')}.click()`);
        await page.waitFor(`${rowCount} >= 4`, 10000, 'the cleared filter');
        await setField('git-log-author', 'zed');
        await page.waitFor(`${rowCount} === 1`, 10000, 'the author filter again');
        assert.match(await page.evaluate(`document.querySelector('#git-log .gl .subject').textContent`), /topic work/);
        await page.evaluate(`${$('git-log-clear')}.click()`);
        await page.waitFor(`${rowCount} >= 4`, 10000, 'the cleared filter');
        await setField('git-log-text', 'merge');
        await page.waitFor(`${rowCount} === 1`, 10000, 'the text filter');
        await page.evaluate(`${$('git-log-clear')}.click()`);
        await page.waitFor(`${rowCount} >= 4`, 10000, 'the cleared filter');
        await setField('git-log-branch', 'topic');
        await page.waitFor(`!document.querySelector('#git-log .gl .subject[title="main work"]') && document.querySelector('#git-log .gl .subject[title="topic work"]')`, 10000, 'the branch filter');
        await page.evaluate(`${$('git-log-clear')}.click()`);
        await page.waitFor(`${rowCount} >= 4`, 10000, 'the cleared filter');

        // a commit: its files, a branch from it, and a checkout of it
        const clickRow = (subject) => page.evaluate(`[...document.querySelectorAll('#git-log .gl')].find((r) => r.querySelector('.subject')?.textContent === ${JSON.stringify(subject)}).click()`);
        const act = (label) => page.evaluate(`[...document.querySelectorAll('#git-detail .git-acts button')].find((b) => b.textContent === ${JSON.stringify(label)}).click()`);
        await clickRow('main work');
        await page.waitFor(`document.querySelector('#git-detail .gc .path')?.textContent === 'main2.txt'`, 10000, 'the files of the commit');
        await act('New branch here…');
        for (let i = 0; i < 30 && !git('branch').includes('from-log'); i++) await sleep(300);
        assert.equal(git('rev-parse', 'from-log').trim(), git('log', '--format=%H', '--grep=main work', '-1').trim());
        assert.equal(git('branch', '--show-current').trim(), 'main');
        await page.waitFor(`${rowCount} >= 4`, 10000, 'the log again');
        await clickRow('start');
        await page.waitFor(`document.querySelector('#git-detail .meta')?.textContent.startsWith(${JSON.stringify(git('log', '--format=%H', '--grep=^start$', '-1').trim())})`, 10000, 'the actions');
        // a branch that points at the commit is switched to; with none, HEAD is detached (an earlier test may have left main there)
        const startHash = git('log', '--format=%H', '--grep=^start$', '-1').trim();
        const pointing = git('branch', '--points-at', startHash, '--format=%(refname:short)').trim().split(String.fromCharCode(10))[0];
        await act('Checkout');
        for (let i = 0; i < 30 && git('branch', '--show-current').trim() !== pointing; i++) await sleep(300);
        assert.equal(git('branch', '--show-current').trim(), pointing);
        git('switch', '-q', 'main');
        await page.evaluate(`${$('git-refresh')}.click()`);
    });

    test('a click on a change marker shows Revert, Show diff and the next and previous change', async () => {
        freshRepo();
        await open('hist.py');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; const m = ed.getModel(); const n = m.getLineCount(); const R = window.pymagic.monaco.Range;
            ed.executeEdits('t', [{ range: new R(2, 1, 2, 1), text: '    x = 1' + String.fromCharCode(10) }, { range: new R(n, 1, n, 1), text: '# tail' + String.fromCharCode(10) }]); return true; })()`);
        await page.waitFor(`document.querySelectorAll('.git-bar-added, .git-bar-modified').length >= 2 && window.pymagic.editor.getModel().getLineDecorations(4).some((d) => d.options.linesDecorationsClassName?.startsWith('git-bar'))`, 15000, 'two change markers').catch(async (e) => { throw new Error(e.message + ' ' + (await page.evaluate(`(async () => JSON.stringify([window.pymagic.editor.getModel()?.uri.toString(), window.pymagic.editor.getModel()?.getValue().slice(0, 200), document.querySelectorAll('.git-bar-added, .git-bar-modified, .git-bar-deleted').length, await (await fetch('/api/git/status')).text(), document.querySelector('.settings-shade, .ask-shade') ? 'a window is open' : '']))()`))); });
        const clickBar = async (nth) => {
            const at = await page.evaluate(`(() => { const r = [...document.querySelectorAll('.git-bar-added, .git-bar-modified')].map((b) => b.getBoundingClientRect()).sort((a, b) => a.top - b.top)[${nth}]; return { x: r.left + 1, y: r.top + r.height / 2 }; })()`);
            await page.click(at.x, at.y);
            await page.waitFor(`document.querySelector('.git-popup')`, 5000, 'the popup');
        };
        await clickBar(0);
        assert.equal(await page.evaluate(`[...document.querySelectorAll('.git-popup button')].map((b) => b.textContent).join(',')`), 'Revert,Show diff,Commit lines…,↑,↓');
        // next: the cursor goes to the second change and the popup follows it
        await page.evaluate(`document.querySelector('.git-popup .git-next-change').click()`);
        await page.waitFor(`window.pymagic.editor.getPosition().lineNumber >= 4 && document.querySelector('.git-popup')`, 5000, 'the second change').catch(async (e) => { throw new Error(e.message + ' ' + (await page.evaluate(`JSON.stringify([window.pymagic.editor.getPosition(), document.querySelector('.git-popup')?.textContent, [...document.querySelectorAll('.git-bar-added, .git-bar-modified, .git-bar-deleted')].map((b) => b.className + Math.round(b.getBoundingClientRect().top)),window.pymagic.editor.getModel().getValue()])`))); });
        // revert that one: the tail goes, the first stays
        await page.evaluate(`document.querySelector('.git-popup .git-revert-change').click()`);
        await page.waitFor(`!window.pymagic.editor.getModel().getValue().includes('# tail') && window.pymagic.editor.getModel().getValue().includes('x = 1')`, 5000, 'the revert');
        await clickBar(0);
        await page.evaluate(`document.querySelector('.git-popup .git-show-diff').click()`);
        await page.waitFor(`${$('git-diff')}.classList.contains('open')`, 10000, 'the diff');
        await page.evaluate(`${$('git-diff-close')}.click()`);
    });
});

describe('changelists, the shelf, and choosing lines from the editor gutter', () => {
    const git = (...args) => {
        const r = spawnSync('git', args, { cwd: projectRoot, encoding: 'utf8' });
        assert.equal(r.status, 0, `git ${args.join(' ')}: ${r.stderr}`);
        return r.stdout;
    };
    const NL = String.fromCharCode(10);
    const numbered = (n, ...changes) => {
        const lines = Array.from({ length: n }, (_, i) => `line ${i + 1}`);
        for (const [at, text] of changes) lines[at - 1] = text;
        return lines.join(NL) + NL;
    };
    // the changelist each row of the changes list sits under, read from the page
    const listsOnPage = () => page.evaluate(`(() => { const out = {}; let list = '';
        for (const el of document.getElementById('git-changes').children) {
            if (el.classList.contains('gc-list')) list = el.dataset.list;
            else if (el.classList.contains('gc')) out[el.querySelector('.path').textContent.replace(/[0-9]+.[0-9]+ lines$/, '')] = list;
        } return out; })()`);
    const refresh = async () => { await page.evaluate(`${$('git-refresh')}.click()`); await sleep(400); };
    const waitRows = (expr, label) => page.waitFor(`(() => { const rows = [...document.querySelectorAll('#git-changes .gc')].map((r) => r.querySelector('.path').textContent); return ${expr}; })()`, 15000, label);
    const clickRowButton = (file, cls) => page.evaluate(`(() => { const row = [...document.querySelectorAll('#git-changes .gc')].find((r) => r.querySelector('.path').textContent.startsWith(${JSON.stringify(file)}));
        row.querySelector(${JSON.stringify('button.' + cls)}).click(); return true; })()`);
    const status = () => git('status', '--porcelain').split(NL).filter(Boolean).map((l) => l.slice(3)).sort();

    test('a changelist commits only its own files, the shelf puts files away and back, and the gutter picks lines', async () => {
        freshRepo();
        // the change-marker test above leaves hist.py edited in the open editor; the file as committed is what this test starts from
        git('checkout', '--', 'hist.py');
        await page.evaluate(`window.pymagic.editor.getModel().setValue(${JSON.stringify(readFileSync(join(projectRoot, 'hist.py'), 'utf8'))})`);
        await sleep(3000);
        git('init', '-q', '-b', 'main');
        git('config', 'user.name', 'Tester');
        git('config', 'user.email', 'tester@example.com');
        git('config', 'core.autocrlf', 'false');
        // PyMagic's own state (recent files, the index, the changelists and the shelf) is not part of this repository
        writeFileSync(join(projectRoot, '.git', 'info', 'exclude'), '.pymagic/' + NL + 'nb_demo.ipynb' + NL);
        writeFileSync(join(projectRoot, 'cl_a.py'), numbered(14));
        writeFileSync(join(projectRoot, 'cl_b.py'), numbered(14));
        writeFileSync(join(projectRoot, 'cl_g.py'), numbered(14));
        git('add', '-A');
        git('commit', '-q', '--allow-empty', '-m', 'changelist base');
        writeFileSync(join(projectRoot, 'cl_a.py'), numbered(14, [3, 'line 3 A']));
        writeFileSync(join(projectRoot, 'cl_b.py'), numbered(14, [3, 'line 3 B']));
        // two changes far apart: two hunks
        writeFileSync(join(projectRoot, 'cl_g.py'), numbered(14, [2, 'line 2 G'], [13, 'line 13 G']));

        await page.evaluate(`window.confirm = () => true; window.prompt = () => window.__answer ?? ''`);
        await page.evaluate(`${$('t-git')}.click()`);
        await refresh();
        await waitRows(`['cl_a.py', 'cl_b.py', 'cl_g.py'].every((f) => rows.some((r) => r.startsWith(f)))`, 'the three changed files');
        assert.deepEqual(await listsOnPage(), { 'cl_a.py': 'Changes', 'cl_b.py': 'Changes', 'cl_g.py': 'Changes' });

        // a new changelist becomes the active one: a file that changes now goes into it
        await page.evaluate(`window.__answer = 'Feature'`);
        await page.evaluate(`${$('git-cl-new')}.click()`);
        await page.waitFor(`document.querySelector('.gc-list[data-list="Feature"]')`, 15000, 'the new changelist');
        writeFileSync(join(projectRoot, 'cl_c.py'), 'new = 1' + NL);
        await refresh();
        await waitRows(`rows.some((r) => r.startsWith('cl_c.py'))`, 'the new file');
        assert.equal((await listsOnPage())['cl_c.py'], 'Feature');

        // move a file between lists, with the button of its row
        await clickRowButton('cl_b.py', 'move');
        await page.waitFor(`document.querySelector('.git-popup')`, 5000, 'the move menu');
        await page.evaluate(`[...document.querySelectorAll('.git-popup button')].find((b) => b.textContent === 'Feature').click()`);
        await page.waitFor(`[...document.querySelectorAll('#git-changes .gc-list, #git-changes .gc')].findIndex((e) => e.classList.contains('gc') && e.textContent.includes('cl_b.py')) > [...document.querySelectorAll('#git-changes .gc-list, #git-changes .gc')].findIndex((e) => e.dataset.list === 'Feature')`, 15000, 'cl_b.py under Feature');
        assert.deepEqual(await listsOnPage(), { 'cl_a.py': 'Changes', 'cl_g.py': 'Changes', 'cl_b.py': 'Feature', 'cl_c.py': 'Feature' });
        // kept in a project file
        const saved = JSON.parse(readFileSync(join(projectRoot, '.pymagic', 'changelists.json'), 'utf8'));
        assert.equal(saved.active, 'Feature');
        assert.deepEqual(saved.lists.find((l) => l.name === 'Feature').files.sort(), ['cl_b.py', 'cl_c.py']);

        // the commit takes the files of the chosen list and no others
        await page.evaluate(`(() => { const s = ${$('git-cl-commit')}; s.value = 'Feature'; s.dispatchEvent(new Event('change')); ${$('git-message')}.value = 'the feature'; ${$('git-commit')}.click(); })()`);
        for (let i = 0; i < 40 && git('log', '-1', '--format=%s').trim() !== 'the feature'; i++) await sleep(300);
        assert.deepEqual(git('show', '--name-only', '--format=', 'HEAD').split(NL).filter(Boolean).sort(), ['cl_b.py', 'cl_c.py']);
        assert.deepEqual(status(), ['cl_a.py', 'cl_g.py']);

        // from the gutter: the change bar of the first change of cl_g.py offers to commit just those lines
        await open('cl_g.py');
        await page.evaluate(`${$('t-git')}.click()`);
        await refresh();
        await page.waitFor(`window.pymagic.editor.getModel().getAllDecorations().some((d) => d.options.linesDecorationsClassName?.startsWith('git-bar-'))`, 15000, 'change bars');
        const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.revealLine(2); const box = ed.getContainerDomNode().getBoundingClientRect();
            return { x: box.left + ed.getLayoutInfo().decorationsLeft + 2, y: box.top + ed.getTopForLineNumber(2) - ed.getScrollTop() + 8 }; })()`);
        await page.click(at.x, at.y);
        await page.waitFor(`[...document.querySelectorAll('.git-popup button')].some((b) => b.textContent.startsWith('Commit lines'))`, 8000, 'the gutter popup');
        await page.evaluate(`[...document.querySelectorAll('.git-popup button')].find((b) => b.textContent.startsWith('Commit lines')).click()`);
        await page.waitFor(`${$('git-lines')}.classList.contains('open')`, 10000, 'the lines window');
        assert.match(await page.evaluate(`${$('git-lines-count')}.textContent`), /^2 of 4 changed lines/);
        await page.evaluate(`${$('git-lines-done')}.click()`);
        // cl_a.py stays out: untick it
        await page.evaluate(`[...document.querySelectorAll('#git-changes .gc')].find((r) => r.querySelector('.path').textContent.startsWith('cl_a.py')).querySelector('input').click()`);
        await page.evaluate(`(() => { const s = ${$('git-cl-commit')}; s.value = 'Changes'; s.dispatchEvent(new Event('change')); ${$('git-message')}.value = 'one change of g'; ${$('git-commit')}.click(); })()`);
        for (let i = 0; i < 40 && git('log', '-1', '--format=%s').trim() !== 'one change of g'; i++) await sleep(300);
        assert.equal(git('show', 'HEAD:cl_g.py'), numbered(14, [2, 'line 2 G']));
        assert.equal(readFileSync(join(projectRoot, 'cl_g.py'), 'utf8'), numbered(14, [2, 'line 2 G'], [13, 'line 13 G']));
        assert.deepEqual(status(), ['cl_a.py', 'cl_g.py']);

        // shelve the default list: the files are clean again and a patch is kept
        await refresh();
        await page.evaluate(`window.__answer = 'put away'`);
        await page.evaluate(`[...document.querySelectorAll('#git-changes .gc-list[data-list="Changes"] button')].find((b) => b.textContent === 'Shelve').click()`);
        await page.waitFor(`document.querySelector('#git-shelf .gs-row[data-shelf]')`, 15000, 'the shelved set');
        // the shelf may show the set a moment before the files are back to the last commit
        for (let i = 0; i < 30 && status().length; i++) await sleep(300);
        assert.deepEqual(status(), []);
        assert.equal(readFileSync(join(projectRoot, 'cl_a.py'), 'utf8'), numbered(14));
        assert.equal(readFileSync(join(projectRoot, 'cl_g.py'), 'utf8'), numbered(14, [2, 'line 2 G']));
        assert.match(await page.evaluate(`${$('git-shelf')}.textContent`), /put away.*2 files/);

        // its diff
        await page.evaluate(`[...document.querySelectorAll('#git-shelf .gs-row button')].find((b) => b.textContent === 'Diff').click()`);
        await page.waitFor(`document.querySelector('.shelf-patch')?.textContent.includes('line 13 G')`, 10000, 'the shelved diff');
        assert.match(await page.evaluate(`document.querySelector('.shelf-patch').textContent`), /\+line 3 A/);
        await page.evaluate(`document.querySelectorAll('.ask-shade').forEach((s) => s.remove())`);

        // and back
        await page.evaluate(`[...document.querySelectorAll('#git-shelf .gs-row button')].find((b) => b.textContent === 'Unshelve').click()`);
        await page.waitFor(`!document.querySelector('#git-shelf .gs-row[data-shelf]')`, 15000, 'the shelf empty again');
        assert.deepEqual(status(), ['cl_a.py', 'cl_g.py']);
        assert.equal(readFileSync(join(projectRoot, 'cl_a.py'), 'utf8'), numbered(14, [3, 'line 3 A']));
        assert.equal(readFileSync(join(projectRoot, 'cl_g.py'), 'utf8'), numbered(14, [2, 'line 2 G'], [13, 'line 13 G']));
    });

    test('the changes of a file move one by one to another list, a commit of that list takes just them, and some changes are shelved', async () => {
        writeFileSync(join(projectRoot, 'cp_parts.py'), numbered(30));
        git('add', '-A');
        git('commit', '-q', '-m', 'parts base');
        writeFileSync(join(projectRoot, 'cp_parts.py'), numbered(30, [3, 'line 3 X'], [15, 'line 15 X'], [28, 'line 28 X']));
        await refresh();
        await waitRows(`rows.some((r) => r.startsWith('cp_parts.py'))`, 'the file');
        await page.evaluate(`window.__answer = 'Fixes'`);
        await page.evaluate(`${$('git-cl-new')}.click()`);
        await page.waitFor(`document.querySelector('.gc-list[data-list="Fixes"]')`, 15000, 'the list Fixes');

        // the window of its changes: three rows, the middle one is moved to Fixes
        await clickRowButton('cp_parts.py', 'changes');
        await page.waitFor(`document.querySelectorAll('.git-parts-row').length === 3`, 15000, 'the three changes');
        assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.git-parts-row .git-parts-where')].map((e) => e.textContent)`), ['line 3', 'line 15', 'line 28']);
        assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.git-parts-row .git-parts-preview')].map((e) => e.textContent)`), ['line 3 X', 'line 15 X', 'line 28 X']);
        await page.evaluate(`document.querySelectorAll('.git-parts-row input')[1].click()`);
        await page.evaluate(`(() => { const t = document.getElementById('git-parts-target'); t.value = 'Fixes'; t.dispatchEvent(new Event('change')); document.getElementById('git-parts-move').click(); })()`);
        await page.waitFor(`[...document.querySelectorAll('#git-changes .gc .part')].map((p) => p.textContent).sort().join() === '1 of 3 changes,2 of 3 changes'`, 15000, 'the file in two lists');
        // the window reads the lists again after the move; the file is in the list that was active when it appeared
        await page.waitFor(`[...document.querySelectorAll('.git-parts-row .git-parts-list-name')].map((e) => e.textContent).join() === 'Feature,Fixes,Feature'`, 15000, 'the lists named in the window');
        assert.ok(readFileSync(join(projectRoot, '.pymagic', 'changelists.json'), 'utf8').includes('"parts"'), 'kept in the project file');
        await page.evaluate(`[...document.querySelectorAll('.git-parts .ask-buttons button')].find((b) => b.textContent === 'Close').click()`);

        // a commit of the list Fixes takes the one change
        await page.evaluate(`(() => { const s = ${$('git-cl-commit')}; s.value = 'Fixes'; s.dispatchEvent(new Event('change')); ${$('git-message')}.value = 'just the fifteen'; ${$('git-commit')}.click(); })()`);
        for (let i = 0; i < 40 && git('log', '-1', '--format=%s').trim() !== 'just the fifteen'; i++) await sleep(300);
        const shown = git('show', '--format=', 'HEAD');
        assert.ok(shown.includes('+line 15 X'), shown);
        assert.ok(!shown.includes('line 3 X') && !shown.includes('line 28 X'), shown);
        assert.equal(readFileSync(join(projectRoot, 'cp_parts.py'), 'utf8'), numbered(30, [3, 'line 3 X'], [15, 'line 15 X'], [28, 'line 28 X']), 'the other two are still in the working tree');
        await refresh();
        await page.waitFor(`![...document.querySelectorAll('#git-changes .gc .part')].length`, 15000, 'the marks gone with the committed change');

        // two changes of three are put on the shelf, the third stays in the file
        await clickRowButton('cp_parts.py', 'changes');
        await page.waitFor(`document.querySelectorAll('.git-parts-row').length === 2`, 15000, 'the two changes left');
        await page.evaluate(`document.querySelectorAll('.git-parts-row input')[0].click()`);
        await page.evaluate(`window.__answer = 'the first of two'`);
        await page.evaluate(`document.getElementById('git-parts-shelve').click()`);
        await page.waitFor(`!document.querySelector('.git-parts-shade')`, 15000, 'the window to close');
        const text = readFileSync(join(projectRoot, 'cp_parts.py'), 'utf8');
        assert.ok(!text.includes('line 3 X') && text.includes('line 28 X'), text);
        const shelf = await page.evaluate(`fetch('/api/git/shelf').then((r) => r.json())`);
        assert.equal(shelf[0].name, 'the first of two');
        assert.deepEqual(shelf[0].files.map((f) => f.path), ['cp_parts.py']);
    });

    test('single lines of one change move to another list, and a commit of that list takes just those lines', async () => {
        const nl = String.fromCharCode(10);
        writeFileSync(join(projectRoot, 'cp_lines.py'), numbered(20));
        git('add', '-A');
        git('commit', '-q', '-m', 'lines base');
        const added = numbered(20).split(nl);
        added.splice(5, 0, 'new a', 'new b', 'new c');
        writeFileSync(join(projectRoot, 'cp_lines.py'), added.join(nl));
        await refresh();
        await waitRows(`rows.some((r) => r.startsWith('cp_lines.py'))`, 'the file');

        // one change of three lines: ▸ shows them, and the middle one goes to Fixes
        await clickRowButton('cp_lines.py', 'changes');
        await page.waitFor(`document.querySelectorAll('.git-parts-row').length === 1`, 15000, 'the change');
        await page.evaluate(`document.querySelector('.git-parts-row .git-parts-open').click()`);
        await page.waitFor(`document.querySelectorAll('.git-parts-line').length === 3`, 5000, 'its lines');
        assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.git-parts-line .git-parts-text')].map((e) => e.textContent)`), ['+ new a', '+ new b', '+ new c']);
        assert.equal(await page.evaluate(`document.querySelector('.git-parts-row input').checked`), false, 'the ▸ does not tick the change');
        // the file is in the list that was active when it appeared; the line goes to another one
        const owner = await page.evaluate(`document.querySelector('.git-parts-row .git-parts-list-name').textContent`);
        const other = owner === 'Fixes' ? 'Feature' : 'Fixes';
        await page.evaluate(`document.querySelectorAll('.git-parts-line input')[1].click()`);
        await page.evaluate(`(() => { const t = document.getElementById('git-parts-target'); t.value = ${JSON.stringify(other)}; t.dispatchEvent(new Event('change')); document.getElementById('git-parts-move').click(); })()`);
        const lineLists = `[...document.querySelectorAll('.git-parts-line .git-parts-list-name')].map((e) => e.textContent).join()`;
        await page.waitFor(`${lineLists} === ${JSON.stringify([owner, other, owner].join())}`, 15000, 'the lines in their lists')
            .catch(async (e) => { throw new Error(e.message + ' ' + (await page.evaluate(`JSON.stringify([${lineLists}, document.querySelector('.git-parts .db-form-error')?.textContent, [...document.getElementById('git-parts-target').options].map((o) => o.value)])`))); });
        assert.equal(await page.evaluate(`document.querySelector('.git-parts-row .git-parts-list-name').textContent`), `${owner} + ${other}`);
        await page.evaluate(`[...document.querySelectorAll('.git-parts .ask-buttons button')].find((b) => b.textContent === 'Close').click()`);
        await page.waitFor(`[...document.querySelectorAll('#git-changes .gc .part')].map((p) => p.textContent).join().includes('1 of 1 change')`, 15000, 'the file in part in two lists');

        // a commit of that list writes "new b" and nothing else
        await page.evaluate(`(() => { const s = ${$('git-cl-commit')}; s.value = ${JSON.stringify(other)}; s.dispatchEvent(new Event('change')); ${$('git-message')}.value = 'just new b'; ${$('git-commit')}.click(); })()`);
        for (let i = 0; i < 40 && git('log', '-1', '--format=%s').trim() !== 'just new b'; i++) await sleep(300);
        const committed = numbered(20).split(nl);
        committed.splice(5, 0, 'new b');
        assert.equal(git('show', 'HEAD:cp_lines.py'), committed.join(nl));
        assert.equal(readFileSync(join(projectRoot, 'cp_lines.py'), 'utf8'), added.join(nl), 'the other two are still in the working tree');
    });
});

// last, and only when asked for: a notebook on a REAL Jupyter server. PYMAGIC_TEST_JUPYTER names the Python of an environment that has jupyter_server and ipykernel
// (nothing here installs them). The server is started by this block, on this computer, with a token of its own.
describe('a notebook on a real Jupyter server: Variables and Debug Cell', { skip: !process.env.PYMAGIC_TEST_JUPYTER }, () => {
    const REL = 'nb_more.ipynb';
    const TOKEN = 'tok-real-e2e';
    const nb = (expr) => page.evaluate(`(() => { const s = window.pymagic.notebook.stateOf(${JSON.stringify(REL)}); return ${expr}; })()`);
    let server, url;
    before(async () => {
        const port = await new Promise((ok) => { const probe = createServer(); probe.listen(0, '127.0.0.1', () => { const n = probe.address().port; probe.close(() => ok(n)); }); });
        url = `http://127.0.0.1:${port}`;
        server = spawn(process.env.PYMAGIC_TEST_JUPYTER, ['-m', 'jupyter_server', '--no-browser', '--ip=127.0.0.1', `--port=${port}`, '--ServerApp.port_retries=0', `--ServerApp.root_dir=${projectRoot}`],
            { cwd: projectRoot, env: { ...process.env, PYTHONPATH: '', JUPYTER_TOKEN: TOKEN }, stdio: 'ignore' });   // (not the stand-in jupyter_server that the other tests put on PYTHONPATH)
        let up = false;
        for (let i = 0; i < 120 && !up; i++) {
            await sleep(500);
            try { up = (await fetch(url + '/api/kernelspecs', { headers: { Authorization: 'token ' + TOKEN } })).ok; } catch { /* not listening yet */ }
        }
        assert.ok(up, 'the Jupyter server did not start');
    });
    after(() => { try { server?.kill(); } catch { /* gone already */ } });

    test('the notebook is put on the server, its variables are listed, and Debug Cell stops at a breakpoint, steps and runs on', async () => {
        await open(REL);
        await page.waitFor(`document.querySelector('#notebook.active .nb-cell')`, 15000, 'the notebook');
        await nb(`(s.serverBtn.click(), 1)`);
        await page.waitFor(`document.querySelector('.nb-servers #nb-server-url')`, 5000, 'the servers window');
        await page.evaluate(`(() => { document.getElementById('nb-server-url').value = ${JSON.stringify(url)}; document.getElementById('nb-server-token').value = ${JSON.stringify(TOKEN)}; document.getElementById('nb-server-name').value = 'Real'; })()`);
        await page.evaluate(`document.getElementById('nb-server-add').click()`);
        await page.evaluate(`document.getElementById('nb-server-use').click()`);
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).serverBtn.textContent.includes('Real')`, 5000, 'the selector');

        // the first cell runs on the server, and the names it left are asked of the kernel (the cells ran before, on the built-in kernel: their counts are cleared so that the wait is for this run)
        await nb(`(s.nb.cells[0].execution_count = null, s.nb.cells[1].execution_count = null, s.nb.cells[1].outputs = [], 1)`);
        await nb(`(s.views[0].el.querySelector('.nb-tools button').click(), 1)`);
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).nb.cells[0].execution_count`, 90000, 'the first cell on the server');
        const names = await page.waitFor(`(() => { const t = window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).varsEl.textContent; return t.includes('Point') && t; })()`, 30000, 'the variables of the server');
        assert.match(names, /Point/);
        // exactly the names the cell made: none of IPython's own
        assert.equal(await nb(`[...s.varsEl.querySelectorAll('tr[data-path]')].map((r) => r.querySelector('.nb-var-label').textContent).join()`), 'Point,d,p');

        // Debug Cell on the cell with a breakpoint on line 2
        await nb(`(s.views[1].el.__create(), 1)`);
        await page.waitFor(`!!window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).views[1].editor`, 10000, 'the editor of the cell');
        await nb(`(s.views[1].editor.setPosition({ lineNumber: 2, column: 1 }), s.views[1].editor.focus(), 1)`);
        await page.key(120, 'F9');
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).views[1].editor.getModel().getLineDecorations(2).some((d) => d.options.glyphMarginClassName?.startsWith('bp-glyph'))`, 5000, 'the breakpoint');
        await nb(`(s.views[1].el.querySelector('.nb-debug-btn').click(), 1)`);
        await page.waitFor(`${$('runstate')}.textContent === 'paused'`, 90000, 'the stop in the cell, on the server');
        assert.equal(await nb(`s.views[1].editor.getModel().getAllDecorations().filter((d) => d.options.className === 'debug-line').map((d) => d.range.startLineNumber).join()`), '2');
        assert.match(await page.evaluate(`${$('frames')}.textContent`), /nb_more_ipynb\.cell2\.py/);
        await page.waitFor(`(() => { const t = ${$('vars')}.textContent; return t.includes('a') && t.includes('4'); })()`, 15000, 'the variables of the stop');
        await page.evaluate(`${$('b-over')}.click()`);
        await page.waitFor(`${$('runstate')}.textContent === 'paused' && window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).views[1].editor.getModel().getAllDecorations().some((d) => d.options.className === 'debug-line' && d.range.startLineNumber === 3)`, 30000, 'the step to line 3');
        // remove the breakpoint and resume: the cell runs to its end on the server
        await nb(`(s.views[1].editor.setPosition({ lineNumber: 2, column: 1 }), s.views[1].editor.focus(), 1)`);
        await page.key(120, 'F9');
        await page.evaluate(`${$('b-debug')}.click()`);
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).nb.cells[1].execution_count`, 90000, 'the end of the cell');
        assert.match(await nb(`s.nb.cells[1].outputs.map((o) => (o.text ?? []).join('')).join('')`), /c = 9/);
        await page.waitFor(`${$('b-stop')}.disabled`, 15000, 'the end of the debug session');
        // the names of the cell are the server's kernel's: the Variables window lists them
        await page.waitFor(`window.pymagic.notebook.stateOf(${JSON.stringify(REL)}).varsEl.textContent.includes('c')`, 30000, 'c in the variables');
        // back to the built-in kernel for whatever follows
        await nb(`(s.serverBtn.click(), 1)`);
        await page.waitFor(`document.querySelector('.nb-servers .nb-server-row input')`, 5000, 'the servers window again');
        await page.evaluate(`document.querySelector('.nb-servers .nb-server-row input').click()`);
        await page.evaluate(`document.getElementById('nb-server-use').click()`);
    });
});

describe('New Project: the other kinds', () => {
    test("Express's view engines and bin/www are shown in their languages, EJS's code as JavaScript", async () => {
        const languageOf = async (rel) => { await open(rel); return page.evaluate('window.pymagic.editor.getModel().getLanguageId()'); };
        assert.equal(await languageOf('express/views/index.hjs'), 'handlebars');
        assert.equal(await languageOf('express/bin/www'), 'javascript');
        assert.equal(await languageOf('express/views/index.ejs'), 'ejs');
        // the tag, the attribute, the code inside its value and the comment each have their token (named as Monaco's HTML names them)
        const tokens = await page.evaluate(`window.pymagic.monaco.editor.tokenize(window.pymagic.editor.getModel().getValue(), 'ejs').flat().map((t) => t.type)`);
        for (const want of ['tag.html', 'attribute.name.html', 'delimiter.html', 'identifier.js', 'comment.html'])
            assert.ok(tokens.includes(want), want + ' in ' + tokens.join(' '));
    });

    test('Run configurations: a switch is a small box at the start of its row, not stretched across the dialog', async () => {
        await page.evaluate(`document.getElementById('run-config').click()`);
        const rows = `[...document.querySelectorAll('.ctx-menu .ctx-item')]`;
        await page.waitFor(`${rows}.some((r) => r.textContent.includes('Edit configurations'))`, 10000, 'the run configuration menu');
        await page.evaluate(`${rows}.find((r) => r.textContent.includes('Edit configurations')).click()`);
        await page.waitFor(`!!document.querySelector('.runcfg')`, 10000, 'the dialog');
        // a Python script configuration: its switches (content roots, sources roots, ...)
        await page.evaluate(`[...document.querySelectorAll('.runcfg-tools button')].find((b) => b.textContent.includes('Add')).click()`);
        await page.waitFor(`${rows}.some((r) => r.textContent === 'Python script')`, 5000, 'the kinds');
        await page.evaluate(`${rows}.find((r) => r.textContent === 'Python script').click()`);
        const boxes = await page.waitFor(`(() => { const b = [...document.querySelectorAll('.runcfg .set-control input[type=checkbox]')]; return b.length ? b.map((x) => { const r = x.getBoundingClientRect(), c = x.parentElement.getBoundingClientRect(); return [Math.round(r.width), Math.round(r.left - c.left)]; }) : null; })()`, 10000, 'the switches');
        for (const [width, left] of boxes) {
            assert.ok(width <= 20, `a switch ${width}px wide`);
            assert.ok(left <= 2, `a switch ${left}px from the start of its column`);
        }
        // the dialog closed without keeping the new configuration
        await page.evaluate(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))`);
        await page.waitFor(`!document.querySelector('.runcfg')`, 5000, 'the dialog closed');
    });

    test('Go to definition into a library: F12 on json.dumps opens json read only at dumps, Escape closes it', async () => {
        await open('calc.py');
        // print(json.dumps(...)): the caret on dumps
        await page.evaluate(`(() => { const e = window.pymagic.editor; e.setPosition({ lineNumber: 9, column: 14 }); e.focus(); e.trigger('test', 'editor.action.revealDefinition', {}); })()`);
        const title = await page.waitFor(`document.querySelector('.fs-libview h2')?.textContent ?? ''`, 20000, 'the library window');
        assert.match(title, /json\/__init__\.pyi?$/);
        // the line under the caret is the definition, and the file cannot be changed
        const shown = await page.evaluate(`(() => { const ed = window.pymagic.monaco.editor.getEditors().find((x) => x.getContainerDomNode().closest('.fs-libview')); const at = ed.getPosition(); return { line: ed.getModel().getLineContent(at.lineNumber), readOnly: ed.getOption(window.pymagic.monaco.editor.EditorOption.readOnly) }; })()`);
        assert.match(shown.line, /def dumps\(/);
        assert.equal(shown.readOnly, true);
        await page.evaluate(`document.querySelector('#sigdialog').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))`);
        await page.waitFor(`!document.querySelector('.fs-libview')`, 5000, 'the window closed');
        // the project's file stays in the editor
        assert.equal(await page.evaluate('window.pymagic.editor.getModel().uri.path'), '/calc.py');
    });

    test('node_modules is shown in the project tree, excluded and read when it is opened', async () => {
        const row = await page.waitFor(`(() => { const li = document.querySelector('li[data-path="node_modules"]'); return li ? li.className + ' | ' + li.title : ''; })()`, 15000, 'node_modules in the tree');
        assert.match(row, /mark-excluded/);
        assert.match(row, /npm packages/);
        // what is inside it is not read until it is opened
        assert.equal(await page.evaluate(`!!document.querySelector('li[data-path="node_modules/flowbite"]')`), false);
        const tree = await page.evaluate(`fetch('/api/tree').then((r) => r.json())`);
        assert.ok(tree.lazy.includes('node_modules'));
        assert.ok(!tree.files.some((f) => f.path.startsWith('node_modules/')));
    });

    test('Tests: a test inherited from a base class in another file goes to its method there, and has no gutter mark of its own', async () => {
        const proj = project({
            'tests/__init__.py': '',
            'tests/base.py': 'import unittest\n\n\nclass ApiCase(unittest.TestCase):\n    def test_status(self):\n        pass\n',
            'tests/test_child.py': 'from .base import ApiCase\n\n\nclass UserApi(ApiCase):\n    def test_user(self):\n        pass\n',
        });
        const other = await launch(proj);
        const q = other.page;
        try {
            await q.evaluate(`document.getElementById('t-tests').click()`);
            // every node opened, then the inherited test double-clicked
            const row = `[...document.querySelectorAll('#tests-tree .trow')].find((r) => r.querySelector('.tname').textContent === 'test_status' && r.closest('li').parentElement.closest('li')?.querySelector('.tname').textContent === 'UserApi')`;
            await q.waitFor(`(() => { [...document.querySelectorAll('#tests-tree .trow .caret')].find((c) => c.textContent === '▸')?.click(); return !!${row}; })()`, 30000, 'the inherited test in the tree');
            // double-clicked again while the file is not open (the tree may draw itself again as discovery ends)
            const at = await q.waitFor(`(() => { const e = window.pymagic.editor; if (e.getModel().uri.path === '/tests/base.py') return e.getPosition().lineNumber; ${row}?.dispatchEvent(new MouseEvent('dblclick', { bubbles: true })); return 0; })()`, 15000, 'tests/base.py opened');
            assert.equal(at, 5);
            // in the subclass's file: the class and its own test have marks, the inherited one none
            await q.evaluate(`window.pymagic.openFile('tests/test_child.py')`);
            const marks = await q.waitFor(`(() => { const m = window.pymagic.editor.getModel(); return m.uri.path === '/tests/test_child.py' && m.getAllDecorations().filter((d) => d.options.glyphMarginClassName?.startsWith('test-run')).map((d) => d.range.startLineNumber).sort().join(','); })()`, 15000, 'the gutter marks');
            assert.equal(marks, '4,5');
        }
        finally { await other.close(); }
    });

    test('a Django project opened for the first time has a Django server to run, chosen in the toolbar', async () => {
        const dj = project({
            'manage.py': "import os\nimport sys\n\nif __name__ == '__main__':\n    os.environ.setdefault('DJANGO_SETTINGS_MODULE', 'shop.settings')\n    from django.core.management import execute_from_command_line\n    execute_from_command_line(sys.argv)\n",
            'shop/__init__.py': '',
            'shop/settings.py': "INSTALLED_APPS = ['store']\nROOT_URLCONF = 'shop.urls'\n",
            'shop/urls.py': 'urlpatterns = []\n',
            'store/__init__.py': '',
        });
        const other = await launch(dj);
        try {
            assert.equal(await other.page.waitFor(`document.querySelector('#run-config span').textContent === 'Django server' && 'Django server'`, 20000, 'the Django server configuration'), 'Django server');
            const kept = JSON.parse(await other.page.evaluate(`fetch('/api/file?path=.pymagic/run-configurations.json').then((r) => r.text())`));
            assert.equal(kept.selected, 'Django server');
            assert.deepEqual([kept.configurations[0].kind, kept.configurations[0].script, kept.configurations[0].settings], ['django', 'manage.py', 'shop.settings']);
        }
        finally { await other.close(); }
    });

    test('Open Project: asked whether it replaces this window\'s project or opens in a new window, with Don\'t ask again', async () => {
        const other = tempDir('pymagic-e2e-other-');
        writeFileSync(join(other, 'other.py'), 'print("other")\n');
        const pages = async () => (await (await fetch(`http://127.0.0.1:${app.port}/json`)).json()).filter((t) => t.type === 'page' && /^http:\/\/127\.0\.0\.1:\d+\/$/.test(t.url));
        const ask = (folder) => page.evaluate(`(window.pymagic.projectDesktop().openProject(${JSON.stringify(folder)}), true)`);
        const buttons = `[...document.querySelectorAll('.ask-box .ask-buttons button')].map((b) => b.textContent).join('|')`;
        const ownPort = await page.evaluate('location.port');
        // the dialog: its three answers and the box to tick; Cancel opens nothing
        await ask(other);
        assert.equal(await page.waitFor(`document.querySelector('.ask-box') && ${buttons}`, 10000, 'the Open Project dialog'), 'This Window|New Window|Cancel');
        assert.match(await page.evaluate(`document.querySelector('.ask-box .ask-check').textContent`), /Don't ask again/);
        await page.evaluate(`document.querySelector('.ask-box button[data-value="cancel"]').click()`);
        await sleep(500);
        assert.equal((await pages()).length, 1);
        // New Window, with Don't ask again: a second window with an engine of its own, and the answer kept as the setting
        await ask(other);
        await page.waitFor(`!!document.querySelector('.ask-box')`, 10000, 'the dialog again');
        await page.evaluate(`(() => { document.querySelector('.ask-box .ask-check input').checked = true; document.querySelector('.ask-box button[data-value="new"]').click(); })()`);
        let second = null;
        for (let i = 0; i < 120 && !second; i++) {
            await sleep(500);
            second = (await pages()).find((t) => new URL(t.url).port !== ownPort);
        }
        assert.ok(second, 'a second window');
        assert.equal((await pages()).length, 2);
        assert.equal(await page.evaluate(`fetch('/api/settings').then((r) => r.json()).then((s) => s.openProjectIn)`), 'new');
        // its engine has the other folder
        const info = await (await fetch(new URL('/api/files', second.url))).text();
        assert.match(info, /other\.py/);
        // the same project again: no dialog (the setting says new), no third window (the open one comes to the front)
        await ask(other);
        await sleep(1500);
        assert.equal((await pages()).length, 2);
        // the second window closed, its engine with it; the setting put back
        const ws = new WebSocket(second.webSocketDebuggerUrl);
        await new Promise((ok) => ws.addEventListener('open', ok));
        ws.send(JSON.stringify({ id: 1, method: 'Runtime.evaluate', params: { expression: 'window.close()' } }));
        await sleep(1500);
        ws.close();
        assert.equal((await pages()).length, 1);
        assert.equal(await fetch(new URL('/api/info', second.url)).then(() => 'up', () => 'down'), 'down');
        await page.evaluate(`window.pymagic.changeSetting({ openProjectIn: 'ask' })`);
    });

    test('Recent projects: one removed from the list, and Clear recent projects asked first (the open project stays)', async () => {
        // the Open Project test opened another folder, so the list has it
        const list = () => page.evaluate('window.pymagicDesktop.recentProjects()');
        const before = await list();
        assert.ok(before.length >= 1, 'a recent project to remove');
        const rowsOf = `[...document.querySelectorAll('.ctx-menu .ctx-item')]`;
        const click = (label) => page.evaluate(`(() => { const r = ${rowsOf}.find((x) => x.querySelector('.ctx-label').textContent === ${JSON.stringify(label)}); if (!r) return false; r.click(); return true; })()`);
        const menu = async () => {
            await page.evaluate(`document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))`);
            await page.evaluate(`document.getElementById('project').click()`);
            return page.waitFor(`document.querySelectorAll('.ctx-menu').length === 1 && ${rowsOf}.some((r) => r.textContent.includes('Open folder')) && ${rowsOf}.map((r) => r.textContent).join('|')`, 10000, 'the project menu');
        };
        // the project name's menu: each project, then the two new rows
        const shown = await menu();
        assert.match(shown, /Remove from recent projects/);
        assert.match(shown, /Clear recent projects/);
        // Clear asks first; Cancel keeps the list
        assert.ok(await click('Clear recent projects'));
        assert.equal(await page.waitFor(`document.querySelector('.ask-box') && [...document.querySelectorAll('.ask-box .ask-buttons button')].map((b) => b.textContent).join('|')`, 10000, 'the Clear dialog'), 'Clear|Cancel');
        await page.evaluate(`document.querySelector('.ask-box button[data-value="cancel"]').click()`);
        await sleep(300);
        assert.deepEqual(await list(), before);
        // Remove from recent projects > the folder: gone from the list and from the menu
        await menu();
        // (the menu reads the list before it opens: the row is clicked again until its submenu is there)
        await page.waitFor(`(() => { if (document.querySelectorAll('.ctx-menu').length > 1) return true; const r = ${rowsOf}.find((x) => x.querySelector('.ctx-label').textContent === 'Remove from recent projects'); r?.click(); return false; })()`, 10000, 'the submenu');
        // the row of the submenu (the list above has the same label, which opens the project)
        assert.ok(await page.evaluate(`(() => { const box = [...document.querySelectorAll('.ctx-menu')].pop(); const r = [...box.querySelectorAll('.ctx-item')].find((x) => x.querySelector('.ctx-label').textContent === ${JSON.stringify(before[0])}); if (!r) return false; r.click(); return true; })()`));
        await page.waitFor(`window.pymagicDesktop.recentProjects().then((l) => !l.includes(${JSON.stringify(before[0])}))`, 5000, 'the project removed');
        // the rest cleared: the open project stays in the list it is kept in (shown nowhere while it is open)
        if (before.length > 1) await page.evaluate('window.pymagicDesktop.forgetRecentProject(null)');
        assert.deepEqual(await list(), []);
        assert.match(await menu(), /No other recent projects/);
        await page.evaluate(`document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))`);
    });

    test('Tailwind CSS: its classes completed in a class attribute, with the variants typed and the project\'s own @theme colours', async () => {
        assert.ok(await page.evaluate('window.pymagicTailwindReady.then((t) => t?.classes())'), 'Tailwind was not found in the project (tw/input.css imports it)');
        // the caret after the class being typed, the suggestion chosen by its label, the line read back and put back
        const complete = async (line, column, label) => {
            await open('tw/page.html');
            const before = await page.evaluate('window.pymagic.editor.getModel().getValue()');
            await page.evaluate(`(() => { const e = window.pymagic.editor; e.setPosition({ lineNumber: ${line}, column: ${column} }); e.focus(); e.trigger('test', 'editor.action.triggerSuggest', {}); })()`);
            const row = `[...document.querySelectorAll('.suggest-widget .monaco-list-row')].find((r) => r.getAttribute('aria-label')?.startsWith(${JSON.stringify(label)}))`;
            const index = await page.waitFor(`(() => { const r = ${row}; return r ? Number(r.dataset.index) + 1 : 0; })()`, 15000, 'the suggestion ' + label) - 1;
            for (let i = 0; i < index; i++) await page.evaluate(`window.pymagic.editor.trigger('test', 'selectNextSuggestion', {})`);
            await page.evaluate(`window.pymagic.editor.trigger('test', 'acceptSelectedSuggestion', {})`);
            const after = await page.evaluate(`window.pymagic.editor.getModel().getLineContent(${line})`);
            await page.evaluate(`window.pymagic.editor.getModel().setValue(${JSON.stringify(before)})`);
            return after;
        };
        // the picture the user sent: ml-au becomes ml-auto
        assert.equal(await complete(2, 22, 'ml-auto'), '    <div class="ml-auto"></div>');
        // a colour of the project's own theme
        assert.equal(await complete(3, 21, 'bg-brand'), '    <p class="bg-brand"></p>');
        // its CSS on hover
        // (earlier tests leave hover widgets in the page: the one with the CSS is looked for, and the hover asked for again while it is not there)
        await open('tw/page.html');
        const hover = await page.waitFor(`(() => {
            const shown = [...document.querySelectorAll('.monaco-hover-content')].map((h) => h.textContent).find((t) => t.includes('display'));
            if (shown) return shown;
            window.twHoverTries = (window.twHoverTries ?? 0) + 1;
            if (window.twHoverTries % 10 === 1) { const e = window.pymagic.editor; e.setPosition({ lineNumber: 1, column: 17 }); e.focus(); e.trigger('test', 'editor.action.showHover', {}); }
            return '';
        })()`, 15000, 'the hover');
        assert.match(hover, /display: flex/);
        // typed as a person types it (no trigger asked for): the list opens on the quote and the dash
        await open('tw/page.html');
        const before = await page.evaluate('window.pymagic.editor.getModel().getValue()');
        await page.evaluate(`window.pymagic.editor.getModel().setValue('<div class=>\\n</div>\\n')`);
        await page.evaluate(`(() => { const e = window.pymagic.editor; e.setPosition({ lineNumber: 1, column: 12 }); e.focus(); })()`);
        for (const ch of '"ml-au') {
            await page.send('Input.dispatchKeyEvent', { type: 'keyDown', key: ch, text: ch });
            await page.send('Input.dispatchKeyEvent', { type: 'keyUp', key: ch });
            await sleep(150);
        }
        await page.waitFor(`document.querySelector('.suggest-widget.visible .monaco-list-row.focused')?.getAttribute('aria-label')?.startsWith('ml-auto')`, 15000, 'ml-auto offered while typing');
        await page.evaluate(`window.pymagic.editor.trigger('test', 'hideSuggestWidget', {})`);
        // a colour added to the stylesheet's @theme and saved is offered at once (the engine is asked again after each save)
        await page.evaluate(`window.pymagic.editor.getModel().setValue(${JSON.stringify(before)})`);
        const css = await page.evaluate(`fetch('/api/file?path=tw/input.css').then((r) => r.text())`);
        const count = await page.evaluate('window.pymagicTailwindReady.then((t) => t.classes())');
        await page.evaluate(`fetch('/api/file?path=tw/input.css', { method: 'PUT', body: ${JSON.stringify(css)} + '@theme { --color-accent: #ff8800; }\\n' }).then(() => window.dispatchEvent(new Event('pymagic:saved')))`);
        try {
            await page.waitFor(`window.pymagicTailwindReady.then((t) => t.classes() > ${count})`, 15000, 'the design system loaded again after the save');
            assert.equal(await complete(4, 21, 'bg-accent'), '    <p class="bg-accent"></p>');
        }
        finally {
            await page.evaluate(`fetch('/api/file?path=tw/input.css', { method: 'PUT', body: ${JSON.stringify(css)} }).then(() => window.dispatchEvent(new Event('pymagic:saved')))`);
        }
    });

    test('Full line completion: the rest of the line in grey, written by the bundled model, taken with Tab', async () => {
        await open('fullline/config.py');
        const before = await page.evaluate('window.pymagic.editor.getModel().getValue()');
        // the caret after "config = ": typing pauses, the model writes the line, it is shown in grey
        await page.evaluate(`(() => { const e = window.pymagic.editor; e.setPosition({ lineNumber: 6, column: 18 }); e.focus(); e.trigger('test', 'editor.action.inlineSuggest.trigger', {}); })()`);
        const ghost = await page.waitFor(`(() => { const g = document.querySelector('.monaco-editor .ghost-text-decoration, .monaco-editor .ghost-text'); return g ? g.textContent : ''; })()`, 30000, 'the grey line');
        assert.match(ghost, /json\.load/);
        await page.evaluate(`window.pymagic.editor.trigger('keyboard', 'editor.action.inlineSuggest.commit', {})`);
        const line = await page.evaluate('window.pymagic.editor.getModel().getLineContent(6)');
        await page.evaluate(`window.pymagic.editor.getModel().setValue(${JSON.stringify(before)})`);
        assert.match(line, /^        config = json\.load/);
    });

    test('HTML tag pairs: renaming an opening tag renames its closing tag, and back, in a template and in JSX', async () => {
        // the name selected, the new one typed: the other name follows (the editor's linked editing, which needs a moment to find the pair)
        const rename = async (rel, line, from, to, typed) => {
            await open(rel);
            const before = await page.evaluate('window.pymagic.editor.getModel().getValue()');
            await page.evaluate(`(() => { const e = window.pymagic.editor; e.focus(); e.setSelection({ startLineNumber: ${line}, startColumn: ${from}, endLineNumber: ${line}, endColumn: ${to} }); })()`);
            await sleep(800);
            await page.evaluate(`window.pymagic.editor.trigger('keyboard', 'type', { text: ${JSON.stringify(typed)} })`);
            await sleep(400);
            const after = await page.evaluate('window.pymagic.editor.getModel().getValue()');
            await page.evaluate(`window.pymagic.editor.getModel().setValue(${JSON.stringify(before)})`);
            return after;
        };
        // <p> becomes <h2>: its </p> too
        assert.equal((await rename('tags/page.html', 2, 2, 3, 'h2')).split('\n')[1], '<h2>Welcome to {{ title }}</h2>');
        // from the closing tag: </h1> becomes </h3>, its <h1> too
        assert.equal((await rename('tags/page.html', 1, 18, 20, 'h3')).split('\n')[0], '<h3>{{ title }}</h3>');
        // JSX: <section> becomes <article>, the self-closing Item left alone
        assert.equal((await rename('tags/App.jsx', 2, 4, 11, 'article')).split('\n')[1], '  <article className="a"><Item key={1} /></article>');
    });

    test('Emmet: an abbreviation is expanded by completion in a template and in a stylesheet, not inside a tag', async () => {
        // the abbreviation at the end of the first line, Emmet's suggestion chosen among the others, the expansion read back, the file put back
        // shown: the text of Emmet's row (HTML shows the abbreviation, CSS what it expands to)
        const expand = async (rel, shown, line = 1) => {
            await open(rel);
            const before = await page.evaluate('window.pymagic.editor.getModel().getValue()');
            await page.evaluate(`(() => { const e = window.pymagic.editor; const text = e.getModel().getLineContent(${line}); e.setPosition({ lineNumber: ${line}, column: text.length + 1 }); e.focus(); e.trigger('test', 'editor.action.triggerSuggest', {}); })()`);
            const row = `[...document.querySelectorAll('.suggest-widget .monaco-list-row')].find((r) => r.textContent.includes(${JSON.stringify(shown)}))`;
            const index = await page.waitFor(`(() => { const r = ${row}; return r ? Number(r.dataset.index) + 1 : 0; })()`, 15000, "Emmet's suggestion " + shown) - 1;
            for (let i = 0; i < index; i++) await page.evaluate(`window.pymagic.editor.trigger('test', 'selectNextSuggestion', {})`);
            await page.evaluate(`window.pymagic.editor.trigger('test', 'acceptSelectedSuggestion', {})`);
            const after = await page.evaluate('window.pymagic.editor.getModel().getValue()');
            await page.evaluate(`window.pymagic.editor.getModel().setValue(${JSON.stringify(before)})`);
            return after;
        };
        const html = await expand('emmet/page.html', 'ul>li*2');
        assert.match(html, /<ul>\s*<li><\/li>\s*<li><\/li>\s*<\/ul>/, html);
        assert.match(await expand('emmet/site.css', 'margin: 10px', 2), /a \{\s*margin: 10px;\s*\}/);
        // Hogan.js (shown as Handlebars) and a Vue component (shown as HTML)
        assert.match(await expand('emmet/page.hjs', 'ul>li*2'), /<ul>\s*<li><\/li>\s*<li><\/li>\s*<\/ul>/);
        assert.match(await expand('emmet/App.vue', 'ul>li*2'), /<ul>\s*<li><\/li>/);
        // JSX: className, in a .jsx file
        assert.match(await expand('emmet/App.jsx', 'div.card>h2', 2), /<div className="card">\s*<h2><\/h2>\s*<\/div>/);
        // a JavaScript file that is not JSX (Express's own code) is offered nothing
        await open('emmet/server.js');
        await page.evaluate(`(() => { const e = window.pymagic.editor; e.setPosition({ lineNumber: 2, column: 9 }); e.focus(); e.trigger('test', 'editor.action.triggerSuggest', {}); })()`);
        await sleep(1500);
        assert.equal(await page.evaluate(`[...document.querySelectorAll('.suggest-widget .monaco-list-row')].some((r) => r.textContent.includes('div.card'))`), false);
        await page.key(27, 'Escape');
        // inside a tag nothing is offered: the place is told from the tokens
        await open('emmet/page.html');
        await page.evaluate(`(() => { const e = window.pymagic.editor; e.setPosition({ lineNumber: 2, column: 11 }); e.focus(); e.trigger('test', 'editor.action.triggerSuggest', {}); })()`);
        await sleep(1500);
        assert.equal(await page.evaluate(`[...document.querySelectorAll('.suggest-widget .monaco-list-row')].some((r) => r.textContent.includes('div.x'))`), false);
        await page.key(27, 'Escape');
    });

    test('the Python and Other groups; a kind of Other has its own choices and no interpreter', async () => {
        await page.evaluate(`document.querySelector('#brand .mark').dispatchEvent(new MouseEvent('click', { bubbles: true }))`);
        await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((d) => d.textContent.trim() === 'File')`, 5000, 'the main menu');
        await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.trim() === 'File').click()`);
        await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((d) => d.textContent.trim().startsWith('New project'))`, 5000, 'the File menu');
        await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.trim().startsWith('New project')).click()`);
        await page.waitFor(`document.querySelector('#np-kinds-other button')`, 10000, 'New Project');
        assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('#np-kinds button')].map((b) => b.textContent)`), ['Pure Python', 'FastAPI', 'Flask', 'Django', 'Jupyter']);
        assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('#np-kinds-other button')].map((b) => b.textContent)`),
            ['HTML5 Boilerplate', 'Bootstrap', 'Vite', 'React Native', 'Next.js', 'Node.js', 'Express', 'Laravel']);
        const shown = (id) => page.evaluate(`getComputedStyle(document.getElementById('${id}')).display !== 'none'`);
        const pick = (label) => page.evaluate(`[...document.querySelectorAll('#np-kinds-other button, #np-kinds button')].find((b) => b.textContent === '${label}').click()`);
        // Vite: its templates, no interpreter, no version
        await pick('Vite');
        assert.deepEqual([await shown('np-js'), await shown('np-python-env'), await shown('np-js-template-row'), await shown('np-js-version-row'), await shown('np-js-view-row')], [true, false, true, false, false]);
        assert.equal(await page.evaluate(`document.getElementById('np-js-template').value`), 'react-ts');
        // Express: the Node runtime, its generator's default command as PyCharm shows it, its view and stylesheet engines
        await pick('Express');
        await page.waitFor(`document.getElementById('np-js-gen-label').textContent === 'express-generator:'`, 20000, "express-generator's row");
        assert.equal(await page.evaluate(`document.getElementById('np-js-command').value`), 'npx --package express-generator express');
        assert.ok(await page.evaluate(`document.getElementById('np-js-runtime').options.length > 0 && /node\\.exe/i.test(document.getElementById('np-js-runtime').options[0].textContent)`), 'a Node runtime listed');
        assert.deepEqual([await shown('np-js-view-row'), await shown('np-js-css-row'), await shown('np-js-template-row')], [true, true, false]);
        assert.equal(await page.evaluate(`[...document.getElementById('np-js-view').options].map((o) => o.value).join()`), 'dust,ejs,hbs,hjs,pug,twig,vash,none');
        assert.equal(await page.evaluate(`document.getElementById('np-js-view').value`), 'pug');
        assert.equal(await page.evaluate(`[...document.getElementById('np-js-css').options].map((o) => o.textContent).join()`), 'Plain CSS,LESS,Stylus,Compass,Sass');
        // a command changed is kept for its kind; another kind shows its own
        await page.evaluate(`(() => { const c = document.getElementById('np-js-command'); c.value = 'npx --package express-generator@4.16.1 express'; c.dispatchEvent(new Event('input')); })()`);
        await pick('Vite');
        await page.waitFor(`document.getElementById('np-js-gen-label').textContent === 'create-vite:'`, 20000, "create-vite's row");
        assert.equal(await page.evaluate(`document.getElementById('np-js-command').value`), 'npx create-vite');
        await pick('Express');
        await page.waitFor(`document.getElementById('np-js-gen-label').textContent === 'express-generator:'`, 20000, "express-generator's row again");
        assert.equal(await page.evaluate(`document.getElementById('np-js-command').value`), 'npx --package express-generator@4.16.1 express');
        // Node.js: nothing to choose, nothing to install
        await pick('Node.js');
        assert.deepEqual([await shown('np-js-install-row'), await shown('np-js-view-row')], [false, false]);
        // back to a Python kind: the interpreter again
        await pick('Flask');
        assert.deepEqual([await shown('np-js'), await shown('np-python-env')], [false, true]);
        await page.evaluate(`document.getElementById('np-cancel').click()`);
    });
});

describe('the Open work of 3 November: the HTTP client\'s scripts, Before launch, the console\'s history, the whole profile, imports, injections, FTPS, Mermaid', () => {
    const put = (rel, text) => { mkdirSync(dirname(join(app.root, rel)), { recursive: true }); writeFileSync(join(app.root, rel), text); };
    const runAt = (line) => page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: ${line}, column: 2 }); ed.focus(); return ed.getAction('pymagic.http.runAtCursor').run(); })()`);

    test('HTTP client: a response handler\'s test is shown in the Tests part, and its global fills the next request', async () => {
        put('scripted.http', 'GET {{host}}/items\n> {% client.test("ok", function () { client.assert(response.status === 200, "status"); }); client.global.set("seen", response.body.method); %}\n\n###\nGET {{host}}/again?m={{seen}}\n');
        await open('scripted.http');
        await runAt(1);
        await page.waitFor(`document.getElementById('http-status').textContent.includes('tests 1/1')`, 15000, 'the handler\'s test');
        await runAt(5);
        await page.waitFor(`document.getElementById('http-body').textContent.includes('/again?m=GET')`, 15000, 'the global in the next request');
    });

    test('HTTP client: jsonPath in a handler, and client.global kept for the next start of PyMagic, out of git', async () => {
        put('jsonpath.http', 'POST {{host}}/login\nContent-Type: application/json\n\n{"user": "ada"}\n\n> {% client.global.set("who", jsonPath(response.body, "$.method") + ":" + jsonPath(response.body, "$.path")); %}\n');
        await open('jsonpath.http');
        await runAt(1);
        const file = join(app.root, '.pymagic', 'http-globals.json');
        for (let i = 0; i < 60 && !(existsSync(file) && readFileSync(file, 'utf8').includes('"who"')); i++) await sleep(250);
        const kept = JSON.parse(readFileSync(file, 'utf8'));
        assert.equal(kept.who, 'POST:/login', JSON.stringify(kept));
        assert.equal(kept.seen, 'GET', 'the global of the test before is kept too');
        assert.ok(readFileSync(join(app.root, '.pymagic', '.gitignore'), 'utf8').split(/\r?\n/).includes('http-globals.json'), 'kept out of version control');
        // what the next start reads back
        assert.deepEqual(await page.evaluate(`fetch('/api/http-globals').then((r) => r.json())`), kept);
    });

    test('HTTP client: >> file keeps the response in the project, under a new name the second time', async () => {
        put('saved.http', 'GET {{host}}/items\n>> out/r.json\n');
        await open('saved.http');
        await runAt(1);
        const first = join(app.root, 'out', 'r.json'), second = join(app.root, 'out', 'r-1.json');
        for (let i = 0; i < 60 && !existsSync(first); i++) await sleep(250);
        assert.equal(JSON.parse(readFileSync(first, 'utf8')).method, 'GET');
        await runAt(1);
        for (let i = 0; i < 60 && !existsSync(second); i++) await sleep(250);
        assert.ok(existsSync(second), 'the second response under out/r-1.json');
    });

    test('Python console: Ctrl+Alt+E lists the commands run, newest first and once each, filtered as it is typed', async () => {
        const type = (code) => page.evaluate(`(() => { const i = document.getElementById('pyc-input'); i.value = ${JSON.stringify(code)}; i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
        await page.evaluate(`document.getElementById('t-pyconsole').click()`);
        await page.waitFor(`[...document.querySelectorAll('.pyc-out')].some((o) => !o.hidden && o.textContent.includes('Python 3'))`, 30000, 'the console');
        for (const c of ['hx = 1', 'print(hx)', 'hx = 1']) {
            await type(c);
            await sleep(400);
        }
        await page.waitFor(`[...document.querySelectorAll('.pyc-out')].some((o) => !o.hidden && o.textContent.includes('>>> print(hx)'))`, 15000, 'the commands to run');
        await page.evaluate(`document.getElementById('pyc-history').click()`);
        const rows = await page.waitFor(`(() => { const r = [...document.querySelectorAll('#pyc-history-list .pyc-history-row')].map((x) => x.textContent); return r.length ? r : null; })()`, 5000, 'the history list');
        assert.equal(rows[0], 'hx = 1');
        assert.equal(rows.filter((r) => r === 'hx = 1').length, 1);
        assert.ok(rows.includes('print(hx)'));
        await page.evaluate(`(() => { const f = document.querySelector('#pyc-history-list input'); f.value = 'pri'; f.dispatchEvent(new Event('input', { bubbles: true })); f.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
        assert.equal(await page.waitFor(`!document.getElementById('pyc-history-list') && document.getElementById('pyc-input').value`, 5000, 'the command put back'), 'print(hx)');
        await page.evaluate(`document.getElementById('pyc-input').value = ''`);
    });

    test('Python console: the history is kept for the next start of PyMagic, and a new console starts with it', async () => {
        // the commands of the test before are written to .pymagic/console-history.json
        const file = join(app.root, '.pymagic', 'console-history.json');
        for (let i = 0; i < 40 && !(existsSync(file) && readFileSync(file, 'utf8').includes('print(hx)')); i++) await sleep(250);
        const kept = JSON.parse(readFileSync(file, 'utf8'));
        assert.ok(kept.python.includes('print(hx)') && kept.python.includes('hx = 1'), JSON.stringify(kept));
        assert.equal(kept.python.filter((h) => h === 'hx = 1').length, 1);
        assert.ok(readFileSync(join(app.root, '.pymagic', '.gitignore'), 'utf8').split(/\r?\n/).includes('console-history.json'), 'kept out of version control');
        // what the next start reads back
        const read = await page.evaluate(`fetch('/api/console-history').then((r) => r.json())`);
        assert.deepEqual(read.python, kept.python);
        // a new console has it: Up gives the last command run in the other one
        await page.evaluate(`document.getElementById('pyc-new').click()`);
        await page.waitFor(`document.querySelectorAll('.pyc-out').length >= 2`, 10000, 'the new console');
        const up = await page.evaluate(`(() => { const i = document.getElementById('pyc-input'); i.value = ''; i.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true })); return i.value; })()`);
        assert.equal(up, 'hx = 1');
        await page.evaluate(`document.getElementById('pyc-input').value = ''`);
    });

    test('Profiler: the whole program\'s graph draws every function in columns by depth, and a click picks a box', async () => {
        // the file in the editor, not a configuration an earlier test chose (the debug server's)
        await page.evaluate(`document.getElementById('run-config').click()`);
        await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((r) => r.textContent.includes('Current file'))`, 5000, 'the configuration menu');
        await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((r) => r.textContent.includes('Current file')).click()`);
        put('deep.py', 'def leaf():\n    return sum(range(20000))\n\n\ndef mid():\n    return leaf()\n\n\ndef main():\n    mid()\n    leaf()\n\n\nmain()\n');
        await open('deep.py');
        await page.evaluate(`document.getElementById('b-profile').click()`);
        await page.waitFor(`document.getElementById('profiler-tab').classList.contains('active') && document.getElementById('prof-summary').textContent.startsWith('deep.py')`, 30000, 'the profile').catch(async (e) => { throw new Error(e.message + ' | summary: ' + await page.evaluate(`document.getElementById('prof-summary').textContent`) + ' | output: ' + (await page.evaluate(`document.getElementById('output').textContent`)).slice(-400) + ' | file: ' + await page.evaluate('window.pymagic.editor.getModel().uri.path')); });
        await page.evaluate(`document.getElementById('prof-graph').click()`);
        await page.waitFor(`[...document.querySelectorAll('.prof-graph-switch button')].some((b) => b.textContent === 'The whole program')`, 5000, 'the switch');
        await page.evaluate(`[...document.querySelectorAll('.prof-graph-switch button')].find((b) => b.textContent === 'The whole program').click()`);
        const boxes = await page.waitFor(`(() => { const n = [...document.querySelectorAll('#prof-list svg .prof-node')].map((g) => [g.querySelector('.prof-node-name').textContent, g.getBoundingClientRect().left]); return n.length >= 3 ? n : null; })()`, 5000, 'the boxes');
        const x = Object.fromEntries(boxes);
        assert.ok(x.main < x.mid && x.mid < x.leaf, JSON.stringify(x));   // columns by how deep each is called
        await page.evaluate(`[...document.querySelectorAll('#prof-list svg .prof-node')].find((g) => g.querySelector('.prof-node-name').textContent === 'mid').dispatchEvent(new MouseEvent('click', { bubbles: true }))`);
        assert.equal(await page.waitFor(`document.querySelector('#prof-list svg .prof-node.center .prof-node-name')?.textContent`, 5000, 'the picked box'), 'mid');
        await page.evaluate(`[...document.querySelectorAll('.prof-graph-switch button')].find((b) => b.textContent === 'Around one function').click()`);
    });

    test('Intention: import os becomes from os import getcwd, and its uses follow', async () => {
        put('imp_os.py', 'import os\nprint(os.getcwd())\n');
        await open('imp_os.py');
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 1, column: 9 }); ed.focus(); ed.getAction('editor.action.quickFix').run(); })()`);
        const titles = await page.waitFor(`(() => { const rows = [...document.querySelectorAll('.action-widget .monaco-list-row')]; return rows.length ? rows.map((r) => r.textContent.trim()) : null; })()`, 15000, 'the quick-fix menu');
        const title = titles.find((t) => t.includes("from os import getcwd"));
        assert.ok(title, titles.join(' | '));
        const at = await page.evaluate(`(() => { const r = [...document.querySelectorAll('.action-widget .monaco-list-row')].find((x) => x.textContent.includes('from os import getcwd')); const b = r.getBoundingClientRect(); return { x: b.left + 20, y: b.top + b.height / 2 }; })()`);
        await page.click(at.x, at.y);
        assert.equal(await page.waitFor(`window.pymagic.editor.getValue().startsWith('from os') && window.pymagic.editor.getValue()`, 10000, 'the import changed'), 'from os import getcwd\nprint(getcwd())\n');
    });

    test('Language injections: SQL in a JavaScript string by its comment, and in Python strings built from parts', async () => {
        const injected = (rel) => page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); if (!m.uri.path.endsWith(${JSON.stringify(rel)})) return null; const kw = m.getAllDecorations().filter((d) => (d.options.inlineClassName ?? '').includes('inj-kw')).map((d) => m.getValueInRange(d.range)); return kw.length ? kw : null; })()`, 15000, 'the injected keywords of ' + rel);
        put('inj_parts.py', '# language=sql\nq = ("SELECT id "\n     "FROM item "\n     "WHERE id = " + str(3))\n');
        await open('inj_parts.py');
        const py = await injected('inj_parts.py');
        for (const k of ['SELECT', 'FROM', 'WHERE']) assert.ok(py.includes(k), py.join(','));
        put('inj_js.js', '// language=sql\nconst q = "SELECT * FROM t";\n');
        await open('inj_js.js');
        const js = await injected('inj_js.js');
        assert.ok(js.includes('SELECT') && js.includes('FROM'), js.join(','));
    });

    test('Deployment: an FTPS server can be implicit, which moves its port to 990', async () => {
        const fill = (id, value) => page.evaluate(`(() => { const e = document.getElementById(${JSON.stringify(id)}); e.value = ${JSON.stringify(value)}; e.dispatchEvent(new Event('change', { bubbles: true })); })()`);
        await page.evaluate(`${$('t-deployment')}.click()`);
        await page.evaluate(`${$('dep-add')}.click()`);
        await page.waitFor(`document.getElementById('dep-form-type')`, 5000, 'the form');
        const shown = () => page.evaluate(`!document.getElementById('dep-form-implicitTls').closest('label').hidden`);
        await fill('dep-form-type', 'sftp');
        assert.equal(await shown(), false, 'not for SFTP');
        await fill('dep-form-type', 'ftps');
        assert.equal(await shown(), true);
        assert.equal(await page.evaluate(`document.getElementById('dep-form-port').value`), '21');
        await page.evaluate(`(() => { const c = document.getElementById('dep-form-implicitTls'); c.checked = true; c.dispatchEvent(new Event('change', { bubbles: true })); })()`);
        assert.equal(await page.evaluate(`document.getElementById('dep-form-port').value`), '990');
        await page.evaluate(`(() => { const c = document.getElementById('dep-form-implicitTls'); c.checked = false; c.dispatchEvent(new Event('change', { bubbles: true })); })()`);
        assert.equal(await page.evaluate(`document.getElementById('dep-form-port').value`), '21');
        await page.evaluate(`[...document.querySelectorAll('.dep-form button')].find((b) => b.textContent === 'Cancel').click()`);
    });

    test('Inspections: a custom resource against its CRD, OpenAPI 3.1 against its schema, and a regular expression joined from known parts', async () => {
        const marks = (rel, wanted) => page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); if (!m.uri.path.endsWith(${JSON.stringify(rel)})) return null; const f = window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).map((x) => x.startLineNumber + ': ' + x.message); return f.some((x) => x.includes(${JSON.stringify(wanted)})) ? f : null; })()`, 30000, `${wanted} in ${rel}`);
        put('k8s/crds/shelf.yaml', 'apiVersion: apiextensions.k8s.io/v1\nkind: CustomResourceDefinition\nmetadata:\n  name: shelves.shop.example.com\nspec:\n  group: shop.example.com\n  names:\n    kind: Shelf\n    plural: shelves\n  scope: Namespaced\n  versions:\n    - name: v1\n      served: true\n      storage: true\n      schema:\n        openAPIV3Schema:\n          type: object\n          properties:\n            spec:\n              type: object\n              properties:\n                size:\n                  type: integer\n                  minimum: 1\n');
        put('k8s/shelf.yaml', 'apiVersion: shop.example.com/v1\nkind: Shelf\nmetadata:\n  name: top\nspec:\n  size: 0\n');
        await open('k8s/shelf.yaml');
        assert.ok((await marks('k8s/shelf.yaml', 'Should be at least 1')).some((x) => x.startsWith('6: ')));
        put('openapi.yaml', 'openapi: 3.1.0\ninfo:\n  title: Shop\npaths: {}\n');
        await open('openapi.yaml');
        await marks('openapi.yaml', 'The property version is required');
        put('joined_re.py', 'import re\n\nSEP = r"[aa]"\nWORD = re.compile(r"\\w+" + SEP)\n');
        await open('joined_re.py');
        await marks('joined_re.py', 'twice');
    });

    test('Markdown: a Mermaid pie chart is drawn by the Mermaid library, which is loaded for it', async () => {
        put('pie.md', '# Pets\n\n```mermaid\npie title Pets\n    "Dogs" : 3\n    "Cats" : 2\n```\n');
        await open('pie.md');
        if (await page.evaluate(`!document.querySelector('.md-pane:not([hidden])')`)) await page.evaluate(`${$('md-toggle')}.click()`);
        const text = await page.waitFor(`document.querySelector('.md-body .md-mermaid-lib svg')?.textContent`, 30000, 'the pie chart');
        assert.ok(text.includes('Dogs') && text.includes('Cats'), text);
        assert.equal(await page.evaluate(`!!window.pymagicMermaid`), true);
        await page.evaluate(`${$('md-toggle')}.click()`);
    });

    test('Before launch: another configuration runs first, and a failing one stops the launch', async () => {
        const proj = project({
            'prep.py': "print('prepared')\n",
            'main.py': "print('main ran')\n",
            '.pymagic/run-configurations.json': JSON.stringify({ selected: 'main', configurations: [
                { name: 'prep', kind: 'script', script: 'prep.py' },
                { name: 'main', kind: 'script', script: 'main.py', before: 'prep' }] }),
        });
        const other = await launch(proj);
        const q = other.page;
        const out = () => q.evaluate(`document.getElementById('output').textContent`);
        try {
            await q.waitFor(`document.querySelector('#run-config span').textContent === 'main'`, 20000, 'main chosen');
            await q.evaluate(`document.getElementById('b-run').click()`);
            await q.waitFor(`document.getElementById('output').textContent.includes('main ran')`, 30000, 'main to run');
            const text = await out();
            assert.ok(text.indexOf('prepared') >= 0 && text.indexOf('prepared') < text.indexOf('main ran'), text);
            // prep fails: main does not start
            writeFileSync(join(proj, 'prep.py'), 'import sys\nsys.exit(2)\n');
            await q.evaluate(`document.getElementById('b-run').click()`);
            await q.waitFor(`/exit code 2/.test(document.getElementById('output').textContent) && /not started/.test(document.getElementById('output').textContent)`, 30000, 'the launch stopped');
            assert.ok(!(await out()).includes('main ran'), await out());
        }
        finally { await other.close(); }
    });
});

describe('the type engine of 3 November: BDD steps, mock.patch, tuple assignment balance', () => {
    const put = (rel, text) => { mkdirSync(dirname(join(app.root, rel)), { recursive: true }); writeFileSync(join(app.root, rel), text); };
    const marks = (rel, wanted) => page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); if (!m.uri.path.endsWith(${JSON.stringify(rel)})) return null; const f = window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).map((x) => x.startLineNumber + ': ' + x.message); return f.some((x) => x.includes(${JSON.stringify(wanted)})) ? f : null; })()`, 30000, `${wanted} in ${rel}`);
    const markers = () => page.evaluate(`(() => { const m = window.pymagic.editor.getModel(); return window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).map((x) => x.startLineNumber + ': ' + x.message); })()`);

    test('BDD: a .feature file has Gherkin colours, and a step no definition matches is marked', async () => {
        put('features/steps/steps.py', "from behave import given\n\n\n@given('a shop')\ndef step_impl(context):\n    pass\n");
        put('features/shop.feature', 'Feature: F\n  Scenario: S\n    Given a shop\n    When I pay\n');
        await open('features/shop.feature');
        assert.equal(await page.evaluate('window.pymagic.editor.getModel().getLanguageId()'), 'gherkin');
        const found = await marks('features/shop.feature', "Undefined step reference: 'I pay'");
        assert.ok(found.some((x) => x.startsWith('4: ')), found.join(' | '));
        assert.ok(!found.some((x) => x.includes("'a shop'")), 'the defined step is not marked');
    });

    test('TOML: a pyproject.toml is coloured, not plain text (headers, keys, strings each their own colour)', async () => {
        put('toml-colours/pyproject.toml', '[project]\nname = "shop"\nversion = "0.1.0"\nauthors = [\n    { name = "me", email = "me@example.com" }\n]\ndependencies = []\n\n[project.scripts]\nshop = "shop:main"\n');
        await open('toml-colours/pyproject.toml');
        assert.equal(await page.evaluate('window.pymagic.editor.getModel().getLanguageId()'), 'toml');
        const types = await page.evaluate(`window.pymagic.monaco.editor.tokenize(window.pymagic.editor.getModel().getValue(), 'toml').flat().map((t) => t.type)`);
        for (const want of ['type.toml', 'variable.toml', 'string.toml', 'delimiter.bracket.toml']) assert.ok(types.includes(want), want + ' in ' + types.join(' '));
        // as drawn: the colour of each part in the editor's lines
        const colourOf = (text) => page.waitFor(`(() => { const s = [...document.querySelectorAll('.monaco-editor .view-lines span span')].find((x) => x.textContent === ${JSON.stringify(text)}); return s ? getComputedStyle(s).color : null; })()`, 10000, text + ' drawn');
        const header = await colourOf('project'), key = await colourOf('name'), value = await colourOf('"shop"');
        assert.ok(new Set([header, key, value]).size === 3, `${header} ${key} ${value}`);
    });

    test('mock.patch as a decorator hands the mock to its parameter; a target that does not exist is marked', async () => {
        put('app/__init__.py', '');
        put('app/services.py', 'def notify():\n    pass\n');
        put('test_patched.py', "from unittest.mock import patch\n\n\n@patch('app.services.notify')\ndef test_a(m):\n    m.assert_called_once()\n\n\n@patch('app.services.notfy')\ndef test_b(m):\n    pass\n\n\ntest_a()\n");
        await open('test_patched.py');
        const found = await marks('test_patched.py', 'notfy');
        assert.ok(!found.some((x) => x.includes("Parameter 'm' unfilled")), found.join(' | '));
    });

    test('Tuple assignment balance: too many and too few values are marked, a starred target takes the rest', async () => {
        put('unpack.py', 'a, b = 1, 2, 3\nc, d, e = (1, 2)\nf, *g = 1, 2, 3\nprint(a, b, c, d, e, f, g)\n');
        await open('unpack.py');
        await marks('unpack.py', 'Too many values to unpack');
        const found = await markers();
        assert.ok(found.some((x) => x.startsWith('1: ') && x.includes('Too many values to unpack')), found.join(' | '));
        assert.ok(found.some((x) => x.startsWith('2: ') && x.includes('Need more values to unpack')), found.join(' | '));
        assert.ok(!found.some((x) => x.startsWith('3: ') && /values to unpack/.test(x)), found.join(' | '));
    });
});

describe('the type engine leftovers: BDD go to and completion, Gherkin languages, tuple items', () => {
    const put = (rel, text) => { mkdirSync(dirname(join(app.root, rel)), { recursive: true }); writeFileSync(join(app.root, rel), text); };
    const marks = (rel, wanted) => page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); if (!m.uri.path.endsWith(${JSON.stringify(rel)})) return null; const f = window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).map((x) => x.startLineNumber + ': ' + x.message); return f.some((x) => x.includes(${JSON.stringify(wanted)})) ? f : null; })()`, 30000, `${wanted} in ${rel}`);
    const steps = "from behave import given, when\n\n\n@given('{n:d} apples in the basket')\ndef step_impl(context, n):\n    pass\n\n\n@when('I pay')\ndef step_pay(context):\n    pass\n";

    test('BDD: F12 on a step opens its definition; after When the project\'s when steps are offered', async () => {
        put('shopbdd/steps/shop.py', steps);
        put('shopbdd/shop.feature', 'Feature: Shop\n  Scenario: S\n    Given 3 apples in the basket\n    When I pay\n');
        await open('shopbdd/shop.feature');
        await page.evaluate(`(() => { const e = window.pymagic.editor; e.setPosition({ lineNumber: 3, column: 14 }); e.focus(); e.trigger('test', 'editor.action.revealDefinition', {}); })()`);
        const at = await page.waitFor(`(() => { const e = window.pymagic.editor; const m = e.getModel(); return m.uri.path.endsWith('shopbdd/steps/shop.py') ? m.getLineContent(e.getPosition().lineNumber) : null; })()`, 20000, 'the step definition');
        assert.match(at, /def step_impl\(context, n\)/);
        await open('shopbdd/shop.feature');
        await page.evaluate(`(() => { const e = window.pymagic.editor; const m = e.getModel(); const end = m.getLineCount(); e.executeEdits('e2e', [{ range: { startLineNumber: end, startColumn: 1, endLineNumber: end, endColumn: m.getLineMaxColumn(end) }, text: '    When ' }]); e.setPosition({ lineNumber: end, column: 10 }); e.focus(); e.trigger('test', 'editor.action.triggerSuggest', {}); })()`);
        const labels = await page.waitFor(`(() => { const r = [...document.querySelectorAll('.suggest-widget .monaco-list-row')].map((x) => x.getAttribute('aria-label') ?? ''); return r.some((x) => x.startsWith('I pay')) ? r : null; })()`, 15000, 'the when steps');
        assert.ok(!labels.some((x) => x.includes('apples')), labels.join(' | '));
        await page.evaluate(`window.pymagic.editor.trigger('test', 'hideSuggestWidget', {})`);
    });

    test('Gherkin in German: its keywords are read, an undefined step is marked; an unknown language is marked', async () => {
        put('shopde/steps/shop.py', steps);
        put('shopde/laden.feature', '# language: de\nFunktionalität: Laden\n  Szenario: S\n    Angenommen 3 apples in the basket\n    Wenn I leave\n');
        await open('shopde/laden.feature');
        const found = await marks('shopde/laden.feature', "Undefined step reference: 'I leave'");
        assert.ok(found.some((x) => x.startsWith('5: ')), found.join(' | '));
        assert.ok(!found.some((x) => x.startsWith('4: ')), found.join(' | '));
        // the file's own `# language: de` makes Funktionalität a keyword (alone the line would be English)
        const keyword = await page.evaluate(`window.pymagic.monaco.editor.tokenize(window.pymagic.editor.getModel().getValue(), 'gherkin')[1].map((x) => x.type).join(' ')`);
        assert.match(keyword, /keyword/);
        put('shopde/xx.feature', '# language: xx\nFeature: F\n');
        await open('shopde/xx.feature');
        await marks('shopde/xx.feature', "Unknown language: 'xx'");
    });

    test('At the top level a branch that ends in a NoReturn call does not reach the name: the hover says int', async () => {
        put('toplevel.py', 'import os\nfrom typing import NoReturn\n\n\ndef fail() -> NoReturn:\n    raise SystemExit\n\n\nx = None\nif os.name:\n    x = 1\nelse:\n    fail()\nprint(x)\n');
        await open('toplevel.py');
        let text = null;
        for (let i = 0; i < 6 && !text; i++) {
            await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 14, column: 7 }); ed.focus(); ed.trigger('e2e', 'editor.action.showHover', {}); })()`);
            text = await page.waitFor(`[...document.querySelectorAll('.monaco-hover-content')].map((h) => h.textContent).join(' ').trim() || null`, 4000, 'the hover').catch(() => null);
        }
        assert.ok(text && /\bx: int\b/.test(text) && !/None/.test(text), text ?? 'no hover');
    });

    test('Tuples of one length with other item types are marked; the same items are not', async () => {
        put('tuples.py', 'def pair() -> tuple[int, int]:\n    return 1, "a"\n\n\nx: tuple[int, int] = (1, "a")\ny: tuple[int, int] = (1, 2)\n');
        await open('tuples.py');
        const found = await marks('tuples.py', "Expected type 'tuple[int, int]', got 'tuple[int, str]' instead");
        // the return and the annotated assignment (the arguments of a call wait for the type mismatch option)
        assert.ok(found.some((x) => x.startsWith('2: ') && x.includes('tuple[int, str]')), found.join(' | '));
        assert.ok(found.some((x) => x.startsWith('5: ') && x.includes('tuple[int, str]')), found.join(' | '));
        assert.ok(!found.some((x) => x.startsWith('6: ') && x.includes('Expected type')), found.join(' | '));
    });
});

describe('Incorrect type: arguments of the wrong type, unions, # type: ignore', () => {
    const put = (rel, text) => { mkdirSync(dirname(join(app.root, rel)), { recursive: true }); writeFileSync(join(app.root, rel), text); };
    const markers = () => page.evaluate(`(() => { const m = window.pymagic.editor.getModel(); return window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).map((x) => x.startLineNumber + ': ' + x.message); })()`);
    const until = (test, what) => page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); const f = window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).map((x) => x.startLineNumber + ': ' + x.message); return (${test})(f) ? f : null; })()`, 30000, what);
    const src = 'def need(x: str) -> None: ...\n\n\ndef g(s: str | None, n: int):\n    need(n)\n    need(s)\n    need(n)  # type: ignore\n    if s is None:\n        return\n    need(s)\n';

    test('A wrong argument is marked; a str | None is too (every member of a union), not after the None is ruled out; # type: ignore silences its line', async () => {
        put('wrongtypes.py', src);
        await open('wrongtypes.py');
        const found = await until((f) => f.some((x) => x.startsWith('5: ')) && f.some((x) => x.startsWith('6: ')), 'the two reports');
        assert.ok(found.some((x) => x === "5: Expected type 'str', got 'int' instead"), found.join(' | '));
        assert.ok(found.some((x) => x === "6: Expected type 'str', got 'str | None' instead"), found.join(' | '));
        assert.ok(!found.some((x) => x.startsWith('7: ') || x.startsWith('10: ')), found.join(' | '));
    });

    test('Settings: with "Check every member of a union" off, a str | None goes where a str is wanted (PyCharm before 2026.1)', async () => {
        await open('wrongtypes.py');
        await until((f) => f.some((x) => x.startsWith('6: ')), 'the union report');
        try {
            await page.evaluate(`window.pymagic.changeSetting({ strictUnions: false })`);
            const found = await until((f) => f.some((x) => x.startsWith('5: ')) && !f.some((x) => x.startsWith('6: ')), 'the union report gone');
            assert.ok(found.some((x) => x.startsWith("5: Expected type 'str', got 'int'")), found.join(' | '));
        }
        finally {
            await page.evaluate(`window.pymagic.changeSetting({ strictUnions: true })`);
        }
        await until((f) => f.some((x) => x.startsWith('6: ')), 'the union report back');
    });
});

describe('JSON Schemas: the status bar picker, ignoring a file, a mapping, "$schema"', () => {
    const put = (rel, text) => { mkdirSync(dirname(join(app.root, rel)), { recursive: true }); writeFileSync(join(app.root, rel), text); };
    const schemaMarks = (rel) => `(() => { const m = window.pymagic.editor.getModel(); if (!m.uri.path.endsWith(${JSON.stringify(rel)})) return null; return window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri, owner: 'pymagic-schema' }).map((x) => x.startLineNumber + ': ' + x.message); })()`;
    const status = () => page.evaluate(`document.getElementById('schema')?.textContent ?? ''`);
    const pick = async (label) => {
        await page.evaluate(`document.getElementById('schema').click()`);
        await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((d) => d.textContent.trim() === ${JSON.stringify(label)})`, 10000, 'the menu row ' + label);
        await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.trim() === ${JSON.stringify(label)}).click()`);
    };
    const clearMappings = () => { mkdirSync(join(app.root, '.pymagic'), { recursive: true }); writeFileSync(join(app.root, '.pymagic/schema-mappings.json'), '{"mappings":[],"ignored":[]}\n'); };

    test('tsconfig.json, with comments, is checked against TSConfig; the status bar names it; ignoring the file takes its marks away', async () => {
        clearMappings();
        put('web/tsconfig.json', '{\n  // the compiler\n  "compilerOptions": {\n    "strict": "yes",\n    "target": "ES2022",\n  },\n}\n');
        await open('web/tsconfig.json');
        const found = await page.waitFor(`(() => { const f = ${schemaMarks('web/tsconfig.json')}; return f && f.some((x) => x.startsWith('4: ')) ? f : null; })()`, 30000, 'the strict mark');
        assert.ok(!found.some((x) => x.startsWith('5: ')), found.join(' | '));   // a good target, and the comments and trailing commas are fine
        await page.waitFor(`document.getElementById('schema')?.textContent === 'Schema: TSConfig'`, 10000, 'Schema: TSConfig');
        await pick('Ignore JSON schema for file');
        await page.waitFor(`(() => { const f = ${schemaMarks('web/tsconfig.json')}; return f && f.length === 0; })()`, 15000, 'the marks gone');
        assert.equal(await status(), 'Schema: ignored');
        assert.match(readFileSync(join(app.root, '.pymagic/schema-mappings.json'), 'utf8'), /"web\/tsconfig.json"/);
        await pick('Ignore JSON schema for file');   // checked again
        await page.waitFor(`(() => { const f = ${schemaMarks('web/tsconfig.json')}; return f && f.length > 0; })()`, 15000, 'the marks back');
    });

    test('a file\'s own "$schema" naming a schema of the project, and a file mapped to Jest from the picker', async () => {
        clearMappings();
        put('schemas/app.schema.json', '{"type": "object", "required": ["name"], "properties": {"name": {"type": "string"}}}\n');
        put('conf/app.json', '{\n  "$schema": "../schemas/app.schema.json",\n  "size": 1\n}\n');
        await open('conf/app.json');
        await page.waitFor(`(() => { const f = ${schemaMarks('conf/app.json')}; return f && f.some((x) => x.includes('The property name is required')) ? f : null; })()`, 30000, 'name required');
        await page.waitFor(`document.getElementById('schema')?.textContent === 'Schema: schemas/app.schema.json'`, 10000, 'the project schema named');

        put('conf/test-settings.json', '{\n  "roots": "src"\n}\n');
        await open('conf/test-settings.json');
        await page.waitFor(`document.getElementById('schema')?.textContent === 'No JSON schema'`, 15000, 'no schema');
        await pick('Jest');
        await page.waitFor(`(() => { const f = ${schemaMarks('conf/test-settings.json')}; return f && f.some((x) => x.startsWith('2: ') && x.includes('Should be array')) ? f : null; })()`, 30000, 'roots as Jest has it');
        assert.equal(await status(), 'Schema: Jest');
        const kept = JSON.parse(readFileSync(join(app.root, '.pymagic/schema-mappings.json'), 'utf8'));
        assert.deepEqual(kept.mappings, [{ name: 'Jest', schema: 'jest', patterns: ['conf/test-settings.json'] }]);
        clearMappings();
    });

    test('SchemaStore: a "$schema" in its .json form, a file only its catalog knows, icons in the menu, and its schemas behind a search', async () => {
        clearMappings();
        put('conf/with-schema.json', '{\n  "$schema": "https://json.schemastore.org/tsconfig.json",\n  "compilerOptions": {}\n}\n');
        await open('conf/with-schema.json');
        await page.waitFor(`document.getElementById('schema')?.textContent === 'Schema: TSConfig'`, 15000, 'TSConfig by its address');
        put('web/nodemon.json', '{\n  "watch": ["src"]\n}\n');
        await open('web/nodemon.json');
        await page.waitFor(`document.getElementById('schema')?.textContent === 'Schema: nodemon.json'`, 15000, 'nodemon by SchemaStore\'s catalog');
        // the menu: every row has its icon (the ticked one its tick), SchemaStore's tick and its search
        await page.evaluate(`document.getElementById('schema').click()`);
        await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((d) => d.textContent.startsWith('SchemaStore.org schemas ('))`, 10000, 'the SchemaStore row');
        const bare = await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].filter((r) => !r.querySelector('.ctx-check svg')).map((r) => r.textContent)`);
        assert.deepEqual(bare, []);
        assert.ok(await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((r) => r.textContent === 'nodemon.json (SchemaStore.org)' && r.querySelector('use[href="#i-tick"]'))`), 'nodemon ticked');
        await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.startsWith('SchemaStore.org schemas (')).click()`);
        await page.waitFor(`document.querySelector('.schema-store input')`, 5000, 'the search');
        await page.evaluate(`(() => { const i = document.querySelector('.schema-store input'); i.value = 'lerna'; i.dispatchEvent(new Event('input')); })()`);
        await page.waitFor(`[...document.querySelectorAll('.schema-store-row')].some((b) => b.textContent.startsWith('lerna.json'))`, 5000, 'lerna found');
        await page.evaluate(`[...document.querySelectorAll('.schema-store-row')].find((b) => b.textContent.startsWith('lerna.json')).click()`);
        await page.waitFor(`document.getElementById('schema')?.textContent === 'Schema: lerna.json'`, 15000, 'mapped to lerna');
        assert.deepEqual(JSON.parse(readFileSync(join(app.root, '.pymagic/schema-mappings.json'), 'utf8')).mappings, [{ name: 'lerna.json', schema: 'store:lerna', patterns: ['web/nodemon.json'] }]);
        clearMappings();
    });

    test('Settings > JSON Schemas lists the project\'s mappings; Remove takes one away', async () => {
        writeFileSync(join(app.root, '.pymagic/schema-mappings.json'), JSON.stringify({ mappings: [{ name: 'Mine', schema: 'tsconfig', patterns: ['base/*.json'] }], ignored: ['old.json'] }));
        await page.evaluate(`${$('b-gear')}.click()`);
        await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((d) => d.textContent.trim().startsWith('Settings…'))`, 5000, 'the gear\'s menu');
        await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.trim().startsWith('Settings…')).click()`);
        await page.waitFor(`document.querySelector('.settings-nav')`, 5000, 'the Settings window');
        await page.evaluate(`[...document.querySelectorAll('.settings-nav button')].find((b) => b.textContent === 'JSON Schemas').click()`);
        const rows = await page.waitFor(`(() => { const r = [...document.querySelectorAll('.schema-mapping-row')].map((x) => x.textContent); return r.length === 2 ? r : null; })()`, 10000, 'the two rows');
        assert.match(rows[0], /Mine → TSConfig/);
        assert.match(rows[0], /base\/\*\.json/);
        assert.match(rows[1], /old\.json \(no schema\)/);
        await page.evaluate(`[...document.querySelector('.schema-mapping-row').querySelectorAll('button')].find((b) => b.textContent === 'Remove').click()`);
        await page.waitFor(`document.querySelectorAll('.schema-mapping-row').length === 1`, 10000, 'one row left');
        assert.deepEqual(JSON.parse(readFileSync(join(app.root, '.pymagic/schema-mappings.json'), 'utf8')).mappings, []);
        await page.evaluate(`[...document.querySelectorAll('.settings-foot button')].find((b) => b.textContent === 'Done').click()`);
        clearMappings();
    });
});

describe('the project tree: the project folder as its first row, and its menu as PyCharm has it', () => {
    const rowOf = (path) => `document.querySelector('#files li[data-path="${path}"]')`;
    const rightClick = (path) => page.evaluate(`(() => { const li = ${rowOf(path)}; const r = li.getBoundingClientRect(); li.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, clientX: r.left + 20, clientY: r.top + 5 })); })()`);
    const labels = (level) => page.evaluate(`[...document.querySelectorAll('.ctx-menu')][${level}] ? [...[...document.querySelectorAll('.ctx-menu')][${level}].querySelectorAll('.ctx-item .ctx-label')].map((x) => x.textContent) : null`);
    const choose = async (level, label) => {
        await page.waitFor(`(() => { const m = [...document.querySelectorAll('.ctx-menu')][${level}]; return !!m && [...m.querySelectorAll('.ctx-item')].some((r) => r.querySelector('.ctx-label').textContent === ${JSON.stringify(label)}); })()`, 5000, 'the row ' + label);
        await page.evaluate(`[...[...document.querySelectorAll('.ctx-menu')][${level}].querySelectorAll('.ctx-item')].find((r) => r.querySelector('.ctx-label').textContent === ${JSON.stringify(label)}).click()`);
    };
    const answer = async (text) => {
        await page.waitFor(`document.querySelector('.ask-box input')`, 5000, 'the name box');
        await page.evaluate(`(() => { const i = document.querySelector('.ask-box input'); i.value = ${JSON.stringify(text)}; [...document.querySelectorAll('.ask-box button')].find((b) => b.textContent === 'OK').click(); })()`);
    };

    test('the first row is the project folder, with its path; everything else is inside it, and it closes and opens', async () => {
        const root = await page.waitFor(`(() => { const li = document.querySelector('#files li'); return li?.dataset.kind === 'root' ? { name: li.querySelector('.fname').textContent, path: li.querySelector('.fpath').textContent } : null; })()`, 10000, 'the root row');
        assert.equal(root.name, basename(app.root));
        assert.equal(root.path.replace(/\\/g, '/').toLowerCase(), app.root.replace(/\\/g, '/').replace(/\/$/, '').toLowerCase());
        // the project's top-level rows are one step in from it
        const pads = await page.evaluate(`[document.querySelector('#files li.root').style.paddingLeft, ${rowOf('calc.py')}?.style.paddingLeft]`);
        assert.deepEqual(pads, ['8px', '22px']);
        await page.evaluate(`document.querySelector('#files li.root').click()`);
        await page.waitFor(`document.querySelectorAll('#files li').length === 1`, 5000, 'only the root');
        await page.evaluate(`document.querySelector('#files li.root').click()`);
        await page.waitFor(`!!${rowOf('calc.py')}`, 5000, 'the files again');
    });

    test('New on the project folder: PyCharm\'s entries; a Python file gets .py, a package its __init__.py, .gitignore its start', async () => {
        await rightClick('');
        await choose(0, 'New');
        const entries = await page.waitFor(`(() => { const m = [...document.querySelectorAll('.ctx-menu')][1]; return m ? [...m.querySelectorAll('.ctx-item .ctx-label')].map((x) => x.textContent) : null; })()`, 5000, 'the New submenu');
        for (const e of ['Python File', 'File', 'Scratch File', 'Directory', 'Python Package', 'HTML File', 'Stylesheet', 'Dockerfile', 'Dev Container Config…', 'Docker Compose File', 'HTTP Request', 'OpenAPI Specification', 'EditorConfig File', '.ignore File'])
            assert.ok(entries.includes(e), `${e} in ${entries.join(', ')}`);
        // each has its icon, as in PyCharm (the sprite's symbol, drawn where a tick would be)
        const missing = await page.evaluate(`[...[...document.querySelectorAll('.ctx-menu')][1].querySelectorAll('.ctx-item')].filter((r) => !r.querySelector('.ctx-check svg.ctx-icon use[href^="#i-"]')).map((r) => r.textContent)`);
        assert.deepEqual(missing, []);
        assert.equal(await page.evaluate(`document.querySelector('#i-docker') !== null && document.querySelector('#i-notebook') !== null`), true);
        await choose(1, 'Python File');
        await answer('made_by_new');
        await page.waitFor(`window.pymagic.editor.getModel().uri.path.endsWith('/made_by_new.py')`, 15000, 'the new file open');
        assert.ok(existsSync(join(app.root, 'made_by_new.py')));

        await rightClick('');
        await choose(0, 'New');
        await choose(1, 'Python Package');
        await answer('newpkg');
        await page.waitFor(`window.pymagic.editor.getModel().uri.path.endsWith('/newpkg/__init__.py')`, 15000, 'the package\'s __init__.py');

        await rightClick('');
        await choose(0, 'New');
        await choose(1, '.ignore File');
        await choose(2, '.dockerignore (Docker)');
        await page.waitFor(`window.pymagic.editor.getModel().uri.path.endsWith('/.dockerignore')`, 15000, '.dockerignore open');
        assert.match(readFileSync(join(app.root, '.dockerignore'), 'utf8'), /\.venv/);
    });

    test('a file\'s menu: Copy path/reference gives its absolute path, its path, its name and its dotted reference; Diagrams and More run/debug', async () => {
        mkdirSync(join(app.root, 'shop/orders'), { recursive: true });
        writeFileSync(join(app.root, 'shop/orders/models.py'), 'class Order:\n    pass\n');
        writeFileSync(join(app.root, 'shop/__init__.py'), '');
        writeFileSync(join(app.root, 'shop/orders/__init__.py'), '');
        await page.evaluate(`document.getElementById('ex-refresh').click()`);
        await page.waitFor(`!!${rowOf('shop')}`, 10000, 'shop in the tree');
        await page.evaluate(`window.pymagic.openFile('shop/orders/models.py')`);
        await page.waitFor(`!!${rowOf('shop/orders/models.py')}`, 10000, 'the file in the tree');
        await rightClick('shop/orders/models.py');
        const top = await labels(0);
        for (const e of ['New', 'Copy path/reference', 'Find usages', 'Rename…', 'Diagrams', 'Local history']) assert.ok(top.includes(e), `${e} in ${top.join(', ')}`);
        await choose(0, 'Copy path/reference');
        const copies = await page.waitFor(`(() => { const m = [...document.querySelectorAll('.ctx-menu')][1]; return m ? [...m.querySelectorAll('.ctx-item .ctx-label')].map((x) => x.textContent) : null; })()`, 5000, 'the copy submenu');
        assert.ok(copies.includes('Path from the project folder: shop/orders/models.py'), copies.join(' | '));
        assert.ok(copies.includes('File name: models.py'), copies.join(' | '));
        assert.ok(copies.includes('Reference: shop.orders.models'), copies.join(' | '));
        assert.ok(copies.some((c) => c.startsWith('Absolute path: ')), copies.join(' | '));
        await page.evaluate(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))`);
        await page.evaluate(`document.body.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }))`);
    });
});

describe('an environment made in the project from outside', () => {
    test('a venv made by a command (uv sync, python -m venv) is found at once: taken as the interpreter when the project had none of its own, listed otherwise', async () => {
        assert.ok(!existsSync(join(projectRoot, 'venv')), 'the test project has no venv folder');
        const before = await page.evaluate(`fetch('/api/interpreter').then((r) => r.json())`);
        const made = spawnSync('python', ['-m', 'venv', '--without-pip', join(projectRoot, 'venv')], { encoding: 'utf8' });
        assert.equal(made.status, 0, made.stderr);
        try {
            const own = before.path && before.path.toLowerCase().startsWith(projectRoot.toLowerCase());
            // the picker lists it without a reload; and, when the project had no environment of its own, it is the one chosen
            const found = await page.waitFor(`[...document.getElementById('interp').options].find((o) => /[\\\\/]venv[\\\\/]/i.test(o.value))?.value`, 90000, 'the new venv in the picker');
            assert.ok(found, 'listed');
            if (!own) {
                await page.waitFor(`document.getElementById('interp').value.toLowerCase().includes('\\\\venv\\\\')`, 30000, 'the new venv chosen');
                const now = await page.evaluate(`fetch('/api/interpreter').then((r) => r.json())`);
                assert.ok(/[\\/]venv[\\/]/i.test(now.path), now.path);
            } else {
                assert.equal((await page.evaluate(`fetch('/api/interpreter').then((r) => r.json())`)).path, before.path, 'the project\'s own environment is kept');
            }
        } finally {
            // the interpreter the other tests use
            if (before.path) await page.evaluate(`fetch('/api/interpreter', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path: ${JSON.stringify(before.path)} }) }).then((r) => r.status)`);
            removeTree(join(projectRoot, 'venv'));
        }
    });

    test('a package installed from outside (uv add, pip install in the terminal) is seen without a restart: its "not installed" marker goes', async () => {
        const before = await page.evaluate(`fetch('/api/interpreter').then((r) => r.json())`);
        const envDir = join(projectRoot, 'pkgenv');
        const made = spawnSync('python', ['-m', 'venv', '--without-pip', envDir], { encoding: 'utf8' });
        assert.equal(made.status, 0, made.stderr);
        try {
            const python = join(envDir, 'Scripts', 'python.exe');
            const chose = await page.evaluate(`fetch('/api/interpreter', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path: ${JSON.stringify(python)} }) }).then((r) => r.status)`);
            assert.equal(chose, 200);
            await page.evaluate(`window.dispatchEvent(new Event('pymagic:interpreter'))`);
            mkdirSync(join(projectRoot, 'pkgcheck'), { recursive: true });
            writeFileSync(join(projectRoot, 'pkgcheck', 'requirements.txt'), 'shoplib\n');
            await open('pkgcheck/requirements.txt');
            const marker = `(() => { const m = window.pymagic.editor.getModel(); return window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).some((x) => x.message.includes("'shoplib' is not installed")); })()`;
            await page.waitFor(marker, 30000, 'shoplib marked as not installed');
            // what pip writes into site-packages, by something else than the Packages panel
            const site = join(envDir, 'Lib', 'site-packages');
            mkdirSync(join(site, 'shoplib'), { recursive: true });
            writeFileSync(join(site, 'shoplib', '__init__.py'), 'X = 1\n');
            mkdirSync(join(site, 'shoplib-1.0.dist-info'), { recursive: true });
            writeFileSync(join(site, 'shoplib-1.0.dist-info', 'METADATA'), 'Metadata-Version: 2.1\nName: shoplib\nVersion: 1.0\n');
            writeFileSync(join(site, 'shoplib-1.0.dist-info', 'RECORD'), '');
            await page.waitFor(`!${marker}`, 40000, 'the marker gone once the install settled');
        } finally {
            if (before.path) await page.evaluate(`fetch('/api/interpreter', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path: ${JSON.stringify(before.path)} }) }).then((r) => r.status)`);
            await page.evaluate(`window.dispatchEvent(new Event('pymagic:interpreter'))`);
            removeTree(envDir);
            removeTree(join(projectRoot, 'pkgcheck'));
        }
    });

    test('npm install from outside: a package.json dependency marked "not installed" loses its marker once node_modules has it', async () => {
        const app = join(projectRoot, 'npmcheck');
        mkdirSync(app, { recursive: true });
        writeFileSync(join(app, 'package.json'), '{\n  "name": "shop",\n  "dependencies": {\n    "express": "^4.18.2"\n  }\n}\n');
        try {
            await open('npmcheck/package.json');
            const marked = `(() => { const m = window.pymagic.editor.getModel(); return window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).some((x) => x.message.startsWith("Package 'express' is not installed")); })()`;
            await page.waitFor(marked, 20000, 'express marked as not installed');
            // what npm install writes, by something else than PyMagic
            mkdirSync(join(app, 'node_modules', 'express', 'lib'), { recursive: true });
            writeFileSync(join(app, 'node_modules', 'express', 'package.json'), '{ "name": "express", "version": "4.18.2" }');
            writeFileSync(join(app, 'node_modules', 'express', 'lib', 'express.js'), 'module.exports = 1;\n');
            writeFileSync(join(app, 'node_modules', '.package-lock.json'), '{}');
            await page.waitFor(`!${marked}`, 30000, 'the marker gone once the install settled');
        } finally {
            removeTree(app);
        }
    });
});

describe('Settings > Plugins', () => {
    const openPlugins = async () => {
        await page.evaluate(`${$('b-gear')}.click()`);
        await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((d) => d.textContent.trim().startsWith('Settings…'))`, 5000, 'the gear\'s menu');
        await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.trim().startsWith('Settings…')).click()`);
        await page.waitFor(`document.querySelector('.settings-nav')`, 5000, 'the Settings window');
        await page.evaluate(`[...document.querySelectorAll('.settings-nav button')].find((b) => b.textContent === 'Plugins').click()`);
        await page.waitFor(`document.querySelectorAll('#plugins-list .plugin-row').length >= 8`, 5000, 'the plugins');
    };
    const box = (id) => `document.querySelector('#plugins-list .plugin-row[data-plugin="${id}"] input')`;
    const closeSettings = () => page.evaluate(`[...document.querySelectorAll('.settings-foot button')].find((b) => b.textContent === 'Done').click()`);
    // the setting written (it is saved a moment after a change), then the window reloaded
    const reload = async (plugins) => {
        await page.waitFor(`fetch('/api/settings').then((r) => r.json()).then((s) => JSON.stringify(s.plugins ?? {}) === ${JSON.stringify(JSON.stringify(plugins))})`, 10000, 'the plugins saved');
        await page.evaluate('location.reload()');
        await sleep(1000);
        await page.waitFor('!!(window.pymagic && window.pymagic.openFile && window.pymagic.editor.getModel())', 60000, 'the editor after the reload');
    };
    after(async () => {
        await page.evaluate(`window.pymagic.changeSetting({ plugins: {} })`);
        await reload({});
    });

    test('a plugin switched off is gone after the reload: its tab, button and menu entries; switched on, it is back', async () => {
        await openPlugins();
        assert.equal(await page.evaluate(`[...document.querySelectorAll('#plugins-list .plugin-row input')].every((b) => b.checked)`), true, 'every plugin starts on');
        assert.equal(await page.evaluate(`document.getElementById('plugins-reload').hidden`), true);
        // found by a word of its description
        await page.evaluate(`(() => { const s = document.getElementById('plugins-search'); s.value = 'call graph'; s.dispatchEvent(new Event('input')); })()`);
        assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('#plugins-list .plugin-row')].map((r) => r.dataset.plugin)`), ['profiler']);
        await page.evaluate(`${box('profiler')}.click()`);
        assert.equal(await page.evaluate(`document.getElementById('plugins-reload').hidden`), false, 'the reload is asked for');
        await closeSettings();
        // still there until the reload
        assert.equal(await page.evaluate(`${$('t-profiler')}.classList.contains('plugin-off')`), false);
        await reload({ profiler: false });
        for (const id of ['t-profiler', 'profiler-tab', 'b-profile'])
            assert.equal(await page.evaluate(`getComputedStyle(${$(id)}).display`), 'none', id + ' hidden');
        assert.equal(await page.evaluate(`getComputedStyle(${$('t-database')}).display === 'none'`), false, 'the other plugins stay');
        // not in the Tools menu (the others still are), nor in the Run menu
        await page.evaluate(`document.querySelector('#brand .mark').dispatchEvent(new MouseEvent('click', { bubbles: true }))`);
        const subOf = async (label) => {
            await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((d) => d.textContent.trim() === ${JSON.stringify(label)})`, 5000, 'the main menu');
            await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.trim() === ${JSON.stringify(label)}).click()`);
            await sleep(200);
            return page.evaluate(`[...document.querySelectorAll('.ctx-menu')].pop().textContent`);
        };
        const tools = await subOf('Tools');
        assert.ok(!tools.includes('Profiler') && tools.includes('Database'), tools);
        assert.ok(!(await subOf('Run')).includes('Profile'));
        await page.key(27, 'Escape');
        await page.waitFor(`!document.querySelector('.ctx-menu')`, 5000, 'the menu closed');
        await openPlugins();
        assert.equal(await page.evaluate(`${box('profiler')}.checked`), false);
        assert.equal(await page.evaluate(`document.getElementById('plugins-reload').hidden`), true, 'what is set up is what the setting says');
        await page.evaluate(`${box('profiler')}.click()`);
        await closeSettings();
        await reload({});
        assert.notEqual(await page.evaluate(`getComputedStyle(${$('t-profiler')}).display`), 'none', 'back after the reload');
    });

    test('Django, Jupyter Notebooks, Docker and Git switched off: their parts gone, Pull Requests off with Git, a notebook opened as its text', async () => {
        const off = { django: false, notebooks: false, docker: false, git: false };
        await page.evaluate(`window.pymagic.changeSetting({ plugins: ${JSON.stringify(off)} })`);
        await reload(off);
        for (const id of ['t-git', 'git-tab', 'r-git', 'branch-chip', 'b-update', 'b-commit', 'b-push', 't-prs', 't-django', 'st-django'])
            assert.equal(await page.evaluate(`getComputedStyle(${$(id)}).display`), 'none', id + ' hidden');
        // the diff viewer stays (tests and comparisons use it); the Git status is not asked for
        assert.ok(await page.evaluate(`!!${$('git-diff')}`));
        // a notebook opens as its JSON text
        assert.equal(await page.evaluate('window.pymagic.notebook'), null);
        await open('nb_demo.ipynb');
        assert.match(await page.evaluate('window.pymagic.editor.getModel().getValue()'), /"cells"/);
        // Pull Requests is off with Git, and says so
        await openPlugins();
        assert.deepEqual(await page.evaluate(`[${box('prs')}.checked, ${box('prs')}.disabled]`), [false, true]);
        assert.match(await page.evaluate(`document.querySelector('#plugins-list .plugin-row[data-plugin="prs"]').textContent`), /Needs Git, which is off/);
        await closeSettings();
        // no Docker kinds among the remote interpreters
        await page.evaluate(`${$('b-remote')}.click()`);
        await page.waitFor(`document.querySelector('.ask-box select')`, 5000, 'the remote interpreter window');
        assert.deepEqual(await page.evaluate(`[...document.querySelector('.ask-box select').options].map((o) => o.value)`), ['wsl', 'ssh']);
        await page.evaluate(`[...document.querySelectorAll('.ask-box button')].find((b) => b.textContent === 'Cancel').click()`);
        // New Project offers neither Django nor Jupyter, nor a Git repository
        await page.evaluate(`document.querySelector('#brand .mark').dispatchEvent(new MouseEvent('click', { bubbles: true }))`);
        await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((d) => d.textContent.trim() === 'File')`, 5000, 'the main menu');
        await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.trim() === 'File').click()`);
        await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((d) => d.textContent.trim().startsWith('New project'))`, 5000, 'the File menu');
        const menus = await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].map((d) => d.textContent.trim()).join('|')`);
        assert.ok(!/(^|\|)Git(\||$)/.test(menus), 'no Git menu: ' + menus);
        await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.trim().startsWith('New project')).click()`);
        await page.waitFor(`document.querySelector('#np-kinds button')`, 10000, 'New Project');
        assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('#np-kinds button')].map((b) => b.textContent)`), ['Pure Python', 'FastAPI', 'Flask']);
        assert.equal(await page.evaluate(`getComputedStyle(document.getElementById('np-git').closest('label')).display`), 'none');
        await page.evaluate(`document.getElementById('np-cancel').click()`);
    });

    test('Flask, FastAPI, Coverage, Diagrams, Docker, WSL and SSH switched off: their parts gone, and no remote interpreter button with no kind left', async () => {
        const off = { flask: false, fastapi: false, coverage: false, diagrams: false, docker: false, wsl: false, ssh: false };
        await page.evaluate(`window.pymagic.changeSetting({ plugins: ${JSON.stringify(off)} })`);
        await reload(off);
        for (const id of ['t-coverage', 'coverage-tab', 'b-cov', 'b-remote'])
            assert.equal(await page.evaluate(`getComputedStyle(${$(id)}).display`), 'none', id + ' hidden');
        assert.notEqual(await page.evaluate(`getComputedStyle(${$('t-git')}).display`), 'none', 'Git is back on');
        // the main menu: no Diagrams, no Run with coverage, no remote interpreter
        const menuOf = async (label) => {
            await page.evaluate(`document.querySelector('#brand .mark').dispatchEvent(new MouseEvent('click', { bubbles: true }))`);
            await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((d) => d.textContent.trim() === ${JSON.stringify(label)})`, 5000, 'the main menu');
            await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.trim() === ${JSON.stringify(label)}).click()`);
            await sleep(200);
            const text = await page.evaluate(`[...document.querySelectorAll('.ctx-menu')].pop().textContent`);
            await page.key(27, 'Escape');
            await page.waitFor(`!document.querySelector('.ctx-menu')`, 5000, 'the menu closed');
            return text;
        };
        const tools = await menuOf('Tools');
        assert.ok(!tools.includes('Diagrams') && !tools.includes('WSL, Docker or SSH'), tools);
        assert.ok(!(await menuOf('Run')).includes('coverage'));
        // New Project offers neither Flask nor FastAPI
        await page.evaluate(`document.querySelector('#brand .mark').dispatchEvent(new MouseEvent('click', { bubbles: true }))`);
        await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((d) => d.textContent.trim() === 'File')`, 5000, 'the main menu');
        await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.trim() === 'File').click()`);
        await page.waitFor(`[...document.querySelectorAll('.ctx-menu .ctx-item')].some((d) => d.textContent.trim().startsWith('New project'))`, 5000, 'the File menu');
        await page.evaluate(`[...document.querySelectorAll('.ctx-menu .ctx-item')].find((d) => d.textContent.trim().startsWith('New project')).click()`);
        await page.waitFor(`document.querySelector('#np-kinds button')`, 10000, 'New Project');
        assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('#np-kinds button')].map((b) => b.textContent)`), ['Pure Python', 'Django', 'Jupyter']);
        await page.evaluate(`document.getElementById('np-cancel').click()`);
        // the engine lists no remote interpreter kinds
        const targets = await page.evaluate(`fetch('/api/interpreter/targets').then((r) => r.json())`);
        assert.deepEqual([targets.wsl.length, targets.ssh.installed, targets.docker.available, targets.compose.services.length], [0, false, false, 0]);
    });
});

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
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

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
    'remote_job.py': 'def work(n):\n    total = n * 2\n    return total\n\n\nprint("job", work(21), flush=True)\n',
    'tests/__init__.py': '',
    'tests/test_calc.py': 'import unittest\n\n\nclass Calc(unittest.TestCase):\n    def test_ok(self):\n        self.assertEqual(1 + 1, 2)\n\n    def test_bad(self):\n        self.assertEqual(1 + 1, 3)\n',
};

let app, page;
const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;
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
let altPython = '';
let projectRoot = '';
before(async () => {
    // the scratch files of this run, not the user's own
    process.env.PYMAGIC_SCRATCHES = mkdtempSync(join(tmpdir(), 'pymagic-e2e-scratches-'));
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
    ] }));
    projectRoot = root;
    app = await launch(root);
    page = app.page;
});
after(async () => { await app?.close(); echo?.close(); });
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
    const share = mkdtempSync(join(tmpdir(), 'pymagic-e2e-share-'));
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

    test('package.json, pyproject.toml and a GitHub workflow are checked against the kept schemas', async () => {
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

    test('Renovate, OpenAPI and a Kubernetes manifest (the second document of a file) are checked against the kept schemas', async () => {
        const schema = (file) => open(file).then(() => page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); const found = window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).filter((x) => x.code === 'schema'); return found.length ? found.map((x) => x.startLineNumber + ':' + x.startColumn + ' ' + x.message) : null; })()`, 20000, 'the schema problems of ' + file));
        assert.deepEqual(await schema('renovate.json'), ['2:3 Should be array, not integer']);
        assert.deepEqual(await schema('openapi.yaml'), ['2:1 The property version is required']);
        assert.deepEqual(await schema('k8s.yaml'), ['10:3 Should be integer, not string']);
        // OpenAPI 3.1 is not checked (its schema needs $dynamicRef)
        await open('openapi31.yaml');
        await new Promise((r) => setTimeout(r, 2500));
        assert.deepEqual(await page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ resource: window.pymagic.editor.getModel().uri }).filter((x) => x.code === 'schema').length`), 0);
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
        const dir = mkdtempSync(join(tmpdir(), 'pymagic-e2e-remote-'));
        writeFileSync(join(dir, 'launcher.py'), `import sys\nsys.path.insert(0, ${JSON.stringify(helper)})\n${snippet}\nimport runpy\nrunpy.run_path(${JSON.stringify(join(projectRoot, 'remote_job.py'))}, run_name='__main__')\n`);
        const program = spawn('python', ['-u', 'launcher.py'], { cwd: dir });
        let printed = '';
        program.stdout.on('data', (d) => { printed += d; });
        const exited = new Promise((ok) => program.on('exit', ok));
        try {
            assert.equal(await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 30000, 'the stop'), 'work remote_job.py:2');
            assert.match(await output(), /A program connected from 127\.0\.0\.1/);
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
        wheels = mkdtempSync(join(tmpdir(), 'pymagic-e2e-wheels-'));
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
        const env = mkdtempSync(join(tmpdir(), 'pymagic-e2e-pkgenv-'));
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
});

// The page in a real browser: PyMagic.exe started on a project of its own and driven as a user would, over the DevTools
// protocol (app.mjs). Run after building the exe:
//   node desktop/package.mjs --no-installer
//   node --test desktop/e2e/page.e2e.mjs
// Each check is one a change has broken or could break unseen by the Node tests of the page's pure modules.
import { after, afterEach, before, describe, test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
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
    'package.json': '{\n  "name": "Bad Name",\n  "scripts": {\n    "t": 1\n  }\n}\n',
    'pyproject.toml': '[project]\nname = 5\n',
    'broken.toml':'[tool]\nname = "x"\nname = "y"\nflag = True\n',
    'broken.yml': 'a: 1\nb: 2\na: 3\n',
    'notes.md': '# Notes\n\nSee [the guide](docs/nope.md) and [here](#notes).\n\n[g](docs/guide.md#nowhere) [h](docs/guide.md#setup)\n',
    'docs/guide.md': '# Guide\n\n## Setup\n',
    'shop_lib.py': 'def total(prices):\n    return sum(prices)\n',
    'inj.py': 'import re\n\n# language=json\nCONFIG = \'{"a": 1}\'\nQUERY = "SELECT id, name FROM item WHERE price > 1"\nHALF = "SELECT  FROM item"\nPATTERN = re.compile(r"^(\\d+)-[a-z]{2}$")\n',
    'typo.py': '# Recieve the mesage here\n\n\ndef fetch_mesage():\n    return 1\n',
    'hist.py': 'def f():\n    return 0\n',
    'intent.py': "name = 'x'\nprint('Hello ' + name + '!')\nvalue = 255\n",
    'rx.py': 'import re\nPATTERN = re.compile(r"[aa]\\-x")\nN = re.compile(f"{PATTERN.pattern}[cc]")\n',
    'api.http':'### the items\nGET {{host}}/items\n    ?page=2\nAccept: application/json\n\n###\nPOST {{host}}/items\nContent-Type: application/json\nAuthorization: Bearer {{token}}\n\n{"name": "pen"}\n',
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

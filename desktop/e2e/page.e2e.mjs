// The page in a real browser: PyMagic.exe started on a project of its own and driven as a user would, over the DevTools
// protocol (app.mjs). Run after building the exe:
//   node desktop/package.mjs --no-installer
//   node --test desktop/e2e/page.e2e.mjs
// Each check is one a change has broken or could break unseen by the Node tests of the page's pure modules.
import { after, afterEach, before, describe, test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
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
    'api.http': '### the items\nGET {{host}}/items\n    ?page=2\nAccept: application/json\n\n###\nPOST {{host}}/items\nContent-Type: application/json\nAuthorization: Bearer {{token}}\n\n{"name": "pen"}\n',
    'tests/__init__.py': '',
    'tests/test_calc.py': 'import unittest\n\n\nclass Calc(unittest.TestCase):\n    def test_ok(self):\n        self.assertEqual(1 + 1, 2)\n\n    def test_bad(self):\n        self.assertEqual(1 + 1, 3)\n',
};

let app, page;
const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;
const open = async (rel) => {
    await page.evaluate(`window.pymagic.openFile(${JSON.stringify(rel)})`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith(${JSON.stringify('/' + rel)})`, 15000, rel);
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

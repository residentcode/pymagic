// The MCP server in the built PyMagic.exe, used as an AI client uses it: Streamable HTTP's initialize and tools/list, then each tool on a project of
// the check's own (read, make, replace, find by name and glob, search by text and regex, the tree, a file's problems, a symbol's hover and
// definition, Rename across files, Reformat, open in the editor, the open files, the run configurations and running one, a shell command with the
// project's interpreter, the project's info), a path outside the project refused, the stdio bridge run by PyMagic.exe itself as Node.js, and
// Settings > MCP Server showing it listening. Brave mode is on in the check's own settings, so no question is asked.
// Run: node desktop/e2e/mcp.check.mjs
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { join, dirname } from 'node:path';
import { launch, project, sleep, EXE } from './app.mjs';

const port = await new Promise((ok) => { const s = createServer(); s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => ok(p)); }); });
const root = project({
    'shop.py': 'class Shop:\n    def total(self, prices: list[float]) -> float:\n        """Adds the prices."""\n        return sum(prices)\n\n\ndef broken():\n    return undefined_name\n',
    'main.py': 'from shop import Shop\n\nprint(Shop().total([1.0, 2.5]))\n',
    'messy.py': 'x=[1,2 ,3]\ndef f( a,b ):\n  return a+b\n',
    'hello.py': 'import sys\nprint("hello from run", sys.argv[1:])\nsys.exit(3)\n',
    'pkg/deep/notes.md': '# notes\nSHOP is here\n',
    '.pymagic/run-configurations.json': JSON.stringify({ configurations: [{ name: 'hello', kind: 'script', script: 'hello.py', args: 'one two', cwd: '', env: {} }], selected: null }),
});
const app = await launch(root, null, { mcp: { enabled: true, port, brave: true } });
const { page } = app;
const url = `http://127.0.0.1:${port}/mcp`;
let id = 0;
async function rpc(method, params) {
    const res = await fetch(url, { method: 'POST', headers: { 'content-type': 'application/json', accept: 'application/json, text/event-stream' }, body: JSON.stringify({ jsonrpc: '2.0', id: ++id, method, params }) });
    assert.equal(res.status, 200, method);
    return res.json();
}
async function call(name, args = {}) {
    const r = await rpc('tools/call', { name, arguments: args });
    assert.ok(r.result, JSON.stringify(r));
    return { text: r.result.content[0].text, isError: r.result.isError };
}
const disk = (rel) => readFileSync(join(root, rel), 'utf8');

try {
    let init = null;
    for (let i = 0; i < 40 && !init; i++) { init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'check', version: '1' } }).catch(() => null); if (!init) await sleep(500); }
    assert.ok(init, 'the MCP server answers');
    assert.equal(init.result.serverInfo.name, 'PyMagic');
    // the address opened in a browser says what it is
    const visit = await fetch(url, { headers: { accept: 'text/html' } });
    assert.equal(visit.status, 200);
    assert.match(await visit.text(), /PyMagic's MCP server is running/);
    const tools = (await rpc('tools/list', {})).result.tools.map((t) => t.name);
    console.log('tools:', tools.length);
    assert.ok(tools.includes('rename_refactoring') && tools.includes('execute_terminal_command'));

    const text = await call('get_file_text_by_path', { pathInProject: 'main.py' });
    assert.equal(text.text, disk('main.py'));
    assert.match((await call('get_file_text_by_path', { pathInProject: 'shop.py', maxLinesCount: 2 })).text, /<<6 lines left out>>$/);
    const outside = await call('get_file_text_by_path', { pathInProject: '../escape.txt' });
    assert.equal(outside.isError, true);
    assert.match(outside.text, /not a path inside the project/);
    assert.equal((await call('get_file_text_by_path', { pathInProject: 'nope.py' })).isError, true);

    assert.match((await call('create_new_file', { pathInProject: 'notes/todo.txt', text: 'buy milk\n' })).text, /Made notes\/todo.txt/);
    assert.equal(disk('notes/todo.txt'), 'buy milk\n');
    assert.equal((await call('create_new_file', { pathInProject: 'notes/todo.txt', text: 'x' })).isError, true);
    assert.match((await call('replace_text_in_file', { pathInProject: 'notes/todo.txt', oldText: 'milk', newText: 'bread' })).text, /Replaced 1 occurrence/);
    assert.equal(disk('notes/todo.txt'), 'buy bread\n');

    const glob = (await call('find_files_by_glob', { globPattern: '**/*.py' })).text.split('\n').sort();
    assert.deepEqual(glob, ['hello.py', 'main.py', 'messy.py', 'shop.py']);
    assert.equal((await call('find_files_by_name_keyword', { nameKeyword: 'NOTES' })).text, 'pkg/deep/notes.md');
    const found = (await call('search_in_files_by_text', { searchText: 'Shop', fileMask: '*.py' })).text;
    console.log(found);
    assert.match(found, /^main\.py:1: from shop import \|\|Shop\|\|$/m);
    assert.match(found, /^shop\.py:1: class \|\|Shop\|\|:$/m);
    assert.ok(!found.includes('notes.md'));
    const insensitive = (await call('search_in_files_by_text', { searchText: 'shop', caseSensitive: false, directoryToSearch: 'pkg' })).text;
    assert.equal(insensitive, 'pkg/deep/notes.md:2: ||SHOP|| is here');
    assert.match((await call('search_in_files_by_regex', { regexPattern: 'def \\w+\\(' })).text, /shop\.py:7: \|\|def broken\(\|\|\)/);

    const tree = (await call('list_directory_tree', { maxDepth: 1 })).text;
    console.log(tree);
    assert.match(tree, /├── pkg\/\n│   └── …/);
    assert.match(tree, /shop\.py/);

    const problems = (await call('get_file_problems', { filePath: 'shop.py' })).text;
    console.log(problems);
    // (an unresolved name is a warning, as in PyCharm)
    assert.match(problems, /^shop\.py:8:12 warning: Unresolved reference 'undefined_name' \[unresolved-reference\]$/m);
    assert.match((await call('get_file_problems', { filePath: 'shop.py', errorsOnly: true })).text, /No errors in shop\.py/);
    assert.match((await call('get_project_problems', {})).text, /shop\.py:8:12 warning: Unresolved reference 'undefined_name'/);

    const info = (await call('get_symbol_info', { filePath: 'main.py', line: 3, column: 14 })).text;
    console.log(info);
    assert.match(info, /total/);
    assert.match(info, /Adds the prices/);
    assert.match(info, /Defined at: shop\.py:2:9/);

    assert.match((await call('rename_refactoring', { pathInProject: 'shop.py', symbolName: 'Shop.total', newName: 'grand_total' })).text, /Renamed Shop\.total to grand_total in: /);
    assert.match(disk('shop.py'), /def grand_total\(self/);
    assert.match(disk('main.py'), /Shop\(\)\.grand_total\(/);
    assert.equal((await call('rename_refactoring', { pathInProject: 'shop.py', symbolName: 'Nothing', newName: 'x' })).isError, true);

    assert.match((await call('reformat_file', { path: 'messy.py' })).text, /Formatted messy\.py/);
    assert.match(disk('messy.py'), /^x = \[1, 2, 3\]\n\n\ndef f\(a, b\):\n {4}return a \+ b\n$/);

    assert.match((await call('open_file_in_editor', { filePath: 'shop.py', line: 7 })).text, /Opened shop\.py at line 7/);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('shop.py') && window.pymagic.editor.getPosition().lineNumber === 7`, 10000, 'shop.py at line 7');
    const open = JSON.parse((await call('get_all_open_file_paths')).text);
    assert.equal(open.activeFile, 'shop.py');
    assert.ok(open.openFiles.includes('shop.py'));

    const configs = JSON.parse((await call('get_run_configurations')).text);
    assert.deepEqual(configs.configurations.map((c) => c.name), ['hello']);
    const ran = await call('execute_run_configuration', { configurationName: 'hello', timeout: 60000 });
    console.log(ran.text);
    assert.match(ran.text, /^Exit code: 3/);
    assert.match(ran.text, /hello from run \['one', 'two'\]/);
    assert.equal(ran.isError, true);   // a non-zero exit
    assert.equal((await call('execute_run_configuration', { configurationName: 'none' })).isError, true);

    const shell = await call('execute_terminal_command', { command: 'echo from-shell && python -c "import sys; print(sys.prefix)"' });
    console.log(shell.text);
    assert.match(shell.text, /^Exit code: 0\nfrom-shell/);
    const proj = JSON.parse((await call('get_project_info')).text);
    console.log('project:', JSON.stringify(proj));
    assert.equal(proj.projectPath.toLowerCase(), root.toLowerCase());
    if (proj.interpreter) {
        // the project's interpreter is the first python on PATH
        const where = (await call('execute_terminal_command', { command: 'where python' })).text.split(/\r?\n/)[1];
        assert.equal(where.trim().toLowerCase(), proj.interpreter.path.toLowerCase());
    }
    const slow = await call('execute_terminal_command', { command: 'ping -n 30 127.0.0.1 >nul', timeout: 1500 });
    assert.match(slow.text, /^Stopped after 1500 ms/);
    assert.equal((await call('get_file_text_by_path', { pathInProject: 'main.py', projectPath: 'C:\\not\\open' })).isError, true);

    // the stdio bridge, run by PyMagic.exe as Node.js (as Claude Desktop and Codex start it): its script is in the app's archive
    const script = join(dirname(EXE), 'resources', 'app.asar', 'mcp-stdio.js');
    const lines = await new Promise((resolve) => {
        const child = spawn(EXE, [script, String(port)], { env: { ...process.env, ELECTRON_RUN_AS_NODE: '1' }, stdio: ['pipe', 'pipe', 'pipe'] });
        let out = '';
        child.stdout.on('data', (c) => { out += c; });
        child.on('close', () => resolve(out.trim().split('\n').filter(Boolean).map((l) => JSON.parse(l))));
        child.stdin.end([{ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2024-11-05' } }, { jsonrpc: '2.0', method: 'notifications/initialized' },
            { jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'find_files_by_name_keyword', arguments: { nameKeyword: 'hello' } } }].map((m) => JSON.stringify(m)).join('\n') + '\n');
    });
    console.log('stdio:', JSON.stringify(lines).slice(0, 200));
    assert.equal(lines[0].result.protocolVersion, '2024-11-05');
    assert.equal(lines[1].result.content[0].text, 'hello.py');

    // Settings > MCP Server
    await page.evaluate(`window.pymagic.runCommand('pymagic.window.settings')`);
    await page.waitFor(`!!document.querySelector('.settings-nav')`, 10000, 'the settings');
    await page.evaluate(`[...document.querySelectorAll('.settings-nav button')].find((b) => b.textContent === 'MCP Server').click()`);
    const shown = await page.waitFor(`document.querySelector('.settings-page')?.textContent.includes('Listening: ${url}') && document.querySelector('.settings-page').textContent`, 10000, 'Listening');
    assert.match(shown, /Claude Desktop/);
    assert.match(shown, /Last calls/);
    assert.ok(existsSync(join(root, 'notes', 'todo.txt')));
    console.log('OK');
} finally {
    await app.close();
}

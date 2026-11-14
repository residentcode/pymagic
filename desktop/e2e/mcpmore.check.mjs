// More of the MCP server's tools in the built PyMagic.exe, used as an AI client uses them: the problems of files that are not Python and not
// open (a TypeScript file's type error through another file, an HTML file's element left open), a TypeScript name's signature and where it is
// defined, Rename of a TypeScript function across files, a Python file's quick fixes listed and one carried out, the project's Git state and
// a commit found by its message, and a breakpoint set, listed and taken away.
// Run: node desktop/e2e/mcpmore.check.mjs   (needs git on PATH)
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { createServer } from 'node:net';
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const port = await new Promise((ok) => { const s = createServer(); s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => ok(p)); }); });
const root = project({
    'main.py': 'import os\n\n\ndef area(w, h):\n    return w * h\n\n\nprint(area(2, 3), sqrt(4))\n',
    'src/util.ts': 'export function twice(n: number): number { return n * 2; }\n',
    'src/main.ts': "import { twice } from './util';\nconst a: string = twice(2);\nexport const b = twice(3);\n",
    'page.html': '<!DOCTYPE html>\n<html>\n<body>\n<section>\n<p>text</p>\n</body>\n</html>\n',
    'tsconfig.json': '{ "compilerOptions": { "strict": true, "module": "ESNext", "moduleResolution": "Bundler" } }\n',
    'package.json': '{"name":"probe"}',
});
const git = (...args) => execFileSync('git', args, { cwd: root, stdio: 'pipe' }).toString();
git('init', '-q');
git('-c', 'user.name=Check', '-c', 'user.email=check@example.invalid', 'add', '-A');
git('-c', 'user.name=Check', '-c', 'user.email=check@example.invalid', 'commit', '-q', '-m', 'first commit of the shop');

const app = await launch(root, null, { mcp: { enabled: true, port, brave: true } });
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
const disk = (rel) => readFileSync(join(root, rel), 'utf8').replace(/\r\n/g, '\n');

try {
    let init = null;
    for (let i = 0; i < 40 && !init; i++) { init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'check', version: '1' } }).catch(() => null); if (!init) await sleep(500); }
    assert.ok(init, 'the MCP server answers');
    const tools = (await rpc('tools/list', {})).result.tools.map((t) => t.name);
    console.log('tools:', tools.length);
    for (const t of ['get_quick_fixes', 'apply_quick_fix', 'get_project_vcs_status', 'find_commit_by_message', 'toggle_debugger_breakpoint', 'get_debugger_breakpoints']) assert.ok(tools.includes(t), t);

    // the problems of files that are not open: TypeScript's through the other file, HTML's own
    const ts = await call('get_file_problems', { filePath: 'src/main.ts' });
    console.log('src/main.ts:', ts.text);
    assert.match(ts.text, /2:\d+ .*Type 'number' is not assignable to type 'string'/);
    const html = await call('get_file_problems', { filePath: 'page.html' });
    console.log('page.html:', html.text);
    assert.match(html.text, /4:1 .*Element <section> is not closed/);
    assert.equal((await call('get_file_problems', { filePath: 'nope.ts' })).isError, true);

    // a TypeScript name: what it is and where it is defined
    const info = await call('get_symbol_info', { filePath: 'src/main.ts', line: 3, column: 19 });
    console.log('twice:', info.text.replace(/\n+/g, ' | '));
    assert.match(info.text, /twice\(n: number\): number/);
    assert.match(info.text, /Defined at: src\/util\.ts:1:17/);

    // Rename across files
    const renamed = await call('rename_refactoring', { pathInProject: 'src/util.ts', symbolName: 'twice', newName: 'double' });
    console.log(renamed.text);
    assert.match(renamed.text, /Renamed twice to double in: /);
    assert.equal(disk('src/util.ts'), 'export function double(n: number): number { return n * 2; }\n');
    assert.equal(disk('src/main.ts'), "import { double } from './util';\nconst a: string = double(2);\nexport const b = double(3);\n");
    assert.equal((await call('rename_refactoring', { pathInProject: 'src/util.ts', symbolName: 'nothing', newName: 'x' })).isError, true);

    // a Python file's quick fixes, and one carried out
    const fixes = await call('get_quick_fixes', { filePath: 'main.py' });
    console.log('quick fixes:', fixes.text.replace(/\n/g, ' | '));
    const first = /^main\.py:(\d+) (.+)$/m.exec(fixes.text);
    assert.ok(first, fixes.text);
    const before = disk('main.py');
    const applied = await call('apply_quick_fix', { filePath: 'main.py', line: +first[1], title: first[2] });
    console.log(applied.text);
    assert.match(applied.text, /^Applied ".+" in: main\.py/);
    assert.notEqual(disk('main.py'), before);
    assert.equal((await call('apply_quick_fix', { filePath: 'main.py', line: 5, title: 'no such fix' })).isError, true);

    // Git: what changed since the commit, and the commit by its message
    const status = await call('get_project_vcs_status');
    console.log(status.text.replace(/\n/g, ' | '));
    assert.match(status.text, /^Branch: (master|main)/);
    assert.match(status.text, /^main\.py — modified$/m);
    assert.match(status.text, /^src\/util\.ts — modified$/m);
    assert.ok(!/page\.html/.test(status.text));
    const commit = await call('find_commit_by_message', { text: 'shop' });
    console.log(commit.text);
    assert.match(commit.text, /^[0-9a-f]{7,10} .*first commit of the shop$/);
    assert.equal((await call('find_commit_by_message', { text: 'nothing of the kind' })).text, 'No commit has that in its message.');

    // breakpoints
    assert.equal((await call('toggle_debugger_breakpoint', { filePathInProject: 'main.py', line: 5 })).text, 'Set the breakpoint at main.py:5.');
    const list = JSON.parse((await call('get_debugger_breakpoints')).text);
    console.log('breakpoints:', JSON.stringify(list));
    assert.ok(list.some((b) => b.file === 'main.py' && b.line === 5));
    assert.equal((await call('toggle_debugger_breakpoint', { filePathInProject: 'main.py', line: 5 })).text, 'Removed the breakpoint at main.py:5.');
    assert.ok(!JSON.parse((await call('get_debugger_breakpoints')).text).some((b) => b.file === 'main.py' && b.line === 5));
    assert.equal((await call('toggle_debugger_breakpoint', { filePathInProject: 'main.py', line: 500 })).isError, true);
    console.log('OK');
} finally {
    await app.close();
}

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
    'src/spell.ts': `const user = { name: 'a' };
export const shown = user.nam;
`,
    'list.html': `<!DOCTYPE html>
<html>
<body>
<ul>
    <li>a</li>
  <li>b</li>
</ul>
</body>
</html>
`,
    'calc.py': `def area(w, h):
    size = w * h
    return size


sizes = [2, 3]; total = area(sizes[0], sizes[1])
print("total", total)
`,
    'wait.py': `import time
for i in range(600):
    time.sleep(0.1)
`,
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
    for (const t of ['get_git_branches', 'create_git_branch', 'checkout_git_branch', 'commit_changes', 'start_debugger_session', 'get_debugger_state', 'debugger_step', 'get_debugger_variables', 'evaluate_debugger_expression', 'stop_debugger_session', 'get_quick_fixes', 'apply_quick_fix', 'get_project_vcs_status', 'find_commit_by_message', 'toggle_debugger_breakpoint', 'get_debugger_breakpoints']) assert.ok(tools.includes(t), t);

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

    // a quick fix of another language: TypeScript's own for a misspelt name, listed and carried out
    const tsFixes = await call('get_quick_fixes', { filePath: 'src/spell.ts' });
    console.log('TypeScript quick fixes:', tsFixes.text.replace(/\n/g, ' | '));
    assert.match(tsFixes.text, /^src\/spell\.ts:2 Change spelling to 'name'$/m);
    const spelt = await call('apply_quick_fix', { filePath: 'src/spell.ts', line: 2, title: 'Change spelling' });
    console.log(spelt.text);
    assert.match(spelt.text, /^Applied "Change spelling to 'name'" in: src\/spell\.ts/);
    assert.equal(disk('src/spell.ts'), "const user = { name: 'a' };\nexport const shown = user.name;\n");
    // and PyMagic's own for markup: the indentation of an HTML file
    const htmlFixes = await call('get_quick_fixes', { filePath: 'list.html' });
    console.log('HTML quick fixes:', htmlFixes.text.replace(/\n/g, ' | '));
    assert.match(htmlFixes.text, /^list\.html:\d+ Fix /m);

    // Git: branches, a branch made, chosen files committed, back to the first branch
    const branch0 = /^Current: (\S+)/.exec((await call('get_git_branches')).text)?.[1];
    assert.ok(branch0 === 'master' || branch0 === 'main', branch0);
    assert.equal((await call('create_git_branch', { name: 'feature/spelling' })).text, 'Made the branch feature/spelling and switched to it.');
    assert.equal((await call('create_git_branch', { name: 'feature/spelling' })).isError, true);
    const branches = (await call('get_git_branches')).text;
    console.log(branches.replace(/\n/g, ' | '));
    assert.match(branches, /^Current: feature\/spelling$/m);
    assert.match(branches, /^\* feature\/spelling$/m);
    assert.equal((await call('commit_changes', { message: 'nothing', paths: 'page.html' })).isError, true);
    assert.equal((await call('commit_changes', { message: '  ' })).isError, true);
    const committed = await call('commit_changes', { message: 'The spelling of name', paths: 'src/spell.ts, src/util.ts' });
    console.log(committed.text);
    assert.match(committed.text, /^Committed 2 files as [0-9a-f]{7,10}: src\/spell\.ts, src\/util\.ts$/);
    assert.match(git('log', '--oneline', '-1'), /The spelling of name/);
    assert.match(git('show', '--stat', '--format=', 'HEAD'), /src\/spell\.ts[\s\S]*src\/util\.ts/);
    const after = (await call('get_project_vcs_status')).text;
    assert.ok(!/spell\.ts|util\.ts/.test(after) && /^main\.py — modified$/m.test(after), after);
    // (main.py's and src/main.ts's changes go along to the other branch: Git keeps them when nothing is in the way)
    assert.equal((await call('checkout_git_branch', { name: branch0 })).text, `Switched to ${branch0}.`);
    assert.equal(git('rev-parse', '--abbrev-ref', 'HEAD').trim(), branch0);
    assert.equal((await call('checkout_git_branch', { name: 'no-such-branch' })).isError, true);
    assert.ok(!(await call('create_git_branch', { name: 'later', checkout: false })).isError);
    assert.equal(git('rev-parse', '--abbrev-ref', 'HEAD').trim(), branch0);
    assert.match(git('branch', '--list', 'later'), /later/);

    // the debugger: a breakpoint, a session started, its values read, stepped, and run to its end
    assert.match((await call('get_debugger_state')).text, /^No program is being debugged\./);
    assert.equal((await call('debugger_step', { action: 'STEP_OVER' })).isError, true);
    await call('toggle_debugger_breakpoint', { filePathInProject: 'calc.py', line: 2 });
    const started = await call('start_debugger_session', { filePath: 'calc.py' });
    console.log(started.text.replace(/\n/g, ' | '));
    assert.match(started.text, /^Paused at a breakpoint at calc\.py:2 in area/);
    assert.match(started.text, /^#0 area calc\.py:2$/m);
    assert.match(started.text, /^#1 <module> calc\.py:6$/m);
    assert.equal((await call('start_debugger_session', { filePath: 'calc.py' })).isError, true);
    const vars = await call('get_debugger_variables');
    console.log(vars.text.replace(/\n/g, ' | '));
    assert.match(vars.text, /^ {2}w: int = 2$/m);
    assert.match(vars.text, /^ {2}h: int = 3$/m);
    assert.match((await call('evaluate_debugger_expression', { expression: 'w * h + 1' })).text, /^int: 7$/);
    assert.equal((await call('evaluate_debugger_expression', { expression: 'nothing_here' })).isError, true);
    const outer = await call('get_debugger_variables', { frame: 1, expression: 'sizes' });
    console.log(outer.text.replace(/\n/g, ' | '));
    assert.match(outer.text, /^sizes: list = \[2, 3\]/);
    assert.match(outer.text, /^ {2}\S*1\S*: int = 3$/m);
    const over = await call('debugger_step', { action: 'STEP_OVER' });
    assert.match(over.text, /^Paused after the step at calc\.py:3 in area/);
    assert.match((await call('get_debugger_variables')).text, /^ {2}size: int = 6$/m);
    const out = await call('debugger_step', { action: 'STEP_OUT' });
    console.log(out.text.split('\n')[0]);
    assert.match(out.text, /^Paused after the step at calc\.py:6 in <module>/);
    const ended = await call('debugger_step', { action: 'CONTINUE' });
    console.log(ended.text.replace(/\n/g, ' | ').slice(0, 200));
    assert.match(ended.text, /^The program ended with exit code 0\./);
    assert.match(ended.text, /total 6/);
    assert.equal((await call('stop_debugger_session')).text, 'No program is running.');
    // one that waits is paused, and stopped
    await call('toggle_debugger_breakpoint', { filePathInProject: 'calc.py', line: 2 });
    const waiting = await call('start_debugger_session', { filePath: 'wait.py', timeout: 4000 });
    console.log(waiting.text.split('\n')[0]);
    assert.match(waiting.text, /^The program is running \(not paused\)\./);
    assert.match((await call('debugger_step', { action: 'PAUSE', timeout: 10000 })).text, /^Paused paused at wait\.py:\d+ in <module>|^Paused/);
    assert.equal((await call('stop_debugger_session')).text, 'Stopped.');
    console.log('OK');
} finally {
    await app.close();
}

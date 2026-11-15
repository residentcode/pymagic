// A debug session of JavaScript through the MCP server in the built PyMagic.exe, as an AI client drives one: a breakpoint set in a .js
// file, the file started under the debugger and paused there, its values read and an expression evaluated, a step, then run to its end.
// Run: node desktop/e2e/mcpjsdebug.check.mjs   (needs node on PATH)
import assert from 'node:assert/strict';
import { createServer } from 'node:net';
import { launch, project, sleep } from './app.mjs';

const port = await new Promise((ok) => { const s = createServer(); s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => ok(p)); }); });
const root = project({
    'package.json': '{"name":"probe"}',
    'app.js': [
        'function area(w, h) {',
        '    const size = w * h;',
        '    return size;',
        '}',
        'const sizes = [2, 3];',
        'console.log("total", area(sizes[0], sizes[1]));',
        '',
    ].join('\n'),
});
const app = await launch(root, null, { mcp: { enabled: true, port, brave: true } });
const url = `http://127.0.0.1:${port}/mcp`;
let n = 0;
async function rpc(method, params) {
    const res = await fetch(url, { method: 'POST', headers: { 'content-type': 'application/json', accept: 'application/json, text/event-stream' }, body: JSON.stringify({ jsonrpc: '2.0', id: ++n, method, params }) });
    assert.equal(res.status, 200, method);
    return res.json();
}
async function call(name, args = {}) {
    const r = await rpc('tools/call', { name, arguments: args });
    assert.ok(r.result, JSON.stringify(r));
    return { text: r.result.content[0].text, isError: r.result.isError };
}

try {
    let init = null;
    for (let i = 0; i < 40 && !init; i++) { init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'check', version: '1' } }).catch(() => null); if (!init) await sleep(500); }
    assert.ok(init, 'the MCP server answers');
    assert.equal((await call('toggle_debugger_breakpoint', { filePathInProject: 'app.js', line: 2 })).text, 'Set the breakpoint at app.js:2.');
    const started = await call('start_debugger_session', { filePath: 'app.js', timeout: 60000 });
    console.log('started:', started.text.replace(/\n/g, ' | '));
    assert.ok(!started.isError, started.text);
    assert.match(started.text, /^Paused at a breakpoint at app\.js:2 in area/);
    const vars = await call('get_debugger_variables');
    console.log('variables:', vars.text.replace(/\n/g, ' | '));
    assert.match(vars.text, /\bw: \w* ?= 2\b/);
    assert.match(vars.text, /\bh: \w* ?= 3\b/);
    const value = await call('evaluate_debugger_expression', { expression: 'w * h + 1' });
    console.log('w * h + 1:', value.text);
    assert.match(value.text, /\b7\b/);
    const stepped = await call('debugger_step', { action: 'STEP_OVER' });
    console.log('stepped:', stepped.text.split('\n')[0]);
    assert.match(stepped.text, /^Paused after the step at app\.js:3 in area/);
    const ended = await call('debugger_step', { action: 'CONTINUE' });
    console.log('ended:', ended.text.replace(/\n/g, ' | '));
    assert.match(ended.text, /exit code 0/);
    assert.match(ended.text, /total 6/);
    console.log('OK');
} finally {
    await app.close();
}

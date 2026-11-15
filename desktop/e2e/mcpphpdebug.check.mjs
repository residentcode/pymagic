// A debug session of PHP through the MCP server in the built PyMagic.exe, as an AI client drives one: PHP is the test machine's (Settings >
// PHP over SSH, Xdebug there); a breakpoint set in a .php file, the file started under the debugger and paused there (the place named as the
// project's file here), its values read and an expression evaluated, then run to its end with its output.
// Run: PYMAGIC_CT_HOST=… PYMAGIC_CT_KEY=… PYMAGIC_CT_KNOWN_HOSTS=… node desktop/e2e/mcpphpdebug.check.mjs
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { launch, sleep } from './app.mjs';

const { PYMAGIC_CT_HOST: host, PYMAGIC_CT_KEY: key, PYMAGIC_CT_KNOWN_HOSTS: known } = process.env;
assert.ok(host && key && known, 'PYMAGIC_CT_HOST, PYMAGIC_CT_KEY and PYMAGIC_CT_KNOWN_HOSTS');
const spec = `ssh:root@${host}?i=${key}&kh=${known}|php`;
const root = join(mkdtempSync(join(tmpdir(), 'pymagic-e2e-mcpphp-')), 'shop');
mkdirSync(root, { recursive: true });
writeFileSync(join(root, 'main.php'), [
    '<?php',                                    // 1
    'function area($w, $h) {',                  // 2
    '    $size = $w * $h;',                     // 3
    '    return $size;',                        // 4
    '}',                                        // 5
    "echo 'total ' . area(2, 3) . PHP_EOL;",    // 6
    '',
].join('\n'));
const port = await new Promise((ok) => { const s = createServer(); s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => ok(p)); }); });
const app = await launch(root, { phpPath: spec }, { mcp: { enabled: true, port, brave: true } });
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
    assert.equal((await call('toggle_debugger_breakpoint', { filePathInProject: 'main.php', line: 3 })).text, 'Set the breakpoint at main.php:3.');
    const started = await call('start_debugger_session', { filePath: 'main.php', timeout: 90000 });
    console.log('started:', started.text.replace(/\n/g, ' | '));
    assert.ok(!started.isError, started.text);
    assert.match(started.text, /^Paused at a breakpoint at main\.php:3 in area/);
    const vars = await call('get_debugger_variables');
    console.log('variables:', vars.text.replace(/\n/g, ' | '));
    assert.match(vars.text, /\$?w: \w* ?= 2\b/);
    const value = await call('evaluate_debugger_expression', { expression: '$w * $h + 1' });
    console.log('$w * $h + 1:', value.text);
    assert.match(value.text, /\b7\b/);
    const ended = await call('debugger_step', { action: 'CONTINUE', timeout: 60000 });
    console.log('ended:', ended.text.replace(/\n/g, ' | '));
    assert.match(ended.text, /exit code 0/);
    assert.match(ended.text, /total 6/);
    console.log('OK');
} finally {
    await app.close();
}

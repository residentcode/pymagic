// The question before an AI agent's call that runs the person's code or changes the repository (desktop/mcp-main.js): asked for a command,
// a run configuration, a debug session, a commit and a branch switch; refused, nothing is done; "Don't ask again" and Settings' brave mode
// stop the asking. `node --test desktop/test/mcp-ask.test.mjs`
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { setupMcp } = require('../mcp-main.js');

function world(mcp = {}) {
    const settings = { mcp: { enabled: false, ...mcp } };
    const asked = [], sent = [];
    let answer = { response: 1, checkboxChecked: false };
    const win = { isDestroyed: () => false, isFocused: () => true, webContents: { executeJavaScript: async (code) => { sent.push(code); return { text: 'done' }; } } };
    const app = setupMcp({
        windows: new Set([{ win, port: 1, project: 'C:\\work\\shop', used: 1 }]), version: '0',
        readSettings: () => settings, writeSettings: (s) => { Object.assign(settings, s); },
        dialog: { showMessageBox: async (_w, options) => { asked.push(options); return answer; } },
    });
    return { app, asked, sent, settings, answer: (a) => { answer = a; } };
}

const ASKING = [
    ['execute_run_configuration', { configurationName: 'serve' }, 'Run', /serve/],
    ['start_debugger_session', { filePath: 'main.py' }, 'Debug', /main\.py/],
    ['commit_changes', { message: 'Fix the total', paths: 'a.py' }, 'Commit', /Fix the total[\s\S]*a\.py/],
    ['checkout_git_branch', { name: 'release' }, 'Switch', /release/],
];

test('asked first, and refused: nothing reaches the project', async () => {
    for (const [name, args, verb, detail] of ASKING) {
        const w = world();
        const r = await w.app.callTool(name, args);
        assert.equal(w.asked.length, 1, name);
        assert.deepEqual(w.asked[0].buttons, [verb, `Don't ${verb.toLowerCase()}`]);
        assert.equal(w.asked[0].defaultId, 1);
        assert.match(w.asked[0].detail, detail);
        assert.equal(r.isError, true);
        assert.match(r.text, /^The person chose not to /);
        assert.equal(w.sent.length, 0, name);
    }
    // a shell command too (it is run by the app itself: refused, it is not started)
    const w = world();
    const r = await w.app.callTool('execute_terminal_command', { command: 'echo never' });
    assert.equal(w.asked.length, 1);
    assert.match(w.asked[0].detail, /echo never/);
    assert.deepEqual([r.isError, r.text], [true, 'The person chose not to run the command.']);
});

test('allowed: the call goes to the project; "Don\'t ask again" is kept', async () => {
    const w = world();
    w.answer({ response: 0, checkboxChecked: false });
    assert.equal((await w.app.callTool('commit_changes', { message: 'm' })).text, 'done');
    assert.equal(w.sent.length, 1);
    assert.match(w.sent[0], /"commit_changes"/);
    assert.match(w.asked[0].detail, /Every changed file/);
    assert.equal(w.settings.mcp.brave, undefined);
    w.answer({ response: 0, checkboxChecked: true });
    await w.app.callTool('checkout_git_branch', { name: 'main' });
    assert.equal(w.settings.mcp.brave, true);
    // not asked any more
    await w.app.callTool('start_debugger_session', { filePath: 'main.py' });
    assert.equal(w.asked.length, 2);
    assert.equal(w.sent.length, 3);
});

test('brave mode asks nothing; what only reads or edits files is never asked', async () => {
    const brave = world({ brave: true });
    for (const [name, args] of ASKING) await brave.app.callTool(name, args);
    assert.equal(brave.asked.length, 0);
    assert.equal(brave.sent.length, ASKING.length);
    const w = world();
    for (const [name, args] of [['get_file_text_by_path', { pathInProject: 'a.py' }], ['get_debugger_state', {}], ['debugger_step', { action: 'CONTINUE' }], ['create_git_branch', { name: 'x' }], ['get_quick_fixes', { filePath: 'a.ts' }]])
        await w.app.callTool(name, args);
    assert.equal(w.asked.length, 0);
    assert.equal(w.sent.length, 5);
});

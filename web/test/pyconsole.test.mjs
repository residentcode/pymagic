// Checks of web/src/pyconsole-model.js: `node web/test/pyconsole.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/pyconsole-model.js', import.meta.url), 'utf8');
const { consoleName, queueLabel, CommandQueue, historyMatches } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

test('a new console takes the lowest free number', () => {
    assert.equal(consoleName([]), 'Python console');
    assert.equal(consoleName(['Python console']), 'Python console (2)');
    assert.equal(consoleName(['Python console', 'Python console (3)']), 'Python console (2)');
    assert.equal(consoleName(['Django console'], 'Django console'), 'Django console (2)');
});

test('the queue shows the first line of a command, shortened', () => {
    assert.equal(queueLabel('x = 1'), 'x = 1');
    assert.equal(queueLabel('\ndef f():\n    return 1\n'), 'def f(): …');
    assert.equal(queueLabel('a'.repeat(60), 10), 'aaaaaaaaa…');
});

test('commands run one at a time, in the order they were given', async () => {
    const log = [];
    const gates = [];
    const queue = new CommandQueue((item) => new Promise((done) => { log.push('start ' + item.code); gates.push(() => { log.push('end ' + item.code); done({ more: false, code: item.code }); }); }));
    const first = queue.add({ code: 'a' });
    const second = queue.add({ code: 'b' });
    const third = queue.add({ code: 'c' });
    await Promise.resolve();
    assert.deepEqual(log, ['start a']);
    assert.equal(queue.busy, true);
    assert.deepEqual(queue.waiting.map((w) => w.code), ['b', 'c']);
    gates[0]();
    assert.equal((await first).code, 'a');
    await new Promise((r) => setTimeout(r, 0));
    assert.deepEqual(log, ['start a', 'end a', 'start b']);
    gates[1]();
    await second;
    await new Promise((r) => setTimeout(r, 0));
    gates[2]();
    await third;
    assert.deepEqual(log, ['start a', 'end a', 'start b', 'end b', 'start c', 'end c']);
    await new Promise((r) => setTimeout(r, 0));
    assert.equal(queue.busy, false);
});

test('a waiting command can be taken out, and all of them dropped; the one running goes on', async () => {
    let release;
    const queue = new CommandQueue(() => new Promise((done) => { release = () => done('ok'); }));
    const running = queue.add({ code: 'slow' });
    const b = queue.add({ code: 'b' });
    const c = queue.add({ code: 'c' });
    const d = queue.add({ code: 'd' });
    await Promise.resolve();
    assert.equal(queue.remove(0), true);
    assert.equal(await b, null);
    assert.equal(queue.remove(5), false);
    queue.clear();
    assert.equal(await c, null);
    assert.equal(await d, null);
    assert.equal(queue.waiting.length, 0);
    release();
    assert.equal(await running, 'ok');
});

test('a command that fails does not stop the ones behind it', async () => {
    const queue = new CommandQueue((item) => (item.code === 'bad' ? Promise.reject(new Error('gone')) : Promise.resolve(item.code)));
    const results = await Promise.all([queue.add({ code: 'bad' }), queue.add({ code: 'good' })]);
    assert.deepEqual(results, [null, 'good']);
});

test('the queue tells when its line-up changes', async () => {
    const seen = [];
    const queue = new CommandQueue(() => Promise.resolve(1), () => seen.push(queue.waiting.length + (queue.busy ? 1 : 0)));
    await queue.add({ code: 'x' });
    assert.ok(seen.length >= 2 && seen.at(-1) === 0, seen.join(','));
});

test('the history list (Ctrl+Alt+E): the newest first, each once, filtered in any case', () => {
    const history = ['x = 1', 'print(x)', 'def f():\n    return X', 'x = 1', '  '];
    assert.deepEqual(historyMatches(history), ['x = 1', 'def f():\n    return X', 'print(x)']);
    assert.deepEqual(historyMatches(history, ' X '), ['x = 1', 'def f():\n    return X', 'print(x)']);
    assert.deepEqual(historyMatches(history, 'print'), ['print(x)']);
    assert.deepEqual(historyMatches([], 'a'), []);
});

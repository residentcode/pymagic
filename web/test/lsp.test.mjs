import test from 'node:test';
import assert from 'node:assert/strict';
import { LspClient } from '../src/lsp.js';

/** A client whose socket only records what it would send. */
function client() {
    const c = new LspClient();
    const sent = [];
    c.ws = { readyState: WebSocket.OPEN, send: (text) => sent.push(JSON.parse(text)) };
    return { c, sent };
}

/** A cancellation token as Monaco hands one to a provider. */
function token() {
    const listeners = [];
    return {
        isCancellationRequested: false,
        onCancellationRequested(fn) { listeners.push(fn); },
        cancel() { this.isCancellationRequested = true; listeners.forEach((fn) => fn()); },
    };
}

test('a request whose token is cancelled tells the engine and rejects', async () => {
    const { c, sent } = client();
    const t = token();
    const answer = c.request('textDocument/hover', {}, t);
    t.cancel();
    await assert.rejects(answer, (e) => e.name === 'Canceled');
    assert.deepEqual(sent.map((m) => m.method), ['textDocument/hover', '$/cancelRequest']);
    assert.equal(sent[1].params.id, sent[0].id);
    // the answer that may still come is dropped
    c.dispatch({ jsonrpc: '2.0', id: sent[0].id, result: { contents: 'late' } });
});

test('an answered request is not cancelled afterwards, and a token cancelled already sends nothing', async () => {
    const { c, sent } = client();
    const t = token();
    const answer = c.request('textDocument/hover', {}, t);
    c.dispatch({ jsonrpc: '2.0', id: sent[0].id, result: 42 });
    assert.equal(await answer, 42);
    t.cancel();
    assert.equal(sent.length, 1);
    const done = token();
    done.cancel();
    await assert.rejects(c.request('textDocument/completion', {}, done));
    assert.equal(sent.length, 1);
});

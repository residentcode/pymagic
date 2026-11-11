// The HTTP client's requests that stay open, and gRPC without reflection, in the built PyMagic.exe, each sent from the editor's ▶ Run as a
// person sends it: a WEBSOCKET request kept open (its messages, then one typed in the HTTP tab, then Close), a GRAPHQL subscription over
// graphql-transport-ws (its events as they come, then Stop), and a GRPC call to a server without reflection, described by the project's own
// .proto file, with well-known types (Timestamp, Duration, Struct, ...) and its answer compressed with gzip; then gRPC calls kept open: a
// bidirectional one (each answer before the next message is typed) and a client-streaming one (its array, a typed message, a wrong field
// not sent, Close and the one answer).
// Run: PYMAGIC_TEST_GRPC_PYTHON=<python with grpcio, grpcio-tools, websockets> node desktop/e2e/httplive.check.mjs
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { launch, project, removeTree, sleep } from './app.mjs';

const python = process.env.PYMAGIC_TEST_GRPC_PYTHON;
assert.ok(python, 'PYMAGIC_TEST_GRPC_PYTHON names a Python with grpcio, grpcio-tools and websockets');

const PROTO = `syntax = "proto3";
package wk;
import "google/protobuf/timestamp.proto";
import "google/protobuf/duration.proto";
import "google/protobuf/struct.proto";
service Clock {
  rpc Echo (Stamp) returns (Stamp);
  rpc Talk (stream Stamp) returns (stream Stamp);
  rpc Total (stream Stamp) returns (Stamp);
}
message Stamp {
  google.protobuf.Timestamp at = 1;
  google.protobuf.Duration took = 2;
  google.protobuf.Struct extra = 3;
  string label = 4;
}
`;
const SERVERS = String.raw`
import asyncio, json, os, sys
from concurrent import futures
here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, here)
from grpc_tools import protoc
import grpc_tools
include = os.path.join(os.path.dirname(grpc_tools.__file__), "_proto")
assert protoc.main(["protoc", "-I" + here, "-I" + include, "--python_out=" + here, "--grpc_python_out=" + here, os.path.join(here, "wk.proto")]) == 0
import grpc
import wk_pb2, wk_pb2_grpc
from websockets.asyncio.server import serve

class Clock(wk_pb2_grpc.ClockServicer):
    def Echo(self, request, context):
        reply = wk_pb2.Stamp()
        reply.CopyFrom(request)
        reply.took.FromTimedelta(request.took.ToTimedelta() * 2)
        reply.label = request.label.upper()
        return reply
    def Talk(self, requests, context):
        for r in requests:
            yield wk_pb2.Stamp(label="heard " + r.label)
    def Total(self, requests, context):
        return wk_pb2.Stamp(label="+".join(r.label for r in requests))

async def echo(ws):
    await ws.send("welcome")
    async for m in ws:
        await ws.send("echo: " + m)

async def subscriptions(ws):
    # graphql-transport-ws: connection_init, its ack, a subscribe, its events (next) until the client completes it
    ticking = None
    async def tick(id):
        for i in range(1, 1000):
            await ws.send(json.dumps({"id": id, "type": "next", "payload": {"data": {"tick": i}}}))
            await asyncio.sleep(0.4)
    async for raw in ws:
        m = json.loads(raw)
        if m["type"] == "connection_init":
            await ws.send(json.dumps({"type": "connection_ack"}))
        elif m["type"] == "subscribe":
            assert m["payload"]["query"].startswith("subscription"), m
            ticking = asyncio.ensure_future(tick(m["id"]))
        elif m["type"] == "complete" and ticking:
            ticking.cancel()
            await ws.close()

async def main():
    g = grpc.server(futures.ThreadPoolExecutor(max_workers=4), compression=grpc.Compression.Gzip)
    wk_pb2_grpc.add_ClockServicer_to_server(Clock(), g)
    gport = g.add_insecure_port("127.0.0.1:0")
    g.start()
    async with serve(echo, "127.0.0.1", 0) as a, serve(subscriptions, "127.0.0.1", 0, subprotocols=["graphql-transport-ws"]) as b:
        print("ready", a.sockets[0].getsockname()[1], b.sockets[0].getsockname()[1], gport, flush=True)
        await asyncio.Future()

asyncio.run(main())
`;
const servers = mkdtempSync(join(tmpdir(), 'pymagic-e2e-httplive-'));
writeFileSync(join(servers, 'wk.proto'), PROTO);
writeFileSync(join(servers, 'servers.py'), SERVERS);
const child = spawn(python, ['-u', join(servers, 'servers.py')], { stdio: ['ignore', 'pipe', 'pipe'] });
let errors = '';
child.stderr.on('data', (d) => { errors += d; });
const ready = await new Promise((ok, fail) => {
    const timer = setTimeout(() => fail(new Error('the servers did not start: ' + errors)), 90000);
    child.stdout.on('data', (d) => { const m = /ready (\d+) (\d+) (\d+)/.exec(String(d)); if (m) { clearTimeout(timer); ok(m.slice(1).map(Number)); } });
});
const [wsPort, gqlPort, grpcPort] = ready;
console.log('servers: WebSocket', wsPort, 'GraphQL subscriptions', gqlPort, 'gRPC', grpcPort);

const root = project({
    'api.http': [
        '### socket', 'WEBSOCKET ws://127.0.0.1:' + wsPort + '/', '', '=== wait-for-server', 'one', '',
        '### ticks', 'GRAPHQL ws://127.0.0.1:' + gqlPort + '/graphql', '', 'subscription {', '  tick', '}', '',
        '### clock', 'GRPC 127.0.0.1:' + grpcPort + '/wk.Clock/Echo', '', '{"at": "2024-05-01T10:00:00.250Z", "took": "1.5s", "extra": {"a": [1, true]}, "label": "tick"}', '',
        '### talk', 'GRPC 127.0.0.1:' + grpcPort + '/wk.Clock/Talk', '', '{"label": "first"}', '',
        '### total', 'GRPC 127.0.0.1:' + grpcPort + '/wk.Clock/Total', '', '[{"label": "a"}, {"label": "b"}]', '',
    ].join('\n'),
    'protos/wk.proto': PROTO,
});
const app = await launch(root);
const { page } = app;
const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;
const body = () => page.evaluate(`${$('http-body')}.textContent`);
try {
    await page.evaluate(`window.pymagic.openFile('api.http')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('api.http')`, 20000, 'api.http open');
    // ▶ Run above the request: the code lens of its line
    const runAt = async (n) => {
        await page.evaluate(`${$('http-status')}.textContent = ''`);
        await page.waitFor(`[...document.querySelectorAll('.codelens-decoration a')].filter((a) => a.textContent.includes('Run')).length >= 3`, 20000, 'the Run lenses');
        // the request's line in view; its lens is the Run link just above that line (Monaco runs a lens on the mouse's own events: a click where it is)
        const line = await page.evaluate(`window.pymagic.editor.getModel().getLinesContent().map((l, i) => [l, i + 1]).filter(([l]) => /^(WEBSOCKET|GRAPHQL|GRPC) /.test(l))[${n}][1]`);
        await page.evaluate(`window.pymagic.editor.revealLineInCenter(${line})`);
        await sleep(500);
        const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; const box = ed.getContainerDomNode().getBoundingClientRect(); const lineTop = box.top + ed.getTopForLineNumber(${line}) - ed.getScrollTop();
            const a = [...document.querySelectorAll('.codelens-decoration a')].filter((a) => a.textContent.includes('Run') && a.getBoundingClientRect().height > 0 && a.getBoundingClientRect().bottom <= lineTop + 2)
                .sort((x, y) => y.getBoundingClientRect().top - x.getBoundingClientRect().top)[0];
            const r = a.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
        await page.click(at.x, at.y);
    };

    // WEBSOCKET kept open: its message, then one typed in the HTTP tab, then Close
    await runAt(0);
    await page.waitFor(`!${$('http-live')}.hidden && /open/.test(${$('http-status')}.textContent)`, 30000, 'the WebSocket open');
    await page.waitFor(`${$('http-body')}.textContent.includes('echo: one')`, 20000, "the echo of the file's message");
    await page.evaluate(`(() => { const box = ${$('http-live-text')}; box.value = 'typed here'; box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
    await page.waitFor(`${$('http-body')}.textContent.includes('echo: typed here')`, 20000, 'the echo of the typed message');
    await page.evaluate(`${$('http-live-close')}.click()`);
    await page.waitFor(`${$('http-live')}.hidden && /^101/.test(${$('http-status')}.textContent)`, 20000, 'the WebSocket closed');
    const talk = (await body()).split('\n').map((l) => l.replace(/\s+\d+ ms\s+/, ' '));
    console.log('WEBSOCKET kept open:', talk);
    assert.deepEqual(talk, ['← welcome', '→ one', '← echo: one', '→ typed here', '← echo: typed here']);

    // GRAPHQL subscription: its events as they come, then Stop
    await runAt(1);
    await page.waitFor(`!${$('http-live')}.hidden && /graphql-transport-ws/.test(${$('http-status')}.textContent)`, 30000, 'the subscription open');
    await page.waitFor(`/"tick": 3/.test(${$('http-body')}.textContent)`, 20000, 'three events');
    assert.equal(await page.evaluate(`${$('http-live-text')}.hidden`), true, 'nothing to type in a subscription');
    await page.evaluate(`${$('http-live-close')}.click()`);
    await page.waitFor(`${$('http-live')}.hidden`, 20000, 'the subscription stopped');
    const events = await body();
    console.log('GRAPHQL subscription:', events.split('\n').slice(0, 9).join(' / '));
    assert.match(events, /"tick": 1[\s\S]*"tick": 2[\s\S]*"tick": 3/);

    // GRPC without reflection: the project's protos/wk.proto, the well-known types in their JSON forms, the answer gzipped
    await runAt(2);
    const status = await page.waitFor(`(() => { const t = ${$('http-status')}.textContent; return t && !t.endsWith('…') ? t : null; })()`, 40000, 'the gRPC answer');
    const answer = JSON.parse(await body());
    console.log('GRPC:', status, JSON.stringify(answer));
    assert.match(status, /^gRPC 0 OK/);
    assert.deepEqual(answer, { at: '2024-05-01T10:00:00.250Z', took: '3s', extra: { a: [1, true] }, label: 'TICK' });
    await page.evaluate(`document.querySelector('#http-parts button[data-part="headers"]').click()`);
    assert.match(await body(), /grpc-encoding: gzip/);
    await page.evaluate(`document.querySelector('#http-parts button[data-part="body"]').click()`);

    const type = (text) => page.evaluate(`(() => { const box = ${$('http-live-text')}; box.value = ${JSON.stringify(text)}; box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
    const lines = async () => (await body()).split('\n').map((l) => l.replace(/\s+\d+ ms\s+/, ' ').trim()).filter(Boolean);
    // GRPC bidirectional, kept open: the file's message answered, then a typed one answered, Close
    await runAt(3);
    await page.waitFor(`!${$('http-live')}.hidden && /open \\(bidirectional\\)/.test(${$('http-status')}.textContent)`, 40000, 'the bidirectional call open');
    await page.waitFor(`${$('http-body')}.textContent.includes('heard first')`, 20000, "the answer to the file's message");
    assert.match(await page.evaluate(`${$('http-live-text')}.placeholder`), /wk\.Stamp as JSON/);
    await type('{"label": "second"}');
    await page.waitFor(`${$('http-body')}.textContent.includes('heard second')`, 20000, 'the answer to the typed message');
    await page.evaluate(`${$('http-live-close')}.click()`);
    const talked = await page.waitFor(`(() => { const t = ${$('http-status')}.textContent; return ${$('http-live')}.hidden && /^gRPC 0 OK/.test(t) ? t : null; })()`, 20000, 'the call ended');
    const chat = await lines();
    console.log('GRPC bidirectional kept open:', talked, chat);
    assert.deepEqual(chat.map((l) => l.replace(/\s+/g, ' ')), ['→ {"label":"first"}', '← {', '"label": "heard first"', '}', '→ {"label":"second"}', '← {', '"label": "heard second"', '}', '× closed (0 OK)']);

    // GRPC client streaming: the file's array, a typed message, one with a wrong field (not sent), Close, the one answer
    await runAt(4);
    await page.waitFor(`!${$('http-live')}.hidden && /open \\(client streaming\\)/.test(${$('http-status')}.textContent)`, 40000, 'the client-streaming call open');
    await type('{"label": "c"}');
    await page.waitFor(`${$('http-body')}.textContent.includes('{"label":"c"}')`, 20000, 'the typed message sent');
    await type('{"labl": "d"}');
    await page.waitFor(`${$('http-body')}.textContent.includes('not sent')`, 20000, 'the wrong one not sent');
    await page.evaluate(`${$('http-live-close')}.click()`);
    await page.waitFor(`${$('http-live')}.hidden && /^gRPC 0 OK/.test(${$('http-status')}.textContent)`, 20000, 'the call ended');
    const total = await lines();
    console.log('GRPC client streaming kept open:', total);
    assert.equal(total.filter((l) => l.startsWith('→')).length, 3);
    assert.ok(total.some((l) => /^! not sent: .*labl/.test(l)), total.join(' | '));
    assert.ok(total.some((l) => l.includes('"label": "a+b+c"')), total.join(' | '));
    // the history keeps it, with gRPC's status
    assert.match(await page.evaluate(`document.querySelector('#http-history .http-item')?.textContent ?? ''`), /Total|total|0/);
    console.log('OK');
} catch (e) {
    console.log('HTTP tab:', await page.evaluate(`${$('http-status')}.textContent + ' | live bar hidden: ' + ${$('http-live')}.hidden + ' | ' + ${$('http-body')}.textContent.slice(0, 500)`).catch(() => '?'));
    throw e;
} finally {
    await app.close();
    child.kill();
    await sleep(300);
    removeTree(servers);
}

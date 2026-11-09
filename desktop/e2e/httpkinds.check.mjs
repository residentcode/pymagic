// The HTTP client's GRAPHQL, WEBSOCKET and GRPC requests in the built PyMagic.exe, each written in an .http file and run as a person runs it,
// against servers of the check's own: a GraphQL endpoint that answers what it was sent, a WebSocket echo with a greeting, and a gRPC server
// with reflection (Python's grpcio). Run: PYMAGIC_TEST_GRPC_PYTHON=<python with grpcio, grpcio-reflection, grpcio-tools, websockets> node desktop/e2e/httpkinds.check.mjs
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { createInterface } from 'node:readline';
import { launch, project, removeTree, sleep } from './app.mjs';

const python = process.env.PYMAGIC_TEST_GRPC_PYTHON;
assert.ok(python, 'PYMAGIC_TEST_GRPC_PYTHON names a Python with grpcio, grpcio-reflection, grpcio-tools and websockets');

const SERVERS = String.raw`
import asyncio, json, os, sys, threading
from concurrent import futures
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, here)
from grpc_tools import protoc
assert protoc.main(["protoc", "-I" + here, "--python_out=" + here, "--grpc_python_out=" + here, os.path.join(here, "demo.proto")]) == 0
import grpc
from grpc_reflection.v1alpha import reflection
import demo_pb2, demo_pb2_grpc
from websockets.asyncio.server import serve

class GraphQL(BaseHTTPRequestHandler):
    def do_POST(self):
        sent = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        body = json.dumps({"data": {"query": sent["query"], "variables": sent["variables"], "type": self.headers["Content-Type"]}}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def log_message(self, *a): pass

class Greeter(demo_pb2_grpc.GreeterServicer):
    def SayHello(self, request, context):
        return demo_pb2.HelloReply(message="hello " + request.name, total=2**40, tags=list(request.tags) + ["seen"])

async def echo(ws):
    await ws.send("welcome")
    async for m in ws:
        await ws.send("echo: " + m)

async def main():
    http = ThreadingHTTPServer(("127.0.0.1", 0), GraphQL)
    threading.Thread(target=http.serve_forever, daemon=True).start()
    g = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
    demo_pb2_grpc.add_GreeterServicer_to_server(Greeter(), g)
    reflection.enable_server_reflection(("demo.Greeter", reflection.SERVICE_NAME), g)
    gport = g.add_insecure_port("127.0.0.1:0")
    g.start()
    async with serve(echo, "127.0.0.1", 0) as ws:
        wport = ws.sockets[0].getsockname()[1]
        print("ready", http.server_address[1], wport, gport, flush=True)
        await asyncio.Future()

asyncio.run(main())
`;
const PROTO = `syntax = "proto3";
package demo;
service Greeter { rpc SayHello (HelloRequest) returns (HelloReply); }
message HelloRequest { string name = 1; repeated string tags = 2; }
message HelloReply { string message = 1; int64 total = 2; repeated string tags = 3; }
`;

const servers = project({ 'servers.py': SERVERS, 'demo.proto': PROTO });
const child = spawn(python, ['-u', join(servers, 'servers.py')], { stdio: ['ignore', 'pipe', 'inherit'] });
const ports = await new Promise((ok, fail) => {
    const timer = setTimeout(() => fail(new Error('the servers did not start')), 60000);
    createInterface({ input: child.stdout }).on('line', (line) => { if (line.startsWith('ready ')) { clearTimeout(timer); ok(line.split(' ').slice(1).map(Number)); } });
});
const [httpPort, wsPort, grpcPort] = ports;
console.log('servers: GraphQL', httpPort, 'WebSocket', wsPort, 'gRPC', grpcPort);

const root = project({
    'api.http': [
        '### query', 'GRAPHQL http://127.0.0.1:' + httpPort + '/graphql', '', 'query ($id: ID!) {', '  item(id: $id) { name }', '}', '', '{ "id": "7" }', '',
        '### socket', 'WEBSOCKET ws://127.0.0.1:' + wsPort + '/', '', '=== wait-for-server', 'one', '=== wait-for-server', 'two', '',
        '### call', 'GRPC 127.0.0.1:' + grpcPort + '/demo.Greeter/SayHello', '', '{"name": "Ada", "tags": ["x"]}', '',
        '### wrong', 'GRPC 127.0.0.1:' + grpcPort + '/demo.Greeter/SayHello', '', '{"nam": "Ada"}', '',
    ].join('\n'),
});
const app = await launch(root);
const { page } = app;
try {
    await page.evaluate(`window.pymagic.openFile('api.http')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('api.http')`, 20000, 'api.http open');
    const run = async (name) => {
        await page.evaluate(`document.getElementById('http-status').textContent = ''`);
        await page.evaluate(`window.dispatchEvent(new CustomEvent('pymagic:runHttp', { detail: { file: 'api.http', request: ${JSON.stringify(name)} } }))`);
        const status = await page.waitFor(`(() => { const t = document.getElementById('http-status').textContent; return t && !t.endsWith('…') ? t : null; })()`, 40000, name);
        await page.evaluate(`document.querySelector('#http-parts button[data-part="body"]').click()`);
        return { status, body: await page.evaluate(`document.getElementById('http-body').textContent`) };
    };

    const g = await run('query');
    console.log('GRAPHQL:', g.status, '\n' + g.body);
    assert.match(g.status, /^200 OK/);
    const sent = JSON.parse(g.body).data;
    assert.equal(sent.query, 'query ($id: ID!) {\n  item(id: $id) { name }\n}');
    assert.deepEqual(sent.variables, { id: '7' });
    assert.equal(sent.type, 'application/json');

    const w = await run('socket');
    console.log('WEBSOCKET:', w.status, '\n' + w.body);
    assert.match(w.status, /^101 Switching Protocols/);
    assert.deepEqual(w.body.split('\n').map((l) => l.replace(/\s+\d+ ms\s+/, ' ')), ['← welcome', '→ one', '← echo: one', '→ two', '← echo: two']);

    const c = await run('call');
    console.log('GRPC:', c.status, '\n' + c.body);
    assert.match(c.status, /^gRPC 0 OK/);
    assert.deepEqual(JSON.parse(c.body), { message: 'hello Ada', total: '1099511627776', tags: ['x', 'seen'] });

    const bad = await run('wrong');
    console.log('GRPC with a wrong field:', bad.status);
    assert.match(bad.status, /HelloRequest has no field nam \(it has name, tags\)/);

    // the history shows each, a gRPC answer by its code
    const rows = await page.evaluate(`[...document.querySelectorAll('#http-history .http-item')].map((r) => r.querySelector('.http-code').textContent + ' ' + r.querySelector('.http-what').textContent)`);
    console.log('history:', rows);
    assert.ok(rows.some((r) => r.startsWith('0 GRPC')) && rows.some((r) => r.startsWith('101 WEBSOCKET')) && rows.some((r) => r.startsWith('200 GRAPHQL')));
    console.log('OK');
} finally {
    await app.close();
    child.kill();
    await sleep(300);
    removeTree(servers);
}

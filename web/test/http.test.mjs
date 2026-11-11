// Checks of web/src/http-model.js: `node --test web/test/http.test.mjs`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
// a made-up token and password, put together here so that a secret scanner does not take them for real ones
const FAKE_TOKEN = ['fake', 'token'].join('-');
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/http-model.js', import.meta.url), 'utf8');
const { parseRequests, requestAt, fileVariables, environment, substitute, resolve, parseCurl, formatRequest, toCurl, prettyBody, environmentRows, environmentFiles, bodyParts, runScript, graphqlPayload, websocketMessages, websocketTranscript, grpcTarget } =
    await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const FILE = [
    '@token = abc',
    '',
    '### List the items',
    'GET {{host}}/api/items',
    '    ?page=2',
    '    &size=10',
    'Accept: application/json',
    '',
    '###',
    '# @name create',
    '// a comment',
    'POST {{host}}/api/items HTTP/1.1',
    'Content-Type: application/json',
    'Authorization: Bearer {{token}}',
    '',
    '{"name": "pen",',
    ' "id": "{{$uuid}}"}',
    '',
    '> {% client.global.set("id", response.body.id); %}',
    '',
    '###',
    'PUT http://localhost:8000/api/items/1',
    'Content-Type: application/json',
    '',
    '< ./item.json',
    '',
    '###',
    'https://example.com/plain',
].join('\n');

test('parseRequests: the requests of a file, with names, URLs over lines, headers and bodies', () => {
    const rs = parseRequests(FILE);
    assert.equal(rs.length, 4);
    assert.deepEqual(rs.map((r) => [r.line, r.name, r.method]), [[4, 'List the items', 'GET'], [12, 'create', 'POST'], [22, null, 'PUT'], [28, null, 'GET']]);
    assert.equal(rs[0].url, '{{host}}/api/items?page=2&size=10');
    assert.deepEqual(rs[0].headers, [['Accept', 'application/json']]);
    assert.equal(rs[0].body, null);
    assert.equal(rs[1].body, '{"name": "pen",\n "id": "{{$uuid}}"}');   // the response handler left out
    assert.deepEqual(rs[1].headers.map(([k]) => k), ['Content-Type', 'Authorization']);
    assert.equal(rs[2].bodyFile, './item.json');
    assert.equal(rs[3].url, 'https://example.com/plain');
    assert.equal(requestAt(rs, 17).name, 'create');
    assert.equal(requestAt(rs, 1).name, 'List the items');
    assert.deepEqual(parseRequests('# just a comment\n\n'), []);
});

test('variables: the file\'s, the environment\'s (private over public, $shared under both), and dynamic ones', () => {
    assert.deepEqual(fileVariables(FILE), { token: 'abc' });
    const env = environment({ $shared: { v: '1' }, dev: { host: 'http://localhost:8000', key: 'public' }, prod: { host: 'https://x' } }, { dev: { key: 'secret' } }, 'dev');
    assert.deepEqual(env.names, ['dev', 'prod']);
    assert.deepEqual(env.vars, { v: '1', host: 'http://localhost:8000', key: 'secret' });
    assert.deepEqual(substitute('{{host}}/a?k={{ key }}&x={{nope}}', env.vars), { text: 'http://localhost:8000/a?k=secret&x={{nope}}', missing: ['nope'] });
    assert.match(substitute('{{$uuid}}', {}).text, /^[0-9a-f-]{36}$/);
    assert.match(substitute('{{$timestamp}}', {}).text, /^\d{10}$/);
    const r = resolve(parseRequests(FILE)[1], { ...env.vars, ...fileVariables(FILE) });
    assert.equal(r.request.url, 'http://localhost:8000/api/items');
    assert.deepEqual(r.request.headers[1], ['Authorization', 'Bearer abc']);
    assert.deepEqual(r.missing, []);
});

test('parseCurl: what browsers\' "Copy as cURL" gives, POSIX and Windows', () => {
    const posix = parseCurl("curl 'https://api.example.com/items?x=1' \\\n  -H 'Accept: application/json' \\\n  -H 'Authorization: Bearer t' \\\n  --data-raw '{\"a\":1}' --compressed");
    assert.deepEqual(posix, { method: 'POST', url: 'https://api.example.com/items?x=1', headers: [['Accept', 'application/json'], ['Authorization', 'Bearer t'], ['Content-Type', 'application/x-www-form-urlencoded']], body: '{"a":1}', bodyFile: null });
    const windows = parseCurl('curl "http://localhost:8000/api/x" ^\n  -X PUT ^\n  -H "Content-Type: application/json" ^\n  -d "{\\"a\\": 2}"');
    assert.equal(windows.method, 'PUT');
    assert.equal(windows.body, '{"a": 2}');
    assert.deepEqual(windows.headers, [['Content-Type', 'application/json']]);
    assert.deepEqual(parseCurl('curl -u ada:pw https://x.org').headers, [['Authorization', 'Basic ' + btoa('ada:pw')]]);
    assert.equal(parseCurl('curl https://x.org -d @data.json').bodyFile, 'data.json');
    assert.equal(parseCurl('ls -la'), null);
});

test('formatRequest and toCurl: a request written back as .http lines and as a cURL command', () => {
    const r = { method: 'POST', url: 'http://localhost/a', headers: [['Content-Type', 'application/json']], body: "{\"it's\": 1}", bodyFile: null };
    assert.equal(formatRequest(r), 'POST http://localhost/a\nContent-Type: application/json\n\n{"it\'s": 1}\n');
    assert.equal(toCurl(r), "curl -X POST 'http://localhost/a' \\\n  -H 'Content-Type: application/json' \\\n  --data-raw '{\"it'\\''s\": 1}'");
    assert.deepEqual(parseCurl(toCurl(r)), { ...r, headers: r.headers });   // and read back the same
});

test('prettyBody: JSON indented, other text as it is', () => {
    assert.equal(prettyBody('{"a":[1,2]}', 'application/json'), '{\n  "a": [\n    1,\n    2\n  ]\n}');
    assert.equal(prettyBody('<p>hi</p>', 'text/html'), '<p>hi</p>');
    assert.equal(prettyBody('{bad', 'application/json'), '{bad');
});

test('the environment files as rows of the editor in the HTTP tab, and back', () => {
    const pub = { $shared: { version: 'v1' }, dev: { host: 'http://localhost:8000', port: 8000, token: 'public-old' }, prod: { host: 'https://example.com' }, empty: {} };
    const priv = { dev: { token: 'secret-dev' }, prod: { token: 'secret-prod' } };
    const { envs, rows } = environmentRows(pub, priv);
    assert.deepEqual(envs, ['$shared', 'dev', 'prod', 'empty']);
    const show = (r) => `${r.env}.${r.name}=${r.value}${r.secret ? ' (private)' : ''}`;
    // a number is shown as text; the private value of token is the one used, so it is the one shown
    assert.deepEqual(rows.map(show), ['$shared.version=v1', 'dev.host=http://localhost:8000', 'dev.port=8000', 'dev.token=secret-dev (private)', 'prod.host=https://example.com', 'prod.token=secret-prod (private)']);
    // back: the secret stays private and the public file no longer holds the old value; the empty environment stays choosable
    const files = environmentFiles(envs, rows);
    assert.deepEqual(files.public, { $shared: { version: 'v1' }, dev: { host: 'http://localhost:8000', port: '8000' }, prod: { host: 'https://example.com' }, empty: {} });
    assert.deepEqual(files.private, { dev: { token: 'secret-dev' }, prod: { token: 'secret-prod' } });
    // a variable made public again, an unnamed row left out, nothing private any more
    const edited = rows.map((r) => ({ ...r, secret: false })).concat([{ env: 'dev', name: '  ', value: 'x', secret: true }]);
    assert.deepEqual(environmentFiles(['dev', 'prod'], edited).private, null);
    assert.equal(environmentFiles(['dev', 'prod'], edited).public.dev.token, 'secret-dev');
    // nothing at all
    assert.deepEqual(environmentFiles([], []), { public: null, private: null });
    assert.deepEqual(environmentRows(null, null), { envs: [], rows: [] });
});

test('response handlers, >> files and pre-request scripts are read from a request', () => {
    const text = [
        '< {%',
        '    request.variables.set("id", "7");',
        '%}',
        'GET https://example.com/items/{{id}}',
        'Accept: application/json',
        '> {%',
        '    client.test("ok", () => client.assert(response.status === 200));',
        '%}',
        '>> ./out/item.json',
        '',
        '###',
        'POST https://example.com/items',
        'Content-Type: application/json',
        '',
        '{"name": "pen"}',
        '',
        '> {% client.global.set("made", response.body.id); %}',
        '> ./checks.js',
        '>>! saved.json',
        '',
        '###',
        'GET https://example.com/plain',
    ].join('\n');
    const [get, post, plain] = parseRequests(text);
    assert.equal(get.line, 4);
    assert.equal(get.pre.trim(), 'request.variables.set("id", "7");');
    assert.deepEqual(get.headers, [['Accept', 'application/json']]);
    assert.equal(get.body, null);
    assert.equal(get.handlers.length, 1);
    assert.match(get.handlers[0].script, /client\.test\("ok"/);
    assert.deepEqual(get.output, { file: './out/item.json', overwrite: false });
    assert.equal(post.body, '{"name": "pen"}');
    assert.deepEqual(post.handlers, [{ script: ' client.global.set("made", response.body.id); ' }, { file: './checks.js' }]);
    assert.deepEqual(post.output, { file: 'saved.json', overwrite: true });
    assert.equal(plain.pre, null);
    assert.deepEqual(plain.handlers, []);
    assert.equal(plain.output, null);
});

test('bodyParts: the files of < lines sent as they are, a multipart body with CRLF lines', () => {
    const body = ['--B', 'Content-Disposition: form-data; name="title"', '', 'A cat', '--B', 'Content-Disposition: form-data; name="photo"; filename="cat.png"',
        'Content-Type: image/png', '', '< ./cat.png', '--B', 'Content-Disposition: form-data; name="meta"', '', '<@ meta.json', '--B--'].join('\n');
    const parts = bodyParts(body, 'multipart/form-data; boundary=B');
    assert.deepEqual(parts, [
        { text: '--B\r\nContent-Disposition: form-data; name="title"\r\n\r\nA cat\r\n--B\r\nContent-Disposition: form-data; name="photo"; filename="cat.png"\r\nContent-Type: image/png\r\n\r\n' },
        { file: './cat.png', vars: false },
        { text: '\r\n' },
        { text: '--B\r\nContent-Disposition: form-data; name="meta"\r\n\r\n' },
        { file: 'meta.json', vars: true },
        { text: '\r\n' },
        { text: '--B--\r\n' },
    ]);
    // the text that the parts make, the files left as their names
    assert.equal(parts.map((x) => x.text ?? `[${x.file}]`).join(''),
        '--B\r\nContent-Disposition: form-data; name="title"\r\n\r\nA cat\r\n--B\r\nContent-Disposition: form-data; name="photo"; filename="cat.png"\r\nContent-Type: image/png\r\n\r\n[./cat.png]\r\n--B\r\nContent-Disposition: form-data; name="meta"\r\n\r\n[meta.json]\r\n--B--\r\n');
    // an ordinary body keeps its own line ends; a body of text only is one part
    assert.deepEqual(bodyParts('{\n  "a": 1\n}', 'application/json'), [{ text: '{\n  "a": 1\n}' }]);
    assert.deepEqual(bodyParts(null, 'text/plain'), []);
});

test('jsonPath in a response handler: members, items, wildcards, any depth, slices and lists; one place gives its value, others a list', () => {
    const body = { store: { book: [{ title: 'A', price: 8 }, { title: 'B', price: 12 }, { title: 'C', price: 5 }], 'odd key': 1, bike: { price: 20 } }, n: null };
    const at = (path, value = body) => runScript({ jsonPathOnly: true, value, path });
    assert.equal(at('$.store.book[0].title'), 'A');
    assert.equal(at("$['store']['odd key']"), 1);
    assert.equal(at('$.store.book[-1].title'), 'C');
    assert.deepEqual(at('$.store.book[*].title'), ['A', 'B', 'C']);
    assert.deepEqual(at('$..price'), [8, 12, 5, 20]);
    assert.deepEqual(at('$.store.book[0:2].price'), [8, 12]);
    assert.deepEqual(at('$.store.book[0,2].title'), ['A', 'C']);
    assert.equal(at('$.store.nothing'), null);
    assert.equal(at('$.store.bike.price', JSON.stringify(body)), 20);   // a body that is still text
    assert.equal(at('$.store.*').length, 3);
    assert.throws(() => at('store.book'), /starts with \$/);
    // in a handler, as PyCharm's newer HTTP client has it
    const r = runScript({
        script: 'client.global.set("token", jsonPath(response.body, "$.auth.token")); client.log(jsonPath(response.body, "$.items[*].id").join("+"));',
        response: { status: 200, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ auth: { token: FAKE_TOKEN }, items: [{ id: 1 }, { id: 2 }] }) },
    });
    assert.equal(r.error, undefined);
    assert.deepEqual(r.globals, { token: FAKE_TOKEN });
    assert.deepEqual(r.log, ['1+2']);
});

test('runScript: client, response and request as PyCharm gives them to a handler', () => {
    const response = { status: 201, headers: { 'Content-Type': 'application/json; charset=utf-8', 'Set-Cookie': 'a=1, b=2', Date: 'Mon, 03 Nov 2026 10:00:00 GMT' }, body: '{"id": 42, "tags": ["x"]}' };
    const r = runScript({
        script: [
            'client.test("created", () => { client.assert(response.status === 201, "status"); });',
            'client.test("json", () => { client.assert(response.contentType.mimeType === "application/json"); client.assert(response.body.tags[0] === "x"); });',
            'client.test("fails", () => { client.assert(response.body.id === 1, "the id is 1"); });',
            'client.test("throws", () => { response.body.nothing.here; });',
            'client.global.set("id", response.body.id);',
            'client.global.set("obj", {a: 1});',
            'client.global.clear("old");',
            'client.log("date", response.headers.valueOf("date"), response.headers.valuesOf("set-cookie").length);',
            'client.log(request.environment.get("host"), request.method);',
            'client.exit();',
            'client.log("not reached");',
        ].join('\n'),
        response, globals: { old: 'x', kept: 'y' }, environment: { host: 'http://h' }, request: { method: 'POST', url: 'http://h/items', headers: [] },
    });
    assert.deepEqual(r.tests.map((t) => [t.name, t.passed]), [['created', true], ['json', true], ['fails', false], ['throws', false]]);
    assert.equal(r.tests[2].error, 'the id is 1');
    assert.match(r.tests[3].error, /TypeError/);
    assert.deepEqual(r.globals, { kept: 'y', id: '42', obj: '{"a":1}' });
    assert.deepEqual(r.log, ['date Mon, 03 Nov 2026 10:00:00 GMT 2', 'http://h POST']);
    assert.equal(r.error, undefined);
    // an error of the script itself, a failed assert outside a test, and a pre-request script's variables
    assert.match(runScript({ script: 'nope(', response }).error, /SyntaxError/);
    assert.deepEqual(runScript({ script: 'client.assert(false, "bad")', response }).tests, [{ name: 'bad', passed: false, error: 'bad' }]);
    assert.deepEqual(runScript({ script: 'request.variables.set("n", 5)' }).variables, { n: '5' });
    // a body that is not JSON stays text
    assert.equal(runScript({ script: 'client.log(typeof response.body)', response: { status: 200, headers: {}, body: 'plain' } }).log[0], 'string');
});

test('GRAPHQL, WEBSOCKET and GRPC requests are read as PyCharm writes them', () => {
    const [g, w, r] = parseRequests([
        '### a query', 'GRAPHQL http://localhost:8000/graphql', 'Authorization: Bearer x', '', 'query ($id: ID!) {', '  item(id: $id) { name }', '}', '', '{', '  "id": "7"', '}',
        '### a socket', 'WEBSOCKET ws://localhost:8000/ws', '', '{"hello": 1}', '=== wait-for-server', 'two', '===', 'three',
        '### a call', 'GRPC localhost:50051/demo.Greeter/SayHello', '', '{"name": "Ada"}',
    ].join('\n'));
    assert.deepEqual([g.method, g.url, g.headers], ['GRAPHQL', 'http://localhost:8000/graphql', [['Authorization', 'Bearer x']]]);
    assert.deepEqual(graphqlPayload(g.body), { query: 'query ($id: ID!) {\n  item(id: $id) { name }\n}', variables: { id: '7' } });
    assert.deepEqual([w.method, w.url], ['WEBSOCKET', 'ws://localhost:8000/ws']);
    assert.deepEqual(websocketMessages(w.body), [{ text: '{"hello": 1}', wait: false }, { text: 'two', wait: true }, { text: 'three', wait: false }]);
    assert.deepEqual([r.method, r.url, r.body], ['GRPC', 'localhost:50051/demo.Greeter/SayHello', '{"name": "Ada"}']);
});

test('a GraphQL query without variables, and one that is only braces', () => {
    assert.deepEqual(graphqlPayload('{ items { id } }'), { query: '{ items { id } }', variables: null });
    // the query's own lines that start with { are not taken for the variables
    assert.deepEqual(graphqlPayload('{\n  items { id }\n}'), { query: '{\n  items { id }\n}', variables: null });
    assert.deepEqual(graphqlPayload(null), { query: '', variables: null });
});

test('a gRPC address: plain text by default, TLS with grpcs:// or https://', () => {
    assert.deepEqual(grpcTarget('localhost:50051/demo.Greeter/SayHello'), { address: 'http://localhost:50051', service: 'demo.Greeter', method: 'SayHello' });
    assert.deepEqual(grpcTarget('grpcs://api.example.com:443/pkg.v1.Store/Put'), { address: 'https://api.example.com:443', service: 'pkg.v1.Store', method: 'Put' });
    assert.equal(grpcTarget('localhost:50051/SayHello'), null);
});

test('the talk of a WebSocket request as text', () => {
    assert.equal(websocketTranscript([{ dir: 'sent', text: 'hi', ms: 3 }, { dir: 'received', text: 'a\nb', ms: 12 }], { status: 1000, description: 'bye' }),
        '→     3 ms  hi\n←    12 ms  a\n            b\n× closed (1000 bye)');
    // a gRPC call kept open: a typed message not sent, and the call's status
    assert.equal(websocketTranscript([{ dir: 'sent', text: '{"value":1}', ms: 5 }, { dir: 'note', text: 'not sent: Number has no field valu', ms: 9 }], { status: '0 OK', description: '' }),
        '→     5 ms  {"value":1}\n!     9 ms  not sent: Number has no field valu\n× closed (0 OK)');
});

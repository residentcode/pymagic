// Checks of web/src/http-model.js: `node --test web/test/http.test.mjs`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/http-model.js', import.meta.url), 'utf8');
const { parseRequests, requestAt, fileVariables, environment, substitute, resolve, parseCurl, formatRequest, toCurl, prettyBody } =
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

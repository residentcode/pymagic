import test from 'node:test';
import assert from 'node:assert/strict';
import { summaryLine, parameterRows, countsLine, routeKey, rowLabel, requestFor, requestText, parseRequest, clientCode, docOf, openApi, filterRoutes, fillPath } from '../src/routes-model.js';

const route = {
    methods: 'POST', path: '/items/{item_id}', name: 'create', uri: 'file:///x/main.py', response: 'ItemOut', status: '201', tags: ['items', 'admin'],
    params: [
        { name: 'item_id', kind: 'path', type: 'int', required: true },
        { name: 'q', kind: 'query', type: 'str | None', required: false, detail: 'max_length=50' },
        { name: 'user', kind: 'dependency', type: null, required: true, detail: 'current_user', children: [
            { name: 'token', kind: 'header', type: 'str', required: true },
            { name: 'db', kind: 'dependency', required: true, detail: 'get_db' },
        ] },
    ],
};

test('the line about what the route gives', () => {
    assert.equal(summaryLine(route), 'response ItemOut · status 201 · tags items, admin');
    assert.equal(summaryLine({ methods: 'GET', path: '/' }), '');
    assert.equal(summaryLine({ response: 'X' }), 'response X');
});

test('the rows of the parameters, a dependency\'s own one level deeper', () => {
    const rows = parameterRows(route.params);
    assert.deepEqual(rows.map((r) => [r.depth, r.kind, r.name]), [[0, 'path', 'item_id'], [0, 'query', 'q'], [0, 'dependency', 'user'], [1, 'header', 'token'], [1, 'dependency', 'db']]);
    assert.equal(rows[1].detail, 'max_length=50');
    assert.equal(rows[1].required, false);
    assert.equal(rows[2].type, '');
    assert.deepEqual(parameterRows(undefined), []);
});

test('how many of each kind', () => {
    assert.equal(countsLine(route.params), '1 path · 1 query · 1 dependency');
    assert.equal(countsLine([{ kind: 'dependency' }, { kind: 'dependency' }, { kind: 'body' }]), '1 body · 2 dependencies');
    assert.equal(countsLine([]), '');
});

test('a route keeps its key and a parameter reads as written', () => {
    assert.equal(routeKey(route), 'POST /items/{item_id} create file:///x/main.py');
    assert.equal(rowLabel({ name: 'q', type: 'str | None' }), 'q: str | None');
    assert.equal(rowLabel({ name: 'user', type: '' }), 'user');
});

const ROUTE = { methods: 'POST', path: '/items/{item_id}', name: 'update_item', framework: 'fastapi', params: [
    { name: 'item_id', kind: 'path', type: 'int', required: true }, { name: 'q', kind: 'query', type: 'str', required: true }, { name: 'page', kind: 'query', type: 'int', required: false },
    { name: 'item', kind: 'body', type: 'Item', required: true, children: [{ name: 'name', kind: 'field', type: 'str' }, { name: 'price', kind: 'field', type: 'float' }] }] };

test('the request of an endpoint: the path filled, the required query, a JSON body; read back from its text', () => {
    const r = requestFor(ROUTE, 'http://127.0.0.1:8000/');
    assert.equal(r.url, 'http://127.0.0.1:8000/items/1?q=string');
    assert.deepEqual(JSON.parse(r.body), { name: 'string', price: 1.5 });
    assert.equal(r.headers['Content-Type'], 'application/json');
    assert.deepEqual(parseRequest(requestText(r)), r);
    assert.equal(parseRequest('nonsense'), null);
    assert.equal(fillPath('shop/<int:pk>/<slug:slug>/'), 'shop/1/value/');
    assert.equal(requestFor({ methods: 'ANY', path: 'admin/…', framework: 'library' }).url, 'http://127.0.0.1:8000/admin/');
});

test('client code in curl, Python and JavaScript', () => {
    const r = requestFor(ROUTE);
    assert.ok(clientCode(r, 'curl').startsWith("curl -X POST 'http://127.0.0.1:8000/items/1?q=string' \\\n  -H 'Accept: application/json'"));
    assert.ok(clientCode(r, 'python').includes('requests.post("http://127.0.0.1:8000/items/1?q=string", headers={"Accept":"application/json","Content-Type":"application/json"}, json={"name":"string","price":1.5})'));
    assert.ok(clientCode(r, 'js').startsWith('const response = await fetch("http://127.0.0.1:8000/items/1?q=string", {\n  "method": "POST"'));
});

test('a view\'s docstring, the OpenAPI document and the filters', () => {
    assert.equal(docOf('def index(request):\n    """Lists the items.\n\n    Newest first.\n    """\n    return x\n', 0), 'Lists the items.\n\nNewest first.');
    assert.equal(docOf('def f(\n    a,\n):\n    "One line."\n', 0), 'One line.');
    assert.equal(docOf('def f():\n    return 1\n', 0), '');
    const doc = openApi([ROUTE, { methods: 'GET', path: 'shop/<int:pk>/', name: 'detail', framework: 'django' }, { methods: 'ANY', path: 'admin/…', framework: 'library', name: 'admin.site.urls' }]);
    assert.deepEqual(Object.keys(doc.paths), ['/items/{item_id}', '/shop/{pk}/']);
    const op = doc.paths['/items/{item_id}'].post;
    assert.deepEqual(op.parameters.map((p) => `${p.in}:${p.name}:${p.schema.type}:${p.required}`), ['path:item_id:integer:true', 'query:q:string:true', 'query:page:integer:false']);
    assert.deepEqual(op.requestBody.content['application/json'].schema.properties, { name: { type: 'string' }, price: { type: 'number' } });
    const routes = [ROUTE, { methods: 'ANY', path: 'admin/…', framework: 'library', name: 'admin' }, { methods: 'GET', path: '/shop/', framework: 'test', name: 'test_home' }];
    assert.deepEqual(filterRoutes(routes, { method: 'GET' }).map((r) => r.name), ['admin', 'test_home']);
    assert.deepEqual(filterRoutes(routes, { origins: ['project'] }).map((r) => r.name), ['update_item']);
    assert.deepEqual(filterRoutes(routes, { text: 'items' }).map((r) => r.name), ['update_item']);
});

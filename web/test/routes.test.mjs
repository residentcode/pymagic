import test from 'node:test';
import assert from 'node:assert/strict';
import { summaryLine, parameterRows, countsLine, routeKey, rowLabel } from '../src/routes-model.js';

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

// Checks of web/src/db-model.js: `node --test web/test/db.test.mjs`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/db-model.js', import.meta.url), 'utf8');
const { cellText, typedValue, statementAt, aliasesOf, sqlCompletions, changesOf, toCsv } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

test('cellText and typedValue: NULL, blobs, numbers and text', () => {
    assert.equal(cellText(null), 'NULL');
    assert.equal(cellText({ blob: '00ff', size: 2 }), '<blob 2 bytes>');
    assert.equal(cellText(2.5), '2.5');
    assert.equal(cellText({ real: '2.0' }), '2.0');
    assert.equal(cellText(''), '');
    assert.equal(typedValue('NULL'), null);
    assert.equal(typedValue('null'), 'null');
    assert.equal(typedValue('12'), '12');
});

test('statementAt: the statement around the cursor, semicolons in strings and comments not counting', () => {
    const text = "SELECT 1;\nSELECT ';' -- a ; here\n FROM t;\n\nUPDATE t SET a = 1";
    assert.equal(statementAt(text, 3).text, 'SELECT 1');
    assert.equal(statementAt(text, 15).text, "SELECT ';' -- a ; here\n FROM t");
    assert.equal(statementAt(text, text.length).text, 'UPDATE t SET a = 1');
    assert.equal(statementAt('SELECT 1;', 9).text, 'SELECT 1');           // right after the semicolon
    assert.equal(statementAt('SELECT 1;\n', 10).text, 'SELECT 1');        // nothing after it
    assert.equal(statementAt('SELECT 1', 4).text, 'SELECT 1');
});

const objects = [
    { name: 'item', kind: 'table', columns: [{ name: 'id', type: 'INTEGER' }, { name: 'name', type: 'TEXT' }] },
    { name: 'tag', kind: 'table', columns: [{ name: 'label', type: 'TEXT' }] },
    { name: 'cheap', kind: 'view', columns: [{ name: 'name', type: '' }] },
];

test('aliasesOf: the tables a statement names, and their aliases', () => {
    assert.deepEqual(aliasesOf('SELECT * FROM item i JOIN tag AS t ON t.x = i.id WHERE', ['item', 'tag']), { item: 'item', i: 'item', tag: 'tag', t: 'tag' });
    assert.deepEqual(aliasesOf('SELECT * FROM item WHERE id = 1', ['item']), { item: 'item' });
    assert.deepEqual(aliasesOf('SELECT * FROM nothing', ['item']), {});
});

test('sqlCompletions: columns after a table or an alias and a dot, else tables, the named tables\' columns and keywords', () => {
    assert.deepEqual(sqlCompletions('SELECT i.', 'SELECT i. FROM item i', objects).map((c) => c.label), ['id', 'name']);
    const all = sqlCompletions('SELECT ', 'SELECT  FROM tag', objects);
    assert.deepEqual(all.filter((c) => c.kind !== 'keyword').map((c) => `${c.kind}:${c.label}`), ['table:item', 'table:tag', 'view:cheap', 'column:label']);
    assert.ok(all.some((c) => c.label === 'WHERE'));
    // no table named yet: every column, each once
    assert.deepEqual(sqlCompletions('SELECT ', 'SELECT ', objects).filter((c) => c.kind === 'column').map((c) => c.label), ['id', 'name', 'label']);
    assert.deepEqual(sqlCompletions('SELECT x.', 'SELECT x.', objects), []);
});

test('changesOf: edits by row key, inserts, deletes; a deleted row\'s edits are dropped', () => {
    const rows = [{ key: { rowid: 1 } }, { key: { rowid: 2 } }, { key: { rowid: 3 } }];
    const edits = new Map([[0, { 1: 'pencil' }], [1, { 1: 'x' }]]);
    const changes = changesOf(['id', 'name'], rows, edits, [{ 1: 'lamp' }], new Set([1, 2]));
    assert.deepEqual(changes, [
        { kind: 'update', key: { rowid: 1 }, values: { name: 'pencil' } },
        { kind: 'insert', values: { name: 'lamp' } },
        { kind: 'delete', key: { rowid: 2 } },
        { kind: 'delete', key: { rowid: 3 } },
    ]);
});

test('toCsv: quoting as spreadsheets read it, NULL empty', () => {
    assert.equal(toCsv(['a', 'b'], [[1, 'x, "y"'], [null, 'line\nbreak']]), 'a,b\r\n1,"x, ""y"""\r\n,"line\nbreak"\r\n');
});

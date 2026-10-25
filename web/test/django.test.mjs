// Checks of web/src/django-model.js: `node web/test/django.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/django-model.js', import.meta.url), 'utf8');
const d = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

test('a command line is split into words, quotes group words', () => {
    assert.deepEqual(d.splitArguments('sqlmigrate shop 0001'), ['sqlmigrate', 'shop', '0001']);
    assert.deepEqual(d.splitArguments('  loaddata   "my fixtures.json"  '), ['loaddata', 'my fixtures.json']);
    assert.deepEqual(d.splitArguments("createsuperuser --username 'Ann Lee'"), ['createsuperuser', '--username', 'Ann Lee']);
    assert.deepEqual(d.splitArguments('   '), []);
});

test('a migration is shown as its number and its name', () => {
    assert.equal(d.migrationLabel({ name: '0001_initial' }), '0001  initial');
    assert.equal(d.migrationLabel({ name: '0007_book_add_price' }), '0007  book add price');
    assert.equal(d.migrationLabel({ name: 'odd' }), 'odd');
});

test('the debug server runs without the reloader and under the debugger', () => {
    const debug = d.COMMANDS.filter((c) => c[2]);
    assert.equal(debug.length, 1);
    assert.deepEqual(debug[0][1], ['runserver', '--noreload']);
});

test('every command is manage.py followed by words, none empty', () => {
    for (const [label, args] of d.COMMANDS) {
        assert.ok(label.length > 0);
        assert.ok(args.length > 0 && args.every((a) => a.length > 0));
    }
});

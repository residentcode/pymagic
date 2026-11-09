// Checks of Laravel's page parts without a browser: `node --test web/test/laravel.test.mjs`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { artisanMatches } from '../src/laravel-model.js';
import { languageIdFor } from '../src/file-language.js';
import { startOf, blank, read, KINDS } from '../src/runconfig-model.js';

const COMMANDS = [
    { name: 'make:model', description: 'Create a new Eloquent model class' },
    { name: 'make:controller', description: 'Create a new controller class' },
    { name: 'migrate', description: 'Run the database migrations' },
    { name: 'migrate:fresh', description: 'Drop all tables and re-run all migrations' },
    { name: 'route:list', description: 'List all registered routes' },
    { name: 'db:seed', description: 'Seed the database with records' },
];

test("Artisan's commands as they are typed: the name's start, then inside it, then the description", () => {
    assert.deepEqual(artisanMatches(COMMANDS, 'make').map((c) => c.name), ['make:controller', 'make:model']);
    assert.deepEqual(artisanMatches(COMMANDS, 'migrat').map((c) => c.name), ['migrate', 'migrate:fresh']);
    assert.deepEqual(artisanMatches(COMMANDS, 'model').map((c) => c.name), ['make:model']);
    assert.deepEqual(artisanMatches(COMMANDS, 'database').map((c) => c.name), ['db:seed', 'migrate']);
    assert.deepEqual(artisanMatches(COMMANDS, 'nothing'), []);
    // nothing typed: the ones run last first, in that order, then the others by name
    assert.deepEqual(artisanMatches(COMMANDS, '', ['route:list', 'migrate']).map((c) => c.name).slice(0, 3), ['route:list', 'migrate', 'db:seed']);
});

test("Blade templates and Artisan's script are told by their names", () => {
    const languages = [{ id: 'php', extensions: ['.php', '.phtml'] }, { id: 'html', extensions: ['.html'] }];
    assert.equal(languageIdFor('resources/views/welcome.blade.php', languages), 'blade');
    assert.equal(languageIdFor('app/Models/User.php', languages), 'php');
    assert.equal(languageIdFor('artisan', languages, '#!/usr/bin/env php'), 'php');
});

test('the Laravel server, an Artisan command and a PHP script as run configurations', () => {
    assert.deepEqual(startOf({ ...blank('Server', 'laravel') }), { artisan: ['serve', '--host=127.0.0.1', '--port=8000'], env: {}, cwd: '' });
    assert.deepEqual(startOf({ ...blank('Server', 'laravel'), host: '0.0.0.0', port: '9000', args: '--no-reload' }).artisan, ['serve', '--host=0.0.0.0', '--port=9000', '--no-reload']);
    assert.deepEqual(startOf({ ...blank('Make', 'artisan'), command: 'make:model Post -m' }).artisan, ['make:model', 'Post', '-m']);
    assert.deepEqual(startOf({ ...blank('S', 'php'), script: 'scripts/report.php', args: '--month 3' }), { file: 'scripts/report.php', args: ['--month', '3'], env: {}, cwd: '' });
    assert.equal(blank('Server', 'laravel').port, '8000');
    assert.deepEqual(read({ configurations: [{ ...blank('Make', 'artisan'), command: 'migrate --seed' }] }).configurations[0].command, 'migrate --seed');
    assert.ok(['laravel', 'artisan', 'php'].every((k) => KINDS.some(([id]) => id === k)));
});

test("PHP's value under the mouse: a variable and the properties before the one pointed at, never a call", async () => {
    const { phpExpressionAt } = await import('../src/debug-model.js');
    const line = '        $total = $order->customer->name . count($items) . $this?->id;';
    const at = (text) => line.indexOf(text) + 1;
    assert.equal(phpExpressionAt(line, at('$total')), '$total');
    assert.equal(phpExpressionAt(line, at('total')), '$total');
    assert.equal(phpExpressionAt(line, at('order')), '$order');
    assert.equal(phpExpressionAt(line, at('customer')), '$order->customer');
    assert.equal(phpExpressionAt(line, at('name')), '$order->customer->name');
    assert.equal(phpExpressionAt(line, at('items')), '$items');
    assert.equal(phpExpressionAt(line, at('id;')), '$this?->id');
    assert.equal(phpExpressionAt(line, at('count')), null);            // a call is not read: it would run
    assert.equal(phpExpressionAt(line, at(' = ')), null);
    assert.equal(phpExpressionAt('$user->save();', 9), null);          // a method
});

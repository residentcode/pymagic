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

const HELP = `usage: manage.py migrate [-h] [--noinput] [--database DATABASE] [--fake] [--plan] [app_label] [migration_name]

Updates database schema.

positional arguments:
  app_label             App label of an application to synchronize the state.
  migration_name        Database state will be brought to the state after that migration.

options:
  -h, --help            show this help message and exit
  --noinput, --no-input
                        Tells Django to NOT prompt the user for input of any kind.
  --database DATABASE   Nominates a database to synchronize. Defaults to the
                        "default" database.
  --fake                Mark migrations as run without actually running them.
  -v {0,1,2,3}, --verbosity {0,1,2,3}
                        Verbosity level
`;

test('the manage.py console reads the commands and the options of a command from Django\'s help', () => {
    assert.deepEqual(d.parseCommands('check\nmigrate\n\nrunserver\nmigrate\n'), ['check', 'migrate', 'runserver']);
    const options = d.parseOptions(HELP);
    assert.deepEqual(options.map((o) => o.flags.join(' ')), ['-h --help', '--noinput --no-input', '--database', '--fake', '-v --verbosity']);
    assert.equal(options[2].help, 'Nominates a database to synchronize. Defaults to the "default" database.');
    assert.equal(options[1].help, 'Tells Django to NOT prompt the user for input of any kind.');
});

test('Tab completes a command, then the options of that command', () => {
    const commands = ['makemigrations', 'migrate', 'runserver'];
    const optionsOf = (c) => (c === 'migrate' ? d.parseOptions(HELP) : null);
    assert.deepEqual(d.completeManage('m', commands, optionsOf), { word: 'm', items: ['makemigrations', 'migrate'] });
    assert.deepEqual(d.completeManage('mi', commands, optionsOf).items, ['migrate']);
    assert.deepEqual(d.completeManage('migrate --n', commands, optionsOf), { word: '--n', items: ['--no-input', '--noinput'] });
    assert.deepEqual(d.completeManage('migrate -', commands, optionsOf).items.slice(0, 2), ['--database', '--fake']);
    assert.deepEqual(d.completeManage('migrate sho', commands, optionsOf).items, []);
});

test('the structure grouped by app or by kind, each item in the deepest app that holds it', () => {
    const apps = [{ name: 'shop', path: 'shop' }, { name: 'shop.blog', path: 'shop/blog' }];
    const items = [
        { kind: 'view', name: 'index', rel: 'shop/views.py' }, { kind: 'model', name: 'Book', rel: 'shop/models.py' },
        { kind: 'admin', name: 'BookAdmin', rel: 'shop/admin.py' }, { kind: 'model', name: 'Post', rel: 'shop/blog/models.py' }, { kind: 'view', name: 'home', rel: 'site/views.py' },
    ];
    const byApp = d.groupStructure(items, apps, 'app');
    assert.deepEqual(byApp.map((g) => `${g.title}: ${g.items.map((i) => i.name).join(',')}`), ['(no app): home', 'shop: Book,BookAdmin,index', 'shop.blog: Post']);
    const byKind = d.groupStructure(items, apps, 'kind');
    assert.deepEqual(byKind.map((g) => `${g.title}: ${g.items.map((i) => `${i.name}@${i.app}`).join(',')}`), ['Models: Book@shop,Post@shop.blog', 'Admin: BookAdmin@shop', 'Views: home@(no app),index@shop']);
});

test('a new model, a new view and a model registered in the admin, with the imports they need', () => {
    assert.equal(d.addModel('from django.db import models\n\n# Create your models here.\n', 'Order'),
        'from django.db import models\n\n# Create your models here.\n\n\nclass Order(models.Model):\n    name = models.CharField(max_length=100)\n\n    def __str__(self):\n        return self.name\n');
    assert.ok(d.addModel('', 'A').startsWith('from django.db import models\n'));
    assert.equal(d.addView('', 'about', 'shop'), 'from django.shortcuts import render\n\n\ndef about(request):\n    return render(request, "shop/about.html")\n');
    assert.equal(d.registerModel('from django.contrib import admin\nfrom .models import Book\n', 'Author'), 'from django.contrib import admin\nfrom .models import Book, Author\n\nadmin.site.register(Author)\n');
    assert.equal(d.registerModel('# empty\n', 'Book'), 'from django.contrib import admin\nfrom .models import Book\n# empty\n\nadmin.site.register(Book)\n');
});

test('a ModelAdmin and a ModelSerializer made for a model, with its fields', () => {
    assert.equal(d.addModelAdmin('from django.contrib import admin\n', 'Book', ['title', 'author']),
        'from django.contrib import admin\nfrom .models import Book\n\n\n@admin.register(Book)\nclass BookAdmin(admin.ModelAdmin):\n    list_display = ("title", "author")\n');
    assert.ok(d.addModelAdmin('', 'Tag', ['label']).includes('list_display = ("label",)'));
    assert.equal(d.addModelSerializer('', 'Book', ['title']),
        'from rest_framework import serializers\nfrom .models import Book\n\n\nclass BookSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Book\n        fields = ["id", "title"]\n');
});

// Checks of web/src/runanything-model.js: `node web/test/runanything.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { splitArgs, parseCommand, score, suggest, doubleTap } from '../src/runanything-model.js';

test('words of a command line, quoted ones whole', () => {
    assert.deepEqual(splitArgs('a "b c" \'d e\' f'), ['a', 'b c', 'd e', 'f']);
    assert.deepEqual(splitArgs('  '), []);
});

test('what a line says to do', () => {
    assert.deepEqual(parseCommand('python -m http.server 8000'), { kind: 'module', module: 'http.server', args: ['8000'] });
    assert.deepEqual(parseCommand('-m pytest -x'), { kind: 'module', module: 'pytest', args: ['-x'] });
    assert.deepEqual(parseCommand('pip install requests "rich>=13"'), { kind: 'pip', args: ['install', 'requests', 'rich>=13'] });
    assert.deepEqual(parseCommand('manage.py migrate --plan'), { kind: 'manage', args: ['migrate', '--plan'] });
    assert.deepEqual(parseCommand('python manage.py runserver 0.0.0.0:9000'), { kind: 'manage', args: ['runserver', '0.0.0.0:9000'] });
    assert.deepEqual(parseCommand('src/app.py --debug x'), { kind: 'script', file: 'src/app.py', args: ['--debug', 'x'] });
    assert.deepEqual(parseCommand('python tools\\run.py a'), { kind: 'script', file: 'tools/run.py', args: ['a'] });
    assert.deepEqual(parseCommand('open My Project'), { kind: 'project', name: 'My Project' });
    assert.deepEqual(parseCommand('Run tests'), { kind: 'name', text: 'Run tests' });
    assert.deepEqual(parseCommand('open'), { kind: 'name', text: 'open' });
    assert.deepEqual(parseCommand(''), { kind: 'name', text: '' });
});

test('how well a name matches', () => {
    assert.equal(score('Django server', 'django server'), 100);
    assert.ok(score('Django server', 'dj') > score('Run Django', 'dj'));
    assert.ok(score('Run Django server', 'serv') > score('Django server', 'dsr') && score('Django server', 'dsr') > 0);
    assert.equal(score('abc', 'xyz'), 0);
    assert.equal(score('anything', ''), 1);
});

test('suggestions: configurations, scripts and commands for the first word', () => {
    const ctx = { configs: ['Run tests', 'Django server'], scripts: ['app.py', 'tools/serve.py'], manage: 'manage.py', recents: [{ name: 'shop', path: 'C:/p/shop' }] };
    const labels = (text) => suggest(text, ctx).map((s) => `${s.kind}:${s.label}`);
    assert.equal(labels('dj')[0], 'config:Django server');
    assert.ok(!labels('dj').includes('config:Run tests'));
    assert.ok(labels('serve').includes('script:tools/serve.py'));
    assert.ok(labels('serve').includes('config:Django server'));
    assert.ok(labels('pip').includes('pip:pip install'));
    assert.ok(labels('pyth').includes('module:python -m '));
    assert.ok(labels('op').includes('project:open …'));
    // no manage.py in the project: no Django commands
    assert.ok(!suggest('migrate', { ...ctx, manage: null }).some((s) => s.kind === 'manage'));
});

test('suggestions after the first word', () => {
    const ctx = { configs: [], scripts: [], manage: 'manage.py', recents: [{ name: 'shop', path: 'C:/p/shop' }, { name: 'blog', path: 'C:/p/blog' }] };
    const mig = suggest('manage.py mig', ctx).map((s) => s.label);
    assert.equal(mig[0], 'migrate');
    assert.ok(mig.includes('makemigrations') && mig.includes('sqlmigrate') && !mig.includes('runserver'));
    assert.equal(suggest('manage.py mig', ctx)[0].fill, 'manage.py migrate');
    assert.deepEqual(suggest('pip uni', { configs: [] }).map((s) => s.fill), ['pip uninstall']);
    assert.deepEqual(suggest('open bl', ctx).map((s) => [s.label, s.detail]), [['blog', 'C:/p/blog']]);
    // arguments after the command are not completed
    assert.deepEqual(suggest('manage.py migrate shop', ctx), []);
});

test('Control twice: two quick taps with no key between', () => {
    const s = {};
    const tap = (t, other) => {
        doubleTap(s, { type: 'keydown', key: 'Control' }, t);
        if (other) doubleTap(s, { type: 'keydown', key: 'c' }, t);
        return doubleTap(s, { type: 'keyup', key: 'Control' }, t + 20);
    };
    assert.equal(tap(1000, false), false);
    assert.equal(tap(1200, false), true);
    assert.equal(tap(1400, false), false);                // the pair is used up
    assert.equal(tap(5000, false), false);
    assert.equal(tap(5700, false), false);                // too slow
    assert.equal(tap(5900, true), false);                 // Ctrl+C is not a tap
    assert.equal(tap(6000, false), false);
});

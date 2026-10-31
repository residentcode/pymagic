import test from 'node:test';
import assert from 'node:assert/strict';
import { read, startOf, parseEnv, formatEnv, uniqueName, blank, splitShared } from '../src/runconfig-model.js';

test('what is stored is read into well-formed configurations, the broken ones left out', () => {
    const { configurations, selected } = read({
        selected: 'serve',
        configurations: [
            { name: 'serve', script: 'app.py', args: '--port 8000', cwd: 'src', env: { DEBUG: '1', BAD: 3 } },
            { name: 'serve', script: 'other.py' },            // the same name twice
            { name: '', script: 'x.py' },                      // no name
            { name: 'no script' },                             // no script
            'nonsense',
            { name: 'plain', script: 'run.py' },
        ],
    });
    assert.deepEqual(configurations.map((c) => c.name), ['serve', 'plain']);
    assert.deepEqual(configurations[0].env, { DEBUG: '1' });   // a number is not a value
    assert.equal(configurations[1].args, '');
    assert.equal(selected, 'serve');
});

test('a chosen configuration that is not in the list is not chosen', () => {
    assert.equal(read({ selected: 'gone', configurations: [{ name: 'a', script: 'a.py' }] }).selected, null);
    assert.deepEqual(read(undefined), { configurations: [], selected: null });
    assert.deepEqual(read({ configurations: 'x' }), { configurations: [], selected: null });
});

test('the run is told the file, the arguments split like a command line, the variables and the folder', () => {
    const start = startOf({ name: 'x', script: 'app.py', args: '--name "Ada Lovelace" -v \'a b\'', cwd: 'src', env: { A: '1' } });
    assert.deepEqual(start, { file: 'app.py', args: ['--name', 'Ada Lovelace', '-v', 'a b'], env: { A: '1' }, cwd: 'src' });
});

test('variables are typed one per line', () => {
    assert.deepEqual(parseEnv('A=1\n# note\n\nB = two words \nbroken\n=novalue\nC=a=b'), { A: '1', B: 'two words', C: 'a=b' });
    assert.equal(formatEnv({ A: '1', B: '2' }), 'A=1\nB=2');
    assert.deepEqual(parseEnv(formatEnv({ X: 'y' })), { X: 'y' });
});

test('a new configuration gets a name nobody has', () => {
    const list = [{ name: 'New configuration' }, { name: 'New configuration 2' }];
    assert.equal(uniqueName(list, 'New configuration'), 'New configuration 3');
    assert.equal(uniqueName([], 'a'), 'a');
    assert.equal(blank().cwd, '');
});

test('each kind starts its program: a module, Django, uvicorn, Flask, pytest', () => {
    assert.deepEqual(startOf({ ...blank('m'), kind: 'module', module: 'http.server', args: '9000' }), { module: 'http.server', args: ['9000'], env: {}, cwd: '' });
    assert.deepEqual(startOf(blank('d', 'django')).args, ['runserver', '127.0.0.1:8000']);
    assert.deepEqual(startOf({ ...blank('d', 'django'), host: '0.0.0.0', port: '9000', reload: false }), { file: 'manage.py', args: ['runserver', '0.0.0.0:9000', '--noreload'], env: {}, cwd: '' });
    assert.deepEqual(startOf(blank('f', 'fastapi')), { module: 'uvicorn', args: ['main:app', '--host', '127.0.0.1', '--port', '8000', '--reload'], env: {}, cwd: '' });
    assert.deepEqual(startOf(blank('f', 'flask')).args, ['--app', 'app', 'run', '--host', '127.0.0.1', '--port', '5000', '--debug']);
    assert.deepEqual(startOf({ ...blank('t', 'pytest'), target: 'tests/test_a.py::test_x', args: '-x' }).args, ['tests/test_a.py::test_x', '-x']);
});

test("the run's settings: .env files, uv run, the roots on PYTHONPATH; the new fields are read with defaults", () => {
    const s = startOf({ ...blank('x'), script: 'a.py', envFiles: '.env, config/dev.env', uv: true, uvPath: 'C:/uv.exe', contentRoots: false });
    assert.deepEqual(s, { file: 'a.py', args: [], env: {}, cwd: '', envFiles: ['.env', 'config/dev.env'], uv: true, uvPath: 'C:/uv.exe', contentRoots: false });
    const { configurations } = read({ configurations: [{ name: 'old', script: 'a.py' }, { name: 'srv', kind: 'fastapi', app: 'api:app', reload: 'yes', shared: true }, { name: 'odd', kind: 'rails', script: 'x.py' }] });
    assert.equal(configurations[0].kind, 'script');
    assert.equal(configurations[0].openPanel, true);
    assert.equal(configurations[1].script, '');
    assert.equal(configurations[1].app, 'api:app');
    assert.equal(configurations[1].reload, true);   // not a switch: the default
    assert.equal(configurations[2].kind, 'script');
    assert.deepEqual(splitShared(configurations).shared.map((c) => c.name), ['srv']);
});

test('a Django server: its settings, a test server with fixtures, all the code debugged; Django tests go to the test tree', () => {
    const s = startOf({ ...blank('d', 'django'), settings: 'cfg.dev', testServer: true, fixtures: 'users.json, items.json', justMyCode: false });
    assert.deepEqual(s, { file: 'manage.py', args: ['testserver', 'users.json', 'items.json', '--addrport', '127.0.0.1:8000', '--noinput'], env: { DJANGO_SETTINGS_MODULE: 'cfg.dev' }, cwd: '', allCode: true });
    const t = startOf({ ...blank('t', 'djangoTests'), target: 'shop.tests.ItemTests', failfast: true });
    assert.deepEqual(t.tests, { ids: ['shop.tests.ItemTests'], framework: 'django', settings: undefined, failfast: true });
    assert.equal(read({ configurations: [{ name: 'x', kind: 'djangoTests', failfast: true, before: 'migrate' }] }).configurations[0].before, 'migrate');
});

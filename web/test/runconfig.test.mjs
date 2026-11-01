import test from 'node:test';
import assert from 'node:assert/strict';
import { read, startOf, parseEnv, formatEnv, uniqueName, blank, splitShared, compoundMembers, KINDS, parseMappings, formatMappings, portOf } from '../src/runconfig-model.js';

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

test('a compound names the configurations it starts together; the ones that cannot be started are left out with the reason', () => {
    const { configurations } = read({ configurations: [
        { name: 'api', kind: 'fastapi' }, { name: 'worker', script: 'worker.py' }, { name: 'tests', kind: 'djangoTests' },
        { name: 'other', kind: 'compound', members: 'api' },
        { name: 'all', kind: 'compound', members: 'api\r\nworker\n\n api \nall\nother\ntests\nghost' },
    ] });
    const all = configurations.find((c) => c.name === 'all');
    assert.ok(KINDS.some(([k]) => k === 'compound'));
    assert.equal(all.kind, 'compound');
    const { members, skipped } = compoundMembers(all, (n) => configurations.find((c) => c.name === n) ?? null);
    assert.deepEqual(members.map((c) => c.name), ['api', 'worker']);   // in the order named, once each
    assert.deepEqual(skipped.map((k) => k.name), ['all', 'other', 'tests', 'ghost']);
    assert.match(skipped[0].why, /itself/);
    assert.match(skipped[1].why, /another compound/);
    assert.match(skipped[2].why, /Tests panel/);
    assert.match(skipped[3].why, /no such/);
    assert.deepEqual(compoundMembers(blank('x', 'compound'), () => null), { members: [], skipped: [] });
    assert.equal(blank('x', 'compound').members, '');
});

test('a Python Debug Server configuration starts a listener with its address, port and path mappings', () => {
    assert.ok(KINDS.some(([k]) => k === 'debugserver'));
    const fresh = blank('Server', 'debugserver');
    assert.equal(fresh.port, '5678');
    assert.deepEqual(startOf(fresh).debugServer, { host: '127.0.0.1', port: 5678, connectHost: '', mappings: [] });
    const { configurations } = read({ configurations: [
        { name: 'dbg', kind: 'debugserver', host: '*', port: '0', connectHost: ' build-box ', mappings: 'src=/app/src\n# a comment\nC:\\proj = /srv/proj\nbroken\n=/root\n' },
        { name: 'bad port', kind: 'debugserver', port: '99999' },
        { name: 'all', kind: 'compound', members: 'dbg' },
    ] });
    const dbg = configurations[0];
    assert.equal(dbg.kind, 'debugserver');
    assert.deepEqual(startOf(dbg).debugServer, {
        host: '*', port: 0, connectHost: 'build-box',
        mappings: [{ local: 'src', remote: '/app/src' }, { local: 'C:\\proj', remote: '/srv/proj' }, { local: '', remote: '/root' }],
    });
    assert.equal(startOf(configurations[1]).debugServer.port, 5678);
    // a debug server is not started with a compound: it needs the debugger
    const { members, skipped } = compoundMembers(configurations[2], (n) => configurations.find((c) => c.name === n) ?? null);
    assert.equal(members.length, 0);
    assert.match(skipped[0].why, /debugger/);
});

test('mapping lines and ports', () => {
    assert.deepEqual(parseMappings('a => /x\r\nb=/y'), [{ local: 'a', remote: '/x' }, { local: 'b', remote: '/y' }]);
    assert.equal(formatMappings(parseMappings('a=/x\nb=/y')), 'a=/x\nb=/y');
    assert.deepEqual(parseMappings(undefined), []);
    assert.equal(portOf('5679'), 5679);
    assert.equal(portOf(' 0 '), 0);
    assert.equal(portOf(''), 5678);
    assert.equal(portOf('x'), 5678);
    assert.equal(portOf('65536'), 5678);
    assert.equal(portOf('1.5'), 5678);
});

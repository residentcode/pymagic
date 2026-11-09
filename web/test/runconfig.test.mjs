import test from 'node:test';
import assert from 'node:assert/strict';
import { read, startOf, parseEnv, formatEnv, uniqueName, blank, splitShared, compoundMembers, KINDS, parseMappings, formatMappings, portOf, firstConfiguration, beforeSteps } from '../src/runconfig-model.js';

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

test('a Django project opened for the first time gets a Django server, with its manage.py and settings module', () => {
    const django = { found: true, manage: 'src\\manage.py', settingsModule: 'shop.settings' };
    const c = firstConfiguration(null, null, django);
    assert.equal(c.name, 'Django server');
    assert.equal(c.kind, 'django');
    assert.equal(c.script, 'src/manage.py');
    assert.equal(c.settings, 'shop.settings');
    assert.deepEqual(startOf(c).args.slice(0, 1), ['runserver']);
    // configurations kept already (in the project or shared), or no Django: nothing made
    assert.equal(firstConfiguration({ configurations: [] }, null, django), null);
    assert.equal(firstConfiguration(null, { configurations: [] }, django), null);
    assert.equal(firstConfiguration(null, null, { found: false }), null);
    assert.equal(firstConfiguration(null, null, null), null);
});

test('Before launch: configurations (after their own tasks), manage.py commands and the browser, in order; the ones that cannot run left out', () => {
    const list = [
        { ...blank('migrate', 'django'), script: 'site/manage.py', args: 'migrate' },
        { ...blank('seed'), script: 'seed.py', before: 'migrate' },
        { ...blank('loop A'), script: 'a.py', before: 'loop B' },
        { ...blank('loop B'), script: 'b.py', before: 'loop A' },
        { ...blank('all', 'compound'), members: 'seed' },
        { ...blank('tests', 'djangoTests') },
    ];
    const find = (name) => list.find((c) => c.name === name) ?? null;
    const server = { ...blank('Server', 'django'), script: 'site/manage.py', before: 'seed\nmanage.py collectstatic --noinput\n# a note\nbrowser http://127.0.0.1:8000/\nnone\nall\ntests\nbrowser localhost' };
    const { steps, skipped } = beforeSteps(server, find);
    assert.deepEqual(steps.map((s) => s.label), ['migrate', 'seed', 'manage.py collectstatic --noinput', 'browser http://127.0.0.1:8000/']);
    assert.deepEqual(steps[2], { manage: ['collectstatic', '--noinput'], file: 'site/manage.py', label: 'manage.py collectstatic --noinput' });
    assert.equal(steps[0].config.name, 'migrate');
    assert.deepEqual(skipped.map((k) => k.name), ['none', 'all', 'tests', 'browser localhost']);
    // the field as it was: one name
    assert.deepEqual(beforeSteps({ ...blank('x'), before: 'migrate' }, find).steps.map((s) => s.label), ['migrate']);
    // a script's manage.py command is the project's manage.py; a configuration that comes back to itself is left out
    assert.equal(beforeSteps({ ...blank('x'), before: 'manage.py check' }, find).steps[0].file, 'manage.py');
    const loop = beforeSteps(find('loop A'), find);
    assert.deepEqual(loop.steps.map((s) => s.label), ['loop B']);
    assert.equal(loop.skipped[0].why, 'it would run itself again');
    assert.deepEqual(beforeSteps(blank('nothing'), find), { steps: [], skipped: [] });
});

test('an HTTP request kept as a run configuration: the HTTP client is told its file, request and environment', () => {
    assert.ok(KINDS.some(([k]) => k === 'http'));
    const c = { ...blank('Get items', 'http'), script: 'api/items.http', request: 'list', environment: 'dev' };
    assert.deepEqual(startOf(c).http, { file: 'api/items.http', request: 'list', environment: 'dev' });
    const [kept] = read({ configurations: [c] }).configurations;
    assert.equal(kept.kind, 'http');
    assert.equal(kept.request, 'list');
    assert.equal(kept.environment, 'dev');
    // all the requests of the file when none is named; a compound sends it with the others; Before launch leaves it out
    assert.equal(startOf({ ...c, request: '' }).http.request, '');
    const server = { ...blank('Server', 'script'), script: 'app.py' };
    const find = (n) => [c, server].find((x) => x.name === n) ?? null;
    assert.deepEqual(compoundMembers({ name: 'All', kind: 'compound', members: 'Server\nGet items' }, find).members.map((m) => m.name), ['Server', 'Get items']);
    assert.equal(beforeSteps({ ...server, before: 'Get items' }, find).skipped[0].why, 'an HTTP request runs in the HTTP tab');
});

test('Node.js and npm configurations start Node, with nothing of Python', () => {
    const node = { ...blank('Server', 'node'), script: 'src/server.ts', args: '--port 3000', env: { DEBUG: 'app' }, interpreter: 'C:/py/python.exe', uv: true };
    assert.deepEqual(startOf(node), { file: 'src/server.ts', args: ['--port', '3000'], env: { DEBUG: 'app' }, cwd: '' });
    assert.deepEqual(startOf({ ...node, node: 'C:/nodejs/node.exe', justMyCode: false }), { file: 'src/server.ts', args: ['--port', '3000'], node: 'C:/nodejs/node.exe', env: { DEBUG: 'app' }, cwd: '', allCode: true });
    const npm = { ...blank('Dev', 'npm'), npmScript: 'dev', packageDir: 'web', args: '--host' };
    assert.deepEqual(startOf(npm), { npmScript: 'dev', packageDir: 'web', args: ['--host'], env: {}, cwd: '' });
    // no script named: npm start, as npm itself does
    assert.equal(startOf({ ...blank('S', 'npm') }).npmScript, 'start');
    // kept and read back as they are
    const back = read({ configurations: [{ ...npm }, { ...node }] }).configurations;
    assert.deepEqual(back.map((c) => [c.kind, c.npmScript, c.packageDir, c.script]), [['npm', 'dev', 'web', ''], ['node', '', '', 'src/server.ts']]);
    assert.ok(KINDS.some(([k]) => k === 'npm') && KINDS.some(([k]) => k === 'node'));
});

test('JavaScript Debug opens a page of the dev server in the browser, its web root a folder of the project', () => {
    assert.deepEqual(startOf({ ...blank('Page', 'browser'), url: 'http://localhost:3000/', packageDir: 'web' }), { browser: { url: 'http://localhost:3000/', webRoot: 'web' }, env: {}, cwd: '' });
    assert.deepEqual(startOf({ ...blank('Page', 'browser'), browserPath: 'C:/chrome.exe' }).browser, { url: 'http://localhost:5173/', browserPath: 'C:/chrome.exe' });
});

import test from 'node:test';
import assert from 'node:assert/strict';
import { read, startOf, parseEnv, formatEnv, uniqueName, blank } from '../src/runconfig-model.js';

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

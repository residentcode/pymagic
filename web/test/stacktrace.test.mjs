// Checks of web/src/stacktrace-model.js: `node web/test/stacktrace.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/stacktrace-model.js', import.meta.url), 'utf8');
const { projectFileFor, readTrace } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const files = ['main.py', 'app/models.py', 'app/views.py', 'tests/test_views.py', 'other/models.py'];

test('a path from another machine is the project file with the longest same ending', () => {
    assert.equal(projectFileFor('/home/ci/build/app/models.py', files), 'app/models.py');
    assert.equal(projectFileFor('C:\\work\\proj\\app\\views.py', files), 'app/views.py');
    assert.equal(projectFileFor('/srv/main.py', files), 'main.py');
    assert.equal(projectFileFor('/usr/lib/python3.13/json/decoder.py', files), null);
    assert.equal(projectFileFor('/x/models.py', files), null);   // app/ or other/: unresolved
});

test('traceback, pytest and "at" lines become places; the other lines stay text', () => {
    const trace = [
        'Traceback (most recent call last):',
        '  File "/home/ci/app/views.py", line 42, in index',
        '    return render(page)',
        '  File "/usr/lib/python3.13/json/decoder.py", line 355, in raw_decode',
        'ValueError: bad',
        'tests/test_views.py:17: AssertionError',
        'error at app/models.py:8',
    ].join('\r\n');
    const lines = readTrace(trace, files);
    assert.equal(lines.length, 7);
    assert.deepEqual(lines[1], { text: '  File "/home/ci/app/views.py", line 42, in index', path: '/home/ci/app/views.py', line: 42, rel: 'app/views.py' });
    assert.equal(lines[2].path, null);
    assert.deepEqual([lines[3].line, lines[3].rel], [355, null]);
    assert.deepEqual([lines[5].rel, lines[5].line], ['tests/test_views.py', 17]);
    assert.deepEqual([lines[6].rel, lines[6].line], ['app/models.py', 8]);
});

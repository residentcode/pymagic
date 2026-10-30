// Checks of web/src/tests-model.js: `node web/test/tests-model.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/tests-model.js', import.meta.url), 'utf8');
const { summarizeRun, statistics, junitXml, htmlReport, addRun, missingRunner } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const results = [
    { id: 'tests/test_a.py::test_ok', outcome: 'passed', duration: 0.5 },
    { id: 'tests/test_a.py::Cart::test_bad', outcome: 'failed', duration: 1.25, message: 'AssertionError: 2 != 3', traceback: 'Traceback <here> & "there"' },
    { id: 'tests/test_b.py::test_skip', outcome: 'skipped', duration: 0, message: 'later' },
    { id: 'tests/test_b.py::test_boom', outcome: 'error', duration: 0.25, message: 'KeyError' },
];
const run = { when: '2026-10-31T10:00:00Z', framework: 'pytest', results };

test('counts and statistics: the slowest tests and files first', () => {
    assert.deepEqual(summarizeRun(results), { total: 4, passed: 1, failed: 1, errors: 1, skipped: 1, duration: 2 });
    const s = statistics(results);
    assert.deepEqual(s.tests.map((t) => t.id.split('::').pop()), ['test_bad', 'test_ok', 'test_boom', 'test_skip']);
    assert.deepEqual(s.files, [{ file: 'tests/test_a.py', tests: 2, failed: 1, duration: 1.75 }, { file: 'tests/test_b.py', tests: 2, failed: 1, duration: 0.25 }]);
});

test('JUnit XML: a suite per file, the class in classname, failures escaped', () => {
    const x = junitXml(run);
    assert.ok(x.startsWith('<?xml version="1.0" encoding="UTF-8"?>\n<testsuites name="PyMagic" tests="4" failures="1" errors="1" skipped="1" time="2.000"'));
    assert.ok(x.includes('<testsuite name="tests/test_a.py" tests="2" failures="1" errors="0" skipped="0" time="1.750">'));
    assert.ok(x.includes('<testcase classname="tests.test_a.Cart" name="test_bad" time="1.250">'));
    assert.ok(x.includes('<failure message="AssertionError: 2 != 3">Traceback &lt;here&gt; &amp; &quot;there&quot;</failure>'));
    assert.ok(x.includes('<testcase classname="tests.test_a" name="test_ok" time="0.500"/>'));
    assert.ok(x.includes('<skipped message="later"/>'));
    assert.ok(x.includes('<error message="KeyError">KeyError</error>'));
});

test('the HTML report and the history', () => {
    const h = htmlReport(run);
    assert.ok(h.includes('4 tests: 1 passed, 1 failed, 1 errors, 1 skipped · 2.00 s'));
    assert.ok(h.includes('<td>tests/test_a.py::Cart::test_bad</td>'));
    let history = [];
    for (let i = 0; i < 25; i++)
        history = addRun(history, { when: String(i), results: [] });
    assert.equal(history.length, 20);
    assert.equal(history[0].when, '24');
    assert.deepEqual(addRun(null, { when: 'x' }), [{ when: 'x' }]);
});

test('the package a run is missing', () => {
    assert.equal(missingRunner('cannot run tests with pytest: No module named pytest'), 'pytest');
    assert.equal(missingRunner('pytest is not installed: running unittest tests only'), 'pytest');
    assert.equal(missingRunner("cannot run tests with trial: No module named 'twisted'"), 'twisted');
    assert.equal(missingRunner('Ran 3 tests'), null);
});

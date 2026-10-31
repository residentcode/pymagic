// Checks of web/src/requirements-model.js: `node web/test/requirements.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/requirements-model.js', import.meta.url), 'utf8');
const { kindOf, requirementLines, upperBound, problems } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

test('the files that are checked', () => {
    assert.equal(kindOf('requirements.txt'), 'requirements');
    assert.equal(kindOf('requirements-dev.txt'), 'requirements');
    assert.equal(kindOf('requirements/base.txt'), 'requirements');
    assert.equal(kindOf('sub/pyproject.toml'), 'pyproject');
    assert.equal(kindOf('notes.txt'), null);
});

test('requirements.txt lines, options and comments aside', () => {
    const e = requirementLines('# deps\nDjango>=5.0  # the web\n-r base.txt\n  requests[socks]==2.31\nhttps://x/y.zip\n', 'requirements');
    assert.deepEqual(e.map((x) => [x.name, x.line, x.start, x.end]), [['Django', 2, 1, 7], ['requests', 4, 3, 11]]);
});

test('pyproject: PEP 621 lists and Poetry tables', () => {
    const text = '[project]\nname = "shop"\ndependencies = [\n    "django>=5",\n    "rich",\n]\n\n[project.optional-dependencies]\ntest = ["pytest>=8"]\n\n' +
        '[tool.poetry.dependencies]\npython = "^3.11"\nhttpx = "^0.27"\nattrs = { version = "~23.1", optional = true }\n\n[tool.poetry.group.dev.dependencies]\nruff = "0.4.*"\n';
    const e = requirementLines(text, 'pyproject');
    assert.deepEqual(e.map((x) => `${x.name}@${x.line}:${x.start}${x.poetry ? ' ' + x.poetry : ''}`), ['django@4:6', 'rich@5:6', 'pytest@9:10', 'httpx@13:1 ^0.27', 'attrs@14:1 ~23.1', 'ruff@17:1 0.4.*']);
});

test('the upper end of a Poetry constraint', () => {
    assert.equal(upperBound('^1.2'), '2');
    assert.equal(upperBound('^0.27'), '0.28');
    assert.equal(upperBound('~23.1'), '23.2');
    assert.equal(upperBound('0.4.*'), '0.5');
    assert.equal(upperBound('>=1,<3'), '3');
    assert.equal(upperBound('2.0.1'), '2.0.1.0.1');
    assert.equal(upperBound('*'), null);
});

test('not installed, Poetry ranges the newest release is out of, stubs made for another version', () => {
    const e = [
        { name: 'Django', line: 1, start: 1, end: 7 },
        { name: 'pillow', line: 2, start: 1, end: 7 },
        { name: 'httpx', line: 3, start: 1, end: 6, poetry: '^0.27' },
        { name: 'rich', line: 4, start: 1, end: 5, poetry: '^13.0' },
        { name: 'types-requests', line: 5, start: 1, end: 15 },
    ];
    const installed = new Map([['django', '5.1'], ['httpx', '0.27.2'], ['rich', '13.9.4'], ['types-requests', '2.28.11'], ['requests', '2.32.3']]);
    const latest = new Map([['httpx', '0.28.1'], ['rich', '13.9.4']]);
    const p = problems(e, installed, latest).map((x) => `${x.line} ${x.code}: ${x.message}`);
    assert.deepEqual(p, [
        "2 requirement-not-installed: Package 'pillow' is not installed in the interpreter",
        '3 poetry-outdated: httpx 0.28.1 is out, and "^0.27" leaves it out',
        '5 stubs-mismatch: types-requests 2.28.11 is made for requests 2.28, and requests 2.32.3 is installed',
    ]);
    assert.deepEqual(problems(e, null), []);
});

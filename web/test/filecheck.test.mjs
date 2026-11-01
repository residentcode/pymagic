// Checks of web/src/filecheck-model.js: `node web/test/filecheck.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/filecheck-model.js', import.meta.url), 'utf8');
const m = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
const codes = (list) => list.map((p) => `${p.code}@${p.line}`);

test('which files are checked', () => {
    assert.equal(m.kindOf('pyproject.toml'), 'toml');
    assert.equal(m.kindOf('.github/workflows/ci.yml'), 'yaml');
    assert.equal(m.kindOf('docker-compose.yaml'), 'yaml');
    assert.equal(m.kindOf('.env'), 'env');
    assert.equal(m.kindOf('config/.env.local'), 'env');
    assert.equal(m.kindOf('prod.env'), 'env');
    assert.equal(m.kindOf('Dockerfile'), 'dockerfile');
    assert.equal(m.kindOf('docker/Dockerfile.dev'), 'dockerfile');
    assert.equal(m.kindOf('README.md'), 'markdown');
    assert.equal(m.kindOf('app.py'), 'python');
    assert.equal(m.kindOf('notes.txt'), null);
});

test('TOML: a good file has no problems, with multi-line strings and arrays', () => {
    const text = '# top\n[project]\nname = "shop" # the name\nversion = \'1.0\'\nreadme = """\nline [with] = brackets\n"""\ndependencies = [\n    "django>=5",\n    "rich",\n]\nwhen = 1979-05-27T07:32:00Z\nbig = 1_000\n\n[tool.ruff.lint]\nselect = ["E", "F"]\ninline = { a = 1, b = "x" }\n\n[[tool.items]]\nid = 1\n\n[[tool.items]]\nid = 2\n';
    assert.deepEqual(m.tomlProblems(text), []);
});

test('TOML: syntax problems', () => {
    assert.deepEqual(codes(m.tomlProblems('[project\nname = "x"\n')), ['toml-header@1']);
    assert.deepEqual(codes(m.tomlProblems('name "x"\n')), ['toml-no-equals@1']);
    assert.deepEqual(codes(m.tomlProblems('name =\n')), ['toml-no-value@1']);
    assert.deepEqual(codes(m.tomlProblems('flag = True\n')), ['toml-value@1']);
    assert.deepEqual(codes(m.tomlProblems('name = "open\nother = 1\n')), ['toml-string@1']);
    assert.deepEqual(codes(m.tomlProblems('list = [1, 2\nx = 1\n')), ['toml-brackets@1']);
    assert.deepEqual(codes(m.tomlProblems('a = 1\na = 2\n')), ['toml-duplicate-key@2']);
    assert.deepEqual(codes(m.tomlProblems('[t]\nx = 1\n[t]\ny = 2\n')), ['toml-duplicate-table@3']);
    // the same key in two tables is fine
    assert.deepEqual(m.tomlProblems('[a]\nx = 1\n[b]\nx = 2\n'), []);
});

test('YAML: a good file', () => {
    const text = 'name: CI\non:\n  push:\n    branches: [main, "dev"]\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n      - name: Run\n        run: |\n          echo "a: 1"\n          echo "a: 2"\n      - name: Again\n        run: pytest\n';
    assert.deepEqual(m.yamlProblems(text), []);
});

test('YAML: tabs, repeated keys, unclosed quotes and brackets', () => {
    assert.deepEqual(codes(m.yamlProblems('a:\n\tb: 1\n')), ['yaml-tab@2']);
    assert.deepEqual(codes(m.yamlProblems('a: 1\nb: 2\na: 3\n')), ['yaml-duplicate-key@3']);
    assert.deepEqual(codes(m.yamlProblems('x:\n  a: 1\ny:\n  a: 2\n')), []);
    assert.deepEqual(codes(m.yamlProblems('items:\n  - id: 1\n  - id: 2\n')), []);
    assert.deepEqual(codes(m.yamlProblems('name: "open\nother: 1\n')), ['yaml-quote@1']);
    assert.deepEqual(codes(m.yamlProblems('list: [1, 2\nother: 1\n')), ['yaml-brackets@1']);
    assert.deepEqual(codes(m.yamlProblems('a: 1\n---\na: 2\n')), []);
});

test('.env files', () => {
    assert.deepEqual(m.envProblems('# comment\nDEBUG=1\nexport KEY="a b"\nNAME=\n'), []);
    assert.deepEqual(codes(m.envProblems('DEBUG\n')), ['env-no-equals@1']);
    assert.deepEqual(codes(m.envProblems('1BAD=x\n')), ['env-name@1']);
    assert.deepEqual(codes(m.envProblems('A=1\nA=2\n')), ['env-duplicate@2']);
    assert.deepEqual(codes(m.envProblems('A="open\nB=1\n')), ['env-quote@1']);
    assert.deepEqual(codes(m.envProblems('A=two words\n')), ['env-space@1']);
    assert.deepEqual(m.envProblems('A="two\nlines"\nB=1\n'), []);
});

test('Dockerfile', () => {
    const good = 'ARG PY=3.12\nFROM python:3.12-slim AS build\nWORKDIR /app\nCOPY requirements.txt .\nRUN pip install \\\n    -r requirements.txt\nFROM build\nEXPOSE 8000\nCMD ["python", "app.py"]\n';
    assert.deepEqual(m.dockerfileProblems(good), []);
    assert.deepEqual(codes(m.dockerfileProblems('FROM python\nRUN x\n')), ['docker-latest@1']);
    assert.deepEqual(codes(m.dockerfileProblems('FROM python:latest\n')), ['docker-latest@1']);
    assert.deepEqual(codes(m.dockerfileProblems('RUN x\n')), ['docker-first@1']);
    assert.deepEqual(codes(m.dockerfileProblems('FROM a:1\nRUNN x\n')), ['docker-unknown@2']);
    assert.deepEqual(codes(m.dockerfileProblems('FROM a:1\nCOPY only\n')), ['docker-copy@2']);
    assert.deepEqual(codes(m.dockerfileProblems('FROM a:1\nEXPOSE http\n')), ['docker-port@2']);
    assert.deepEqual(codes(m.dockerfileProblems('FROM a:1\nCMD a\nCMD b\n')), ['docker-repeat@3']);
    assert.deepEqual(codes(m.dockerfileProblems('FROM a:1\nADD app.py /app/\n')), ['docker-add@2']);
    assert.deepEqual(m.dockerfileProblems('FROM a:1\nADD src.tar.gz /app/\n'), []);
    assert.deepEqual(codes(m.dockerfileProblems('FROM a:1\nWORKDIR app\n')), ['docker-workdir@2']);
    assert.deepEqual(codes(m.dockerfileProblems('FROM a:1\nRUN\n')), ['docker-args@2']);
});

test('Markdown links', () => {
    const files = new Set(['README.md', 'docs/guide.md', 'docs/img/a.png', 'src/app.py']);
    const text = '# Title\n\n## Second part\n\n[ok](docs/guide.md) [missing](docs/nope.md) [img](docs/img/a.png) [web](https://x.y/z)\n[anchor](#second-part) [bad anchor](#third) [dir](docs)\n`[code](nothing.md)`\n\n```\n[fenced](nothing.md)\n```\n[up](../outside.md)\n';
    const found = m.markdownProblems(text, { rel: 'README.md', files });
    assert.deepEqual(codes(found), ['md-link@5', 'md-anchor@6', 'md-link@12']);
    // relative to the file's folder
    assert.deepEqual(m.markdownProblems('[a](guide.md) [b](img/a.png) [c](../src/app.py)\n', { rel: 'docs/readme.md', files }), []);
    // without a file list only anchors are checked
    assert.deepEqual(codes(m.markdownProblems('# A\n[x](gone.md) [y](#b)\n', {})), ['md-anchor@2']);
    // repeated headings get -1
    assert.deepEqual(m.markdownProblems('# A\n# A\n[x](#a) [y](#a-1)\n', {}), []);
});

test('regular expression literals', () => {
    assert.deepEqual(m.regexLiterals('x = re.compile(r"\\d+-\\w")').map((l) => [l.pattern, l.col]), [['\\d+-\\w', 18]]);
    assert.deepEqual(m.regexLiterals("re.sub('a|b', '', s)").map((l) => l.pattern), ['a|b']);
    assert.deepEqual(m.regexLiterals('re.match(f"{x}+", s)'), []);
    assert.deepEqual(m.regexLiterals('re.match("a\\\\.b", s)'), []);   // escapes in a non-raw string: not the pattern's own text
    assert.deepEqual(m.regexLiterals('re.fullmatch(r"x", s)').map((l) => l.fullmatch), [true]);
});

test('regular expression problems', () => {
    const one = (p, o) => m.regexProblems(p, o).map((x) => `${x.code}@${x.index}`);
    assert.deepEqual(one('\\d+\\.\\w'), []);
    assert.deepEqual(one('a\\-b'), ['re-escape@1']);
    assert.deepEqual(one('\\/path'), ['re-escape@0']);
    assert.deepEqual(one('[\\.a-z]'), ['re-escape@1']);
    assert.deepEqual(one('[a\\-z]'), []);
    assert.deepEqual(one('[abca]'), ['re-class-repeat@4']);
    assert.deepEqual(one('[a-zA-Z0-9_]'), []);
    assert.deepEqual(one('[\\d\\d]'), []);
    assert.deepEqual(one('x()y'), ['re-empty-group@1']);
    assert.deepEqual(one('(?:)'), ['re-empty-group@0']);
    assert.deepEqual(one('a|b|a'), ['re-alternative@4']);
    assert.deepEqual(one('a|ab'), ['re-alternative@2']);
    assert.deepEqual(one('ab|a'), []);
    assert.deepEqual(one('a|ab', { anchored: true }), []);
    assert.deepEqual(one('^(a|ab)$'), []);
    assert.deepEqual(one('(a|b)(c|c)'), []);          // groups are not looked into for alternatives
});

test('regular expressions in Python source', () => {
    const text = 'import re\nA = re.compile(r"\\-x")\nB = re.search("[aa]", s)\nC = "\\-not a regex"\nD = re.sub(r"ok+", "", s)\n';
    assert.deepEqual(codes(m.pythonRegexProblems(text)), ['re-escape@2', 're-class-repeat@3']);
    const p = m.pythonRegexProblems(text)[0];
    assert.deepEqual([p.start, p.end], [18, 20]);
});

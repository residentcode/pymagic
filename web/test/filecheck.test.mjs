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

test('regular expression literals of every kind', () => {
    const pats = (text) => m.regexLiterals(text).map((l) => l.pattern);
    assert.deepEqual(pats('x = re.compile(r"\\d+-\\w")'), ['\\d+-\\w']);
    assert.deepEqual(pats("re.sub('a|b', '', s)"), ['a|b']);
    assert.deepEqual(m.regexLiterals('re.fullmatch(r"x", s)').map((l) => l.fullmatch), [true]);
    // an escaped (not raw) string is read as Python reads it
    assert.deepEqual(pats('re.match("a\\\\.b\\\\d", s)'), ['a\\.b\\d']);
    assert.deepEqual(pats('re.match("tab\\there", s)'), ['tab\there']);
    assert.deepEqual(pats('re.match("\\d+", s)'), ['\\d+']);                       // an escape Python keeps
    // an f-string: {{ is {, an expression is one character nothing is known of
    assert.deepEqual(pats('re.match(f"a{{2}}{x}+", s)'), ['a{2}\uE000+']);
    // triple-quoted, on several lines
    assert.deepEqual(pats('re.compile("""\n  a+  # one\n  b\n""", re.X)'), ['\n  a+  # one\n  b\n']);
    assert.equal(m.regexLiterals('re.compile("""a""", re.X)')[0].verbose, true);
    assert.equal(m.regexLiterals('re.compile("""a""")')[0].verbose, false);
    // a name set once at the top of the file
    assert.deepEqual(pats('PATTERN = r"a|b"\ndef f():\n    return re.compile(PATTERN)\n'), ['a|b']);
    assert.deepEqual(pats('P = r"a"\nP = r"b"\nre.compile(P)\n'), []);              // set twice: not known
    assert.deepEqual(pats('def f(x):\n    P = r"a"\n    return re.compile(P)\n'), []);   // not at the top level
    // joined to something not known: not the pattern
    assert.deepEqual(pats('re.match("a" + x, s)'), []);
    assert.deepEqual(pats('re.match("a%s" % x, s)'), []);
    assert.deepEqual(pats('re.match("{}b".format(x), s)'), []);
    assert.deepEqual(pats('re.match("{:>3}".format("a"), s)'), []);          // a format spec
    assert.deepEqual(pats('re.match(x, s)'), []);
    assert.deepEqual(pats('P = r"a"\nre.match(P(), s)\n'), []);               // a call of the name
});

test('a pattern joined at run time is read when every part is known', () => {
    const pats = (text) => m.regexLiterals(text).map((l) => l.pattern);
    // literals one after another, also over lines in the call's brackets
    assert.deepEqual(pats('re.match("a" "b", s)'), ['ab']);
    assert.deepEqual(pats('re.compile(r"\\d+"\n           r"[a-z]")'), ['\\d+[a-z]']);
    // + between literals and names set once to one
    assert.deepEqual(pats('SEP = r"[-_]"\nre.split(r"\\w+" + SEP + "x", s)\n'), ['\\w+[-_]x']);
    // .format() with known arguments: {} in order, {0} by place, {name} by name; {{ }} are braces
    assert.deepEqual(pats('re.match("{}-{}".format("a", r"\\d"), s)'), ['a-\\d']);
    assert.deepEqual(pats('re.match("{1}{0}".format("x", "y"), s)'), ['yx']);
    assert.deepEqual(pats('SEP = ","\nre.split("{s}\\\\s*".format(s=SEP), t)\n'), [',\\s*']);
    assert.deepEqual(pats('re.match("a{{2}}{}".format("b"), s)'), ['a{2}b']);
    // a problem of a part is put where that part is written
    const at = (text) => m.pythonRegexProblems(text).map((p) => `${p.code}@${p.line}:${p.start}-${p.end}`);
    assert.deepEqual(at('import re\nSEP = r"[aa]"\nre.compile(r"x" + SEP)\n'), ['re-class-repeat@2:11-12']);
    assert.deepEqual(at('re.compile("{}".format(r"\\-"))\n'), ['re-escape@1:26-28']);
});

test('problems are put where they are in the source, whatever the kind of string', () => {
    const at = (text) => m.pythonRegexProblems(text).map((p) => `${p.code}@${p.line}:${p.start}-${p.end}`);
    // escaped: "\\-" is two characters of source for \- (one escape)
    assert.deepEqual(at('import re\nA = re.compile("a\\\\-b")\n'), ['re-escape@2:18-21']);
    // f-string: the expression is not looked into, its place keeps the columns after it right
    assert.deepEqual(at('B = re.compile(rf"{name}\\-x")\n'), ['re-escape@1:25-27']);
    assert.deepEqual(at('B = re.compile(f"[{a}{a}][bb]")\n'), ['re-class-repeat@1:28-29']);
    // triple-quoted, several lines: the line of the problem
    assert.deepEqual(at('C = re.compile(r"""\n    ok\n    [xx]  # a comment with ( and \\-\n""", re.VERBOSE)\n'), ['re-class-repeat@3:7-8']);
    // a constant used twice is one problem, at its own line
    assert.deepEqual(at('P = r"\\-"\nre.compile(P)\nre.match(P, s)\n'), ['re-escape@1:7-9']);
});

test('a Markdown link to a heading of another file', () => {
    const files = new Set(['docs/guide.md', 'docs/other.txt']);
    const text = '[a](docs/guide.md#setup) [b](docs/guide.md#missing) [c](docs/other.txt#x) [d](docs/guide.md)\n';
    assert.deepEqual(m.markdownTargets(text, 'README.md'), ['docs/guide.md']);
    const anchors = new Map([['docs/guide.md', m.headingAnchors('# Guide\n\n## Setup\n')]]);
    assert.deepEqual(codes(m.markdownProblems(text, { rel: 'README.md', files, anchors })), ['md-anchor@1']);
    // without the other file's headings read, nothing is said
    assert.deepEqual(m.markdownProblems(text, { rel: 'README.md', files }), []);
    // relative to the file's folder
    assert.deepEqual(m.markdownTargets('[x](guide.md#a) [y](../README.md#b)\n', 'docs/index.md'), ['docs/guide.md', 'README.md']);
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

// Checks of web/src/injection-model.js: `node web/test/injection.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { scanPython, findInjections, maskHoles, unescapeBody, escapeBody, regexTokens, fragmentText, sourceRanges } from '../src/injection-model.js';

const bodies = (text) => findInjections(text).map((i) => [i.language, text.slice(i.start, i.end), i.why]);

test('strings and comments are found, a # inside a string is not a comment', () => {
    const { strings, comments } = scanPython('a = "x # y"  # real\nb = r\'\\d+\'\nc = """one\ntwo"""\n');
    assert.deepEqual(comments.map((c) => c.text), [' real']);
    const src = 'a = "x # y"  # real\nb = r\'\\d+\'\nc = """one\ntwo"""\n';
    assert.deepEqual(strings.map((s) => [s.prefix, s.quote, src.slice(s.bodyStart, s.bodyEnd)]), [['', '"', 'x # y'], ['r', "'", '\\d+'], ['', '"""', 'one\ntwo']]);
});

test('a language comment marks the string on its line or the line below', () => {
    assert.deepEqual(bodies('# language=sql\nq = "anything"\n'), [['sql', 'anything', 'comment']]);
    assert.deepEqual(bodies('q = "{}"  # language=JSON\n'), [['json', '{}', 'comment']]);
    assert.deepEqual(bodies('    # lang=html\n    page = """<b>x</b>"""\n'), [['html', '<b>x</b>', 'comment']]);
    assert.deepEqual(bodies('# language=RegExp\nr = "a+b"\n'), [['regex', 'a+b', 'comment']]);
    // a comment with something else, or two lines above: nothing
    assert.deepEqual(bodies('# language=sql\n\nq = "anything"\n'), []);
    assert.deepEqual(bodies('# language=cobol\nq = "x"\n'), []);
});

test('the call a string is the first argument of says what it is', () => {
    assert.deepEqual(bodies('cur.execute("SELECT 1")\n'), [['sql', 'SELECT 1', 'call']]);
    assert.deepEqual(bodies('db.execute(\n    "x y"\n)\n'), [['sql', 'x y', 'call']]);
    assert.deepEqual(bodies('pd.read_sql("anything", con)\n'), [['sql', 'anything', 'call']]);
    assert.deepEqual(bodies('session.execute(text("select 1"))\n'), [['sql', 'select 1', 'call']]);
    assert.deepEqual(bodies('p = re.compile(r"(\\d+)-x")\nre.sub("a|b", "", s)\n').map((b) => b[0]), ['regex', 'regex']);
    assert.deepEqual(bodies('d = json.loads(\'{"a": 1}\')\n'), [['json', '{"a": 1}', 'call']]);
    // the second argument is not the statement
    assert.deepEqual(bodies('log.info("select a from b where c", 1)\n'), [['sql', 'select a from b where c', 'keywords']].slice(0, 1));
    assert.deepEqual(bodies('cur.execute(q, "second")\n'), []);
});

test('SQL by its keywords: a verb and a second keyword, not any text with the word select', () => {
    assert.deepEqual(bodies('q = "SELECT id FROM item WHERE id = 1"\n').map((b) => b[0]), ['sql']);
    assert.deepEqual(bodies('q = """\n    INSERT INTO item (name) VALUES (?)\n"""\n').map((b) => b[0]), ['sql']);
    assert.deepEqual(bodies('q = "UPDATE item SET name = 1"\n').map((b) => b[0]), ['sql']);
    assert.deepEqual(bodies('q = "CREATE TABLE t (id int)"\n').map((b) => b[0]), ['sql']);
    assert.deepEqual(bodies('m = "Please select an item"\n'), []);
    assert.deepEqual(bodies('m = "select"\n'), []);
    assert.deepEqual(bodies('m = "update available"\n'), []);
    assert.deepEqual(bodies('m = b"select a from b"\n'), []);
});

test('f-string holes keep their places', () => {
    const { text, holes } = maskHoles('select {col} from {{t}} where id = {i + 1}', true);
    assert.equal(text, 'select xxxxx from {{t}} where id = xxxxxxx');
    assert.deepEqual(holes, [[7, 12], [38, 45].map((n) => n - 0)].slice(0, 1).concat([[holes[1][0], holes[1][1]]]));
    assert.equal(maskHoles('a {b}', false).text, 'a {b}');
});

test('escapes are read and put back', () => {
    assert.equal(unescapeBody('a\\nb\\t\\"c\\\\', false), 'a\nb\t"c\\');
    assert.equal(unescapeBody('\\d+', true), '\\d+');
    assert.equal(unescapeBody('\\x41', false), null);
    assert.equal(escapeBody('a\nb "q" \\', { raw: false, quote: '"', triple: false }), 'a\\nb \\"q\\" \\\\');
    assert.equal(escapeBody('a\nb', { raw: false, quote: '"""', triple: true }), 'a\nb');
    assert.equal(escapeBody('say "hi"', { raw: false, quote: '"""', triple: true }), 'say "hi\\"');
    assert.equal(escapeBody('a\\d', { raw: true, quote: "'", triple: false }), 'a\\d');
    assert.equal(escapeBody("it's", { raw: true, quote: "'", triple: false }), null);
    // what is read is what is put back
    for (const body of ['plain', 'a\\nb', 'q \\"x\\" \\\\']) assert.equal(escapeBody(unescapeBody(body, false), { raw: false, quote: '"', triple: false }), body);
});

test('the parts of a regular expression', () => {
    const kinds = (p) => regexTokens(p).map((t) => `${t.kind}:${p.slice(t.start, t.end)}`);
    assert.deepEqual(kinds('^(?P<n>\\d+)-[a-z]{2,3}?$'), ['anchor:^', 'group:(?P<n>', 'escape:\\d', 'quantifier:+', 'group:)', 'class:[a-z]', 'quantifier:{2,3}?', 'anchor:$']);
    assert.deepEqual(kinds('(?:a|b)*'), ['group:(?:', 'alternation:|', 'group:)', 'quantifier:*']);
    assert.deepEqual(kinds('[]a]\\]'), ['class:[]a]', 'escape:\\]']);
    assert.deepEqual(kinds('a{x}'), []);
});

test('a string built from parts is one fragment: # language= marks every part, and keywords are read in the whole', () => {
    const src = '# language=sql\nq = "SELECT " + cols + " FROM t WHERE id = " + str(n)\n';
    const found = findInjections(src);
    assert.deepEqual(found.map((i) => [i.language, src.slice(i.start, i.end), i.why]), [['sql', 'SELECT ', 'comment'], ['sql', ' FROM t WHERE id = ', 'comment']]);
    assert.equal(found[0].chain, found[1].chain);
    assert.deepEqual(found[0].chain.values, [true]);
    // what it stands for: the parts, an x for the value between them
    const { text, pieces } = fragmentText(src, found[0]);
    assert.equal(text, 'SELECT x FROM t WHERE id = ');
    // a token over the x is cut at the parts
    assert.deepEqual(sourceRanges(pieces, 0, 6).map(([a, b]) => src.slice(a, b)), ['SELECT']);
    assert.deepEqual(sourceRanges(pieces, 5, 12).map(([a, b]) => src.slice(a, b)), ['T ', ' FRO']);
    // without a comment: SELECT in one part and FROM in the next still say SQL; strings one after the other inside brackets are parts too
    const multi = 'q = ("SELECT a, b "\n     "FROM item "  # the table\n     "WHERE a = ?")\n';
    assert.deepEqual(findInjections(multi).map((i) => [i.language, multi.slice(i.start, i.end), i.why]),
        [['sql', 'SELECT a, b ', 'keywords'], ['sql', 'FROM item ', 'keywords'], ['sql', 'WHERE a = ?', 'keywords']]);
    // the comment over a statement over lines
    assert.equal(findInjections('# language=json\nd = (\n    "{" +\n    "}"\n)\n').length, 2);
    // separate arguments, items of a list and lines of their own are not parts of one string
    assert.deepEqual(findInjections('f("SELECT a", "FROM b")\n'), []);
    assert.deepEqual(findInjections('xs = ["SELECT a",\n      "FROM b"]\n'), []);
    assert.deepEqual(findInjections('a = "SELECT a"\n"FROM b"\n'), []);
    assert.deepEqual(findInjections('m = "Hello " + name + "!"\n'), []);
});

test('JavaScript strings: // language= and /* language= */, the tag of a template, the calls, SQL by its keywords', () => {
    const js = (src) => findInjections(src, 'javascript').map((i) => [i.language, src.slice(i.start, i.end), i.why]);
    assert.deepEqual(js('// language=SQL\nconst q = "anything";\n'), [['sql', 'anything', 'comment']]);
    assert.deepEqual(js('const page = /* language=html */ \'<b>x</b>\';\n'), [['html', '<b>x</b>', 'comment']]);
    assert.deepEqual(js('const t = html`<p>${name}</p>`;\nconst s = css`a { color: red }`;\n'), [['html', '<p>${name}</p>', 'tag'], ['css', 'a { color: red }', 'tag']]);
    assert.deepEqual(js('db.query("UPDATE x SET y = 1");\nconst r = new RegExp("a+b");\nJSON.parse(\'{"a": 1}\');\n').map((b) => b[0]), ['sql', 'regex', 'json']);
    assert.deepEqual(js('const q = `SELECT id FROM item WHERE id = ${id}`;\n'), [['sql', 'SELECT id FROM item WHERE id = ${id}', 'keywords']]);
    // a string built from parts, and a quote in a comment or a template's value that does not end anything
    const parts = '// language=sql\nconst q = "SELECT " + cols +\n  " FROM t";\n';
    assert.deepEqual(js(parts).map((b) => b[1]), ['SELECT ', ' FROM t']);
    assert.deepEqual(js("// it's\nconst a = `x ${f('}')} y`; // language=html\n"), [['html', "x ${f('}')} y", 'comment']]);
    assert.deepEqual(js('const m = "Please select an item";\n'), []);
    // the holes of a template keep their places
    assert.equal(maskHoles('a ${b + {c: 1}.c} d', false, true).text, 'a xxxxxxxxxxxxxxx d');
});

test('the escapes of a JavaScript template are read and put back', () => {
    const t = { raw: false, quote: '`', triple: true, template: true };
    assert.equal(unescapeBody('a \\` \\${x} \\n', false, true), 'a ` ${x} \n');
    assert.equal(unescapeBody('\\$', false), null);
    assert.equal(escapeBody('a ` ${x} \\ b\nc', t), 'a \\` \\${x} \\\\ b\nc');
    for (const body of ['plain', 'a \\` b', 'cost \\${x}']) assert.equal(escapeBody(unescapeBody(body, false, true), t), body);
});

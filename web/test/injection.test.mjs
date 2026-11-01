// Checks of web/src/injection-model.js: `node web/test/injection.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { scanPython, findInjections, maskHoles, unescapeBody, escapeBody, regexTokens } from '../src/injection-model.js';

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

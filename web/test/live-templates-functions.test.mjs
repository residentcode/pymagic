// More of what a live template's variable can be (web/src/live-templates-model.js): a place following another through a change of its
// words' form, a text cut or a regular expression's replacement, and complete() for completion at the place.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { toSnippet, preview, completeStops, EXPRESSIONS, contextAt } from '../src/live-templates-model.js';

/** What the editor writes for a snippet's ${n/search/replacement/flags} when `typed` is in place n: the search and replacement read as it reads them. */
function follows(snippet, typed) {
    // (the replacement goes up to the last / : it may hold the editor's ${2:/downcase})
    const m = /\$\{(\d+)\/((?:[^/\\]|\\.)*)\/([\s\S]*)\/(\w*)\}$/.exec(snippet);
    assert.ok(m, snippet);
    const search = new RegExp(m[2].replace(/\\\//g, '/'), m[4]);
    const format = m[3].replace(/\\\//g, '/');
    return typed.replace(search, (...g) => format.replace(/\$\{(\d+):\/(upcase|downcase)\}|\$\{(\d+):\+([^}]*)\}|\$\{(\d+)\}|\$(\d+)/g, (all, a, how, b, text, c, d) => {
        if (a) return how === 'upcase' ? (g[a] ?? '').toUpperCase() : (g[a] ?? '').toLowerCase();
        if (b) return g[b] ? text : '';
        return g[c ?? d] ?? '';
    }));
}
const one = (expr) => toSnippet('$a$ = $b$', { b: { expr } });

test('a place following another in another form of its words', () => {
    for (const [expr, typed, written] of [
        ['snakeCase(a)', 'userName', 'user_name'], ['snakeCase(a)', 'UserName', 'user_name'], ['snakeCase(a)', 'user name-here', 'user_name_here'],
        ['capitalizeAndUnderscore(a)', 'userName', 'USER_NAME'], ['capitalizeAndUnderscore(a)', 'max size', 'MAX_SIZE'],
        ['lowercaseAndDash(a)', 'UserName', 'user-name'], ['lowercaseAndDash(a)', 'user_name', 'user-name'],
        ['spaceSeparated(a)', 'userName', 'user Name'], ['spaceSeparated(a)', 'user_name', 'user name'],
        ['underscoresToCamelCase(a)', 'user_name_here', 'userNameHere'], ['underscoresToSpaces(a)', 'a_b_c', 'a b c'], ['spacesToUnderscores(a)', 'a b c', 'a_b_c'],
        ['decapitalize(a)', 'UserName', 'userName'], ['firstWord(a)', '  shop of things', 'shop'],
        ['substringBefore(a, ".")', 'views.py', 'views'], ['substringBefore(a, ".")', 'views', 'views'], ['substringBefore(a, "/x")', 'a/b/x/c', 'a/b'],
        ['regularExpression(a, "[aeiou]", "")', 'template', 'tmplt'], ['regularExpression(a, "(\\w+)@(\\w+)", "$2 at $1")', 'me@home', 'home at me'],
        ['regularExpression(a, "/", ".")', 'a/b/c', 'a.b.c'],
    ]) {
        const snippet = one(expr);
        assert.match(snippet, /^\$\{1:a\} = \$\{1\//, expr);
        assert.equal(follows(snippet, typed), written, `${expr} of ${typed}`);
    }
    // (the editor's own words for a change of case are written as they are: ${2:/downcase})
    assert.ok(one('snakeCase(a)').includes('${2:/downcase}') && one('capitalizeAndUnderscore(a)').includes('${1:/upcase}'));
    // the page shows the same with nothing typed
    assert.equal(preview('$a$ = $b$', { a: { def: 'userName' }, b: { expr: 'snakeCase(a)' } }), 'userName = user_name');
    assert.equal(preview('$a$ = $b$', { a: { def: 'views.py' }, b: { expr: 'substringBefore(a, ".")' } }), 'views.py = views');
    assert.equal(preview('$a$ = $b$', { a: { def: 'a-b' }, b: { expr: 'regularExpression(a, "-", "+")' } }), 'a-b = a+b');
});

test('what is not one of them is a plain place', () => {
    for (const expr of ['snakeCase()', 'snakeCase("x")', 'substringBefore(a)', 'substringBefore(a, b)', 'regularExpression(a, "(", "x")', 'regularExpression(a, "x")', 'nothing(a)', 'complete(a)', 'snakeCase(a', 'snakeCase(a,)'])
        assert.equal(one(expr), '${1:a} = ${2:b}', expr);
    // a place cannot follow itself
    assert.equal(toSnippet('$a$', { a: { expr: 'snakeCase(a)' } }), '${1:a}');
});

test('complete(): the place is empty, and its stop is told', () => {
    const body = 'from $module$ import $name$\n$name$($arg$)$END$';
    const vars = { module: { expr: 'complete()' }, arg: { expr: 'completeSmart()', def: 'self' } };
    assert.equal(toSnippet(body, vars), 'from ${1} import ${2:name}\n${2:name}(${3:self})$0');
    assert.deepEqual(completeStops(body, vars), [1, 3]);
    assert.deepEqual(completeStops(body, {}), []);
    assert.deepEqual(completeStops('for ${1:x} in $2', vars), []);
    assert.equal(preview(body, vars), 'from  import name\nname(self)');
});

test('every form the page offers is one that is read', () => {
    for (const [form] of EXPRESSIONS) {
        const expr = form.replace('name', 'a');
        const snippet = toSnippet('$a$ $b$', { b: { expr } });
        assert.notEqual(snippet, '${1:a} ${2:b}', form);
    }
});

test('user(), the class and function of the place, and concat()', () => {
    const at = { user: 'pat', className: 'Shop', functionName: 'total' };
    const vars = { a: { expr: 'user()' }, b: { expr: 'pyClassName()' }, c: { expr: 'pyFunctionName()', skip: true }, d: { expr: 'concat(date(), " by ", user())' } };
    assert.equal(toSnippet('$a$ $b$ $c$ $d$', vars, at), '${1:pat} ${2:Shop} total ${3:${CURRENT_YEAR}-${CURRENT_MONTH}-${CURRENT_DATE} by pat}');
    assert.equal(toSnippet('$a$', { a: { expr: 'jsMethodName()' } }, { functionName: 'add' }), '${1:add}');
    assert.equal(toSnippet('$a$', { a: { expr: 'className()' } }, { className: 'A$b' }), '${1:A\\$b}');
    assert.equal(preview('$a$ / $b$', { a: { expr: 'concat("by ", user())' }, b: { expr: 'methodName()' } }), 'by user / method');
    // a place's name in concat, or arguments where none are taken: not read
    for (const expr of ['concat(a, "x")', 'concat()', 'user(a)', 'concat(nothing())'])
        assert.equal(toSnippet('$a$', { a: { expr } }, at), '${1:a}', expr);
});

test('the class and the function a place is in', () => {
    const lines = (...l) => l;
    const py = lines('class Shop:', '    def total(self, items):', '        x = 1', '        # a note', '', 'def free():', '    pass', '');
    assert.deepEqual(contextAt(py, 2, 'python'), { className: 'Shop', functionName: 'total' });
    assert.deepEqual(contextAt(py, 6, 'python'), { className: '', functionName: 'free' });
    assert.deepEqual(contextAt(py, 7, 'python', 1), { className: '', functionName: '' });
    assert.deepEqual(contextAt(lines('class A:', '    x = 1'), 1, 'python'), { className: 'A', functionName: '' });
    const js = lines('class Cart {', '  add(item) {', '    if (x) {', '      y("}");', '    }', '    ', '  }', '}', 'const run = async () => {', '  go();', '};', 'function plain(a) {', '  b(); // {', '}');
    assert.deepEqual(contextAt(js, 5, 'javascript', 5), { className: 'Cart', functionName: 'add' });
    assert.deepEqual(contextAt(js, 9, 'typescript', 3), { className: '', functionName: 'run' });
    assert.deepEqual(contextAt(js, 12, 'javascript', 3), { className: '', functionName: 'plain' });
    assert.deepEqual(contextAt(js, 8, 'javascript', 1), { className: '', functionName: '' });
});

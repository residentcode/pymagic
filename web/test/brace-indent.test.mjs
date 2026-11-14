// Indentation told and fixed in JavaScript, TypeScript, CSS and JSON (web/src/brace-indent-model.js).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { braceIndentProblems, fixBraceIndentation, tokenize } from '../src/brace-indent-model.js';

const lines = (text, kind) => braceIndentProblems(text, kind).map((p) => `${p.line}: ${p.actual} -> ${p.expected}`);

test('an Express route whose inside and } are out of place; Fix indentation moves only those', () => {
    const text = "const express = require('express');\nconst app = express();\n\n  app.get('/', (req, res) => {\n  res.send('hi');\n});\n\napp.listen(3000);\n";
    assert.deepEqual(lines(text, 'js'), ['4: 2 -> 0', '5: 2 -> 4', '6: 0 -> 2']);
    const fixed = fixBraceIndentation(text, 'js');
    // (the file's unit, 2 spaces, kept while its lines move)
    assert.equal(fixed, text.replace('\n  app.get', '\napp.get'));
    assert.deepEqual(lines(fixed, 'js'), []);
});

test("the file's own unit: 2 spaces, 4 spaces, tabs", () => {
    assert.deepEqual(lines('function f() {\n  if (a) {\n     b();\n  }\n}\n', 'js'), ['3: 5 -> 4']);
    assert.deepEqual(lines('function f() {\n    if (a) {\n        b();\n    }\n}\n', 'js'), []);
    const tabs = 'function f() {\n\tif (a) {\n\t\t\tb();\n\t}\n}\n';
    assert.equal(braceIndentProblems(tabs, 'js')[0].message.startsWith('Indentation is 3 tabs, expected 2 tabs'), true);
    assert.equal(fixBraceIndentation(tabs, 'js'), 'function f() {\n\tif (a) {\n\t\tb();\n\t}\n}\n');
});

test('what code has on purpose is not told', () => {
    const ok = [
        "import { a,\n    b } from './x';",
        'class A extends B\n    implements C {\n    x = 1;\n}',
        'if (a &&\n    b) {\n    c();\n}',
        'promise\n    .then(() => {\n        go();\n    })\n    .catch(() => {\n        stop();\n    });',
        'const s = `\n  ${a ? `{` : "}"}\n`;\nf();',
        "const r = /[{}]/g;\nconst x = a / b / c;",
        'switch (a) {\n    case 1:\n        b();\n        break;\n    default:\n        c();\n}',
        "const o = {\n    a: 1,\n    b: {\n        c: 2,\n    },\n};",
        'const v = a\n    ? {\n          b: 1,\n      }\n    : undefined;',
        "call(a, {\n        b: 1,\n});",
        '(function (global, factory) {\nfactory(global);\n})(this, function () {\nreturn 1;\n});',
        'get x() { try {\n    return 1;\n}\ncatch {\n    return 2;\n} }',
        'if (a)\n    for (const x of xs) {\n        go(x);\n    }',
        'type A =\n  | {\n      b: 1;\n    }\n  | C;',
    ];
    for (const t of ok) assert.deepEqual(lines(t, 'js'), [], t);
    assert.deepEqual(lines('function f() {\n  return <div>\n  x</div>;\n}', 'js'), []);   // JSX: not told
    assert.deepEqual(lines('function f() {\n  if (a) {\n', 'js'), []);                    // being typed: not told
});

test('CSS, SCSS and JSON', () => {
    assert.deepEqual(lines('a {\n    color: red;\n      margin: 0;\n}\n', 'css'), ['3: 6 -> 4']);
    assert.deepEqual(lines('.a,\n.b > .c {\n    color: red;\n}\n@media (x) {\n    a {\n        b: c;\n    }\n}\n', 'css'), []);
    assert.deepEqual(lines('a {\n  -webkit-clip-path: x;\n          clip-path: x;\n  background: url(http://a/b.png);\n}\n', 'css'), []);
    assert.deepEqual(lines('.a {\n  // x {\n  &:hover {\n      b: c;\n  }\n}\n', 'scss'), ['4: 6 -> 4']);
    assert.deepEqual(lines('{\n  "a": [\n    1,\n      2\n  ],\n  "b": {}\n}\n', 'json'), ['4: 6 -> 4']);
    const table = '[\n' + Array.from({ length: 30 }, (_, i) => `[${i}, "x"],`).join('\n') + '\n[0]\n]\n';
    assert.deepEqual(lines(table, 'json'), []);   // a data table laid out at the left edge: its own layout
});

test('the tokens: strings, comments, template literals and regexes are one token each', () => {
    const kinds = tokenize('a = "{" + `x${ {b: 1}.b }` /* { */ // }\n;/}/.test(s)', 'js').map((t) => t.type);
    assert.deepEqual(kinds, ['word', '=', 'string', '+', 'string', 'comment', 'comment', ';', 'regex', 'word', '(', 'word', ')']);
});

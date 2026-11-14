// Typing's indentation in every kind of file in the built PyMagic.exe (indent-rules.js), typed key by key at a person's pace: an opening, Enter
// (the line goes in), a line, Enter and the closing (it comes back out to its opening's indentation), in JavaScript (.js .mjs .cjs .jsx, an
// Express route), TypeScript (.ts .tsx), CSS, SCSS, Less, JSON, YAML, HTML, Django's and Jinja's templates, EJS and Eta, Handlebars, Mustache and
// Hogan.js, Nunjucks, Twig, Liquid, Vash, Blade, Vue, Svelte, Astro, Python and PHP; with no stray %} }} or } %> left behind. Then the
// indentation marks (brace-indent.js) of a JavaScript, a CSS and a JSON file, and Alt+Enter's Fix indentation.
// Run: node desktop/e2e/indenttyping.check.mjs
import assert from 'node:assert/strict';
import { launch, project, sleep } from './app.mjs';

const CASES = [
    // file, opening, line, closing (null: none typed), what the file is then
    ['a.js', 'if (a) {', 'b();', '}', 'if (a) {\n    b();\n}'],
    ['a.mjs', 'export function f() {', 'return 1;', '}', 'export function f() {\n    return 1;\n}'],
    ['a.cjs', 'module.exports = {', 'a: 1,', '};', 'module.exports = {\n    a: 1,\n}'],
    ['app.js', "app.get('/', (req, res) => {", "res.send('x');", '});', "app.get('/', (req, res) => {\n    res.send('x');\n})"],
    ['b.jsx', '<div>', '<span>x</span>', '</div>', '<div>\n    <span>x</span>\n</div>'],
    ['a.ts', 'class A {', 'x = 1;', '}', 'class A {\n    x = 1;\n}'],
    ['a.tsx', 'function A() {', 'return <p/>;', '}', 'function A() {\n    return <p/>;\n}'],
    ['a.css', 'a {', 'color: red;', '}', 'a {\n    color: red;\n}'],
    ['a.scss', '.a {', 'color: red;', '}', '.a {\n    color: red;\n}'],
    ['a.less', '.a {', 'color: red;', '}', '.a {\n    color: red;\n}'],
    ['a.json', '{', '"a": 1', '}', '{\n    "a": 1\n}'],
    ['a.yaml', 'a:', 'b: 1', null, 'a:\n    b: 1'],
    ['a.html', '<div>', '<p>x</p>', '</div>', '<div>\n    <p>x</p>\n</div>'],
    ['templates/t.html', '{% if a %}', '<p>x</p>', '{% endif %}', '{% if a %}\n    <p>x</p>\n{% endif %}'],
    ['t.jinja', '{% for x in xs %}', '<li>{{ x }}</li>', '{% endfor %}', '{% for x in xs %}\n    <li>{{ x }}</li>\n{% endfor %}'],
    ['v.ejs', '<% if (a) { %>', '<p>x</p>', '<% } %>', '<% if (a) { %>\n    <p>x</p>\n<% } %>'],
    ['v.eta', '<% if (a) { %>', '<p>x</p>', '<% } %>', '<% if (a) { %>\n    <p>x</p>\n<% } %>'],
    ['v.hbs', '{{#if a}}', '<p>x</p>', '{{/if}}', '{{#if a}}\n    <p>x</p>\n{{/if}}'],
    ['v.mustache', '{{#a}}', '<p>x</p>', '{{/a}}', '{{#a}}\n    <p>x</p>\n{{/a}}'],
    ['v.hjs', '{{#a}}', '<p>x</p>', '{{/a}}', '{{#a}}\n    <p>x</p>\n{{/a}}'],
    ['v.njk', '{% if a %}', '<p>x</p>', '{% endif %}', '{% if a %}\n    <p>x</p>\n{% endif %}'],
    ['v.twig', '{% if a %}', '<p>x</p>', '{% endif %}', '{% if a %}\n    <p>x</p>\n{% endif %}'],
    ['v.liquid', '{% if a %}', '<p>x</p>', '{% endif %}', '{% if a %}\n    <p>x</p>\n{% endif %}'],
    ['v.vash', '@if (a) {', '<p>x</p>', '}', '@if (a) {\n    <p>x</p>\n}'],
    ['resources/views/v.blade.php', '@if ($a)', '<p>x</p>', '@endif', '@if ($a)\n    <p>x</p>\n@endif'],
    ['C.vue', '<template>', '<div>x</div>', '</template>', '<template>\n    <div>x</div>\n</template>'],
    ['C.svelte', '{#if a}', '<p>x</p>', '{/if}', '{#if a}\n    <p>x</p>\n{/if}'],
    ['C.astro', '<div>', '<p>x</p>', '</div>', '<div>\n    <p>x</p>\n</div>'],
    ['b.py', 'if a:', 'b = 1', null, 'if a:\n    b = 1'],
    ['b.php', '<?php if ($a) {', 'echo 1;', '}', '<?php if ($a) {\n    echo 1;\n}'],
];

const files = { 'main.py': 'x = 1\n', 'manage.py': 'import django\n', 'bad.js': '', 'bad.css': '', 'bad.json': '' };
for (const [f] of CASES) files[f] = '';
const app = await launch(project(files));
const { page } = app;
const text = () => page.evaluate('window.pymagic.editor.getModel().getValue().replace(/\\r\\n/g, "\\n")');
async function typeText(t) {
    for (const ch of t) {
        await page.evaluate(`window.pymagic.editor.trigger('keyboard', 'hideSuggestWidget', {}); window.pymagic.editor.trigger('keyboard', 'type', { text: ${JSON.stringify(ch)} })`);
        await sleep(120);
    }
}
async function open(f) {
    await page.evaluate(`window.pymagic.openFile('${f}')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('${f.split('/').pop()}')`, 20000, f);
    await sleep(500);
}
try {
    const wrong = [];
    for (const [f, opening, body, closing, want] of CASES) {
        await open(f);
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.getModel().setValue(''); ed.setPosition({ lineNumber: 1, column: 1 }); ed.focus(); })()`);
        await sleep(300);
        await typeText(opening);
        await typeText('\n');
        await typeText(body);
        if (closing) {
            // the closing already put in when the opening was typed is left as it is; else it is typed on a line of its own
            const rest = await page.evaluate('(() => { const ed = window.pymagic.editor, m = ed.getModel(), p = ed.getPosition(); return m.getValueInRange({ startLineNumber: p.lineNumber, startColumn: p.column, endLineNumber: m.getLineCount(), endColumn: m.getLineMaxColumn(m.getLineCount()) }); })()');
            if (!rest.trim()) { await typeText('\n'); await typeText(closing); }
        }
        await sleep(300);
        const got = await text();
        if (got !== want) wrong.push(`${f}: ${JSON.stringify(got)} (expected ${JSON.stringify(want)})`);
    }
    console.log(`typed in ${CASES.length} kinds of file`);
    assert.deepEqual(wrong, []);

    // the marks: an Express route shifted, a CSS declaration and a JSON item out of place; Alt+Enter's fix
    const marks = () => page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ owner: 'indent', resource: window.pymagic.editor.getModel().uri }).map((m) => m.startLineNumber + ' ' + m.message)`);
    async function settle(want, ms = 10000) {
        const end = Date.now() + ms;
        let found = [];
        while (Date.now() < end) { found = await marks(); if (want(found)) return found; await sleep(250); }
        assert.fail(`the marks stayed ${JSON.stringify(found)}`);
    }
    async function fix(line) {
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: ${line}, column: 1 }); ed.focus(); ed.trigger('keyboard', 'editor.action.quickFix', {}); })()`);
        await page.waitFor(`[...document.querySelectorAll('.action-widget .monaco-list-row')].some((e) => e.textContent.includes('Fix indentation of the file'))`, 10000, 'the quick fix');
        await page.key(13, 'Enter');
    }
    const MARKED = [
        ['bad.js', "const app = require('express')();\n\n  app.get('/', (req, res) => {\n  res.send('hi');\n});\n", ['3', '4', '5'], "const app = require('express')();\n\napp.get('/', (req, res) => {\n  res.send('hi');\n});\n"],
        ['bad.css', 'a {\n  color: red;\n    margin: 0;\n}\n', ['3'], 'a {\n  color: red;\n  margin: 0;\n}\n'],
        ['bad.json', '{\n  "a": 1,\n      "b": 2\n}\n', ['3'], '{\n  "a": 1,\n  "b": 2\n}\n'],
    ];
    for (const [f, bad, lines, good] of MARKED) {
        await open(f);
        await page.evaluate(`window.pymagic.editor.getModel().setValue(${JSON.stringify(bad)})`);
        const told = await settle((m) => m.length === lines.length);
        console.log(f, told);
        assert.deepEqual(told.map((m) => m.split(' ')[0]), lines);
        await fix(+lines[0]);
        await page.waitFor(`window.pymagic.editor.getModel().getValue().replace(/\\r\\n/g, '\\n') === ${JSON.stringify(good)}`, 10000, `${f} fixed`);
        assert.deepEqual(await settle((m) => m.length === 0), []);
    }
    console.log('OK');
} finally { await app.close(); }

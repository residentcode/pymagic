// PHP's syntax errors while typing in the built PyMagic.exe (php -l on the editor's text, PhpLint.cs): with Settings > PHP naming a php.exe (the
// test's, PYMAGIC_PHP), a missing semicolon typed into a file is marked as an error at the line PHP names, gone when it is fixed, and a broken
// function and an unclosed brace are marked too, with nothing saved; without any PHP the bundled parser marks Route::...{,22 at its column.
// Run: PYMAGIC_PHP=C:\path\php.exe node desktop/e2e/phplint.check.mjs
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const php = process.env.PYMAGIC_PHP;
assert.ok(php, 'PYMAGIC_PHP names a php.exe');
const files = { 'main.py': 'x = 1\n', 'index.php': '<?php\n$a = 1;\necho $a;\n' };

async function errorsOf(page, want, ms = 15000) {
    const end = Date.now() + ms;
    let found = [];
    while (Date.now() < end) {
        found = await page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ owner: 'php-lint', resource: window.pymagic.editor.getModel().uri }).map((m) => ({ line: m.startLineNumber, severity: m.severity, source: m.source, message: m.message }))`);
        if (want(found)) return found;
        await sleep(250);
    }
    assert.fail('the markers stayed ' + JSON.stringify(found));
}
async function typeAt(page, line, column, text) {
    await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: ${line}, column: ${column} }); ed.focus(); })()`);
    for (const ch of text) { await page.evaluate(`window.pymagic.editor.trigger('keyboard', 'hideSuggestWidget', {}); window.pymagic.editor.trigger('keyboard', 'type', { text: ${JSON.stringify(ch)} })`); await sleep(30); }
}

// with PHP
let app = await launch(project(files), { phpPath: php });
try {
    const { page } = app;
    await page.evaluate(`window.pymagic.openFile('index.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('index.php')`, 20000, 'index.php');
    assert.deepEqual(await errorsOf(page, (f) => f.length === 0), []);
    // line 2's semicolon taken away, as a person deletes it: PHP sees it at line 3
    await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 2, column: 8 }); ed.trigger('keyboard', 'deleteLeft', {}); })()`);
    const missing = await errorsOf(page, (f) => f.length === 1);
    console.log('missing ;', missing);
    assert.equal(missing[0].line, 3);
    assert.equal(missing[0].severity, 8);
    assert.equal(missing[0].source, 'PHP');
    assert.match(missing[0].message, /^Syntax error: unexpected token "echo"/);
    await typeAt(page, 2, 7, ';');
    assert.deepEqual(await errorsOf(page, (f) => f.length === 0), []);
    // a function typed with its parenthesis left open
    await typeAt(page, 4, 1, 'function f( {\n');
    const fn = await errorsOf(page, (f) => f.length === 1);
    console.log('function f( {', fn);
    assert.equal(fn[0].line, 4);
    await page.evaluate(`window.pymagic.editor.getModel().setValue('<?php\\nif ($a) {\\n  echo 1;\\n')`);
    const brace = await errorsOf(page, (f) => f.length === 1 && /Unclosed/.test(f[0].message));
    console.log('unclosed {', brace);
    assert.equal(brace[0].message, "Unclosed '{' on line 2");
    // checked as typed: nothing was saved
    assert.equal(readFileSync(join(app.root, 'index.php'), 'utf8'), files['index.php']);
} finally { await app.close(); }

// without any PHP: the bundled parser (php-syntax-model.js) marks the error instead, at its column, and nothing is said
const env = process.env.PYMAGIC_PHP;
delete process.env.PYMAGIC_PHP;
app = await launch(project(files), { phpPath: 'C:\\no\\such\\php.exe' });
try {
    const { page } = app;
    await page.evaluate(`window.pymagic.openFile('index.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('index.php')`, 20000, 'index.php');
    assert.deepEqual(await errorsOf(page, (f) => f.length === 0), []);
    await page.evaluate(`window.pymagic.editor.getModel().setValue("<?php\\nRoute::prefix('admin')->group(function () {\\n    Route::get('/', [A::class, 'index']);\\n});\\n")`);
    await typeAt(page, 2, 44, ',22');
    const found = await errorsOf(page, (f) => f.length === 1);
    console.log('without PHP:', found);
    assert.deepEqual(found, [{ line: 2, severity: 8, source: 'PHP', message: "Syntax error: unexpected ','" }]);
    assert.equal(await page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ owner: 'php-lint' })[0].startColumn`), 44);
    await page.evaluate(`window.pymagic.editor.getModel().setValue("<?php\\nRoute::prefix('admin')->group(function () {\\n});\\n")`);
    assert.deepEqual(await errorsOf(page, (f) => f.length === 0), []);
    assert.notEqual(await page.evaluate(`document.getElementById('toast')?.style.display`), 'block');
    console.log('OK');
} finally { await app.close(); process.env.PYMAGIC_PHP = env; }

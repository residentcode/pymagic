// PHP's indentation without PHP in the built PyMagic.exe: a routes file whose Route::prefix group is indented wrongly gets weak warnings on the
// group's line, its inside and its }, Alt+Enter's Fix indentation (the preferred quick fix) puts them right, touching nothing else; then
// Reformat Code (Ctrl+Alt+L) lays a file out with the built-in formatter, as there is no PHP to run Pint with; a file with a syntax error is
// not formatted, and its indentation is not told.
// Run: node desktop/e2e/phpformat.check.mjs
import assert from 'node:assert/strict';
import { launch, project, sleep } from './app.mjs';

delete process.env.PYMAGIC_PHP;
const routes = "<?php\n\nuse Illuminate\\Support\\Facades\\Route;\n// In routes/web.php\n\n    Route::prefix('admin')->group(function () {\n    Route::get('/', [AdminController::class, 'index'])->name('admin.index');\n    Route::post('/store', [AdminController::class, 'store'])\n        ->name('admin.store');\n});\n";
const app = await launch(project({ 'main.py': 'x = 1\n', 'routes/web.php': routes, 'index.php': '<?php\nif ($a) {\necho   1;}\n' }), { phpPath: 'C:\\no\\such\\php.exe' });
const { page } = app;
const marks = (owner) => page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ owner: '${owner}', resource: window.pymagic.editor.getModel().uri }).map((m) => m.startLineNumber + ' ' + m.severity + ' ' + m.message)`);
async function settle(owner, want, ms = 15000) {
    const end = Date.now() + ms;
    let found = [];
    while (Date.now() < end) { found = await marks(owner); if (want(found)) return found; await sleep(250); }
    const state = await page.evaluate(`(async () => { const m = window.pymagic.editor.getModel(); const r = await fetch('/api/php/lint', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text: m.getValue() }) }).then((x) => x.text()).catch((e) => 'failed: ' + e); return m.getLanguageId() + ' ' + m.uri.path + ' | all marks: ' + JSON.stringify(window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).map((k) => k.owner)) + ' | lint: ' + r; })()`);
    assert.fail(`the marks stayed ${JSON.stringify(found)} (${state})`);
}
async function open(f) {
    await page.evaluate(`window.pymagic.openFile('${f}')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('${f.split('/').pop()}')`, 20000, f);
}
const text = () => page.evaluate('window.pymagic.editor.getModel().getValue()');
try {
    await open('routes/web.php');
    const told = await settle('php-indent', (f) => f.length === 4);
    console.log('told:', told);
    assert.deepEqual(told.map((m) => m.split(' ').slice(0, 2).join(' ')), ['6 2', '7 2', '8 2', '10 2']);   // 2: Info, a weak warning
    assert.match(told[0], /Indentation is 4 spaces, expected none/);
    // Alt+Enter on the group's line: its quick fixes, Fix indentation chosen with Enter
    await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 6, column: 3 }); ed.focus(); })()`);
    await page.evaluate(`window.pymagic.editor.trigger('keyboard', 'editor.action.quickFix', {})`);
    await page.waitFor(`[...document.querySelectorAll('.action-widget .monaco-list-row')].some((e) => e.textContent.includes('Fix indentation of the file'))`, 10000, 'the quick fix');
    await page.key(13, 'Enter');
    await page.waitFor(`window.pymagic.editor.getModel().getValue().includes("\\nRoute::prefix")`, 10000, 'the fix');
    const fixed = await text();
    console.log(fixed);
    // only the group's line was out of place: its inside and its } were right for a group at the left edge
    assert.equal(fixed, routes.replace('\n    Route::prefix', '\nRoute::prefix'));
    assert.deepEqual(await settle('php-indent', (f) => f.length === 0), []);

    // Reformat Code with no PHP: the built-in formatter
    await open('index.php');
    await settle('php-indent', (f) => f.length === 1);
    await page.evaluate(`window.pymagic.runCommand('pymagic.edit.format')`);
    await page.waitFor(`window.pymagic.editor.getModel().getValue() !== '<?php\\nif ($a) {\\necho   1;}\\n'`, 15000, 'the format');
    const formatted = await text();
    console.log(JSON.stringify(formatted));
    assert.equal(formatted, '<?php\nif ($a) {\n    echo 1;\n}\n');
    assert.deepEqual(await settle('php-indent', (f) => f.length === 0), []);
    // a syntax error: not formatted, no indentation told, only the error
    await page.evaluate(`window.pymagic.editor.getModel().setValue('<?php\\nif ($a) {\\necho 1,;\\n}\\n')`);
    await settle('php-lint', (f) => f.length === 1);
    assert.deepEqual(await marks('php-indent'), []);
    await page.evaluate(`window.pymagic.runCommand('pymagic.edit.format')`);
    const said = await page.waitFor(`document.getElementById('toast')?.style.display === 'block' && /syntax/.test(document.getElementById('toast').textContent) && document.getElementById('toast').textContent`, 10000, 'the notice');
    console.log('said:', said);
    assert.match(said, /syntax error/);
    assert.equal(await text(), '<?php\nif ($a) {\necho 1,;\n}\n');
    console.log('OK');
} finally { await app.close(); }

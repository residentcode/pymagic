// PHP's tests and Eloquent in the built PyMagic.exe, on a copy of Laravel's app (PYMAGIC_TEST_LARAVEL_DIR, its SQLite database migrated) with
// the PHP PYMAGIC_PHP names (Xdebug's DLL in its ext folder): the run marks in the margin of a PHPUnit test file, Run with coverage from one
// (the class's lines that ran and did not, in the editor's coverage), a PHP tests run configuration of a folder, and Eloquent's columns as the
// database has them in the quotes of a query (User::where('…')) with their PHP types.
// Run: PYMAGIC_PHP=… PYMAGIC_TEST_LARAVEL_DIR=… node desktop/e2e/phptests.check.mjs
import assert from 'node:assert/strict';
import { cpSync, mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { launch, sleep } from './app.mjs';

const source = process.env.PYMAGIC_TEST_LARAVEL_DIR;
assert.ok(source && process.env.PYMAGIC_PHP, 'PYMAGIC_TEST_LARAVEL_DIR and PYMAGIC_PHP');
const root = join(mkdtempSync(join(tmpdir(), 'pymagic-e2e-phptests-')), 'shop');
cpSync(source, root, { recursive: true, filter: (f) => !/[\\/](node_modules|\.git)$/.test(f) });
mkdirSync(join(root, 'app', 'Support'), { recursive: true });
writeFileSync(join(root, 'app', 'Support', 'Calc.php'), '<?php\n\nnamespace App\\Support;\n\nclass Calc\n{\n    public static function sign(int $n): string\n    {\n        if ($n < 0) {\n            return \'negative\';\n        }\n        return \'positive\';\n    }\n}\n');
writeFileSync(join(root, 'tests', 'Unit', 'CalcTest.php'), '<?php\n\nnamespace Tests\\Unit;\n\nuse App\\Support\\Calc;\nuse PHPUnit\\Framework\\TestCase;\n\nclass CalcTest extends TestCase\n{\n    public function test_positive(): void\n    {\n        $this->assertSame(\'positive\', Calc::sign(3));\n    }\n}\n');
writeFileSync(join(root, 'app', 'PmQuery.php'), "<?php\n\nuse App\\Models\\User;\n\n$found = User::where('em')->get();\n");
mkdirSync(join(root, '.pymagic'), { recursive: true });
writeFileSync(join(root, '.pymagic', 'run-configurations.json'), JSON.stringify({ configurations: [
    { name: 'Unit tests', kind: 'phpTests', target: 'tests/Unit', phpRunner: '', script: '', args: '', cwd: '', env: {} },
], selected: 'Unit tests' }, null, 2));
const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;
const items = `[...document.querySelectorAll('.ctx-menu .ctx-item')]`;

const app = await launch(root);
const { page } = app;
try {
    // the run marks of CalcTest.php: the class and its test
    await page.evaluate(`window.pymagic.openFile('tests/Unit/CalcTest.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('CalcTest.php')`, 20000, 'CalcTest.php');
    const marks = await page.waitFor(`(() => { const m = window.pymagic.editor.getModel(); const lines = []; for (let l = 1; l <= m.getLineCount(); l++) if (m.getLineDecorations(l).some((d) => d.options.glyphMarginClassName?.startsWith('test-run'))) lines.push(l); return lines.length >= 2 ? lines : null; })()`, 60000, 'the run marks');
    console.log('run marks on lines', marks);
    assert.deepEqual(marks, [8, 10]);
    // the test's mark: Run with coverage
    const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.revealLine(10); const box = ed.getContainerDomNode().getBoundingClientRect();
        return { x: box.left + ed.getLayoutInfo().glyphMarginLeft + 4, y: box.top + ed.getTopForLineNumber(10) - ed.getScrollTop() + 8 }; })()`);
    await page.click(at.x, at.y);
    await page.waitFor(`${items}.some((d) => d.textContent.includes('with coverage'))`, 5000, "the mark's menu");
    const menu = await page.evaluate(`${items}.map((d) => d.textContent.trim())`);
    console.log('its menu:', menu);
    assert.ok(!menu.some((t) => t.startsWith('Profile')), 'no profiler for PHP');
    await page.evaluate(`${items}.find((d) => d.textContent.includes('with coverage')).click()`);
    const coverage = await page.waitFor(`fetch('/api/coverage').then((r) => (r.ok ? r.json() : null)).then((c) => c?.files?.['app/Support/Calc.php'] ?? null)`, 180000, 'the coverage of Calc.php');
    console.log('coverage of app/Support/Calc.php:', JSON.stringify(coverage));
    assert.ok(coverage.executed.includes(9) && coverage.executed.includes(12) && !coverage.executed.includes(10) && coverage.executable.includes(10));
    const passed = await page.waitFor(`document.querySelector('#tests-summary')?.textContent || null`, 30000, 'the tests summary');
    console.log('Tests panel:', passed);

    // the PHP tests configuration of tests/Unit: Run runs the folder's tests in the Tests panel
    await page.evaluate(`${$('b-run')}.click()`);
    const summary = await page.waitFor(`(() => { const t = document.querySelector('#tests-summary')?.textContent ?? ''; return /passed/.test(t) && t !== ${JSON.stringify(passed)} ? t : null; })()`, 180000, "the configuration's run");
    const ran = await page.evaluate(`[...document.querySelectorAll('#tests-tree li')].map((li) => li.firstElementChild?.textContent?.trim()).filter(Boolean)`);
    console.log('the configuration ran:', summary, '|', ran.slice(0, 6).join(' / '));
    assert.ok(ran.some((t) => t.includes('CalcTest')) && ran.some((t) => t.includes('ExampleTest')));

    // a column in the quotes of a query: the users table's, as the database has them, with their PHP types
    await page.evaluate(`window.pymagic.openFile('app/PmQuery.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('PmQuery.php')`, 20000, 'PmQuery.php');
    await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 5, column: 25 }); ed.focus(); })()`);
    let rows = [];
    for (let i = 0; i < 20 && !rows.includes('email_verified_at'); i++) {
        await page.evaluate(`window.pymagic.editor.trigger('test', 'hideSuggestWidget', {})`);
        await page.evaluate(`window.pymagic.editor.trigger('test', 'editor.action.triggerSuggest', {})`);
        await sleep(2000);
        rows = await page.evaluate(`[...document.querySelectorAll('.suggest-widget.visible .monaco-list-row')].map((x) => x.getAttribute('aria-label'))`);
    }
    console.log("in User::where('em'):", rows);
    assert.ok(rows.includes('email') && rows.includes('email_verified_at'), rows.join(' | '));
    await page.evaluate(`window.pymagic.editor.trigger('test', 'selectNextSuggestion', {})`);
    await sleep(300);
    const details = await page.evaluate(`[...document.querySelectorAll('.suggest-widget.visible .details-label, .suggest-widget.visible .monaco-list-row .details-label')].map((d) => d.textContent)`);
    console.log('details:', details);
    assert.ok(details.some((d) => d.includes('Carbon|null · datetime column of users')), details.join(' | '));
    await page.key(27, 'Escape');
    console.log('OK');
} finally {
    await app.close();
}

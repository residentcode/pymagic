// PHP's indentation check, Fix indentation and Reformat Code without PHP (web/src/php-format-model.js).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { indentProblems, fixIndentation, formatPhpText } from '../src/php-format-model.js';

const routes = [
    '<?php',
    '',
    'use Illuminate\Support\Facades\Route;',
    '// In routes/web.php',
    '',
    '    Route::prefix(\'admin\')->group(function () {',
    '    Route::get(\'/\', [AdminController::class, \'index\'])->name(\'admin.index\');',
    '    Route::post(\'/model/{model}/store\', [AdminController::class, \'store\'])',
    '        ->name(\'admin.store\');',
    '});',
    '',
].join('\n');

test("the user's routes file: the group's line, its inside and its } are told", () => {
    assert.deepEqual(indentProblems(routes).map((p) => `${p.line}: ${p.actual} -> ${p.expected}`), ['6: 4 -> 0', '7: 4 -> 8', '8: 4 -> 8', '10: 0 -> 4']);
    assert.match(indentProblems(routes)[0].message, /^Indentation is 4 spaces, expected none/);
});

test('Fix indentation moves only the wrong lines, a continuation along with its statement', () => {
    const out = fixIndentation(routes);
    assert.equal(out, [
        '<?php', '', 'use Illuminate\Support\Facades\Route;', '// In routes/web.php', '',
        'Route::prefix(\'admin\')->group(function () {',
        '    Route::get(\'/\', [AdminController::class, \'index\'])->name(\'admin.index\');',
        '    Route::post(\'/model/{model}/store\', [AdminController::class, \'store\'])',
        '        ->name(\'admin.store\');',
        '});', '',
    ].join('\n'));
    assert.deepEqual(indentProblems(out), []);
});

test('what PSR-12 code has is not told: classes, conditions on two lines, chains, closures in arguments, attributes, heredocs, switch', () => {
    const ok = `<?php
#[Attr(
    name: 'x',
)]
final class A extends B
{
    public function __construct(
        private int $x,
    ) {
        parent::__construct();
    }

    public function f($a, $b)
    {
        if ($a &&
            $b) {
            return $this->x
                ? $this->{$a}
                : $b;
        }
        $q->where(function ($q) {
            $q->x();
        })
            ->get(fn () => 1);
        $this->call(
            $a,
            function () {
                return 1;
            },
        );
        $s = <<<EOT
          text {$a}
        EOT;
        switch ($a) {
            case 1:
                break;
        }
        $f = fn () => array_map(function ($v) { return $v; }, $a)
            ? 1 : 2;
    }
}
`;
    assert.deepEqual(indentProblems(ok), []);
    assert.equal(fixIndentation(ok), ok);
    assert.deepEqual(indentProblems('<?php if ($a): ?>\n<p>x</p>\n    <?php endif; ?>\n'), []);
});

test('tabs are told and fixed as tabs', () => {
    const text = '<?php\nfunction f()\n{\n\t\treturn 1;\n}\n';
    assert.equal(indentProblems(text)[0].message.startsWith('Indentation is 2 tabs, expected 1 tab'), true);
    assert.equal(fixIndentation(text), '<?php\nfunction f()\n{\n\treturn 1;\n}\n');
});

test('Reformat Code without PHP lays the file out in PER style', async () => {
    const out = await formatPhpText(routes);
    assert.match(out, /\nRoute::prefix\('admin'\)->group\(function \(\) \{\n {4}Route::get/);
    await assert.rejects(formatPhpText('<?php\nf(,);\n'));
});

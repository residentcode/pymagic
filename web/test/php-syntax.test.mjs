// PHP's syntax check without PHP (web/src/php-syntax-model.js).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { phpSyntaxError } from '../src/php-syntax-model.js';

test('a PHP syntax error is found without PHP: its line, column and message', () => {
    const text = "<?php\nuse Illuminate\Support\Facades\Route;\nRoute::prefix('admin')->group(function () {,22\n    Route::get('/', [A::class, 'index']);\n});\n";
    assert.deepEqual(phpSyntaxError(text), { line: 3, column: 44, message: "Syntax error: unexpected ','" });
    assert.equal(phpSyntaxError("<?php\n$a = 1\n$b = 2;\n").line, 3);
    assert.match(phpSyntaxError("<?php\nfunction f() {\n  if (1) {\n}\n").message, /expecting '}'/);
});

test("modern PHP and what the parser misreads are not errors", () => {
    const ok = "<?php\nenum S: string { case A = 'a'; }\nfinal readonly class P { public function __construct(public int $x = 1) {} }\n"
        + "$f = fn($x) => $x?->y ?? match($x) { 1 => 2, default => 3 };\n#[Attr]\nfunction g(int|string ...$a): never { throw new E(); }\n"
        + "echo <<<EOT\n  hi {$x}\n  EOT;\n?>\n<div><?= $x ?></div>\n";
    assert.equal(phpSyntaxError(ok), null);
    assert.equal(phpSyntaxError("<?php\nreturn static::$factory::new();\n"), null);
    assert.equal(phpSyntaxError("<?php\n$q->where(...array_values($v), boolean: $b);\n"), null);
    assert.equal(phpSyntaxError("<?php\n$ok = $job instanceof ((string) $name);\n"), null);
});

// PHP's tools in the built PyMagic.exe, on a copy of a Laravel app with Larastan (PYMAGIC_TEST_STAN_DIR: larastan/larastan in its vendor, a
// phpstan.neon that includes it; Laravel's Pint too) with the PHP PYMAGIC_PHP names: PHPStan's problems of a file when it is opened and again
// when it is saved, Format with the project's Pint (and Undo), and Eloquent's columns and relations in Phpactor's completion after $user->.
// Run: PYMAGIC_PHP=… PYMAGIC_TEST_STAN_DIR=… PYMAGIC_PHPACTOR_HOME=(a new folder in %TEMP%) node desktop/e2e/phptools.check.mjs
import assert from 'node:assert/strict';
import { cpSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { launch, sleep } from './app.mjs';

const source = process.env.PYMAGIC_TEST_STAN_DIR;
assert.ok(source && process.env.PYMAGIC_PHP, 'PYMAGIC_TEST_STAN_DIR and PYMAGIC_PHP');
const root = join(mkdtempSync(join(tmpdir(), 'pymagic-e2e-phptools-')), 'shop');
cpSync(source, root, { recursive: true, filter: (f) => !/[\\/](node_modules|\.git)$/.test(f) });
// a model with two problems PHPStan finds (line 9: a string for an int; line 14: a method the Eloquent builder has not, which Larastan knows)
writeFileSync(join(root, 'app', 'Models', 'PmBad.php'), ['<?php', '', 'namespace App\\Models;', '', 'class PmBad', '{', '    public function count(): int', '    {', "        return 'many';",
    '    }', '', '    public function first(): ?User', '    {', "        return User::query()->where('email', 'a@b.c')->nope();", '    }', '}', ''].join('\n'));
// a file laid out badly, for Format
const messy = '<?php\nnamespace App;\nclass  Demo{\npublic function  run( $a,$b ){\n if($a==$b){return  array(1,2);}\nreturn [ ];\n}\n}\n';
writeFileSync(join(root, 'app', 'Demo.php'), messy);
// a relation of the user's, and a file where $user-> is being typed
const userFile = join(root, 'app', 'Models', 'User.php');
const user = readFileSync(userFile, 'utf8');
writeFileSync(userFile, user.slice(0, user.lastIndexOf('}')) + '\n    public function posts()\n    {\n        return $this->hasMany(Post::class);\n    }\n}\n');
writeFileSync(join(root, 'app', 'Models', 'Post.php'), '<?php\n\nnamespace App\\Models;\n\nuse Illuminate\\Database\\Eloquent\\Model;\n\nclass Post extends Model\n{\n}\n');
writeFileSync(join(root, 'app', 'PmTyping.php'), '<?php\n\nuse App\\Models\\User;\n\n$user = new User();\n$user->\n');
const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;
const markers = (owner) => `window.pymagic.monaco.editor.getModelMarkers({ owner: '${owner}', resource: window.pymagic.editor.getModel().uri }).map((m) => m.startLineNumber + ': ' + m.message)`;

const app = await launch(root);
const { page } = app;
try {
    // PHPStan's problems of a file when it is opened (Larastan's own among them)
    await page.evaluate(`window.pymagic.openFile('app/Models/PmBad.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('PmBad.php')`, 20000, 'PmBad.php');
    const found = await page.waitFor(`(() => { const m = ${markers('phpstan')}; return m.length >= 2 ? m : null; })()`, 180000, "PHPStan's problems");
    console.log('PHPStan on opening:', found);
    assert.ok(found.some((m) => m.startsWith('9: ') && m.includes('should return int but returns string')), found.join(' | '));
    assert.ok(found.some((m) => m.startsWith('14: ') && m.includes('nope')), found.join(' | '));
    // fixed and saved: the problem of line 9 goes
    await page.evaluate(`(() => { const ed = window.pymagic.editor; const m = ed.getModel(); ed.executeEdits('test', [{ range: new window.pymagic.monaco.Range(9, 1, 9, m.getLineMaxColumn(9)), text: '        return 2;' }]); })()`);
    await page.evaluate(`window.pymagic.runCommand('pymagic.file.save')`);
    const after = await page.waitFor(`(() => { const m = ${markers('phpstan')}; return m.length === 1 ? m : null; })()`, 180000, 'PHPStan again after the save');
    console.log('PHPStan after the fix was saved:', after);
    assert.ok(after[0].startsWith('14: '), after.join(' | '));

    // Format with the project's Pint (Laravel's preset), then Undo gives the text back
    await page.evaluate(`window.pymagic.openFile('app/Demo.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('Demo.php')`, 20000, 'Demo.php');
    await page.evaluate(`${$('b-format')}.click()`);
    const formatted = await page.waitFor(`(() => { const v = window.pymagic.editor.getModel().getValue(); return v.includes('class Demo\\n{') ? v : null; })()`, 60000, 'the file formatted by Pint');
    console.log('formatted by Pint:', JSON.stringify(formatted));
    assert.equal(formatted, '<?php\n\nnamespace App;\n\nclass Demo\n{\n    public function run($a, $b)\n    {\n        if ($a == $b) {\n            return [1, 2];\n        }\n\n        return [];\n    }\n}\n');
    await page.evaluate(`window.pymagic.editor.trigger('test', 'undo', {})`);
    assert.equal(await page.evaluate(`window.pymagic.editor.getModel().getValue()`), messy);

    // Eloquent: after $user-> the users table's columns (Laravel's migration) and the posts relation, with Phpactor's methods (asked again while
    // Phpactor indexes the copy)
    await page.evaluate(`window.pymagic.openFile('app/PmTyping.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('PmTyping.php')`, 20000, 'PmTyping.php');
    await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 6, column: 8 }); ed.focus(); })()`);
    let rows = [];
    for (let i = 0; i < 100 && !(rows.includes('email') && rows.includes('posts')); i++) {
        await page.evaluate(`window.pymagic.editor.trigger('test', 'hideSuggestWidget', {})`);
        await page.evaluate(`window.pymagic.editor.trigger('test', 'editor.action.triggerSuggest', {})`);
        await sleep(3000);
        rows = await page.evaluate(`[...document.querySelectorAll('.suggest-widget.visible .monaco-list-row')].map((x) => x.getAttribute('aria-label')?.split(',')[0])`);
    }
    console.log('completion after $user->:', rows.slice(0, 12));
    assert.ok(rows.includes('email') && rows.includes('email_verified_at') && rows.includes('posts'), rows.join(','));
    // `po`: the relation as a property and Phpactor's method of it
    await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.trigger('test', 'hideSuggestWidget', {}); ed.trigger('test', 'type', { text: 'po' }); ed.trigger('test', 'editor.action.triggerSuggest', {}); })()`);
    await sleep(3000);
    const po = await page.evaluate(`[...document.querySelectorAll('.suggest-widget.visible .monaco-list-row')].map((x) => x.getAttribute('aria-label'))`);
    console.log('after $user->po:', po);
    assert.ok(po.filter((l) => l?.startsWith('posts')).length === 2, po.join(' | '));
    const detail = await page.evaluate(`document.querySelector('.suggest-widget.visible .monaco-list-row[aria-label^="email,"] .details-label, .suggest-widget.visible .monaco-list-row.focused .details-label')?.textContent ?? ''`);
    console.log('its detail:', detail);
    await page.key(27, 'Escape');
    console.log('OK');
} finally {
    await app.close();
}

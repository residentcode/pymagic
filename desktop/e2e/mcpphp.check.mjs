// The MCP server's tools on PHP files in the built PyMagic.exe, with the PHP PYMAGIC_PHP names and PHP's language server (Phpactor, which
// comes with PyMagic): a method's signature and where it is defined, asked from the file that calls it; the method renamed where it is
// declared; a variable renamed in its file.
// Run: PYMAGIC_PHP=C:\path\php.exe PYMAGIC_PHPACTOR_HOME=(a new folder in %TEMP%) node desktop/e2e/mcpphp.check.mjs
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

assert.ok(process.env.PYMAGIC_PHP, 'PYMAGIC_PHP names a php.exe');
const root = project({
    'composer.json': '{ "name": "probe/shop", "autoload": { "psr-4": { "App\\\\": "src/" } } }\n',
    'src/Greeter.php': '<?php\n\nnamespace App;\n\nclass Greeter\n{\n    /** Says hello to someone. */\n    public function greet(string $name): string\n    {\n        $text = \'Hello \' . $name;\n        return $text;\n    }\n}\n',
    'index.php': '<?php\n\nrequire __DIR__ . \'/src/Greeter.php\';\n\nuse App\\Greeter;\n\n$greeter = new Greeter();\necho $greeter->greet(\'you\');\n',
    'main.py': 'x = 1\n',
});
const app = await launch(root);
const { page } = app;
const call = (name, args) => page.evaluate(`window.pymagic.mcp.call(${JSON.stringify(name)}, ${JSON.stringify(args)})`);
const disk = (rel) => readFileSync(join(root, rel), 'utf8').replace(/\r\n/g, '\n');

try {
    await page.waitFor('window.pymagic && window.pymagic.mcp', 40000, 'the tools');
    // the language server starts with the first PHP file and reads the project: asked until it knows the method
    let info = null;
    for (let i = 0; i < 60; i++) {
        info = await call('get_symbol_info', { filePath: 'index.php', line: 8, column: 17 });
        if (/greet/.test(info.text) && /Defined at/.test(info.text)) break;
        await sleep(2000);
    }
    console.log('greet:', info.text.replace(/\s*\n+\s*/g, ' | '));
    assert.match(info.text, /greet\(string \$name\): string/);
    assert.match(info.text, /Says hello to someone/);
    assert.match(info.text, /Defined at: src\/Greeter\.php:8:\d+/);

    // a variable of a file
    const local = await call('rename_refactoring', { pathInProject: 'src/Greeter.php', symbolName: '$text', newName: 'said' });
    console.log(local.text);
    assert.ok(!local.isError, local.text);
    assert.ok(disk('src/Greeter.php').includes("$said = 'Hello ' . $name;\n        return $said;"), disk('src/Greeter.php'));
    // the method, where it is declared and where it is called
    const method = await call('rename_refactoring', { pathInProject: 'src/Greeter.php', symbolName: 'Greeter::greet', newName: 'welcome' });
    console.log(method.text);
    assert.ok(!method.isError, method.text);
    assert.ok(disk('src/Greeter.php').includes('public function welcome(string $name): string'), disk('src/Greeter.php'));
    console.log('index.php now calls:', /->(\w+)\(/.exec(disk('index.php'))?.[1]);
    assert.equal((await call('rename_refactoring', { pathInProject: 'src/Greeter.php', symbolName: 'nothing', newName: 'x' })).isError, true);
    console.log('OK');
} finally {
    await app.close();
}

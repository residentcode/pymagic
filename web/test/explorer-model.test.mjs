// Checks of web/src/explorer-model.js: `node web/test/explorer-model.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// web/package.json does not say "type": "module", so Node would read src/*.js as CommonJS; load the file as a module instead
const source = readFileSync(new URL('../src/explorer-model.js', import.meta.url), 'utf8');
const { moveProblem, copyName, movedPath, join, packageTarget, moduleTarget } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

test('moveProblem: a file into another folder or the project folder is fine', () => {
    assert.equal(moveProblem('pkg/a.py', 'other'), null);
    assert.equal(moveProblem('pkg/a.py', ''), null);
    assert.equal(moveProblem('a.py', 'pkg'), null);
    assert.equal(moveProblem('pkg', 'other/deep'), null);
});

test('moveProblem: not into the folder it is in, nor into itself or below it', () => {
    assert.match(moveProblem('pkg/a.py', 'pkg'), /already/);
    assert.match(moveProblem('a.py', ''), /already/);
    assert.match(moveProblem('pkg', 'pkg'), /itself/);
    assert.match(moveProblem('pkg', 'pkg/sub'), /itself/);
    // a folder whose name starts like another is not inside it
    assert.equal(moveProblem('pkg', 'pkg2'), null);
});

test('copyName: free names stay, taken ones get _copy, _copy2, ...', () => {
    assert.equal(copyName('a.py', ['b.py']), 'a.py');
    assert.equal(copyName('a.py', ['a.py']), 'a_copy.py');
    assert.equal(copyName('a.py', ['a.py', 'a_copy.py', 'a_copy2.py']), 'a_copy3.py');
    assert.equal(copyName('pkg', ['pkg']), 'pkg_copy');
    assert.equal(copyName('.env', ['.env']), '.env_copy');
    assert.equal(copyName('archive.tar.gz', ['archive.tar.gz']), 'archive.tar_copy.gz');
});

test('movedPath: the path itself and what is below it follow, others stay', () => {
    assert.equal(movedPath('pkg', 'pkg', 'lib/pkg'), 'lib/pkg');
    assert.equal(movedPath('pkg/a.py', 'pkg', 'lib/pkg'), 'lib/pkg/a.py');
    assert.equal(movedPath('pkg2/a.py', 'pkg', 'lib/pkg'), 'pkg2/a.py');
    assert.equal(join('', 'a.py'), 'a.py');
    assert.equal(join('pkg', 'a.py'), 'pkg/a.py');
});

test('convert to package and back to a module', () => {
    const none = () => false;
    assert.equal(packageTarget('app/util.py', none), 'app/util/__init__.py');
    assert.equal(packageTarget('util.py', none), 'util/__init__.py');
    assert.equal(packageTarget('app/__init__.py', none), null);
    assert.equal(packageTarget('notes.txt', none), null);
    assert.equal(packageTarget('util.py', (p) => p === 'util'), null);
    assert.equal(moduleTarget('app/util', ['app/util/__init__.py'], none), 'app/util.py');
    assert.equal(moduleTarget('app/util', ['app/util/__init__.py', 'app/util/x.py'], none), null);
    assert.equal(moduleTarget('util', ['util/__init__.py'], (p) => p === 'util.py'), null);
    assert.equal(moduleTarget('', [], none), null);
});

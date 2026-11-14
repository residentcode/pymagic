// The MCP tools' own logic (web/src/mcp-model.js): paths, cut texts, replacing, the file lists, the tree, symbols, problems and matches.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { projectPath, relativeToRoot, truncateLines, replaceText, filesByName, filesByGlob, treeText, findSymbol, problemsText, matchesText, includeOf, hoverText } from '../src/mcp-model.js';

test('a path must stay in the project', () => {
    assert.equal(projectPath('src\\app.py'), 'src/app.py');
    assert.equal(projectPath('./src//a.py'), 'src/a.py');
    assert.throws(() => projectPath('../secret.txt'), /not a path inside the project/);
    assert.throws(() => projectPath('C:/Windows/win.ini'), /not a path inside/);
    assert.throws(() => projectPath('/etc/passwd'), /not a path inside/);
    assert.throws(() => projectPath(''), /needed/);
    assert.equal(projectPath('', { allowRoot: true }), '');
    assert.equal(relativeToRoot('C:\\Work\\proj\\src\\a.py', 'C:\\Work\\proj'), 'src/a.py');
    assert.equal(relativeToRoot('c:/work/PROJ/b.py', 'C:\\Work\\proj'), 'b.py');
    assert.equal(relativeToRoot('D:/other/b.py', 'C:\\Work\\proj'), 'D:/other/b.py');
});

test('long texts are cut where asked', () => {
    const t = ['1', '2', '3', '4', '5'].join('\n');
    assert.equal(truncateLines(t, 10).text, t);
    assert.equal(truncateLines(t, 2).text, '1\n2\n<<3 lines left out>>');
    assert.equal(truncateLines(t, 2, 'START').text, '<<3 lines left out>>\n4\n5');
    assert.equal(truncateLines(t, 3, 'MIDDLE').text, '1\n2\n<<2 lines left out>>\n5');
    assert.equal(truncateLines(t, 1, 'NONE').text, t);
    assert.equal(truncateLines('a\nb\n', 2).text, 'a\nb\n');
    assert.equal(truncateLines('a\nb\nc\n', 2).text, 'a\nb\n<<1 line left out>>');
});

test('replacing: all or the first, with or without case', () => {
    assert.deepEqual(replaceText('a A a', 'a', 'b'), { text: 'b A b', count: 2 });
    assert.deepEqual(replaceText('a A a', 'a', 'b', { caseSensitive: false }), { text: 'b b b', count: 3 });
    assert.deepEqual(replaceText('a A a', 'a', 'b', { all: false }), { text: 'b A a', count: 1 });
    assert.deepEqual(replaceText('xyz', 'q', 'b'), { text: 'xyz', count: 0 });
    assert.deepEqual(replaceText('aaa', 'aa', 'a'), { text: 'aa', count: 1 });
    assert.throws(() => replaceText('x', '', 'y'), /empty/);
});

test('files by name and by glob', () => {
    const files = ['src/app/models.py', 'models.py', 'tests/test_models.py', 'README.md', 'src/app/views.py'];
    assert.deepEqual(filesByName(files, 'MODEL').files, ['models.py', 'src/app/models.py', 'tests/test_models.py']);
    assert.deepEqual(filesByName(files, 'model', 1), { files: ['models.py'], more: 2 });
    assert.deepEqual(filesByGlob(files, '**/*.py').files, ['src/app/models.py', 'models.py', 'tests/test_models.py', 'src/app/views.py']);
    assert.deepEqual(filesByGlob(files, 'src/**/v*.py').files, ['src/app/views.py']);
    assert.deepEqual(filesByGlob(files, '*.py', 'src/app').files, ['src/app/models.py', 'src/app/views.py']);
    assert.deepEqual(filesByGlob(files, 'test_?odels.py').files, ['tests/test_models.py']);
});

test('the tree of a folder, cut at its depth', () => {
    const dirs = ['src', 'src/app', 'src/app/deep', 'docs'];
    const files = ['README.md', 'src/main.py', 'src/app/a.py', 'src/app/deep/x.py', 'docs/index.md'];
    assert.equal(treeText(dirs, files, '', 2), [
        './', '├── docs/', '│   └── index.md', '├── src/', '│   ├── app/', '│   │   └── …', '│   └── main.py', '└── README.md',
    ].join('\n'));
    assert.equal(treeText(dirs, files, 'src/app', 5), ['src/app/', '├── deep/', '│   └── x.py', '└── a.py'].join('\n'));
    assert.throws(() => treeText(dirs, files, 'nope'), /not a folder/);
});

test('a symbol by its name, or Class.member, at any depth', () => {
    const syms = [
        { name: 'helper', range: 1 },
        { name: 'Shop', children: [{ name: 'total', range: 2 }, { name: 'Inner', children: [{ name: 'total', range: 3 }] }] },
    ];
    assert.equal(findSymbol(syms, 'helper').range, 1);
    assert.equal(findSymbol(syms, 'Shop.total').range, 2);
    assert.equal(findSymbol(syms, 'total').range, 2);
    assert.equal(findSymbol(syms, 'Inner.total').range, 3);
    assert.equal(findSymbol(syms, 'Shop.nope'), null);
});

test('problems and matches as lines', () => {
    const ps = [{ line: 3, column: 5, severity: 1, message: 'Unresolved reference \'x\'', code: 'unresolved-reference' }, { line: 1, column: 1, severity: 2, message: 'Unused import' }];
    assert.equal(problemsText('a.py', ps), "a.py:3:5 error: Unresolved reference 'x' [unresolved-reference]\na.py:1:1 warning: Unused import");
    assert.equal(problemsText('a.py', ps, true), "a.py:3:5 error: Unresolved reference 'x' [unresolved-reference]");
    assert.equal(problemsText('a.py', []), 'No problems in a.py.');
    const result = { files: [{ path: 'a.py', matches: [{ line: 0, column: 4, length: 3, text: 'def foo():' }, { line: 4, column: 0, length: 3, text: 'foo()' }] }], truncated: false };
    assert.equal(matchesText(result), 'a.py:1: def ||foo||():\na.py:5: ||foo||()');
    assert.equal(matchesText(result, 1), 'a.py:1: def ||foo||():\n(1 more not shown)');
    assert.equal(matchesText({ files: [] }), 'Nothing found.');
});

test('the search\'s include from a folder and a mask; a hover as text', () => {
    assert.equal(includeOf('', '*.py, *.html'), '*.py,*.html');
    assert.equal(includeOf('src', ''), 'src/**');
    assert.equal(includeOf('src\\app', '*.py'), 'src/app/**/*.py');
    assert.throws(() => includeOf('../x', ''), /not a path inside/);
    assert.equal(hoverText({ contents: { kind: 'markdown', value: '```python\ndef f()\n```' } }), '```python\ndef f()\n```');
    assert.equal(hoverText({ contents: ['a', { language: 'python', value: 'b' }] }), 'a\n\nb');
    assert.equal(hoverText(null), '');
});

// Checks of web/src/termtabs.js: `node web/test/termtabs.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// web/package.json does not say "type": "module", so Node would read src/*.js as CommonJS; load the file as a module instead
const source = readFileSync(new URL('../src/termtabs.js', import.meta.url), 'utf8');
const { nextName, afterClose, cleanName, keptLines, restoredTabs, MAX_PANES } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

test('nextName: the lowest free number, so a closed terminal\'s number comes back', () => {
    assert.equal(nextName([]), 'Terminal 1');
    assert.equal(nextName(['Terminal 1']), 'Terminal 2');
    assert.equal(nextName(['Terminal 1', 'Terminal 3']), 'Terminal 2');
    assert.equal(nextName(['Terminal 2', 'Terminal 3']), 'Terminal 1');
});

test('nextName: renamed terminals do not count, and a name typed like a default does', () => {
    assert.equal(nextName(['tests', 'server']), 'Terminal 1');
    assert.equal(nextName(['Terminal 1', 'Terminal 10']), 'Terminal 2');
    assert.equal(nextName(['Terminal 1', 'Terminal 2']), 'Terminal 3');
});

test('afterClose: closing another terminal leaves the open one open', () => {
    assert.equal(afterClose([1, 2, 3], 1, 2), 2);
    assert.equal(afterClose([1, 2, 3], 3, 2), 2);
});

test('afterClose: closing the open terminal opens its right-hand neighbour, or the left one at the end', () => {
    assert.equal(afterClose([1, 2, 3], 2, 2), 3);
    assert.equal(afterClose([1, 2, 3], 1, 1), 2);
    assert.equal(afterClose([1, 2, 3], 3, 3), 2);
});

test('afterClose: nothing is left after the last one, and an unknown open one falls back to a neighbour', () => {
    assert.equal(afterClose([7], 7, 7), null);
    assert.equal(afterClose([1, 2], 1, null), 2);
    assert.equal(afterClose([1, 2], 9, 9), 1);
});

test('keptLines: the last lines of the screen, without the empty ones at its end', () => {
    assert.deepEqual(keptLines(['a', 'b', '', '  ', '']), ['a', 'b']);
    assert.deepEqual(keptLines(['1', '2', '3', '4'], 2), ['3', '4']);
    assert.deepEqual(keptLines(['', 'x', '', 'y', '']), ['', 'x', '', 'y']);
    assert.deepEqual(keptLines([]), []);
});

test('keptLines: the line that marked an earlier restore is not kept, so restores do not pile up', () => {
    assert.deepEqual(keptLines(['old', '[restored from the last session]', '[restored from the last session]', 'PS> ']), ['old', 'PS> ']);
});

test('restoredTabs: the tabs of the same project with their names, shells and screens', () => {
    const raw = { root: 'C:\\p', tabs: [
        { name: 'server', panes: [{ shell: 'pwsh', lines: ['PS> python -m http.server', 'Serving', ''] }, { shell: 'gitbash', lines: [] }] },
        { name: 'Terminal 2', panes: [{ shell: 'cmd', lines: ['C:\\p>'] }] },
    ] };
    assert.deepEqual(restoredTabs(raw, 'C:\\p'), [
        { name: 'server', panes: [{ shell: 'pwsh', lines: ['PS> python -m http.server', 'Serving'] }, { shell: 'gitbash', lines: [] }] },
        { name: 'Terminal 2', panes: [{ shell: 'cmd', lines: ['C:\\p>'] }] },
    ]);
    assert.deepEqual(restoredTabs(raw, 'C:\\other'), []);
});

test('restoredTabs: what has the wrong shape is dropped, a shell id that could be a command line is not kept', () => {
    assert.deepEqual(restoredTabs(null, ''), []);
    assert.deepEqual(restoredTabs({ root: '', tabs: 'x' }, ''), []);
    const many = Array.from({ length: 6 }, () => ({ shell: 'cmd', lines: [] }));
    const tabs = restoredTabs({ root: '', tabs: [
        { name: '  ', panes: [{ shell: 'cmd.exe /c echo hi', lines: ['ok', 3, null] }, null] },
        { name: 'empty', panes: [] },
        { name: 'wide', panes: many },
    ] }, '');
    assert.deepEqual(tabs[0], { name: 'Terminal', panes: [{ shell: null, lines: ['ok'] }] });
    assert.equal(tabs.length, 2);
    assert.equal(tabs[1].panes.length, MAX_PANES);
});

test('cleanName: spaces folded and trimmed, at most 40 characters, nothing typed keeps the old name', () => {
    assert.equal(cleanName('  tests   watch  '), 'tests watch');
    assert.equal(cleanName('x'.repeat(50)), 'x'.repeat(40));
    assert.equal(cleanName('   '), null);
    assert.equal(cleanName(''), null);
    assert.equal(cleanName(undefined), null);
});

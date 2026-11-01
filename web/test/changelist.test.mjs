// Checks of web/src/changelist-model.js: `node web/test/changelist.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/changelist-model.js', import.meta.url), 'utf8');
const m = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const file = (path) => ({ path, index: ' ', worktree: 'M' });
const state = { active: 'Fix', lists: [{ name: 'Changes', files: ['a.py'] }, { name: 'Fix', files: ['b.py', 'gone.py'] }] };

test('files are grouped by list, and a file no list holds is shown in the default one', () => {
    const g = m.groupFiles([file('a.py'), file('b.py'), file('c.py'), file('.pymagic/changelists.json')], state);
    assert.deepEqual(g.map((x) => [x.name, x.files.map((f) => f.path), x.active, x.isDefault]), [
        ['Changes', ['a.py', 'c.py', '.pymagic/changelists.json'], false, true],
        ['Fix', ['b.py'], true, false],
    ]);
});

test('without a state there is one default list with everything in it', () => {
    const g = m.groupFiles([file('a.py')], null);
    assert.equal(g.length, 1);
    assert.equal(g[0].name, 'Changes');
    assert.equal(g[0].active, true);
    assert.equal(g[0].files.length, 1);
});

test('a commit carries the ticked whole files of the chosen list and nothing of the others', () => {
    const files = [file('a.py'), file('b.py'), file('c.py'), file('d.py')];
    const st = { active: 'Changes', lists: [{ name: 'Changes', files: ['a.py', 'c.py'] }, { name: 'Fix', files: ['b.py', 'd.py'] }] };
    assert.deepEqual(m.commitPaths(files, st, 'Fix', new Set(), new Map()), ['b.py', 'd.py']);
    assert.deepEqual(m.commitPaths(files, st, 'Fix', new Set(['d.py']), new Map()), ['b.py']);
    assert.deepEqual(m.commitPaths(files, st, 'Changes', new Set(), new Map([['c.py', {}]])), ['a.py']);
    assert.deepEqual(m.partialPaths(files, st, 'Changes', new Map([['c.py', {}], ['b.py', {}]])), ['c.py']);
    assert.deepEqual(m.commitPaths(files, st, 'Nope', new Set(), new Map()), []);
});

test('the list to commit falls back to the active one when the chosen one is gone', () => {
    assert.equal(m.commitList(state, 'Changes'), 'Changes');
    assert.equal(m.commitList(state, 'Deleted'), 'Fix');
    assert.equal(m.commitList(state, ''), 'Fix');
    assert.equal(m.commitList(null, ''), 'Changes');
});

test('a patch is told apart line by line', () => {
    const lines = m.patchLines('diff --git a/f b/f\nindex 1..2 100644\n--- a/f\n+++ b/f\n@@ -1,2 +1,2 @@\n one\n-two\n+TWO\n');
    assert.deepEqual(lines.map((l) => l.kind).join(''), 'hhhh@ -+'.replace(/ /g, ' '));
    assert.equal(m.filesLabel(1), '1 file');
    assert.equal(m.filesLabel(3), '3 files');
});

test('a change bar of the editor maps onto the lines of the diff that make it', () => {
    const diff = {
        hunks: [{
            oldStart: 1, newStart: 1,
            lines: [
                { kind: ' ', text: 'a' }, { kind: '-', text: 'b' }, { kind: '+', text: 'B' }, { kind: ' ', text: 'c' },
                { kind: '+', text: 'new1' }, { kind: '+', text: 'new2' }, { kind: ' ', text: 'd' },
            ],
        }],
    };
    // the modified line: old line 2 ("b") became new line 2 ("B")
    let sel = m.linesOfChange(diff, { oldStart: 1, oldLines: ['b'], newStart: 1, newCount: 1 });
    assert.deepEqual([...sel.get(0)].sort(), [1, 2]);
    // the two added lines after "c" (new lines 4 and 5)
    sel = m.linesOfChange(diff, { oldStart: 3, oldLines: [], newStart: 3, newCount: 2 });
    assert.deepEqual([...sel.get(0)].sort(), [4, 5]);
    // a bar that is nowhere in the diff chooses nothing
    assert.equal(m.linesOfChange(diff, { oldStart: 50, oldLines: [], newStart: 50, newCount: 1 }).size, 0);
});

// Checks of the changes-in-part pieces of the Git tab: a file in several changelists (web/src/changelist-model.js `groupFiles`) and the list of a file's changes (web/src/git-parts.js).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { groupFiles } from '../src/changelist-model.js';
import { checkedKeys, placeText, listsText, movePlan } from '../src/git-parts.js';

const files = [{ path: 'a.py' }, { path: 'b.py' }, { path: 'c.py' }];

test('a file of which some changes were moved is in each list that holds some, with how many', () => {
    const state = {
        active: 'Changes',
        lists: [{ name: 'Changes', files: ['a.py', 'b.py'] }, { name: 'Fix', files: ['c.py'] }],
        partCounts: { 'a.py': { Changes: 2, Fix: 1 } },
    };
    const groups = groupFiles(files, state);
    assert.deepEqual(groups[0].files.map((f) => f.path), ['a.py', 'b.py']);
    assert.deepEqual(groups[1].files.map((f) => f.path), ['a.py', 'c.py']);
    assert.deepEqual(groups[0].files[0].part, { here: 2, total: 3 });
    assert.deepEqual(groups[1].files[0].part, { here: 1, total: 3 });
    assert.equal(groups[0].files[1].part, undefined, 'a file that is whole in its list has no count');
    assert.equal(groups[1].files[1].part, undefined);
});

test('without counts the files sit in their lists as before, and a list that is gone sends its part to the default one', () => {
    const state = { active: 'Changes', lists: [{ name: 'Changes', files: ['a.py'] }, { name: 'Fix', files: ['b.py'] }] };
    const groups = groupFiles(files, state);
    assert.deepEqual(groups.map((g) => g.files.map((f) => f.path)), [['a.py', 'c.py'], ['b.py']]);
    const odd = groupFiles([{ path: 'a.py' }], { active: 'Changes', lists: [{ name: 'Changes', files: ['a.py'] }], partCounts: { 'a.py': { Gone: 1, Changes: 1 } } });
    assert.deepEqual(odd[0].files.map((f) => f.part.here), [1, 1]);
});

test('a change is described by where it is and how long it is, and the ticked keys come in the order of the list', () => {
    assert.equal(placeText({ oldLine: 3, newLine: 3, added: 1, removed: 1 }), 'line 3');
    assert.equal(placeText({ oldLine: 10, newLine: 12, added: 4, removed: 0 }), 'line 12 (4 lines)');
    assert.equal(placeText({ oldLine: 7, newLine: 7, added: 0, removed: 1 }), 'line 7 (1 line removed)');
    assert.equal(placeText({ oldLine: 7, newLine: 7, added: 0, removed: 3 }), 'line 7 (3 lines removed)');
    const rows = [{ key: 'a' }, { key: 'b' }, { key: 'c' }];
    assert.deepEqual(checkedKeys(rows, new Set(['c', 'a', 'x'])), ['a', 'c']);
    assert.deepEqual(checkedKeys(rows, new Set()), []);
});

test('the lists of a change whose lines are split, and what a move of ticked changes and lines sends', () => {
    const lines = (...lists) => lists.map((list, i) => ({ kind: '+', text: 'l' + i, list }));
    const rows = [
        { key: 'k1', list: 'Changes', lines: lines('Changes', 'Fix', 'Changes') },
        { key: 'k2', list: 'Fix', lines: lines('Fix', 'Fix') },
        { key: 'k3', list: 'Changes', lines: lines('Changes') },
    ];
    assert.equal(listsText(rows[0]), 'Changes + Fix');
    assert.equal(listsText(rows[1]), 'Fix');
    assert.equal(listsText({ key: 'old', list: 'Changes' }), 'Changes');   // an answer without lines
    // k2 ticked whole (its ticked line goes with it), two lines of k1, nothing of k3
    const plan = movePlan(rows, new Set(['k2']), new Set(['k1@2', 'k1@0', 'k2@1']));
    assert.deepEqual(plan, { keys: ['k2'], lines: [{ key: 'k1', lines: [0, 2] }] });
    assert.deepEqual(movePlan(rows, new Set(), new Set()), { keys: [], lines: [] });
});

test('a file whose one change is split between two lists is "1 of 1" in each, with the total the engine sends', () => {
    const state = { active: 'Changes', lists: [{ name: 'Changes', files: ['a.py'] }, { name: 'Fix', files: [] }], partCounts: { 'a.py': { Changes: 1, Fix: 1 } }, partTotals: { 'a.py': 1 } };
    const grouped = groupFiles([{ path: 'a.py' }], state);
    assert.deepEqual(grouped.map((g) => g.files.map((f) => `${f.part.here}/${f.part.total}`).join()), ['1/1', '1/1']);
});

import test from 'node:test';
import assert from 'node:assert/strict';
import { startingPlan, move, setAction, setMessage, problem, changed, steps, summary } from '../src/rebase-model.js';

const commits = [
    { hash: 'a1', shortHash: 'a1', author: 'T', subject: 'add a' },
    { hash: 'b2', shortHash: 'b2', author: 'T', subject: 'add b' },
    { hash: 'c3', shortHash: 'c3', author: 'T', subject: 'add c' },
];
const names = (rows) => rows.map((r) => r.hash).join(',');

test('a starting plan picks every commit and changes nothing', () => {
    const rows = startingPlan(commits);
    assert.equal(names(rows), 'a1,b2,c3');
    assert.ok(rows.every((r) => r.action === 'pick'));
    assert.equal(changed(rows, commits), false);
    assert.match(problem(rows, commits), /Nothing is changed/);
});

test('moving a row, and not past the ends', () => {
    const rows = startingPlan(commits);
    assert.equal(names(move(rows, 2, -1)), 'a1,c3,b2');
    assert.equal(names(move(rows, 0, 1)), 'b2,a1,c3');
    assert.equal(move(rows, 0, -1), rows);
    assert.equal(move(rows, 2, 1), rows);
    assert.equal(changed(move(rows, 2, -1), commits), true);
});

test('actions, and the message of a reword', () => {
    let rows = setAction(startingPlan(commits), 1, 'reword');
    assert.match(problem(rows, commits), /message of b2 is empty/);
    rows = setMessage(rows, 1, 'Add the file b');
    assert.equal(problem(rows, commits), null);
    assert.deepEqual(steps(rows)[1], { hash: 'b2', action: 'reword', message: 'Add the file b' });
    assert.deepEqual(steps(rows)[0], { hash: 'a1', action: 'pick' });
    // another action takes the message away again
    assert.equal(setAction(rows, 1, 'drop')[1].message, null);
});

test('squashing needs a commit above that is kept', () => {
    let rows = setAction(startingPlan(commits), 0, 'squash');
    assert.match(problem(rows, commits), /cannot be squashed/);
    rows = setAction(setAction(startingPlan(commits), 0, 'drop'), 1, 'fixup');
    assert.match(problem(rows, commits), /cannot be a fixup/);
    rows = setAction(startingPlan(commits), 1, 'squash');
    assert.equal(problem(rows, commits), null);
});

test('dropping everything is refused, dropping some is a change', () => {
    let rows = startingPlan(commits).map((r) => ({ ...r, action: 'drop' }));
    assert.match(problem(rows, commits), /Every commit is dropped/);
    rows = setAction(startingPlan(commits), 2, 'drop');
    assert.equal(problem(rows, commits), null);
});

test('what the plan does, in a line', () => {
    const rows = setAction(move(startingPlan(commits), 2, -1), 0, 'reword');
    assert.equal(summary(rows, commits), '2 moved, 1 reworded');
    assert.equal(summary(startingPlan(commits), commits), '');
    assert.equal(summary(setAction(startingPlan(commits), 1, 'squash'), commits), '1 squashed');
    assert.equal(summary(setAction(startingPlan(commits), 1, 'drop'), commits), '1 dropped');
});

test('a commit marked edit counts as a change and is sent as it is', () => {
    const rows = setAction(startingPlan(commits), 1, 'edit');
    assert.equal(problem(rows, commits), null);
    assert.equal(summary(rows, commits), '1 to edit');
    assert.deepEqual(steps(rows)[1], { hash: commits[1].hash, action: 'edit' });
});

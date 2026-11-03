import { test } from 'node:test';
import assert from 'node:assert/strict';
import { recentProjectItems } from '../src/recentprojects-model.js';

test('the recent projects are followed by Remove from recent projects and Clear recent projects', () => {
    const done = [];
    const rows = recentProjectItems(['C:/a', 'C:/b'], { open: (p) => done.push('open ' + p), remove: (p) => done.push('remove ' + p), clear: () => done.push('clear') });
    assert.deepEqual(rows.map((r) => (r === '-' ? '-' : r.label)), ['C:/a', 'C:/b', '-', 'Remove from recent projects', 'Clear recent projects']);
    rows[1].run();
    rows[3].submenu[0].run();
    rows[4].run();
    assert.deepEqual(rows[3].submenu.map((r) => r.label), ['C:/a', 'C:/b']);
    assert.deepEqual(done, ['open C:/b', 'remove C:/a', 'clear']);
});

test('an empty list is one dim row, with nothing to remove', () => {
    const rows = recentProjectItems([], { open() {}, remove() {}, clear() {} }, 'No other recent projects');
    assert.deepEqual(rows, [{ label: 'No other recent projects', disabled: true }]);
});

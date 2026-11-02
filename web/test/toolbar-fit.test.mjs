// The command bar fitted into the window (web/src/toolbar-fit-model.js): labels first, then commands, least needed first.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fit, menuLabel, HIDE_ORDER } from '../src/toolbar-fit-model.js';

/** A bar of items with widths; `compactSaves` is what hiding the labels gives back. */
function bar(room, widths, compactSaves = 0) {
    const off = new Set(['already']);
    let compact = false;
    const used = () => Object.entries(widths).filter(([id]) => !off.has(id)).reduce((a, [, w]) => a + w, 0) - (compact ? compactSaves : 0);
    return {
        off,
        overflows: () => used() > room,
        setCompact: (on) => { compact = on; },
        hide: (id, on) => { if (on) off.add(id); else off.delete(id); },
        isCompact: () => compact,
    };
}

test('a bar that fits keeps its labels and every command', () => {
    const b = bar(500, { run: 100, a: 100, b: 100 }, 50);
    assert.deepEqual(fit({ ...b, order: ['a', 'b'] }), { compact: false, hidden: [] });
    assert.equal(b.isCompact(), false);
});

test('the labels go before any command, and then the commands in their order until it fits', () => {
    const fitsWithoutLabels = bar(260, { run: 100, a: 100, b: 100 }, 50);
    assert.deepEqual(fit({ ...fitsWithoutLabels, order: ['a', 'b'] }), { compact: true, hidden: [] });
    const two = bar(150, { run: 100, a: 100, b: 100 }, 50);
    assert.deepEqual(fit({ ...two, order: ['a', 'b'] }), { compact: true, hidden: ['a'] });
    const none = bar(10, { run: 100, a: 100, b: 100 });
    assert.deepEqual(fit({ ...none, order: ['a', 'b'] }), { compact: true, hidden: ['a', 'b'] });   // all it can do
    // what was hidden before is shown again first: a wider window gets it back
    const again = bar(1000, { run: 100, a: 100, b: 100 });
    again.off.add('a');
    assert.deepEqual(fit({ ...again, order: ['a', 'b'] }), { compact: false, hidden: [] });
    assert.equal(again.off.has('a'), false);
});

test('run, debug, stop, the configuration, the interpreter and the settings are never given up; the ⋯ menu names a command by its title', () => {
    for (const keep of ['b-run', 'b-debug', 'b-stop', 'run-config', 'interp', 'b-gear', 'b-more']) assert.equal(HIDE_ORDER.includes(keep), false, keep);
    assert.equal(menuLabel('Remove unused imports and sort the rest (Shift+Alt+O)'), 'Remove unused imports and sort the rest');
    assert.equal(menuLabel('Commit: open the Git panel on the commit message (Alt+9)'), 'Commit');
    assert.equal(menuLabel('', 'Pause'), 'Pause');
});

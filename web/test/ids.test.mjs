import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const src = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'src');
const html = readFileSync(resolve(src, 'index.html'), 'utf8');
const ids = new Set([...html.matchAll(/\sid="([^"]+)"/g)].map((m) => m[1]));

/** The ids a script looks up on the page: $('x'), press('x'), dock('x') (a tab t-x) and getElementById('x'). */
function lookedUp(file) {
    const text = readFileSync(resolve(src, file), 'utf8');
    const found = [];
    for (const m of text.matchAll(/\$\('([a-z][\w-]*)'\)/g)) found.push([m[1], m[0]]);
    for (const m of text.matchAll(/press\('([a-z][\w-]*)'\)/g)) found.push([m[1], m[0]]);
    for (const m of text.matchAll(/dock\('([a-z][\w-]*)'\)/g)) found.push([`t-${m[1]}`, m[0]]);
    for (const m of text.matchAll(/getElementById\('([a-z][\w-]*)'\)/g)) found.push([m[1], m[0]]);
    return found;
}

// the menus, the bar and the dialog press buttons and read boxes of other tools: every one must still be on the page
for (const file of ['mainmenu.js', 'toolbar.js', 'rail.js', 'settings-ui.js']) {
    test(`${file} only looks up elements the page has`, () => {
        const missing = lookedUp(file).filter(([id]) => !ids.has(id)).map(([, how]) => how);
        assert.deepEqual(missing, []);
    });
}

test('every id of the page is there once', () => {
    const all = [...html.matchAll(/\sid="([^"]+)"/g)].map((m) => m[1]);
    const twice = all.filter((id, i) => all.indexOf(id) !== i);
    assert.deepEqual(twice, []);
});

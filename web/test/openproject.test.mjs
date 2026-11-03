import { test } from 'node:test';
import assert from 'node:assert/strict';
import { openWhere, settingAfter, sameFolder, OPEN_PROJECT_IN } from '../src/openproject-model.js';
import { merged } from '../src/settings.js';

test('Open project in: asks unless set to this window or a new window', () => {
    assert.equal(openWhere('ask'), 'ask');
    assert.equal(openWhere('this'), 'this');
    assert.equal(openWhere('new'), 'new');
    assert.equal(openWhere(undefined), 'ask');
    assert.equal(openWhere('attach'), 'ask');
    assert.deepEqual(OPEN_PROJECT_IN.map(([v]) => v), ['ask', 'this', 'new']);
    assert.equal(merged({}).openProjectIn, 'ask');
});

test("Don't ask again makes the answer the setting; Cancel changes nothing", () => {
    assert.equal(settingAfter('ask', 'new', true), 'new');
    assert.equal(settingAfter('ask', 'this', true), 'this');
    assert.equal(settingAfter('ask', 'new', false), 'ask');
    assert.equal(settingAfter('ask', 'cancel', true), 'ask');
});

test('the same folder, whatever its case and last backslash', () => {
    assert.ok(sameFolder('C:\\Work\\Shop', 'c:/work/shop/'));
    assert.ok(!sameFolder('C:\\Work\\Shop', 'C:\\Work\\Shop2'));
    assert.ok(!sameFolder('', ''));
});

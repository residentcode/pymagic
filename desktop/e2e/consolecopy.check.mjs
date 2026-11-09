// Copy and paste in the Python console of the built PyMagic.exe: the right-click menu of its output and of its input (Copy of what is
// selected, Paste into the input, Select all, Copy the whole output), and Ctrl+C / Ctrl+V with a selection made by the mouse.
// Run: node desktop/e2e/consolecopy.check.mjs   (needs uv on the PATH for the project's .venv)
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { launch, project, sleep } from './app.mjs';

const root = project({ 'a.py': 'x = 1\n' });
assert.equal(spawnSync('uv', ['venv', '-q', '.venv'], { cwd: root }).status, 0, 'uv venv');
const app = await launch(root);
const { page } = app;
const OUT = `document.querySelector('#pyconsole-tab .pyc-out:not([hidden])')`;
const inputValue = () => page.evaluate(`document.getElementById('pyc-input').value`);
const rightClick = async (expression) => {
    const [x, y] = await page.evaluate(`(() => { const b = ${expression}.getBoundingClientRect(); return [b.left + 20, b.top + 10]; })()`);
    await page.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'right', clickCount: 1 });
    await page.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'right', clickCount: 1 });
    return page.waitFor(`(() => { const m = document.querySelector('.ctx-menu'); return m ? [...m.querySelectorAll('.ctx-item')].map((r) => r.querySelector('.ctx-label').textContent + (r.classList.contains('disabled') || r.getAttribute('aria-disabled') === 'true' ? ' (off)' : '')) : null; })()`, 5000, 'the menu');
};
// a row of the menu clicked as a person does (the clipboard needs a real click)
const choose = async (label) => {
    const [x, y] = await page.evaluate(`(() => { const r = [...document.querySelectorAll('.ctx-menu .ctx-item')].find((i) => i.querySelector('.ctx-label').textContent === ${JSON.stringify(label)}).getBoundingClientRect(); return [r.left + 10, r.top + r.height / 2]; })()`);
    await page.click(x, y);
    await sleep(400);
};
try {
    await page.evaluate(`document.getElementById('t-pyconsole').click()`);
    await page.waitFor(OUT, 30000, 'the console');
    await page.waitFor(`${OUT}.textContent.includes(' on win32')`, 60000, 'the console started');
    await page.evaluate(`(() => { const i = document.getElementById('pyc-input'); i.focus(); i.value = "print('hello world')"; })()`);
    await page.key(13, 'Enter');
    await page.waitFor(`${OUT}.textContent.includes('hello world\\r\\n') || ${OUT}.textContent.split('hello world').length > 2`, 20000, 'the output');

    // the output's menu with nothing selected: Copy is off, Paste and the rest are there
    await page.evaluate(`getSelection().removeAllRanges()`);
    let rows = await rightClick(OUT);
    console.log('output menu, nothing selected:', rows);
    assert.ok(rows.includes('Copy (off)') && rows.includes('Paste') && rows.includes('Select all') && rows.includes('Copy the whole output') && rows.includes('Clear'));
    await page.key(27, 'Escape');

    // a mouse selection of the output, Copy from the menu, Paste from the input's menu
    const box = await page.evaluate(`(() => { const b = ${OUT}.getBoundingClientRect(); return [b.left + 12, b.top + 9, b.left + 260, b.top + 50]; })()`);
    await page.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: box[0], y: box[1], button: 'left', clickCount: 1 });
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: box[2], y: box[3], button: 'left', buttons: 1 });
    await page.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: box[2], y: box[3], button: 'left', clickCount: 1 });
    const selected = await page.evaluate(`getSelection().toString().replace(/\\r\\n/g, '\\n')`);
    assert.ok(selected.includes('hello world'), 'the selection: ' + selected);
    rows = await rightClick(OUT);
    assert.ok(rows.includes('Copy'), 'Copy is on: ' + rows);
    await choose('Copy');
    await page.evaluate(`document.getElementById('pyc-input').value = ''`);
    rows = await rightClick(`document.getElementById('pyc-input')`);
    console.log('input menu:', rows);
    assert.ok(rows.includes('Cut (off)') && rows.includes('Paste'));
    await choose('Paste');
    const pasted = await inputValue();
    console.log('pasted from the menu:', JSON.stringify(pasted));
    assert.equal(pasted, selected);

    // Ctrl+V while the output has the focus goes into the input
    await page.evaluate(`document.getElementById('pyc-input').value = ''; ${OUT}.focus()`);
    await page.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', windowsVirtualKeyCode: 86, key: 'v', code: 'KeyV', modifiers: 2 });
    await page.send('Input.dispatchKeyEvent', { type: 'keyUp', windowsVirtualKeyCode: 86, key: 'v', code: 'KeyV', modifiers: 2 });
    await sleep(400);
    assert.equal(await inputValue(), selected, 'Ctrl+V in the output pastes into the input');
    console.log('Ctrl+V in the output pasted into the input');

    // Copy the whole output, pasted into the input
    await page.evaluate(`document.getElementById('pyc-input').value = ''`);
    await rightClick(OUT);
    await choose('Copy the whole output');
    await rightClick(`document.getElementById('pyc-input')`);
    await choose('Paste');
    const whole = await inputValue();
    assert.ok(whole.includes('Python 3.') && whole.includes(">>> print('hello world')") && whole.includes('hello world'), 'the whole output: ' + whole);
    console.log('the whole output copied and pasted:', JSON.stringify(whole.slice(0, 80)));

    // the input: its text selected (the right-click falls on the selection, as a person's does), Cut takes it out and Paste puts it back
    await page.evaluate(`(() => { const i = document.getElementById('pyc-input'); i.value = 'abc def'; i.focus(); i.select(); })()`);
    rows = await rightClick(`document.getElementById('pyc-input')`);
    assert.ok(rows.includes('Cut') && rows.includes('Copy'), 'the input menu with text selected: ' + rows);
    await choose('Cut');
    assert.equal(await inputValue(), '');
    await rightClick(`document.getElementById('pyc-input')`);
    await choose('Paste');
    assert.equal(await inputValue(), 'abc def');
    console.log('Cut and Paste in the input');
    console.log('OK');
} finally {
    await app.close();
}

// PHP on another machine in the built PyMagic.exe: Settings > PHP names it (ssh:root@host?i=KEY&kh=KNOWN|php), and then Settings says what
// is there (PHP, Xdebug, Phpactor); a .php file has Phpactor's completion after ->, its hover, and its problems in the editor; Debug runs the
// file there with Xdebug and stops at a breakpoint, the frame named as the project's file here, its value in Variables.
// Run: PYMAGIC_CT_HOST=… PYMAGIC_CT_KEY=… PYMAGIC_CT_KNOWN_HOSTS=… node desktop/e2e/phpssh.check.mjs
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { launch, sleep } from './app.mjs';

const { PYMAGIC_CT_HOST: host, PYMAGIC_CT_KEY: key, PYMAGIC_CT_KNOWN_HOSTS: known } = process.env;
assert.ok(host && key && known, 'PYMAGIC_CT_HOST, PYMAGIC_CT_KEY and PYMAGIC_CT_KNOWN_HOSTS');
const spec = `ssh:root@${host}?i=${key}&kh=${known}|php`;
const root = join(mkdtempSync(join(tmpdir(), 'pymagic-e2e-phpssh-')), 'shop');
mkdirSync(join(root, 'src'), { recursive: true });
writeFileSync(join(root, 'src', 'Item.php'), [
    '<?php', 'namespace Shop;', '', '/** One thing of the shop. */', 'class Item', '{',
    '    public function __construct(public string $name, public int $price) {}', '',
    '    /** The price with the tax. */',
    '    public function gross(float $rate): float { return $this->price * (1 + $rate); }', '}', '',
].join('\n'));
const MAIN = [
    '<?php',                                          // 1
    'use Shop\\Item;',                                // 2
    "require __DIR__ . '/src/Item.php';",             // 3
    "$pen = new Item('pen', 3);",                     // 4
    '$total = $pen->gross(0.2);',                     // 5
    "echo 'on ' . PHP_OS_FAMILY . ': ' . $total . PHP_EOL;", // 6
    '',
];
writeFileSync(join(root, 'main.php'), MAIN.join('\n'));
mkdirSync(join(root, '.pymagic'), { recursive: true });
writeFileSync(join(root, '.pymagic', 'run-configurations.json'), JSON.stringify({ configurations: [{ name: 'main', kind: 'php', script: 'main.php', args: '', cwd: '', env: {} }], selected: 'main' }, null, 2));
const $ = (id) => `document.getElementById(${JSON.stringify(id)})`;

const app = await launch(root, { phpPath: spec });
const { page } = app;
try {
    // what is there, as Settings > PHP tells it
    const found = await page.evaluate(`fetch('/api/php').then((r) => r.json())`);
    console.log('found:', JSON.stringify({ php: found.php, version: found.version, xdebug: found.xdebug?.version, phpactor: found.phpactor }));
    assert.match(found.version, /^8\.\d+\.\d+/);
    assert.equal(found.remote, `root@${host}`);
    assert.ok(found.xdebug.loaded && found.phpactor, 'Xdebug and Phpactor there');

    await page.evaluate(`window.pymagic.openFile('main.php')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.getLanguageId() === 'php'`, 20000, 'main.php');

    // completion after `$pen->` typed at the end of the file (asked again until Phpactor has indexed the project)
    await page.evaluate(`(() => { const ed = window.pymagic.editor; const m = ed.getModel(); const last = m.getLineCount();
        ed.setPosition({ lineNumber: last, column: 1 }); ed.focus(); })()`);
    await page.evaluate(`window.pymagic.editor.trigger('test', 'type', { text: '$pen->' })`);
    let labels = [];
    for (let i = 0; i < 40 && !labels.includes('gross'); i++) {
        // (an open list is not asked again: it is closed first)
        await page.evaluate(`window.pymagic.editor.trigger('test', 'hideSuggestWidget', {})`);
        await page.evaluate(`window.pymagic.editor.trigger('test', 'editor.action.triggerSuggest', {})`);
        await sleep(2500);
        labels = await page.evaluate(`[...document.querySelectorAll('.suggest-widget.visible .monaco-list-row')].map((x) => x.getAttribute('aria-label'))`);
    }
    console.log('completion after', JSON.stringify(await page.evaluate(`window.pymagic.editor.getModel().getLineContent(window.pymagic.editor.getPosition().lineNumber)`)), ':', labels.slice(0, 6));
    assert.ok(labels.includes('gross') && labels.includes('price'), labels.join(','));
    await page.key(27, 'Escape');
    // the typed text taken back: the file as it was
    await page.evaluate(`window.pymagic.editor.trigger('test', 'undo', {})`);

    // hover on gross (line 5): its doc comment
    const at = await page.evaluate(`(() => { const ed = window.pymagic.editor; const col = ed.getModel().getLineContent(5).indexOf('gross') + 3;
        const p = ed.getScrolledVisiblePosition({ lineNumber: 5, column: col }); const box = ed.getContainerDomNode().getBoundingClientRect(); return { x: box.left + p.left, y: box.top + p.top + p.height / 2 }; })()`);
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: at.x - 30, y: at.y + 50 });
    await sleep(100);
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: at.x, y: at.y });
    const hover = await page.waitFor(`[...document.querySelectorAll('.monaco-hover')].map((h) => h.textContent).find((t) => t.includes('The price with the tax'))`, 30000, 'the hover');
    console.log('hover:', hover.replace(/\s+/g, ' ').slice(0, 120));

    // a problem: a class that is not there, in the editor's markers
    await page.evaluate(`(() => { const m = window.pymagic.editor.getModel(); m.pushEditOperations([], [{ range: { startLineNumber: 7, startColumn: 1, endLineNumber: 7, endColumn: 1 }, text: '$x = new Missing();\\n' }], () => null); })()`);
    const marker = await page.waitFor(`window.pymagic.monaco.editor.getModelMarkers({ resource: window.pymagic.editor.getModel().uri }).map((m) => m.message).find((t) => t.includes('Missing'))`, 60000, 'the problem');
    console.log('problem:', marker);
    await page.evaluate(`window.pymagic.editor.trigger('test', 'undo', {})`);

    // Debug: main.php run there with Xdebug, stopped at line 6, $total in Variables
    const added = await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.revealLine(6); const box = ed.getContainerDomNode().getBoundingClientRect();
        return { x: box.left + ed.getLayoutInfo().glyphMarginLeft + 5, y: box.top + ed.getTopForLineNumber(6) - ed.getScrollTop() + 8 }; })()`);
    await page.click(added.x, added.y);
    await sleep(300);
    await page.evaluate(`${$('b-debug')}.click()`);
    const stop = await page.waitFor(`!${$('b-over')}.disabled && document.querySelector('#frames div.sel')?.textContent`, 180000, 'the stop')
        .catch(async (e) => { console.log('output:', await page.evaluate(`${$('output')}.textContent`)); throw e; });
    console.log('stopped:', stop);
    assert.ok(stop.includes('main.php:6'), stop);
    const vars = await page.waitFor(`(() => { const t = [...document.querySelectorAll('#vars li > div')].map((d) => d.textContent.replace(/\\s+/g, ' ').trim()); return t.some((x) => x.startsWith('$total')) ? t : null; })()`, 20000, 'the Locals');
    console.log('Variables:', vars.slice(0, 3));
    assert.ok(vars.some((x) => /\$total = 3\.(6|59)/.test(x)), vars.join(' | '));
    await page.evaluate(`${$('b-debug')}.click()`);
    const out = await page.waitFor(`(() => { const t = ${$('output')}.textContent.split('Running').pop(); return /exit code/.test(t) ? t : null; })()`, 60000, 'the end');
    console.log('output:', JSON.stringify(out.trim().split('\n').slice(-3)));
    assert.match(out, /on Linux: 3\.6/);
    console.log('OK');
} finally {
    await app.close();
}

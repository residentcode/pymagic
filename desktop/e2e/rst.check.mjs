// A one-off check, not part of the suite (it needs a Python with docutils): the reStructuredText preview of the built exe.
//   PYMAGIC_CHECK_PYTHON=<python with docutils> node desktop/e2e/rst.check.mjs
import assert from 'node:assert/strict';
import { dirname } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const python = process.env.PYMAGIC_CHECK_PYTHON;
assert.ok(python, 'see the top of this file');
process.env.PATH = dirname(python) + ';' + process.env.PATH;
const rst = 'Title\n=====\n\nA *word*, **strong**, ``code`` and `a link <https://example.org/>`_.\n\n- one\n- two\n\n.. raw:: html\n\n   <script>window.__pwned = 1</script>\n\n`bad <javascript:alert(1)>`_\n\n.. code-block:: python\n\n   print(1)\n\n+-----+-----+\n| a   | b   |\n+=====+=====+\n| 1   | 2   |\n+-----+-----+\n';
const app = await launch(project({ 'doc.rst': rst }));
const page = app.page;
try {
    await page.evaluate(`window.pymagic.openFile('doc.rst')`);
    await sleep(1500);
    await page.evaluate(`document.getElementById('md-toggle').click()`);
    await page.waitFor(`document.querySelector('.md-body h1')`, 30000, 'the preview from docutils');
    const info = await page.evaluate(`(() => { const b = document.querySelector('.md-body'); return {
        h1: b.querySelector('h1')?.textContent, em: b.querySelector('em')?.textContent, strong: b.querySelector('strong')?.textContent,
        li: b.querySelectorAll('li').length, th: [...b.querySelectorAll('th')].map((t) => t.textContent), td: [...b.querySelectorAll('td')].map((t) => t.textContent),
        links: [...b.querySelectorAll('a[data-href]')].map((a) => a.dataset.href), scripts: b.querySelectorAll('script').length, pwned: window.__pwned ?? null,
        text: b.textContent.slice(0, 60) }; })()`);
    console.log(JSON.stringify(info));
    assert.equal(info.h1, 'Title');
    assert.equal(info.em, 'word');
    assert.equal(info.strong, 'strong');
    assert.equal(info.li, 2);
    assert.deepEqual(info.th, ['a', 'b']);
    assert.deepEqual(info.td, ['1', '2']);
    assert.ok(info.links.includes('https://example.org/'));
    assert.ok(!info.links.some((l) => l.startsWith('javascript')), 'a javascript: link is not kept');
    assert.equal(info.scripts, 0);
    assert.equal(info.pwned, null);
    console.log('rst preview ok');
} finally { await app.close(); }

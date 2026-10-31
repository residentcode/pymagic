// Checks of web/src/embedded-model.js: `node web/test/embedded.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/embedded-model.js', import.meta.url), 'utf8');
const { regionsOf, embeddedDocument, inRegion } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const page = '<html>\n<style>\n  p { color: red }\n</style>\n<p style="margin: 0">{{ title }}</p>\n<script>\n  let n = {{ count }};\n  {% if x %}go();{% endif %}\n</script>\n<script type="text/template"><b></b></script>\n</html>\n';

test('the regions of CSS and JavaScript, a script that is not JavaScript left out', () => {
    const css = regionsOf(page, 'css').map(([s, e]) => page.slice(s, e));
    assert.deepEqual(css, ['\n  p { color: red }\n', 'margin: 0']);
    const js = regionsOf(page, 'javascript').map(([s, e]) => page.slice(s, e));
    assert.deepEqual(js, ['\n  let n = {{ count }};\n  {% if x %}go();{% endif %}\n']);
});

test('the documents keep the template\'s length and lines; template expressions become values', () => {
    for (const language of ['css', 'javascript']) {
        const doc = embeddedDocument(page, language);
        assert.equal(doc.length, page.length);
        assert.deepEqual(doc.split('\n').length, page.split('\n').length);
    }
    const js = embeddedDocument(page, 'javascript');
    assert.ok(js.includes('  let n = 0          ;'));
    assert.ok(js.includes('            go();'), js);
    assert.ok(!js.includes('<p'));
    const css = embeddedDocument(page, 'css');
    assert.ok(css.includes('p { color: red }'));
    assert.ok(css.includes('*{margin: 0}'), css);
    assert.ok(inRegion(page, page.indexOf('color'), 'css') && !inRegion(page, page.indexOf('title'), 'css'));
});

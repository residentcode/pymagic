// A one-off check, not part of the suite (it uses the network): the question and the download of SchemaStore's schemas in the built exe.
//   node desktop/e2e/download.check.mjs
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const FILES = {
    'package.json': '{\n  "name": "ok-name",\n  "scripts": {\n    "t": 1\n  },\n  "private": "yes"\n}\n',
    'pyproject.toml': '[project]\nname = 5\n',
};
const markers = `(() => { const m = window.pymagic.editor.getModel(); return window.pymagic.monaco.editor.getModelMarkers({ resource: m.uri }).filter((x) => x.code === 'schema').map((x) => x.startLineNumber + ': ' + x.message); })()`;

async function session(answer) {
    const root = project(FILES);
    const app = await launch(root);
    const page = app.page;
    // the question is a confirm(); answer it and count the asks
    await page.evaluate(`(() => { window.__asked = 0; window.confirm = (t) => { window.__asked++; window.__text = t; return ${answer}; }; })()`);
    await page.evaluate(`window.pymagic.openFile('package.json')`);
    await sleep(4000);
    const result = {
        root,
        asked: await page.evaluate('window.__asked'),
        text: await page.evaluate('window.__text'),
        first: await page.evaluate(markers),
        kept: ['package', 'pyproject'].map((n) => existsSync(join(root, '.pymagic', 'schemas', n + '.json'))),
    };
    if (answer) {
        await page.evaluate(`window.pymagic.openFile('pyproject.toml')`);
        await sleep(4000);
        result.second = await page.evaluate(markers);
        result.askedAfter = await page.evaluate('window.__asked');
        result.kept = ['package', 'pyproject'].map((n) => existsSync(join(root, '.pymagic', 'schemas', n + '.json')));
    }
    await app.close();
    return result;
}

const yes = await session(true);
console.log(JSON.stringify(yes, null, 1));
assert.equal(yes.asked, 1, 'asked once');
assert.match(yes.text, /json\.schemastore\.org/);
assert.ok(yes.first.some((m) => m.startsWith('4: ')), 'scripts.t marked');
assert.ok(yes.first.some((m) => m.startsWith('6: ')), 'private marked');
assert.deepEqual(yes.kept, [true, true]);
assert.deepEqual(yes.second, ['2: Should be string, not integer']);
assert.equal(yes.askedAfter, 1, 'not asked again for the second file');

const no = await session(false);
console.log(JSON.stringify(no, null, 1));
assert.equal(no.asked, 1);
assert.deepEqual(no.first, []);
assert.deepEqual(no.kept, [false, false]);
console.log('download flow ok');

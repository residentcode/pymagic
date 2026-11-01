// A one-off check, not part of the suite (it uses the network): the question and the download of SchemaStore's schemas in the built exe.
//   node desktop/e2e/download.check.mjs
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const FILES = {
    'package.json': '{\n  "name": "ok-name",\n  "scripts": {\n    "t": 1\n  },\n  "private": "yes"\n}\n',
    'pyproject.toml': '[project]\nname = 5\n',
    'docker-compose.yml': 'services:\n  web:\n    image: 5\n    restart_policy: always\n',
    '.gitlab-ci.yml': 'stages: build\n',
    '.pre-commit-config.yaml': 'fail_fast: sometimes\n',
    'mkdocs.yml': 'site_name: 5\nnav: home\n',
    '.github/dependabot.yml': 'version: 3\nupdates: []\n',
    'codecov.yml': 'coverage: yes\n',
    'tox.toml': 'env_list = "py312"\n',
    '.github/workflows/ci.yml': 'on:\n  push:\n    branches: main\njobs:\n  t:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n',
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
        kept: ['package', 'pyproject', 'workflow', 'compose', 'gitlab', 'precommit', 'mkdocs', 'dependabot', 'codecov', 'tox'].map((n) => existsSync(join(root, '.pymagic', 'schemas', n + '.json'))),
    };
    if (answer) {
        await page.evaluate(`window.pymagic.openFile('pyproject.toml')`);
        await sleep(4000);
        result.second = await page.evaluate(markers);
        for (let i = 0; i < 4; i++) {
            await page.evaluate(`window.pymagic.openFile('.github/workflows/ci.yml')`);
            await sleep(3000);
            if ((await page.evaluate('window.pymagic.editor.getModel().uri.toString()')).endsWith('ci.yml')) break;
        }
        result.model = await page.evaluate('window.pymagic.editor.getModel().uri.toString()');
        result.workflow = await page.evaluate(markers);
        for (const file of ['docker-compose.yml', '.gitlab-ci.yml', '.pre-commit-config.yaml', 'mkdocs.yml', '.github/dependabot.yml', 'codecov.yml', 'tox.toml']) {
            for (let i = 0; i < 4; i++) {
                await page.evaluate(`window.pymagic.openFile('${file}')`);
                await sleep(3500);
                if ((await page.evaluate('window.pymagic.editor.getModel().uri.toString()')).endsWith(file)) break;
            }
            (result.more ??= {})[file] = await page.evaluate(markers);
        }
        result.askedAfter = await page.evaluate('window.__asked');
        result.kept = ['package', 'pyproject', 'workflow', 'compose', 'gitlab', 'precommit', 'mkdocs', 'dependabot', 'codecov', 'tox'].map((n) => existsSync(join(root, '.pymagic', 'schemas', n + '.json')));
    }
    await app.close();
    return result;
}

const yes = await session(true);
console.log(JSON.stringify(yes, null, 1));
assert.equal(yes.asked, 1, 'asked once');
assert.match(yes.text, /schemastore\.org/);
assert.ok(yes.first.some((m) => m.startsWith('4: ')), 'scripts.t marked');
assert.ok(yes.first.some((m) => m.startsWith('6: ')), 'private marked');
assert.deepEqual(yes.kept, Array(10).fill(true));
assert.deepEqual(yes.more['docker-compose.yml'], ['3: Should be string, not integer', '4: The property restart_policy is not allowed here']);
assert.deepEqual(yes.more['.gitlab-ci.yml'], ['1: Should be array, not string']);
assert.deepEqual(yes.more['mkdocs.yml'], ['1: Should be string, not integer', '2: Should be array, not string']);
assert.deepEqual(yes.more['.github/dependabot.yml'], ['1: Should be 2']);
assert.deepEqual(yes.more['codecov.yml'], ['1: Should be object, not string']);
assert.deepEqual(yes.more['tox.toml'], ['1: Should be array, not string']);
assert.deepEqual(yes.more['.pre-commit-config.yaml'], ['1: The property repos is required', '1: Should be boolean, not string']);
assert.deepEqual(yes.workflow, ['3: Should be array, not string']);
assert.deepEqual(yes.second, ['2: Should be string, not integer']);
assert.equal(yes.askedAfter, 1, 'not asked again for the second file');

const no = await session(false);
console.log(JSON.stringify(no, null, 1));
assert.equal(no.asked, 1);
assert.deepEqual(no.first, []);
assert.deepEqual(no.kept, Array(10).fill(false));
console.log('download flow ok');

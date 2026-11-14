// A project with several configs, and one too large to be given whole, in the built PyMagic.exe. A solution's tsconfig.json ("files": [],
// references to tsconfig.app.json and tsconfig.node.json, as Vite writes them): a file of src is checked by the app's config (strict, with
// the DOM) and vite.config.ts by the node one (no DOM: document is not a name there), the options following the file in the editor. Then
// the same project with the limits set to two files and no package types: what the open file imports (a file three folders down, what that
// one imports, a package) is read as the file is opened, and a type error through each is found.
// Run: node desktop/e2e/tsconfigs.check.mjs
import assert from 'node:assert/strict';
import { launch, project, sleep } from './app.mjs';

const files = {
    'package.json': '{"name":"probe","dependencies":{"shouty":"1.0.0"}}',
    'tsconfig.json': '{ "files": [], "references": [{ "path": "./tsconfig.app.json" }, { "path": "./tsconfig.node.json" }] }\n',
    'tsconfig.app.json': '{\n  "compilerOptions": { "strict": true, "target": "ES2022", "lib": ["ES2022", "DOM"], "module": "ESNext", "moduleResolution": "Bundler" },\n  "include": ["src"]\n}\n',
    'tsconfig.node.json': '{\n  "compilerOptions": { "strict": true, "target": "ES2023", "lib": ["ES2023"], "module": "ESNext", "moduleResolution": "Bundler" },\n  "include": ["vite.config.ts"]\n}\n',
    'vite.config.ts': 'const title = document.title;\nlet where: string = null;\nexport default { title, where };\n',
    'src/main.ts': "import { twice } from './deep/a/b/util';\nimport { shout } from 'shouty';\nconst body = document.body;\nlet name: string = null;\nconst n: string = twice(2);\nshout(1);\nexport { body, name, n };\n",
    'src/deep/a/b/util.ts': "import { base } from './more';\nexport function twice(n: number): number { return n * base; }\n",
    'src/deep/a/b/more.ts': 'export const base: number = 2;\n',
    'node_modules/shouty/package.json': '{"name":"shouty","version":"1.0.0","types":"index.d.ts"}',
    'node_modules/shouty/index.d.ts': 'export declare function shout(s: string): string;\n',
};

async function run(what, check) {
    const app = await launch(project(files));
    const { page } = app;
    const markers = () => page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ resource: window.pymagic.editor.getModel().uri }).map((m) => m.startLineNumber + ': ' + m.message.split('\\n')[0]).sort()`);
    const open = async (f) => {
        await page.evaluate(`window.pymagic.openFile('${f}')`);
        await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('${f.split('/').pop()}')`, 20000, f);
    };
    /** The open file's findings once they are the same twice, a second apart, and `test` passes. */
    const settled = async (test, tries = 45) => {
        let last = null;
        for (let i = 0; i < tries; i++) {
            const now = JSON.stringify(await markers());
            if (now === last && test(JSON.parse(now))) return JSON.parse(now);
            last = now;
            await sleep(1000);
        }
        assert.fail(`${what}: the findings stayed ${last}`);
    };
    const state = () => page.evaluate(`(async () => { await window.pymagic.tsProject.ready(); return window.pymagic.tsProject.state(); })()`);
    try { await check({ page, open, settled, state }); }
    finally { await app.close(); }
}

// --- several configs
await run('configs', async ({ page, open, settled, state }) => {
    await open('src/main.ts');
    const main = await settled((m) => m.some((x) => x.startsWith('4:')));
    console.log('src/main.ts:', main, (await state()).used);
    // strict (null is not a string), with the DOM (document is known); the argument's type through the other file and the package
    assert.deepEqual(main, ["4: Type 'null' is not assignable to type 'string'.", "5: Type 'number' is not assignable to type 'string'.", "6: Argument of type 'number' is not assignable to parameter of type 'string'."]);
    assert.equal((await state()).used, 'tsconfig.app.json');
    assert.equal((await state()).configs.length, 3);
    await open('vite.config.ts');
    const vite = await settled((m) => m.some((x) => /document/.test(x)));
    console.log('vite.config.ts:', vite, (await state()).used);
    assert.equal((await state()).used, 'tsconfig.node.json');
    assert.ok(vite.some((x) => /^1: Cannot find name 'document'/.test(x)), vite.join(' | '));
    assert.ok(vite.includes("2: Type 'null' is not assignable to type 'string'."));
    // back to the app's file: its config again, document known
    await open('src/main.ts');
    const again = await settled((m) => m.length === 3 && !m.some((x) => /document/.test(x)));
    assert.equal((await state()).used, 'tsconfig.app.json');
    assert.equal(again.length, 3);
    // a config changed on disk is read again: strict taken away, null is fine
    await page.evaluate(`fetch('/api/file?path=tsconfig.app.json', { method: 'PUT', body: ${JSON.stringify('{\n  "compilerOptions": { "strict": false, "lib": ["ES2022", "DOM"], "module": "ESNext", "moduleResolution": "Bundler" },\n  "include": ["src"]\n}\n')} })`);
    const loose = await settled((m) => !m.some((x) => x.startsWith('4:')));
    console.log('strict taken away:', loose);
    assert.equal(loose.length, 2);
});

// --- a project too large to be given whole: two files, no package's types
process.env.PYMAGIC_TS_MAX_FILES = '2';
process.env.PYMAGIC_TS_MAX_PACKAGE_BYTES = '60';   // (the package.json files are given always: no .d.ts fits)
await run('large', async ({ page, open, settled, state }) => {
    const before = await state();
    console.log('given at first:', before.files, 'files, truncated', before.truncated, '| packages truncated', before.packagesTruncated);
    assert.equal(before.truncated, true);
    assert.equal(before.packagesTruncated, true);
    assert.equal(await page.evaluate(`window.pymagic.tsProject.has('src/deep/a/b/util.ts')`), false);
    await open('src/main.ts');
    const main = await settled((m) => m.length === 3);
    console.log('src/main.ts:', main);
    assert.deepEqual(main, ["4: Type 'null' is not assignable to type 'string'.", "5: Type 'number' is not assignable to type 'string'.", "6: Argument of type 'number' is not assignable to parameter of type 'string'."]);
    assert.deepEqual(await page.evaluate(`['src/deep/a/b/util.ts', 'src/deep/a/b/more.ts', 'node_modules/shouty/index.d.ts'].map((p) => window.pymagic.tsProject.has(p))`), [true, true, true]);
});
console.log('OK');

// The TypeScript service and the project in the built PyMagic.exe: an import of another file of the project and of a package (node_modules'
// .d.ts) resolves, a type error through them is found, Go to Definition reaches the other file, the tsconfig's strict holds; a .js file is
// type-checked with // @ts-check (as VS Code does) and not without; a .vue and a .svelte component's <script lang="ts"> has its type errors
// (not Vue's macros, not a name the markup reads, not Svelte's $store); a file made on disk while the project is open is taken.
// Run: node desktop/e2e/tsproject.check.mjs
import assert from 'node:assert/strict';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const root = project({
    'src/util.ts': 'export function twice(n: number): number { return n * 2; }\n',
    'src/main.ts': "import { twice } from './util';\nimport { shout } from 'shouty';\nimport { later } from './later';\nconst a: number = twice(2);\nconst b: string = twice(3);\nshout('x');\nshout(1);\nlater();\nexport { a, b };\n",
    // (two scripts, not modules: their globals are the project's, so they are named apart, as they would have to be)
    'src/plain.js': 'const k = 5;\nk.toUpperCase();\n',
    'src/checked.js': '// @ts-check\nlet n = 5;\nn.toUpperCase();\n',
    'src/Comp.vue': '<template>\n  <p>{{ shown }}</p>\n</template>\n<script setup lang="ts">\nconst props = defineProps<{ title: string }>();\nconst shown = props.title;\nconst wrong: number = "x";\n</script>\n<style lang="scss">\n$c: red;\np { color: $c; }\n</style>\n',
    'src/App.svelte': '<script lang="ts">\n  import { writable } from "./store";\n  const count = writable(0);\n  let total: number = "y";\n  $: doubled = $count * 2;\n</script>\n<p>{doubled} {total}</p>\n',
    'src/store.ts': 'export function writable<T>(v: T) { return { subscribe(f: (v: T) => void) { f(v); return () => {}; } }; }\n',
    'node_modules/shouty/package.json': '{"name":"shouty","version":"1.0.0","types":"index.d.ts"}',
    'node_modules/shouty/index.d.ts': 'export declare function shout(s: string): string;\n',
    'tsconfig.json': '{\n  // strict, as a Vite project has it\n  "compilerOptions": { "strict": true, "target": "ES2022", "module": "ESNext", "moduleResolution": "Bundler", },\n}\n',
    'package.json': '{"name":"probe","dependencies":{"shouty":"1.0.0"}}',
});
const app = await launch(root);
const { page } = app;
const markersOf = (f) => page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ resource: window.pymagic.editor.getModel().uri }).map((m) => m.startLineNumber + ': ' + m.message.split('\\n')[0]).sort()`);
async function open(f) {
    await page.evaluate(`window.pymagic.openFile('${f}')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('${f.split('/').pop()}')`, 20000, f);
}
/** The open file's findings once they have settled (the same twice, a second apart), those `test` passes. */
async function settled(f, test = () => true, tries = 40) {
    let last = null;
    for (let i = 0; i < tries; i++) {
        const now = JSON.stringify(await markersOf(f));
        if (now === last && test(JSON.parse(now))) return JSON.parse(now);
        last = now;
        await sleep(1000);
    }
    throw new Error(`${f}: the findings did not settle as expected: ${last}`);
}
try {
    await page.waitFor(`!!window.pymagic?.tsProject`, 30000, 'the TypeScript project');
    await page.evaluate(`window.pymagic.tsProject.ready()`);
    const st = await page.evaluate(`window.pymagic.tsProject.state()`);
    console.log('given to the TypeScript service:', st);
    assert.equal(st.js, true);
    assert.ok(st.files === 5 && st.packages === 2, JSON.stringify(st));   // util, main, plain, checked, store; shouty's package.json and index.d.ts

    await open('src/main.ts');
    let main = await settled('src/main.ts', (m) => m.some((x) => /^5: /.test(x)));
    console.log('main.ts:', main);
    assert.ok(!main.some((m) => /Cannot find module '\.\/util'|Cannot find module 'shouty'/.test(m)), main.join(' | '));
    assert.ok(main.some((m) => /^5: Type 'number' is not assignable to type 'string'/.test(m)), 'a type error through the import');
    assert.ok(main.some((m) => /^7: Argument of type 'number' is not assignable to parameter of type 'string'/.test(m)), "a type error through the package's types");
    assert.ok(main.some((m) => /^3: Cannot find module '\.\/later'/.test(m)), 'a file that is not there yet');

    // Go to Definition of twice: util.ts
    const def = await page.evaluate(`(async () => { const m = window.pymagic.editor.getModel(); const w = await window.pymagic.monaco.languages.typescript.getTypeScriptWorker(); const c = await w(m.uri); const d = await c.getDefinitionAtPosition(m.uri.toString(), m.getOffsetAt({ lineNumber: 4, column: 21 })); return (d ?? []).map((x) => x.fileName); })()`);
    console.log('definition of twice:', def);
    assert.deepEqual(def, ['pymagic:/src/util.ts']);
    // the editor's own Go to Definition opens util.ts (its model the TypeScript service made is taken over), and back
    await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 4, column: 21 }); ed.focus(); ed.trigger('keyboard', 'editor.action.revealDefinition', {}); })()`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('util.ts') && window.pymagic.editor.getPosition().lineNumber === 1`, 20000, 'util.ts opened at twice');
    console.log('Go to Definition opened', await page.evaluate(`window.pymagic.editor.getModel().uri.toString() + ' at ' + JSON.stringify(window.pymagic.editor.getPosition())`));
    await open('src/util.ts');
    await open('src/main.ts');

    // a file made on disk (by a tool of the terminal): the import resolves
    writeFileSync(join(root, 'src', 'later.ts'), 'export function later(): void {}\n');
    main = await settled('src/main.ts', (m) => !m.some((x) => /later/.test(x)));
    console.log('main.ts after src/later.ts was made on disk:', main);

    // JavaScript: type-checked with // @ts-check, not without (as VS Code)
    await open('src/plain.js');
    const plain = await settled('src/plain.js');
    await open('src/checked.js');
    const checked = await settled('src/checked.js', (m) => m.length > 0);
    console.log('plain.js:', plain, '| checked.js:', checked);
    assert.deepEqual(plain, []);
    assert.ok(checked.some((m) => /^3: Property 'toUpperCase' does not exist on type 'number'/.test(m)), checked.join(' | '));

    // components: <script lang="ts">'s type errors, not defineProps, not a name the markup reads, not the SCSS; Svelte's $count
    await open('src/Comp.vue');
    const vue = await settled('src/Comp.vue', (m) => m.length > 0);
    console.log('Comp.vue:', vue);
    assert.deepEqual(vue, ["7: Type 'string' is not assignable to type 'number'."]);
    await open('src/App.svelte');
    const svelte = await settled('src/App.svelte', (m) => m.length > 0);
    console.log('App.svelte:', svelte);
    assert.deepEqual(svelte, ["4: Type 'string' is not assignable to type 'number'."]);
    console.log('OK');
} finally {
    await app.close();
}

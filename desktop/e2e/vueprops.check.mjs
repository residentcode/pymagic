// A Vue component's markup and script as TypeScript reads them, in the built PyMagic.exe: a v-for's item is its list's element (item.nmae is
// told), the props given to a component it imports are checked against the ones that component declares (a number given to a string prop, a
// text to a number prop; a required one left out is a warning on the tag), and an options object's methods have the instance as this
// (this.total is its data, this.totl is told). Also a project's built declarations (a referenced project's dist/*.d.ts) are read.
// (vue here is a stand-in package with ref and Ref: the real one is not on this computer.)
// Run: node desktop/e2e/vueprops.check.mjs
import assert from 'node:assert/strict';
import { launch, project, sleep } from './app.mjs';

const parent = `<script setup lang="ts">
import Child from './Child.vue'
const list = [{ id: 1, name: 'a' }]
const n = 3
const label = 'x'
</script>

<template>
  <li v-for="(item, i) in list" :key="item.id">{{ item.nmae }} {{ i.toFixed() }} {{ item.name.toUpperCase() }}</li>
  <Child :label="n" count="2" />
  <Child :label="label" :count="n" />
  <child :count="1" />
</template>
`;
const child = `<template><b>{{ label }} {{ count }}</b></template>
<script setup lang="ts">
interface Props { label: string; count?: number }
defineProps<Props>()
</script>
`;
const options = `<template>
  <p>{{ up() }}</p>
</template>
<script lang="ts">
export default {
  data() { return { total: 1 } },
  methods: {
    up() { return this.total.toFixed(1) },
    down() { return this.totl },
  },
}
</script>
`;
const root = project({
    'package.json': '{"name":"probe","dependencies":{"vue":"3.5.0"}}',
    'tsconfig.json': '{ "compilerOptions": { "strict": true, "module": "ESNext", "moduleResolution": "Bundler" }, "references": [{ "path": "./packages/lib" }] }\n',
    'node_modules/vue/package.json': '{"name":"vue","version":"3.5.0","types":"index.d.ts"}',
    'node_modules/vue/index.d.ts': 'export interface Ref<T = any> { value: T; readonly __v_isRef: true }\nexport declare function ref<T>(value: T): Ref<T>;\n',
    'src/Parent.vue': parent,
    'src/Child.vue': child,
    'src/Options.vue': options,
    'packages/lib/tsconfig.json': '{ "compilerOptions": { "composite": true, "declaration": true, "outDir": "dist" }, "include": ["src"] }\n',
    'packages/lib/dist/index.d.ts': 'export declare const twice: (n: number) => number;\n',
    'src/uselib.ts': "import { twice } from '../packages/lib/dist/index';\nexport const s: string = twice(2);\n",
});
const app = await launch(root);
const { page } = app;
const open = async (f) => {
    await page.evaluate(`window.pymagic.openFile('${f}')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('${f.split('/').pop()}')`, 20000, f);
};
const markers = (owner) => page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ resource: window.pymagic.editor.getModel().uri }).filter((m) => ${owner ? `m.owner === '${owner}'` : 'true'}).map((m) => m.startLineNumber + ':' + m.startColumn + ' ' + m.message.split('\\n')[0]).sort()`);
const settled = async (what, test, owner = 'embedded', tries = 60) => {
    let last = null;
    for (let i = 0; i < tries; i++) {
        const now = JSON.stringify(await markers(owner));
        if (now === last && test(JSON.parse(now))) return JSON.parse(now);
        last = now;
        await sleep(1000);
    }
    assert.fail(`${what}: the findings stayed ${last}`);
};

try {
    await page.evaluate(`(async () => { await window.pymagic.tsProject.ready(); })()`);
    await open('src/Parent.vue');
    const found = await settled('Parent.vue', (m) => m.length >= 4);
    console.log('Parent.vue:', found);
    assert.equal(found.length, 4, found.join(' | '));
    assert.match(found[0], /^10:10 Type 'number' is not assignable to type 'string'\./);
    assert.match(found[1], /^10:21 Type 'string' is not assignable to type 'number'\./);
    assert.match(found[2], /^12:4 Child needs the prop label\./);
    assert.match(found[3], /^9:56 Property 'nmae' does not exist on type '\{ id: number; name: string; \}'\./);

    await open('src/Options.vue');
    const opt = await settled('Options.vue', (m) => m.length >= 1);
    console.log('Options.vue:', opt);
    assert.equal(opt.length, 1, opt.join(' | '));
    assert.match(opt[0], /^9:26 Property 'totl' does not exist on type/);

    await open('src/uselib.ts');
    const lib = await settled('uselib.ts', (m) => m.length >= 1, null);
    console.log('uselib.ts:', lib);
    assert.equal(lib.length, 1, lib.join(' | '));
    assert.match(lib[0], /^2:14 Type 'number' is not assignable to type 'string'\./);
    console.log('OK');
} finally {
    await app.close();
}

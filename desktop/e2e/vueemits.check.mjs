// The events and slots of a component another one uses, in the built PyMagic.exe with its TypeScript service: a handler of an event the
// child says it emits is checked against the event's arguments (a function that takes a string for a number is told at @save, a statement's
// $event is the event's argument), an event it does not name falls through as in Vue (nothing told), and a slot's names are of the type the
// child gives them (a name it does not give is told at the slot, a property its items lack inside the slot's content).
// Run: node desktop/e2e/vueemits.check.mjs
import assert from 'node:assert/strict';
import { launch, project, sleep } from './app.mjs';

const parent = `<script setup lang="ts">
import Grid from './Grid.vue'
import List from './List.vue'
function onSave(id: string) { return id }
function keep(id: number) { return id }
function upper(s: string) { return s.toUpperCase() }
let n = 0
</script>

<template>
  <Grid @save="onSave" @pick="(r) => r.id" @click="n++" />
  <Grid @save="keep" @pick="upper($event.name)" />
  <Grid @save="upper($event)" v-slot="{ row, idx }">{{ row.nmae }}</Grid>
  <List>
    <template #item="{ item, label }">{{ item }} {{ label.toUpperCase() }}</template>
  </List>
</template>
`;
const grid = `<script setup lang="ts">
interface Row { id: number; name: string }
defineEmits<{ (e: 'save', id: number): void; (e: 'pick', row: Row): void }>()
defineSlots<{ default(props: { row: Row; index: number }): any }>()
</script>

<template><div><slot :row="{ id: 1, name: 'a' }" :index="0" /></div></template>
`;
const list = `<template><ul><li><slot name="item" :item="1" label="x" /></li></ul></template>
`;
const root = project({
    'package.json': '{"name":"probe","dependencies":{"vue":"3.5.0"}}',
    'tsconfig.json': '{ "compilerOptions": { "strict": true, "module": "ESNext", "moduleResolution": "Bundler" } }\n',
    'node_modules/vue/package.json': '{"name":"vue","version":"3.5.0","types":"index.d.ts"}',
    'node_modules/vue/index.d.ts': 'export interface Ref<T = any> { value: T; readonly __v_isRef: true }\nexport declare function ref<T>(value: T): Ref<T>;\n',
    'src/Parent.vue': parent,
    'src/Grid.vue': grid,
    'src/List.vue': list,
});
const app = await launch(root);
const { page } = app;
const open = async (f) => {
    await page.evaluate(`window.pymagic.openFile('${f}')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('${f.split('/').pop()}')`, 20000, f);
};
const markers = () => page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ resource: window.pymagic.editor.getModel().uri }).filter((m) => m.owner === 'embedded').map((m) => m.startLineNumber + ':' + m.startColumn + ' ' + m.message.split('\\n')[0]).sort()`);
const settled = async (what, test, tries = 60) => {
    let last = null;
    for (let i = 0; i < tries; i++) {
        const now = JSON.stringify(await markers());
        if (now === last && test(JSON.parse(now))) return JSON.parse(now);
        last = now;
        await sleep(1000);
    }
    assert.fail(`${what}: the findings stayed ${last}`);
};

try {
    await page.evaluate(`(async () => { await window.pymagic.tsProject.ready(); })()`);
    // the children first, so the parent's checks have read them
    await open('src/Grid.vue');
    await open('src/List.vue');
    await open('src/Parent.vue');
    const found = await settled('Parent.vue', (m) => m.length >= 4);
    console.log('Parent.vue:', found);
    assert.equal(found.length, 4, found.join(' | '));
    const at = (line, text) => found.some((f) => f.startsWith(line + ':') && f.includes(text));
    // onSave takes a string, the event gives a number
    assert.ok(at(11, "Type '(id: string) => string' is not assignable to type '(id: number) => any'"), 'onSave at @save');
    // upper($event.name) is fine: pick gives a Row; upper($event) gives upper a number
    assert.ok(at(13, "Argument of type 'number' is not assignable to parameter of type 'string'"), 'upper($event) at @save');
    // the default slot gives no idx, and its row has no nmae
    assert.ok(at(13, "Property 'idx' does not exist"), 'idx at v-slot');
    assert.ok(at(13, "Property 'nmae' does not exist"), 'row.nmae');
    // List's item slot gives item (any) and label (a string): label.toUpperCase() is fine; nothing about @click (it falls through)
    assert.ok(!found.some((f) => f.startsWith('15:') || f.startsWith('12:')), found.join(' | '));
    assert.ok(!found.some((f) => f.includes('click')), found.join(' | '));
    console.log('OK');
} finally {
    await app.close();
}

// A Vue component's markup against its script in the built PyMagic.exe: in a <script setup> a ref is its value in the markup (count.toFixed
// is fine, count.lenght is told), a prop is a name of its own, a function's arguments are counted; an options object's data and methods are
// the markup's names; a component imported by another is a module (no "cannot find module"), and Go to Definition on it opens its file.
// Completion inside {{ }} gives the script's names and a value's members; hover and Go to Definition work from the markup.
// (vue here is a stand-in package with ref, computed and Ref: the real one is not on this computer.)
// Run: node desktop/e2e/vuemarkup.check.mjs
import assert from 'node:assert/strict';
import { launch, project, sleep } from './app.mjs';

const counter = `<script setup lang="ts">
import { ref, computed } from 'vue'
import Child from './Child.vue'
const props = defineProps<{ title: string }>()
const count = ref(0)
const user = ref({ name: 'a' })
const double = computed(() => count.value * 2)
function inc(by: number) { count.value += by }
</script>

<template>
  <p @click="inc()">{{ count.toFixed(1) }} {{ count.lenght }} {{ user.nmae }} {{ title.lenght }} {{ double.toFixed() }}</p>
  <Child :label="title.toUpperCase()" />
  <i>{{ user. }}</i>
  <b>{{  }}</b>
</template>
`;
const options = `<template>
  <p>{{ total.toFixed(1) }} {{ total.lenght }} {{ label.toUpperCase() }} {{ twice(2) }} {{ twice() }}</p>
</template>
<script lang="ts">
export default {
  data() { return { total: 0, label: 'x' } },
  methods: { twice(n: number) { return n * 2 } },
}
</script>
`;
const root = project({
    'package.json': '{"name":"probe","dependencies":{"vue":"3.5.0"}}',
    'tsconfig.json': '{ "compilerOptions": { "strict": true, "module": "ESNext", "moduleResolution": "Bundler" } }\n',
    'node_modules/vue/package.json': '{"name":"vue","version":"3.5.0","types":"index.d.ts"}',
    'node_modules/vue/index.d.ts': 'export interface Ref<T = any> { value: T; readonly __v_isRef: true }\nexport declare function ref<T>(value: T): Ref<T>;\nexport declare function computed<T>(get: () => T): Ref<T>;\n',
    'src/Counter.vue': counter,
    'src/Child.vue': '<template><b>{{ label }}</b></template>\n<script setup lang="ts">\ndefineProps<{ label: string }>()\n</script>\n',
    'src/Options.vue': options,
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
/** The suggestions at a place of the open file, by their labels. */
const suggest = async (line, column) => {
    await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: ${line}, column: ${column} }); ed.focus(); ed.trigger('check', 'editor.action.triggerSuggest', {}); })()`);
    const rows = await page.waitFor(`(() => { const rows = [...document.querySelectorAll('.suggest-widget.visible .monaco-list-row .label-name')].map((e) => e.textContent); return rows.length ? rows : null; })()`, 15000, `suggestions at ${line}:${column}`);
    await page.evaluate(`window.pymagic.editor.trigger('check', 'hideSuggestWidget', {})`);
    return rows;
};

try {
    await page.evaluate(`(async () => { await window.pymagic.tsProject.ready(); })()`);
    await open('src/Counter.vue');
    const found = await settled('Counter.vue', (m) => m.length >= 4);
    console.log('Counter.vue:', found);
    assert.equal(found.length, 4, found.join(' | '));
    assert.match(found[0], /^12:14 Expected 1 arguments, but got 0\./);
    assert.match(found[1], /^12:53 Property 'lenght' does not exist on type 'number'\./);
    assert.match(found[2], /^12:71 Property 'nmae' does not exist on type '\{ name: string; \}'\./);
    assert.match(found[3], /^12:88 Property 'lenght' does not exist on type 'string'\. Did you mean 'length'\?/);
    // nothing about the component it imports
    assert.ok(!(await page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ resource: window.pymagic.editor.getModel().uri }).map((m) => m.message).join('\\n')`)).includes('Cannot find module'));

    // completion: a value's members after its dot (the ref's value's, not the ref's), and the script's names in an empty {{ }}
    const members = await suggest(14, 15);
    console.log('user. :', members.slice(0, 5));
    assert.ok(members.includes('name') && !members.includes('value'), members.join(' '));
    const names = await suggest(15, 9);
    console.log('{{ }} :', names.slice(0, 8));
    for (const n of ['count', 'double', 'inc', 'title', 'user']) assert.ok(names.includes(n), n + ' in ' + names.join(' '));

    // hover: the ref's value's type
    await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: 12, column: 26 }); ed.focus(); ed.trigger('keyboard', 'editor.action.showHover', {}); })()`);
    const hover = await page.waitFor(`[...document.querySelectorAll('.monaco-hover-content')].map((e) => e.textContent).find((t) => t.includes('count'))`, 15000, 'the hover');
    console.log('hover:', hover.slice(0, 60));
    assert.match(hover, /count: number/);
    // Go to Definition: from the markup to the script's line; from the import to the other component
    const goes = async (line, column) => {
        await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: ${line}, column: ${column} }); ed.focus(); ed.trigger('keyboard', 'editor.action.revealDefinition', {}); })()`);
        await sleep(1500);
        return page.evaluate(`window.pymagic.editor.getModel().uri.path.split('/').pop() + ':' + window.pymagic.editor.getPosition().lineNumber`);
    };
    let at = null;
    for (let i = 0; i < 10 && at !== 'Counter.vue:5'; i++) at = await goes(12, 26);
    console.log('count is defined at', at);
    assert.equal(at, 'Counter.vue:5');
    for (let i = 0; i < 10 && !/^Child\.vue/.test(at); i++) at = await goes(3, 9);
    console.log('Child is', at);
    assert.match(at, /^Child\.vue:1$/);

    // an options object: its data and methods are the markup's names
    await open('src/Options.vue');
    const opt = await settled('Options.vue', (m) => m.length >= 2);
    console.log('Options.vue:', opt);
    assert.equal(opt.length, 2, opt.join(' | '));
    assert.match(opt[0], /^2:38 Property 'lenght' does not exist on type 'number'\./);
    assert.match(opt[1], /^2:92 Expected 1 arguments, but got 0\./);
    for (let i = 0; i < 10 && at !== 'Options.vue:6'; i++) at = await goes(2, 9);
    console.log('total is defined at', at);
    assert.equal(at, 'Options.vue:6');
    console.log('OK');
} finally {
    await app.close();
}

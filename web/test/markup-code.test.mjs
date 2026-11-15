// A component's markup expressions as code at their own places (web/src/embedded-model.js's withMarkupCode), and what is told of them.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const load = async (file) => import('data:text/javascript;base64,' + Buffer.from(readFileSync(new URL('../src/' + file, import.meta.url), 'utf8')).toString('base64'));
const { embeddedDocument, withMarkupCode, componentPrelude, keepFinding, vueBindings, vueProps, vuePropChecks, vueMarkupDocument, vueScriptDocument, PROPS_AT } = await load('embedded-model.js');

const code = (text, kind) => withMarkupCode(text, embeddedDocument(text, 'javascript', 'component'), kind);
/** The document's lines with something in them, trimmed. */
const lines = (doc) => doc.split('\n').map((l) => l.trim()).filter(Boolean);

test('Svelte: {expr} is code where it stands; a block\'s own names, spreads and comments are left out', () => {
    const text = '<script>\n  let user = { name: "a" };\n  let items = [];\n</script>\n\n<h1 class:on={user.active}>{user.nmae}</h1>\n{#each items as item, i (item.id)}\n  <li on:click={() => pick(item)}>{item.name} {i} {user.name}</li>\n{/each}\n'
        + '{#await load() then value}{value.x}{/await}\n<Child {...user} let:row>{row.id}</Child>\n<!-- {user.gone} -->\n{@html user.html}\n<style>\n  h1 { color: red }\n</style>\n';
    const doc = code(text, 'svelte');
    assert.equal(doc.length, text.length);
    assert.deepEqual(lines(doc), [';', 'let user = { name: "a" };', 'let items = [];', ';user.active; ;user.nmae;', ';user.name;']);
    // each expression at its own offset
    assert.equal(doc.indexOf('user.nmae'), text.indexOf('user.nmae'));
    assert.equal(doc.indexOf('user.name;'), text.indexOf('user.name}'));
    // nothing of the each's item, the await's value, the slot's row, the comment or the style
    for (const gone of ['item.', 'item)', 'value.x', 'row.id', 'user.gone', 'color', 'pick']) assert.ok(!doc.includes(gone), gone);
    assert.ok(doc.split('\n').length === text.split('\n').length);
});

test('Vue: {{ expr }} and bound attributes are code where they stand; a v-for\'s names and $event are left out', () => {
    const text = '<template>\n  <div :class="{ on: state.open }" @click="toggle(state)">\n    <p v-if="props.title">{{ props.titel }}</p>\n    <li v-for="(item, i) in props.items" :key="item.id">{{ item.name }} {{ i }}</li>\n'
        + '    <input v-model="state.text" @input="change($event)" class="plain" title="{{ not code }}">\n    <Slot v-slot="{ row }">{{ row.id }}</Slot>\n  </div>\n</template>\n\n<script setup lang="ts">\nconst props = defineProps<{ title: string, items: { id: number }[] }>()\nconst state = { open: true, text: "" }\n</script>\n';
    const doc = code(text, 'vue');
    assert.equal(doc.length, text.length);
    assert.equal(doc.indexOf('props.titel'), text.indexOf('props.titel'));
    assert.ok(doc.includes(';({ on: state.open })'));
    assert.ok(doc.includes(';(toggle(state))'));
    assert.ok(doc.includes(';(props.title)'));
    assert.ok(doc.includes(';( props.titel )'));
    assert.ok(doc.includes(';(state.text)'));
    assert.ok(doc.includes('const props = defineProps<{ title: string'));
    for (const gone of ['item.', ' i ', '$event', 'row', 'plain', 'not code']) assert.ok(!doc.includes(gone), gone);
    // the script that follows the markup starts a statement of its own
    assert.equal(doc[text.indexOf('<script setup lang="ts">') + '<script setup lang="ts">'.length - 1], ';');
    // a template in another language is not read
    const pug = '<template lang="pug">\np {{ a.b }}\n</template>\n<script setup>\nconst a = {}\n</script>\n';
    assert.ok(!code(pug, 'vue').includes('a.b'));
    assert.equal(code('<p>{{ a }}</p>', null), embeddedDocument('<p>{{ a }}</p>', 'javascript', 'component'));
});

test('what is told of a markup expression', () => {
    // a property that is not there
    assert.ok(keepFinding('2339', "Property 'nmae' does not exist on type '{ name: string; }'.", 'svelte', true));
    assert.ok(keepFinding('2551', "Property 'titel' does not exist on type '{ title: string; }'. Did you mean 'title'?", 'vue', true));
    assert.ok(keepFinding('2554', 'Expected 1 arguments, but got 0.', 'svelte', true));
    // not a name the markup may give, not syntax (an event's statements), not a ref Vue reads by itself, not a store
    assert.ok(!keepFinding('2304', "Cannot find name 'item'.", 'svelte', true));
    assert.ok(!keepFinding('1005', "')' expected.", 'vue', true));
    assert.ok(!keepFinding('2339', "Property 'name' does not exist on type 'Ref<{ name: string; }, { name: string; }>'.", 'vue', true));
    assert.ok(keepFinding('2554', 'Expected 1 arguments, but got 0.', 'vue', true));
    assert.ok(!keepFinding('2339', "Property 'x' does not exist on type '$store'.", 'svelte', true));
    // in the script itself: as before
    assert.ok(keepFinding('1005', "')' expected.", 'vue'));
    assert.ok(keepFinding('2322', "Type 'string' is not assignable to type 'number'.", 'svelte'));
});

test('Svelte\'s $: values: typed by what they are given', () => {
    const p = componentPrelude('svelte', '  let n = 1;\n  $: doubled = n * 2;\n  $: label = `${n} of ${\n    doubled}`;\n  $: list = items\n    .filter((x) => x > 1)\n    .map((x) => x * 2);\n  $: $store = 3;\n  $: n = 2;\n  $: console.log(n);\n');
    assert.match(p, /var doubled = \(n \* 2\);/);
    assert.ok(p.includes('var label = (`${n} of ${\n    doubled}`);'));
    assert.ok(p.includes('var list = (items\n    .filter((x) => x > 1)\n    .map((x) => x * 2));'));
    assert.match(p, /declare let \$store: any;/);
    assert.ok(!/var n =/.test(p));
    assert.ok(p.startsWith('\n;'));
});

const L = (...lines) => lines.join(String.fromCharCode(10));

test('Vue: a v-for item is its list element, an index a number, a slot names any; a name given twice is left out', () => {
    const text = L('<template>', '  <li v-for="(item, i) in list" :key="item.id">{{ item.nmae }} {{ i }}</li>', '  <b v-for="{ id } in list">{{ id }}</b>',
        '  <S v-slot="{ row }">{{ row.x }}</S>', '  <p v-for="x in a">{{ x }}</p><p v-for="x in b">{{ x.y }}</p>', '</template>', '<script setup lang="ts">', 'const list = [{ id: 1 }]', '</script>');
    const { list, declared } = vueBindings(text);
    assert.deepEqual(list.map((b) => [b.kind, b.names, b.source]), [['for', 'item, i', 'list'], ['for', '{ id }', 'list'], ['slot', '{ row }', '']]);
    assert.ok(!declared.has('x'));
    const doc = vueMarkupDocument(text, 'A.vue.1', true);
    assert.ok(doc.includes('var item = __pm_each((list)); var i = __pm_key((list));'));
    assert.ok(doc.includes('var { id } = __pm_each((list));'));
    assert.ok(doc.includes('var { row } = (0 as any);'));
    assert.ok(doc.includes(';( item.nmae )'), 'an expression with a v-for name is checked now');
    assert.ok(!doc.includes('x.y'), 'one with a name given twice is not');
    assert.ok(doc.includes('var list = __pm_v.list;'));
    assert.ok(!doc.includes('var item = __pm_v'));
    // each expression still at its own place
    assert.equal(doc.indexOf('item.nmae'), text.indexOf('item.nmae'));
});

test('Vue: the props a component declares', () => {
    const typed = vueProps(L('<script setup lang="ts">', 'interface Props { label: string; count?: number }', 'defineProps<Props>()', '</script>'));
    assert.deepEqual(typed, { type: '{ "label": string; "count"?: number; }', required: ['label'] });
    const inline = vueProps(L('<script setup lang="ts">', "const p = withDefaults(defineProps<{ title: string, size: 'a' | 'b' }>(), { size: 'a' })", '</script>'));
    assert.deepEqual(inline, { type: '{ "title": string; "size"?: ' + "'a' | 'b'" + '; }', required: ['title'] });
    const runtime = vueProps(L('<script setup>', 'defineProps({ a: String, b: [String, Number], c: { type: Boolean, required: true }, d: { type: Number, required: true, default: 1 } })', '</script>'));
    assert.deepEqual(runtime, { type: '{ "a"?: string; "b"?: string | number; "c": boolean; "d"?: number; }', required: ['c'] });
    assert.deepEqual(vueProps(L('<script>', "export default { props: ['x', 'y'], data() { return {} } }", '</script>')), { type: '{ "x"?: any; "y"?: any; }', required: [] });
    assert.equal(vueProps(L('<script setup>', 'const a = 1', '</script>')), null);
    assert.equal(vueProps('<template><b/></template>'), null);
});

test('Vue: the props given to a component are checked against its own, a required one left out is told', () => {
    const text = L('<script setup lang="ts">', "import Child from './Child.vue'", 'const n = 3', '</script>', '<template>',
        '  <Child :label="n" count="2" class="c" @click="n++" title="free" />', '  <child v-model="n" />', '  <Other :label="n" />', '</template>');
    const props = { './Child.vue': { type: '{ "label": string; "count"?: number; "modelValue"?: number; }', required: ['label'] } };
    const checks = vuePropChecks(text, props, true);
    assert.deepEqual(checks.code, ['declare const __pm_c0: { "label": string; "count"?: number; "modelValue"?: number; };', '__pm_c0["label"] = (n);', '__pm_c0["count"] = "2";',
        'declare const __pm_c1: { "label": string; "count"?: number; "modelValue"?: number; };', '__pm_c1["modelValue"] = (n);']);
    assert.equal(text.slice(...checks.places[1]), ':label');
    assert.equal(text.slice(...checks.places[2]), 'count');
    assert.deepEqual(checks.missing.map((x) => [text.slice(...x.place), x.message]), [['child', 'Child needs the prop label.']]);
    const doc = vueMarkupDocument(text, 'A.vue.1', true, props);
    assert.ok(doc.includes(PROPS_AT + String.fromCharCode(10) + checks.code.join(String.fromCharCode(10))));
    // in a JavaScript component: the props' type as JSDoc
    assert.match(vuePropChecks(text, props, false).code[0], /^[/][*][*] @type [{][{] "label": string;/);
});

test('Vue: an options object is given to __pm_options in as many characters, so its methods have this', () => {
    const text = L('<script>', 'export default {', '  data() { return { total: 1 } },', '  methods: { up() { return this.total } },', '}', '</script>');
    const doc = vueScriptDocument(text, embeddedDocument(text, 'javascript', 'component'), false);
    const head = doc.slice(0, text.length);
    assert.equal(head.length, text.length);
    assert.ok(head.includes('let __pm_o=_o_({'));
    assert.match(head, /\}\n\)/);
    assert.equal(head.indexOf('this.total'), text.indexOf('this.total'));
    assert.equal(head.split(String.fromCharCode(10)).length, text.split(String.fromCharCode(10)).length);
    // export default defineComponent({ ... }) is left to the package's own types
    const named = L('<script>', 'export default defineComponent({ data() { return {} } })', '</script>');
    assert.ok(vueScriptDocument(named, embeddedDocument(named, 'javascript', 'component'), false).includes('const __pm_o = defineComponent('));
    assert.ok(!keepFinding('7023', "'twice' implicitly has return type 'any' because it does not have a return type annotation and is referenced directly or indirectly in one of its return expressions.", 'vue'));
});

// A component's markup expressions as code at their own places (web/src/embedded-model.js's withMarkupCode), and what is told of them.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const load = async (file) => import('data:text/javascript;base64,' + Buffer.from(readFileSync(new URL('../src/' + file, import.meta.url), 'utf8')).toString('base64'));
const { embeddedDocument, withMarkupCode, componentPrelude, keepFinding } = await load('embedded-model.js');

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

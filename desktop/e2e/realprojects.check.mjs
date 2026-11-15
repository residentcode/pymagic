// Real projects with their own packages in the built PyMagic.exe (made by their own tools: Vite's vue-ts template, SvelteKit's sv create,
// create astro's minimal one, and Express apps with handlebars and ejs): the files as their tools write them have no warning or error, and a
// file with mistakes has them told, against the real packages' types (vue's ref and computed, Svelte 5's runes).
// Run: PYMAGIC_REAL_DIR=<folder with vue-app, svelte-app, astro-app, hbs-app, ejs-app, packages installed> node desktop/e2e/realprojects.check.mjs
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { launch, sleep } from './app.mjs';

const base = process.env.PYMAGIC_REAL_DIR;
assert.ok(base && existsSync(join(base, 'vue-app', 'node_modules', 'vue')), 'PYMAGIC_REAL_DIR with the projects, their packages installed');
const write = (root, rel, text) => { mkdirSync(dirname(join(root, rel)), { recursive: true }); writeFileSync(join(root, rel), text); };

/** The warnings and errors of the open file, by every checker: "line:column message". */
async function findings(page) {
    return page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ resource: window.pymagic.editor.getModel().uri }).filter((m) => m.severity >= 4)
        .map((m) => m.startLineNumber + ':' + m.startColumn + ' ' + m.message.split('\\n')[0]).sort()`);
}
async function open(page, rel) {
    await page.evaluate(`window.pymagic.openFile(${JSON.stringify(rel)})`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith(${JSON.stringify(rel.split('/').pop())})`, 20000, rel);
}
/** The findings once they stopped changing and `test` holds (up to a minute). */
async function settled(page, what, test) {
    let last = null;
    for (let i = 0; i < 60; i++) {
        const now = JSON.stringify(await findings(page));
        if (now === last && i >= 4 && test(JSON.parse(now))) return JSON.parse(now);
        last = now;
        await sleep(1000);
    }
    assert.fail(`${what}: the findings stayed ${last}`);
}
const clean = async (page, rel) => {
    await open(page, rel);
    const f = await settled(page, rel, () => true);
    console.log(`  ${rel}:`, f.length ? f : 'nothing');
    assert.deepEqual(f, [], `${rel} as its tool wrote it`);
};

async function project(name, files, steps) {
    const root = join(base, name);
    for (const [rel, text] of Object.entries(files)) write(root, rel, text);
    console.log(`== ${name}`);
    const app = await launch(root, null, null, { keep: true });
    try {
        await app.page.evaluate(`(async () => { await window.pymagic.tsProject?.ready?.(); })()`);
        await steps(app.page);
    } finally { await app.close(); }
}

await project('vue-app', {
    'src/components/Child.vue': '<script setup lang="ts">\ndefineProps<{ label: string; count?: number }>()\n</script>\n\n<template>\n  <b>{{ label }}</b>\n</template>\n',
    'src/components/Probe.vue': `<script setup lang="ts">
import { ref, computed } from 'vue'
import Child from './Child.vue'
const count = ref(0)
const items = ref([{ id: 1, name: 'a' }])
const double = computed(() => count.value * 2)
</script>

<template>
  <p @click="count++">{{ count.toFixed(1) }} {{ count.lenght }} {{ double.toFixed() }}</p>
  <li v-for="item in items" :key="item.id">{{ item.nmae }} {{ item.name.toUpperCase() }}</li>
  <Child :label="count" />
  <Child label="ok" :count="count" />
</template>
`,
}, async (page) => {
    for (const f of ['src/App.vue', 'src/components/HelloWorld.vue', 'src/main.ts', 'vite.config.ts', 'src/components/Child.vue']) await clean(page, f);
    await open(page, 'src/components/Probe.vue');
    const f = await settled(page, 'Probe.vue', (x) => x.length >= 3);
    console.log('  Probe.vue:', f);
    assert.equal(f.length, 3, f.join(' | '));
    assert.match(f[0], /^10:55 Property 'lenght' does not exist on type 'number'/);
    assert.match(f[1], /^11:52 Property 'nmae' does not exist on type '\{ id: number; name: string; \}'/);
    assert.match(f[2], /^12:10 Type 'number' is not assignable to type 'string'/);
});

await project('svelte-app', {
    'src/routes/probe/+page.svelte': `<script lang="ts">
  let count = $state(0);
  let user = $state({ name: 'a' });
  const double = $derived(count * 2);
</script>

<button onclick={() => count++}>{count.toFixed(1)} {count.lenght} {user.nmae} {double.toFixed()}</button>
`,
}, async (page) => {
    for (const f of ['src/routes/+layout.svelte', 'src/routes/+page.svelte', 'src/lib/index.ts', 'vite.config.ts']) await clean(page, f);
    await open(page, 'src/routes/probe/+page.svelte');
    const f = await settled(page, 'probe/+page.svelte', (x) => x.length >= 2);
    console.log('  probe/+page.svelte:', f);
    assert.equal(f.length, 2, f.join(' | '));
    assert.match(f[0], /^7:59 Property 'lenght' does not exist on type 'number'/);
    assert.match(f[1], /^7:73 Property 'nmae' does not exist on type '\{ name: string; \}'/);
});

await project('astro-app', {
    'src/pages/probe.astro': '---\nconst items = ["a", "b"];\n---\n<main>\n  <ul>\n    {items.map((item) => (\n      <li><em>{item}</li>\n    ))}\n  </ul>\n</main>\n',
}, async (page) => {
    for (const f of ['src/pages/index.astro', 'astro.config.mjs']) await clean(page, f);
    await open(page, 'src/pages/probe.astro');
    const f = await settled(page, 'probe.astro', (x) => x.length >= 1);
    console.log('  probe.astro:', f);
    assert.ok(f.some((x) => /^7:11 Element <em> is not closed/.test(x)), f.join(' | '));
});

const server = (engine) => `const express = require('express');\nconst app = express();\napp.set('view engine', '${engine}');\napp.get('/', (req, res) => res.render('home', { items: [{ name: 'a' }], user: null }));\napp.listen(3000);\n`;
await project('hbs-app', {
    'app.js': server('hbs'),
    'views/home.hbs': '<ul>\n  {{#each items}}\n    <li>{{this.name}}</li>\n  {{else}}\n    <li>none</li>\n  {{/each}}\n</ul>\n{{#if user}}\n  <p>Hello {{user.name}}</p>\n{{/if}}\n',
    'views/broken.hbs': '<ul>\n  {{#each items}}\n    <li><b>{{this.name}}</li>\n  {{/each}}\n</ul>\n',
}, async (page) => {
    for (const f of ['views/home.hbs', 'app.js']) await clean(page, f);
    await open(page, 'views/broken.hbs');
    const f = await settled(page, 'broken.hbs', (x) => x.length >= 1);
    console.log('  broken.hbs:', f);
    assert.ok(f.some((x) => /^3:9 Element <b> is not closed/.test(x)), f.join(' | '));
});

await project('ejs-app', {
    'app.js': server('ejs'),
    'views/home.ejs': '<ul>\n  <% items.forEach((item) => { %>\n    <li><%= item.name %></li>\n  <% }) %>\n</ul>\n<% if (user) { %>\n  <p>Hello <%= user.name %></p>\n<% } else { %>\n  <p>Guest</p>\n<% } %>\n',
    'views/broken.ejs': '<ul>\n  <% items.forEach((item) => { %>\n    <li><b><%= item.name %></li>\n  <% }) %>\n</ul>\n',
}, async (page) => {
    for (const f of ['views/home.ejs', 'app.js']) await clean(page, f);
    await open(page, 'views/broken.ejs');
    const f = await settled(page, 'broken.ejs', (x) => x.length >= 1);
    console.log('  broken.ejs:', f);
    assert.ok(f.some((x) => /^3:9 Element <b> is not closed/.test(x)), f.join(' | '));
});
console.log('OK');

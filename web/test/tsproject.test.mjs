// Checks of web/src/tsproject-model.js (the TypeScript service's project) and of the components' scripts in web/src/embedded-model.js:
// `node --test web/test/tsproject.test.mjs`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const load = async (file) => import('data:text/javascript;base64,' + Buffer.from(readFileSync(new URL('../src/' + file, import.meta.url), 'utf8')).toString('base64'));
const { compilerOptionsOf, DEFAULT_OPTIONS, libFile, isCodePath, planRefresh, ROOT } = await load('tsproject-model.js');
const { regionsOf, embeddedDocument, scriptIsTs, componentPrelude, keepFinding } = await load('embedded-model.js');

test("a tsconfig's options as Monaco's TypeScript worker takes them", () => {
    // none: the defaults, @types where node_modules has it
    assert.deepEqual(compilerOptionsOf(null), { ...DEFAULT_OPTIONS, typeRoots: [ROOT + 'node_modules/@types'], allowNonTsExtensions: true });
    const o = compilerOptionsOf({
        strict: true, target: 'ES2020', module: 'NodeNext', moduleResolution: 'NodeNext', jsx: 'preserve', lib: ['DOM', 'ES2022', 'dom.iterable'],
        baseUrl: './src', paths: { '@/*': ['*'] }, outDir: 'dist', composite: true, types: ['node'], noUnusedLocals: true,
    });
    assert.equal(o.strict, true);
    assert.equal(o.target, 7);
    assert.equal(o.module, 199);
    assert.equal(o.moduleResolution, 99);
    assert.equal(o.jsx, 1);
    assert.deepEqual(o.lib, ['lib.dom.d.ts', 'lib.es2022.d.ts', 'lib.dom.iterable.d.ts']);
    assert.equal(o.baseUrl, 'pymagic:/src');
    assert.deepEqual(o.paths, { '@/*': ['*'] });
    assert.deepEqual(o.types, ['node']);
    assert.equal(o.noUnusedLocals, true);
    assert.ok(!('outDir' in o) && !('composite' in o), 'what only building uses is left out');
    // paths without a baseUrl: the config's own folder; a bundler's resolution with an old module falls back to node's
    assert.equal(compilerOptionsOf({ paths: { '~/*': ['./app/*'] }, pathsBase: 'web' }).baseUrl, 'pymagic:/web');
    assert.equal(compilerOptionsOf({ paths: { '~/*': ['./app/*'] } }).baseUrl, 'pymagic:/');
    assert.equal(compilerOptionsOf({ module: 'commonjs' }).moduleResolution, 2);
    assert.equal(compilerOptionsOf({ module: 'esnext' }).moduleResolution, 100);
    assert.equal(compilerOptionsOf({ target: 'es1999' }).target, DEFAULT_OPTIONS.target, 'a value it does not know keeps the default');
    // JavaScript: allowed always, checked as checkJs says
    assert.equal(compilerOptionsOf({ allowJs: false }, 'javascript').allowJs, true);
    assert.equal(compilerOptionsOf({ checkJs: true }, 'javascript').checkJs, true);
    assert.equal(compilerOptionsOf({}, 'javascript').checkJs, undefined);
    assert.equal(libFile('ESNext.Array'), 'lib.esnext.array.d.ts');
});

test('which files the service is given, and what a change on disk reads again', () => {
    assert.ok(isCodePath('src/a.ts') && isCodePath('App.tsx') && isCodePath('lib/x.mjs') && isCodePath('env.d.ts'));
    assert.ok(!isCodePath('node_modules/x/index.js') && !isCodePath('dist/app.js') && !isCodePath('a.min.js') && !isCodePath('.venv/x.js') && !isCodePath('a.py'));
    assert.deepEqual(planRefresh([{ kind: 'changed', path: 'src/a.ts' }, { kind: 'changed', path: 'src/a.ts' }, { kind: 'changed', path: 'README.md' }]), { project: false, packages: false, files: ['src/a.ts'] });
    assert.deepEqual(planRefresh([{ kind: 'created', path: 'src/b.ts' }, { kind: 'changed', path: 'src/a.ts' }]), { project: true, packages: false, files: [] });
    assert.deepEqual(planRefresh([{ kind: 'changed', path: 'tsconfig.app.json' }]), { project: true, packages: false, files: [] });
    assert.deepEqual(planRefresh([{ kind: 'node', path: 'node_modules' }]), { project: false, packages: true, files: [] });
    assert.deepEqual(planRefresh([{ kind: 'changed', path: 'package.json' }]), { project: false, packages: true, files: [] });
    assert.deepEqual(planRefresh([{ kind: 'changed', path: 'node_modules/x/package.json' }]), { project: false, packages: false, files: [] });
    assert.deepEqual(planRefresh([{ kind: 'all', path: '' }]), { project: true, packages: true, files: [] });
});

test("a component's script: TypeScript with lang=ts, its macros declared after the end, the findings it shows", () => {
    const vue = '<template>\n  <p>{{ n }}</p>\n</template>\n<script setup lang="ts">\nconst n: number = 1\n</script>\n<style lang="scss">\n$c: red;\n</style>\n<style>\np { color: red }\n</style>\n';
    assert.ok(scriptIsTs(vue));
    assert.ok(!scriptIsTs('<script>let a</script>'));
    assert.ok(scriptIsTs("<script lang='typescript'>"));
    // the SCSS block is not CSS's to check; a script of another language is left out
    assert.deepEqual(regionsOf(vue, 'css').map(([s, e]) => vue.slice(s, e).trim()), ['p { color: red }']);
    assert.deepEqual(regionsOf('<script lang="coffee">x = 1</script><script lang="ts">let y</script>', 'javascript').length, 1);
    // the document keeps the offsets; a component's {{ }} inside the script stays code
    const doc = embeddedDocument(vue, 'javascript', 'component');
    assert.equal(doc.length, vue.length);
    assert.equal(doc.indexOf('const n: number = 1'), vue.indexOf('const n: number = 1'));
    assert.equal(embeddedDocument('<script>const s = "{{ x }}"</script>', 'javascript', 'component').trim(), 'const s = "{{ x }}"');
    assert.match(componentPrelude("vue"), /declare function defineProps<T = any>/);
    assert.match(componentPrelude('svelte'), /declare function \$state<T>\(initial\?: T\): T;/);
    assert.ok(componentPrelude('vue').startsWith('\n'));
    // Svelte's $: declares what it assigns, unless the script declares it; $:'s comparisons declare nothing
    const reactive = componentPrelude('svelte', '  let total = 1;\n  $: doubled = total * 2;\n  $: total = 3;\n  $: if (x == 1) go();\n  $: $x = 2;\n');
    assert.match(reactive, /declare let doubled: any;/);
    assert.match(reactive, /declare let \$x: any;/);
    assert.ok(!/declare let total/.test(reactive));
    assert.ok(!/declare let if/.test(reactive));
    assert.ok(!keepFinding('2552', "Cannot find name '$count'. Did you mean 'count'?", 'svelte'));
    assert.ok(!keepFinding('7028', 'Unused label.', 'svelte'));
    assert.ok(keepFinding('7028', 'Unused label.', 'vue'));
    // a template's block: syntax only; a component's: type errors too, not names the markup reads, not Svelte's $store
    assert.ok(keepFinding('1005', "';' expected.", null));
    assert.ok(!keepFinding('2304', "Cannot find name 'x'.", null));
    assert.ok(keepFinding('2322', "Type 'string' is not assignable to type 'number'.", 'vue'));
    assert.ok(!keepFinding('6133', "'n' is declared but its value is never read.", 'vue'));
    assert.ok(!keepFinding('2304', "Cannot find name '$count'.", 'svelte'));
    assert.ok(keepFinding('2304', "Cannot find name 'count'.", 'svelte'));
    assert.ok(keepFinding({ value: '2322', target: 'x' }, '', 'svelte'));
});

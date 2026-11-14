// Several configs of one project and a project too large to be given whole (web/src/tsproject-model.js): the config that covers a file, and
// what a file imports.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const load = async (file) => import('data:text/javascript;base64,' + Buffer.from(readFileSync(new URL('../src/' + file, import.meta.url), 'utf8')).toString('base64'));
const { configFor, covers, importsOf } = await load('tsproject-model.js');

test('a solution\'s tsconfig.json: the config it refers to that covers the file (Vite\'s tsconfig.app.json and tsconfig.node.json)', () => {
    const configs = [
        { path: 'tsconfig.json', dir: '', files: [], references: ['tsconfig.app.json', 'tsconfig.node.json'], compilerOptions: {} },
        { path: 'tsconfig.app.json', dir: '', include: ['src'], compilerOptions: { strict: true, lib: ['ES2022', 'DOM'] } },
        { path: 'tsconfig.node.json', dir: '', include: ['vite.config.ts'], compilerOptions: { strict: true, lib: ['ES2023'] } },
    ];
    assert.equal(configFor('src/main.ts', configs).path, 'tsconfig.app.json');
    assert.equal(configFor('src/components/Card.vue', configs).path, 'tsconfig.app.json');
    assert.equal(configFor('vite.config.ts', configs).path, 'tsconfig.node.json');
    // a file none covers: the project's own
    assert.equal(configFor('scripts/build.js', configs).path, 'tsconfig.json');
    assert.equal(configFor('a.ts', []), null);
});

test('a monorepo: the nearest folder\'s config, its include and exclude', () => {
    const configs = [
        { path: 'tsconfig.json', dir: '', compilerOptions: { strict: false } },
        { path: 'packages/api/tsconfig.json', dir: 'packages/api', include: ['packages/api/src/**/*'], exclude: ['packages/api/src/**/*.test.ts'], compilerOptions: { strict: true } },
        { path: 'packages/api/tsconfig.test.json', dir: 'packages/api', include: ['packages/api/src/**/*.test.ts', 'packages/api/test'], compilerOptions: { types: ['vitest'] } },
        { path: 'packages/web/jsconfig.json', dir: 'packages/web', compilerOptions: { checkJs: true } },
    ];
    assert.equal(configFor('packages/api/src/routes/users.ts', configs).path, 'packages/api/tsconfig.json');
    assert.equal(configFor('packages/api/src/routes/users.test.ts', configs).path, 'packages/api/tsconfig.test.json');
    assert.equal(configFor('packages/api/test/setup.ts', configs).path, 'packages/api/tsconfig.test.json');
    assert.equal(configFor('packages/api/scripts/seed.ts', configs).path, 'tsconfig.json');      // not in the package's include: the root's
    assert.equal(configFor('packages/web/src/app.js', configs).path, 'packages/web/jsconfig.json');
    assert.equal(configFor('tools/x.ts', configs).path, 'tsconfig.json');
    // patterns: a folder, a file, wildcards
    assert.ok(covers({ dir: '', include: ['src'] }, 'src/a/b.ts'));
    assert.ok(!covers({ dir: '', include: ['src'] }, 'srcs/a.ts'));
    assert.ok(covers({ dir: '', include: ['**/*.d.ts'] }, 'types/env.d.ts'));
    assert.ok(covers({ dir: '', include: ['*.config.ts'] }, 'vite.config.ts'));
    assert.ok(!covers({ dir: '', include: ['*.config.ts'] }, 'src/vite.config.ts'));
    assert.ok(covers({ dir: '', include: ['src/**/*'], files: ['env.d.ts'] }, 'env.d.ts'));
    assert.ok(!covers({ dir: '', files: [] }, 'src/a.ts'));
    assert.ok(!covers({ dir: 'packages/api' }, 'packages/web/a.ts'));
    assert.ok(!covers({ dir: '' }, 'node_modules/x/index.d.ts'));
});

test('what a file imports: the paths a relative import may be, and the packages', () => {
    const text = "import a from './util';\nimport { b } from \"../lib/b.js\";\nimport type { C } from 'vue';\nimport '@scope/pkg/styles.css';\nconst d = require('lodash/fp');\nconst e = await import('./lazy/e');\n"
        + "import x from 'node:fs';\nimport y from '@/alias/y';\nexport { z } from './z.ts';\n";
    const { files, packages } = importsOf('src/pages/home.ts', text);
    assert.deepEqual(files.map((f) => f[0]), ['src/pages/util.ts', 'src/lib/b.js', 'src/pages/lazy/e.ts', 'src/pages/z.ts']);
    assert.ok(files[0].includes('src/pages/util/index.ts') && files[0].includes('src/pages/util.tsx'));
    assert.ok(files[1].includes('src/lib/b.ts'));                    // ./b.js is b.ts to TypeScript
    assert.deepEqual(packages, ['vue', '@scope/pkg', 'lodash']);
    assert.deepEqual(importsOf('main.ts', "import a from './a'").files[0][0], 'a.ts');
});

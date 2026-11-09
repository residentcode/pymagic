// Checks of src/PyMagic.Server/Run/pm_sourcemap.mjs (the debugger's source maps): `node --test web/test/sourcemap.test.mjs`.
import test from 'node:test';
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { transformSync } from 'esbuild';
import { decodeMappings, sourceFile, prepare, generatedFor, originalFor, loadMap } from '../../src/PyMagic.Server/Run/pm_sourcemap.mjs';

const ROOT = process.platform === 'win32' ? 'C:\\proj' : '/proj';

test('base64 VLQ mappings are read as the specification writes them', () => {
    // "AAAA;AACA,EAAE": line 0 col 0 -> src 0 line 0 col 0; line 1 col 0 -> line 1 col 0; col 2 -> line 1 col 2
    assert.deepEqual(decodeMappings('AAAA;AACA,EAAE'), [
        { line: 0, column: 0, source: 0, sourceLine: 0, sourceColumn: 0 },
        { line: 1, column: 0, source: 0, sourceLine: 1, sourceColumn: 0 },
        { line: 1, column: 2, source: 0, sourceLine: 1, sourceColumn: 2 },
    ]);
    // a segment of one field (no source) is left out; each line starts its column at 0 again; steps can go back (D is -1)
    assert.deepEqual(decodeMappings('IAAI,C;;ADDD').map((s) => [s.line, s.column, s.sourceLine, s.sourceColumn]), [[0, 4, 0, 4], [2, 0, -1, 3]]);
});

test("the bundlers' names of sources are the project's files", () => {
    assert.equal(sourceFile('webpack://my-app/./src/page.tsx', '', null, ROOT), join(ROOT, 'src', 'page.tsx'));
    assert.equal(sourceFile('webpack:///./lib/a.ts?abcd', '', null, ROOT), join(ROOT, 'lib', 'a.ts'));
    assert.equal(sourceFile('turbopack:///[project]/app/page.tsx', '', null, ROOT), join(ROOT, 'app', 'page.tsx'));
    assert.equal(sourceFile('http://localhost:5173/src/main.ts', '', null, ROOT), join(ROOT, 'src', 'main.ts'));
    // relative to the map: beside it on disk, or the address's path when it was served
    const mapFile = pathToFileURL(join(ROOT, 'dist', 'a.js.map')).href;
    assert.equal(sourceFile('../src/a.ts', '', mapFile, ROOT), join(ROOT, 'src', 'a.ts'));
    assert.equal(sourceFile('App.tsx', '', 'http://localhost:5173/src/App.tsx', ROOT), join(ROOT, 'src', 'App.tsx'));
    assert.equal(sourceFile('a.ts', 'src', mapFile, ROOT), join(ROOT, 'dist', 'src', 'a.ts'));
});

const TS = [
    'interface Item {',          // 1
    '  name: string;',           // 2
    '  price: number;',          // 3
    '}',                         // 4
    '',                          // 5
    'export function total(items: Item[], tax: number): number {',   // 6
    '  let sum: number = 0;',    // 7
    '  for (const i of items) {', // 8
    '    sum += i.price;',       // 9
    '  }',                       // 10
    '  return sum * (1 + tax);', // 11
    '}',                         // 12
    '',
].join('\n');

test("a TypeScript file's lines and the compiled code's, both ways, through a real map", () => {
    const out = transformSync(TS, { loader: 'ts', sourcemap: 'external', sourcefile: 'shop.ts', format: 'esm' });
    const loaded = { map: JSON.parse(out.map), url: pathToFileURL(join(ROOT, 'dist', 'shop.js')).href };
    const file = join(ROOT, 'dist', 'shop.ts');
    const prepared = prepare(loaded, ROOT);
    assert.deepEqual(prepared.files, [file]);
    const js = out.code.split('\n');
    // line 9 (`sum += i.price`) runs at the line of the compiled code that says it
    const at = generatedFor(prepared, file, 9);
    assert.ok(at, 'line 9 is in the map');
    assert.match(js[at.line], /sum \+= i\.price/);
    // and from there back to line 9
    assert.deepEqual(originalFor(prepared, at.line, at.column + 1), { file, line: 9, column: originalFor(prepared, at.line, at.column + 1).column });
    // a line with no code of its own (the interface) goes to the next one that has
    const blank = generatedFor(prepared, file, 2);
    assert.equal(blank, null, 'the types are not code, and the next line with code is more than 2 lines on');
    assert.equal(generatedFor(prepared, file, 5)?.sourceLine, 6);
});

test('a map in a data: address, and index maps (sections)', async () => {
    const map = { version: 3, sources: ['a.ts'], mappings: 'AAAA;AACA' };
    const url = 'data:application/json;base64,' + Buffer.from(JSON.stringify(map)).toString('base64');
    const loaded = await loadMap(url, pathToFileURL(join(ROOT, 'a.js')).href);
    assert.deepEqual(loaded.map, map);
    const index = { version: 3, sections: [{ offset: { line: 0, column: 0 }, map: { version: 3, sources: ['x.ts'], mappings: 'AAAA' } }, { offset: { line: 10, column: 0 }, map: { version: 3, sources: ['y.ts'], mappings: 'AAAA;AACA' } }] };
    const p = prepare({ map: index, url: pathToFileURL(join(ROOT, 'out.js')).href }, ROOT);
    assert.deepEqual(originalFor(p, 11, 0), { file: join(ROOT, 'y.ts'), line: 2, column: 0 });
    assert.deepEqual(originalFor(p, 0, 0), { file: join(ROOT, 'x.ts'), line: 1, column: 0 });
});

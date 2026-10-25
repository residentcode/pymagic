// Checks of desktop/icon/pymagic.ico, the file desktop/icon/make-icon.js writes from pymagic.svg
// (`node desktop/test/icon.test.mjs`; tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const dir = resolve(import.meta.dirname, '..', 'icon');
const ico = readFileSync(resolve(dir, 'pymagic.ico'));
const PNG = '89504e470d0a1a0a';

/** The directory of the .ico: one entry per image, with its bytes. */
function entries() {
  const count = ico.readUInt16LE(4);
  return Array.from({ length: count }, (_, i) => {
    const e = 6 + 16 * i;
    const size = ico.readUInt32LE(e + 8), offset = ico.readUInt32LE(e + 12);
    return { w: ico[e] || 256, h: ico[e + 1] || 256, bits: ico.readUInt16LE(e + 6), size, offset, data: ico.subarray(offset, offset + size) };
  });
}

test('the file is an icon whose images all lie inside it', () => {
  assert.equal(ico.readUInt16LE(0), 0);
  assert.equal(ico.readUInt16LE(2), 1);
  const list = entries();
  assert.ok(list.length > 0);
  for (const e of list) assert.ok(e.offset >= 6 + 16 * list.length && e.offset + e.size <= ico.length, `${e.w}px lies outside the file`);
});

test('it has every size Windows asks for, from 16 to 256 pixels', () => {
  const sizes = entries().map(e => e.w);
  for (const n of [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]) assert.ok(sizes.includes(n), `${n}px is missing`);
  for (const e of entries()) assert.equal(e.w, e.h);
});

test('256 is a PNG of that size; the smaller ones are 32-bit bitmaps with a mask', () => {
  for (const e of entries()) {
    if (e.w === 256) {
      assert.equal(e.data.subarray(0, 8).toString('hex'), PNG);
      assert.equal(e.data.readUInt32BE(16), 256);
      assert.equal(e.data.readUInt32BE(20), 256);
      continue;
    }
    assert.equal(e.data.readUInt32LE(0), 40, `${e.w}px has no bitmap header`);
    assert.equal(e.data.readInt32LE(4), e.w);
    assert.equal(e.data.readInt32LE(8), e.w * 2);
    assert.equal(e.data.readUInt16LE(14), 32);
    assert.equal(e.size, 40 + e.w * e.w * 4 + Math.ceil(e.w / 32) * 4 * e.w);
  }
});

test('the .ico was made from the SVG as it is now (after editing pymagic.svg, run `npm run icon` in desktop/)', () => {
  const svg = readFileSync(resolve(dir, 'pymagic.svg'), 'utf8').replace(/\r\n/g, '\n');
  const recorded = readFileSync(resolve(dir, 'pymagic.ico.sha256'), 'utf8').split(/\s+/)[0];
  assert.equal(recorded, createHash('sha256').update(svg).digest('hex'));
});

test('the small images are drawn: most of the tile is opaque and the corners are clear', () => {
  for (const e of entries().filter(x => x.w <= 48)) {
    const n = e.w, alpha = (x, y) => e.data[40 + ((n - 1 - y) * n + x) * 4 + 3];
    let opaque = 0;
    for (let y = 0; y < n; y++) for (let x = 0; x < n; x++) if (alpha(x, y) > 200) opaque++;
    assert.ok(opaque > n * n * 0.7, `${n}px is mostly empty`);
    assert.ok(alpha(0, 0) < 40 && alpha(n - 1, n - 1) < 40, `${n}px has square corners`);
  }
});

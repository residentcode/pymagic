// Draws pymagic.svg at the sizes Windows uses and writes them into pymagic.ico (run from desktop/: npm run icon).
// Electron's own Chromium does the drawing, so no image packages are needed. Sizes up to 128 are stored as 32-bit
// bitmaps and 256 as PNG, the usual layout that Explorer, the taskbar, rcedit and NSIS all read.
const { app, BrowserWindow } = require('electron');
const fs = require('node:fs');
const path = require('node:path');

const SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
const source = path.join(__dirname, 'pymagic.svg');
const out = path.join(__dirname, 'pymagic.ico');

/** The SVG's hash, kept next to the .ico so desktop/test/icon.test.mjs notices an SVG edited without `npm run icon`. */
function svgHash(text) {
  return require('node:crypto').createHash('sha256').update(text.replace(/\r\n/g, '\n')).digest('hex');
}

/** Runs in the page: each size as raw RGBA (base64) and as PNG (base64). */
async function render(svgText, sizes) {
  const img = new Image();
  img.src = 'data:image/svg+xml;base64,' + btoa(unescape(encodeURIComponent(svgText)));
  await img.decode();
  return sizes.map(n => {
    const c = document.createElement('canvas');
    c.width = c.height = n;
    const g = c.getContext('2d');
    g.imageSmoothingQuality = 'high';
    g.drawImage(img, 0, 0, n, n);
    const rgba = g.getImageData(0, 0, n, n).data;
    let bin = '';
    for (let i = 0; i < rgba.length; i += 0x8000) bin += String.fromCharCode.apply(null, rgba.subarray(i, i + 0x8000));
    return { n, rgba: btoa(bin), png: c.toDataURL('image/png').split(',')[1] };
  });
}

/** A 32-bit BGRA bitmap as an icon image: header, rows bottom-up, then an all-clear AND mask. */
function bitmap(n, rgba) {
  const maskRow = Math.ceil(n / 32) * 4;
  const buf = Buffer.alloc(40 + n * n * 4 + maskRow * n);
  buf.writeUInt32LE(40, 0);
  buf.writeInt32LE(n, 4);
  buf.writeInt32LE(n * 2, 8);          // the height counts the colour rows and the mask rows
  buf.writeUInt16LE(1, 12);
  buf.writeUInt16LE(32, 14);
  buf.writeUInt32LE(n * n * 4 + maskRow * n, 20);
  for (let y = 0; y < n; y++)
    for (let x = 0; x < n; x++) {
      const s = (y * n + x) * 4;
      const d = 40 + ((n - 1 - y) * n + x) * 4;
      buf[d] = rgba[s + 2]; buf[d + 1] = rgba[s + 1]; buf[d + 2] = rgba[s]; buf[d + 3] = rgba[s + 3];
    }
  return buf;
}

function ico(images) {
  const head = Buffer.alloc(6 + 16 * images.length);
  head.writeUInt16LE(0, 0);
  head.writeUInt16LE(1, 2);            // 1 = icon
  head.writeUInt16LE(images.length, 4);
  let offset = head.length;
  images.forEach(({ n, data }, i) => {
    const e = 6 + 16 * i;
    head[e] = n >= 256 ? 0 : n;      // 0 means 256
    head[e + 1] = n >= 256 ? 0 : n;
    head.writeUInt16LE(1, e + 4);
    head.writeUInt16LE(32, e + 6);
    head.writeUInt32LE(data.length, e + 8);
    head.writeUInt32LE(offset, e + 12);
    offset += data.length;
  });
  return Buffer.concat([head, ...images.map(i => i.data)]);
}

app.whenReady().then(async () => {
  const win = new BrowserWindow({ show: false });
  try {
    const svg = fs.readFileSync(source, 'utf8');
    await win.loadURL('data:text/html,<!doctype html><title>icon</title>');
    const drawn = await win.webContents.executeJavaScript(`(${render})(${JSON.stringify(svg)}, ${JSON.stringify(SIZES)})`);
    const images = drawn.map(({ n, rgba, png }) => ({ n, data: n >= 256 ? Buffer.from(png, 'base64') : bitmap(n, Buffer.from(rgba, 'base64')) }));
    fs.writeFileSync(out, ico(images));
    fs.writeFileSync(`${out}.sha256`, `${svgHash(svg)}  pymagic.svg\n`);
    console.log(`wrote ${out} (${SIZES.join(', ')} px)`);
  } catch (e) {
    console.error(e);
    process.exitCode = 1;
  }
  app.quit();
});

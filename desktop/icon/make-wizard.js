// Draws the setup wizard's two pictures from their SVGs and writes them as the 24-bit bitmaps NSIS shows (run from desktop/: npm run wizard):
// wizard-sidebar.bmp (164 x 314, left of the first and last pages) and wizard-header.bmp (150 x 57, top of the others). As make-icon.js does,
// Electron's own Chromium draws them, so no image packages are needed.
const { app, BrowserWindow } = require('electron');
const fs = require('node:fs');
const path = require('node:path');

const PICTURES = [['wizard-sidebar', 164, 314], ['wizard-header', 150, 57]];

/** Runs in the page: the SVG drawn at its size, as raw RGBA (base64). */
async function render(svgText, w, h) {
  const img = new Image();
  img.src = 'data:image/svg+xml;base64,' + btoa(unescape(encodeURIComponent(svgText)));
  await img.decode();
  const c = document.createElement('canvas');
  c.width = w;
  c.height = h;
  const g = c.getContext('2d');
  g.imageSmoothingQuality = 'high';
  g.drawImage(img, 0, 0, w, h);
  const rgba = g.getImageData(0, 0, w, h).data;
  let bin = '';
  for (let i = 0; i < rgba.length; i += 0x8000) bin += String.fromCharCode.apply(null, rgba.subarray(i, i + 0x8000));
  return btoa(bin);
}

/** A 24-bit BMP file: the file and info headers, then BGR rows bottom-up, each padded to four bytes. */
function bmp(w, h, rgba) {
  const row = Math.ceil(w * 3 / 4) * 4;
  const buf = Buffer.alloc(54 + row * h);
  buf.write('BM', 0, 'ascii');
  buf.writeUInt32LE(buf.length, 2);
  buf.writeUInt32LE(54, 10);
  buf.writeUInt32LE(40, 14);
  buf.writeInt32LE(w, 18);
  buf.writeInt32LE(h, 22);
  buf.writeUInt16LE(1, 26);
  buf.writeUInt16LE(24, 28);
  buf.writeUInt32LE(row * h, 34);
  buf.writeInt32LE(2835, 38);          // 72 dots per inch
  buf.writeInt32LE(2835, 42);
  for (let y = 0; y < h; y++)
    for (let x = 0; x < w; x++) {
      const s = (y * w + x) * 4;
      const d = 54 + (h - 1 - y) * row + x * 3;
      buf[d] = rgba[s + 2]; buf[d + 1] = rgba[s + 1]; buf[d + 2] = rgba[s];
    }
  return buf;
}

app.whenReady().then(async () => {
  const win = new BrowserWindow({ show: false });
  try {
    await win.loadURL('data:text/html,<!doctype html><title>wizard</title>');
    for (const [name, w, h] of PICTURES) {
      const svg = fs.readFileSync(path.join(__dirname, name + '.svg'), 'utf8');
      const rgba = Buffer.from(await win.webContents.executeJavaScript(`(${render})(${JSON.stringify(svg)}, ${w}, ${h})`), 'base64');
      fs.writeFileSync(path.join(__dirname, name + '.bmp'), bmp(w, h, rgba));
      console.log(`${name}.bmp: ${w} x ${h}`);
    }
  }
  catch (e) { console.error(e); process.exitCode = 1; }
  finally { app.quit(); }
});

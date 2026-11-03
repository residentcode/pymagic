// Removes a folder a tool made (an Edge profile): Edge leaves read-only files in it, which Windows will not delete, and its last processes may
// hold a file for a moment after they are told to end. Files are made writable and the removal is tried again for a few seconds; a folder still
// held after that is reported, not thrown.
import { chmodSync, readdirSync, rmSync, statSync } from 'node:fs';
import { join } from 'node:path';

const pause = (ms) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);

function writable(p) {
  try {
    const st = statSync(p);
    if (st.isDirectory()) for (const name of readdirSync(p)) writable(join(p, name));
    chmodSync(p, 0o666);
  }
  catch { /* gone already */ }
}

export function removeTree(dir) {
  for (let i = 0; i < 20; i++) {
    try { rmSync(dir, { recursive: true, force: true }); return true; }
    catch { writable(dir); pause(500); }
  }
  console.warn('could not remove ' + dir);
  return false;
}

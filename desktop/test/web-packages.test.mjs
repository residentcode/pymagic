import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const { missingWebPackages } = require(resolve(dirname(fileURLToPath(import.meta.url)), '..', 'web-packages.js'));

const installed = (web, name, version) => {
  mkdirSync(join(web, 'node_modules', name), { recursive: true });
  writeFileSync(join(web, 'node_modules', name, 'package.json'), JSON.stringify({ name, version }));
};

test('a package the page asks for that is not installed, or installed at another version, is missing', () => {
  const web = mkdtempSync(join(tmpdir(), 'pm-web-'));
  try {
    writeFileSync(join(web, 'package.json'), JSON.stringify({
      dependencies: { 'monaco-editor': '0.52.2', '@xterm/xterm': '5.5.0', '@xterm/addon-fit': '0.10.0' },
      devDependencies: { esbuild: '0.25.0', loose: '^1.0.0' },
    }));
    installed(web, 'monaco-editor', '0.52.2');
    installed(web, 'esbuild', '0.24.0');
    installed(web, 'loose', '1.4.0');
    assert.deepEqual(missingWebPackages(web), ['@xterm/xterm', '@xterm/addon-fit', 'esbuild']);
    installed(web, '@xterm/xterm', '5.5.0');
    installed(web, '@xterm/addon-fit', '0.10.0');
    installed(web, 'esbuild', '0.25.0');
    assert.deepEqual(missingWebPackages(web), []);
  } finally { rmSync(web, { recursive: true, force: true }); }
});

test('the real page has every package it asks for after npm install', () => {
  assert.deepEqual(missingWebPackages(resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', 'web')), []);
});

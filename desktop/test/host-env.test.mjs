import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const { hostEnvironment, SETTINGS } = require(resolve(dirname(fileURLToPath(import.meta.url)), '..', 'host-env.js'));

test('a computer with four cores or more gets the server collector with four heaps and a small budget for the youngest generation', () => {
  const env = hostEnvironment(8, { PATH: 'x' });
  assert.equal(env.PATH, 'x');
  assert.equal(env.DOTNET_gcServer, '1');
  assert.equal(env.DOTNET_GCHeapCount, '4');
  assert.equal(env.DOTNET_gcConcurrent, '0');
  assert.equal(env.DOTNET_GCgen0MaxBudget, '800000');   // hexadecimal: 8 MB
  assert.equal(parseInt(SETTINGS.DOTNET_GCgen0MaxBudget, 16), 8 * 1024 * 1024);
  assert.equal(hostEnvironment(4, {}).DOTNET_gcServer, '1');
});

test('a computer with fewer cores keeps the default collector', () => {
  assert.deepEqual(hostEnvironment(2, { PATH: 'x' }), { PATH: 'x' });
  assert.deepEqual(hostEnvironment(1, {}), {});
});

test('what the user already set is left alone, whatever the case of the name', () => {
  assert.deepEqual(hostEnvironment(16, { DOTNET_gcServer: '0' }), { DOTNET_gcServer: '0' });
  assert.deepEqual(hostEnvironment(16, { dotnet_gcheapcount: '2' }), { dotnet_gcheapcount: '2' });
  assert.deepEqual(hostEnvironment(16, { COMPlus_GCgen0size: '1000000' }), { COMPlus_GCgen0size: '1000000' });
});

test('the environment it is given is not changed', () => {
  const base = { PATH: 'x' };
  hostEnvironment(8, base);
  assert.deepEqual(base, { PATH: 'x' });
});

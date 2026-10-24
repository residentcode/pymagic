// Builds a self-contained Windows app: desktop/out/PyMagic-win32-x64/PyMagic.exe (nothing else needs to be installed).
import { execFileSync } from 'node:child_process';
import { cpSync, rmSync, mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { packager } from '@electron/packager';

const desktop = resolve(import.meta.dirname);
const repo = resolve(desktop, '..');
const stage = resolve(desktop, 'stage');
const sh = (cmd, args, cwd) => execFileSync(cmd, args, { cwd, stdio: 'inherit', shell: true });

rmSync(stage, { recursive: true, force: true });
mkdirSync(stage, { recursive: true });

console.log('== editor');
sh('node', ['build.mjs'], resolve(repo, 'web'));
cpSync(resolve(repo, 'web', 'dist'), resolve(stage, 'www'), { recursive: true });

console.log('== engine (self-contained)');
sh('dotnet', ['publish', `"${resolve(repo, 'src', 'PyMagic.Web', 'PyMagic.Web.csproj')}"`, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--source', 'https://api.nuget.org/v3/index.json', '--source', '"C:/Program Files (x86)/Microsoft SDKs/NuGetPackages"', '-o', `"${resolve(stage, 'host')}"`, '--nologo', '-v', 'quiet'], repo);
cpSync(resolve(repo, 'examples', 'demo'), resolve(stage, 'demo'), { recursive: true });

console.log('== app');
const paths = await packager({
  dir: desktop,
  out: resolve(desktop, 'out'),
  name: 'PyMagic',
  platform: 'win32',
  arch: 'x64',
  overwrite: true,
  prune: true,
  ignore: [/^\/out/, /^\/stage/, /^\/package\.mjs$/],
  extraResource: [resolve(stage, 'www'), resolve(stage, 'host'), resolve(stage, 'demo')],
});
console.log('Done:', paths.join(', '));

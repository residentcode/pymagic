// Builds a self-contained Windows app, desktop/out/PyMagic-win32-x64/PyMagic.exe (nothing else needs to be installed),
// and its installer, desktop/out/PyMagic-Setup-<version>.exe. `--no-installer` stops after the app folder.
import { execFileSync } from 'node:child_process';
import { cpSync, rmSync, mkdirSync, readdirSync, existsSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { packager } from '@electron/packager';
import { Arch, Platform, build } from 'electron-builder';
import { installerConfig, packagerOptions } from './build-config.mjs';
import webPackages from './web-packages.js';

const { missingWebPackages } = webPackages;

const desktop = resolve(import.meta.dirname);
const repo = resolve(desktop, '..');
const stage = resolve(desktop, 'stage');
const sh = (cmd, args, cwd) => execFileSync(cmd, args, { cwd, stdio: 'inherit', shell: true });
const installer = !process.argv.includes('--no-installer');

// NSIS reads "$" in a path as one of its own variables, so it cannot find the icon; say so before the long build
if (installer && desktop.includes('$')) {
  console.error(`The installer cannot be built from a folder whose path contains "$" (${desktop}). Move the checkout, or use --no-installer.`);
  process.exit(1);
}

rmSync(stage, { recursive: true, force: true });
mkdirSync(stage, { recursive: true });

console.log('== editor');
if (missingWebPackages(resolve(repo, 'web')).length) sh('npm', ['install'], resolve(repo, 'web'));
sh('node', ['build.mjs'], resolve(repo, 'web'));
cpSync(resolve(repo, 'web', 'dist'), resolve(stage, 'www'), { recursive: true });

console.log('== engine (self-contained)');
sh('dotnet', ['publish', `"${resolve(repo, 'src', 'PyMagic.Web', 'PyMagic.Web.csproj')}"`, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--source', 'https://api.nuget.org/v3/index.json', '--source', '"C:/Program Files (x86)/Microsoft SDKs/NuGetPackages"', '-o', `"${resolve(stage, 'host')}"`, '--nologo', '-v', 'quiet'], repo);
cpSync(resolve(repo, 'examples', 'demo'), resolve(stage, 'demo'), { recursive: true });
// the standard library's and third-party packages' stubs, so the type checks do not depend on PyCharm being installed
cpSync(resolve(repo, 'typeshed'), resolve(stage, 'typeshed'), { recursive: true });
// the model of full line completion (531 MB, not in git): fetched once into models/, checked against its SHA-256, and carried by the app
const { fetchModel, MODEL } = await import(pathToFileURL(resolve(repo, 'models', 'fetch.mjs')).href);
await fetchModel(resolve(repo, 'models'));
mkdirSync(resolve(stage, 'models'), { recursive: true });
cpSync(resolve(repo, 'models', MODEL.file), resolve(stage, 'models', MODEL.file));
cpSync(resolve(repo, 'models', 'NOTICE.md'), resolve(stage, 'models', 'NOTICE.md'));
console.log('== licenses');
// PyMagic's own license and the notices of what it carries (THIRD-PARTY-NOTICES.md), with the license text of every part: the schemas',
// the NuGet packages' and .NET's (licenses/), and those of every npm package the page is built from (its dependencies too, which the
// bundle holds). typeshed's and the model's are in their own folders already; Electron puts its own and Chromium's beside PyMagic.exe.
const licenses = resolve(stage, 'licenses');
mkdirSync(resolve(licenses, 'npm'), { recursive: true });
for (const f of ['LICENSE', 'THIRD-PARTY-NOTICES.md']) cpSync(resolve(repo, f), resolve(licenses, f));
cpSync(resolve(repo, 'licenses'), resolve(licenses, 'packages'), { recursive: true });
cpSync(resolve(repo, 'src', 'PyMagic.Server', 'Schemas', 'LICENSES'), resolve(licenses, 'schemas'), { recursive: true });
cpSync(resolve(repo, 'src', 'PyMagic.Server', 'Schemas', 'README.md'), resolve(licenses, 'schemas', 'README.md'));
cpSync(resolve(repo, 'src', 'PyMagic.Server', 'SchemaStore', 'LICENSE.txt'), resolve(licenses, 'schemas', 'schemastore.txt'));
cpSync(resolve(repo, 'src', 'PyMagic.Server', 'Gherkin', 'LICENSE.txt'), resolve(licenses, 'packages', 'gherkin.txt'));
const tree = JSON.parse(execFileSync('npm', ['ls', '--omit=dev', '--all', '--long', '--json'], { cwd: resolve(repo, 'web'), shell: true, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }));
const seen = new Set();
(function walk(node) {
  for (const [name, dep] of Object.entries(node.dependencies ?? {})) {
    const key = `${name}@${dep.version}`;
    if (seen.has(key) || !dep.path) continue;
    seen.add(key);
    const files = existsSync(dep.path) ? readdirSync(dep.path).filter((f) => /^(licen[cs]e|copying|notice|third[-_]?party)/i.test(f)) : [];
    for (const f of files) cpSync(join(dep.path, f), resolve(licenses, 'npm', `${key.replace('/', '__')}-${f}`));
    if (!files.length) console.log(`   (no license file in ${key}: see its package.json, license ${dep.license ?? 'not given'})`);
    walk(dep);
  }
})(tree);
console.log(`   ${seen.size} npm packages`);

// llama.cpp's builds for other systems, which a Windows app never loads
for (const other of ['win-arm64', 'linux-x64', 'linux-arm64', 'linux-musl-x64', 'osx-x64', 'osx-arm64'])
  rmSync(resolve(stage, 'host', 'runtimes', other), { recursive: true, force: true });

console.log('== app');
const paths = await packager(packagerOptions(desktop, stage));
console.log('Done:', paths.join(', '));

if (installer) {
  console.log('== installer (the first run downloads NSIS into electron-builder\'s cache)');
  const made = await build({ projectDir: desktop, prepackaged: paths[0], targets: Platform.WINDOWS.createTarget('nsis', Arch.x64), config: installerConfig() });
  console.log('Done:', made.filter(f => f.endsWith('.exe')).join(', '));
}

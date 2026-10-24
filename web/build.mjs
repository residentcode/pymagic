// Bundles the app and Monaco's editor worker into dist/ (no dev server: the C# host serves dist/).
import { build, context } from 'esbuild';
import { cpSync, mkdirSync } from 'node:fs';

const watch = process.argv.includes('--watch');
mkdirSync('dist', { recursive: true });
cpSync('src/index.html', 'dist/index.html');

const options = {
  entryPoints: {
    main: 'src/main.js',
    'editor.worker': 'node_modules/monaco-editor/esm/vs/editor/editor.worker.js',
  },
  bundle: true,
  format: 'iife',
  outdir: 'dist',
  loader: { '.ttf': 'file' },
  assetNames: '[name]',
  minify: !watch,
  sourcemap: watch,
  logLevel: 'info',
};

if (watch) await (await context(options)).watch();
else await build(options);

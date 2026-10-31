// Bundles the app and Monaco's editor worker into dist/ (no dev server: the C# host serves dist/).
import { build, context } from 'esbuild';
import { cpSync, mkdirSync } from 'node:fs';
import { execSync } from 'node:child_process';

const watch = process.argv.includes('--watch');
mkdirSync('dist', { recursive: true });
cpSync('src/index.html', 'dist/index.html');
cpSync('src/panels.css', 'dist/panels.css');
cpSync('src/look.css', 'dist/look.css');

/** The commit this bundle was made from (with a + when the files of the page or the engine have changes that are not committed), and when. */
function stamp() {
  const git = (args) => execSync(`git ${args}`, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  try {
    const dirty = git('status --porcelain -- web/src src').length > 0;
    return { commit: git('rev-parse --short HEAD') + (dirty ? '+' : ''), builtAt: new Date().toISOString() };
  } catch {
    return { commit: 'unknown', builtAt: new Date().toISOString() };
  }
}

const options = {
  define: { __BUILD__: JSON.stringify(stamp()) },
  entryPoints: {
    main: 'src/main.js',
    'editor.worker': 'node_modules/monaco-editor/esm/vs/editor/editor.worker.js',
    // the language services of HTML (templates), CSS and JavaScript / TypeScript / JSON
    'html.worker': 'node_modules/monaco-editor/esm/vs/language/html/html.worker.js',
    'css.worker': 'node_modules/monaco-editor/esm/vs/language/css/css.worker.js',
    'ts.worker': 'node_modules/monaco-editor/esm/vs/language/typescript/ts.worker.js',
    'json.worker': 'node_modules/monaco-editor/esm/vs/language/json/json.worker.js',
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

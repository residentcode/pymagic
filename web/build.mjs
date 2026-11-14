// Bundles the app and Monaco's editor worker into dist/ (no dev server: the C# host serves dist/).
import { build, context } from 'esbuild';
import { cpSync, mkdirSync } from 'node:fs';
import { execSync } from 'node:child_process';

const watch = process.argv.includes('--watch');
mkdirSync('dist', { recursive: true });
cpSync('src/index.html', 'dist/index.html');
cpSync('src/welcome-tool.html', 'dist/welcome-tool.html');
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
  // process.arch: php-parser's lexer reads it when it loads (PHP's 64-bit integers); the page has no process
  define: { __BUILD__: JSON.stringify(stamp()), 'process.arch': '"x64"' },
  entryPoints: {
    main: 'src/main.js',
    // the welcome screen's New project and Plugins: their dialog alone (welcome-tool.html)
    'welcome-tool': 'src/welcome-tool.js',
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

// Tailwind CSS's design system, on its own (the page loads it only in a project that uses Tailwind): its theme and utilities go in as text
const tailwind = { ...options, entryPoints: { tailwind: 'src/tailwind-entry.js' }, loader: { '.css': 'text' } };
// the Mermaid library, on its own too (the Markdown preview loads it only for a diagram mermaid-lite.js does not draw)
const mermaid = { ...options, entryPoints: { mermaid: 'src/mermaid-entry.js' } };

if (watch) { for (const o of [options, tailwind, mermaid]) await (await context(o)).watch(); }
else { await build(options); await build(tailwind); await build(mermaid); }

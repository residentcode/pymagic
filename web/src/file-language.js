// The language a file is shown in: Monaco's languages by extension, and what Monaco leaves out. View engines of Express and the other kinds of New
// Project (EJS, Hogan.js and Mustache, Vash, Nunjucks), single-file components (Vue, Svelte, Astro), files known by their name (Gemfile, Podfile,
// Dockerfile, .editorconfig, .prettierrc) and scripts without an extension known by their #! line (Express's bin/www). No DOM, no Monaco here:
// the languages Monaco has are passed in. web/test/file-language.test.mjs checks it.

/** Extensions Monaco has no language of, and the language that shows them best. */
export const EXTRA_EXTENSIONS = {
    '.ejs': 'ejs', '.eta': 'ejs',                                   // HTML with JavaScript in <% %> (PyMagic's own language, ejs-language.js)
    '.feature': 'gherkin',                                          // behave's and pytest-bdd's scenarios (PyMagic's own language, gherkin-language.js)
    '.hjs': 'handlebars', '.mustache': 'handlebars',                 // Hogan.js and Mustache: Handlebars' {{ }} without its helpers
    '.vash': 'razor',                                               // Vash is Razor's @ syntax for Node
    '.njk': 'twig', '.nunjucks': 'twig',                            // Nunjucks is Jinja's syntax, as Twig's
    '.blade.php': 'blade',                                          // Laravel's templates (PyMagic's own language, blade-language.js)
    '.vue': 'html', '.svelte': 'html', '.astro': 'html',            // a component: markup with <script> and <style>
    '.webmanifest': 'json', '.jsonc': 'json', '.json5': 'json', '.code-workspace': 'json', '.map': 'json',
    '.gradle': 'kotlin',                                            // Gradle's build scripts (Groovy's syntax is closest to Kotlin's here)
    '.plist': 'xml', '.storyboard': 'xml', '.xib': 'xml', '.xcscheme': 'xml', '.entitlements': 'xml', '.xcworkspacedata': 'xml',
};

/** Files known by their whole name. */
export const FILENAMES = {
    gemfile: 'ruby', podfile: 'ruby', fastfile: 'ruby', rakefile: 'ruby', appfile: 'ruby',
    dockerfile: 'dockerfile', containerfile: 'dockerfile',
    '.gitattributes': 'ini', '.editorconfig': 'ini', '.npmrc': 'ini', '.yarnrc': 'ini', '.env': 'ini', '.flaskenv': 'ini', 'gradle.properties': 'ini',
    '.prettierrc': 'json', '.watchmanconfig': 'json', '.babelrc': 'json', '.eslintrc': 'json', '.swcrc': 'json', '.stylelintrc': 'json',
};

/** The language of a script without an extension, by the program of its #! line (bin/www is node's). */
export function languageOfShebang(firstLine) {
    const m = /^#!\s*(?:\/usr\/bin\/env\s+(?:-\S+\s+)*)?(?:\S*\/)?(\S+)/.exec(firstLine ?? '');
    if (!m) return null;
    const program = m[1].replace(/[\d.]+$/, '');
    return { node: 'javascript', deno: 'typescript', bun: 'javascript', python: 'python', bash: 'shell', sh: 'shell', zsh: 'shell', ruby: 'ruby', pwsh: 'powershell', php: 'php' }[program] ?? null;
}

/**
 * The language of a file: a template (django-html, which also shows Jinja), then its name, the extensions above, Monaco's own (the longest that
 * fits), and for a file without an extension its #! line. `languages` are Monaco's: [{ id, extensions, filenames }].
 */
export function languageIdFor(rel, languages, firstLine = '') {
    const lower = rel.toLowerCase();
    const name = lower.slice(lower.lastIndexOf('/') + 1);
    if (/\.(html?|jinja2?|j2)$/.test(lower)) return 'django-html';
    if (FILENAMES[name]) return FILENAMES[name];
    if (/^\.env\.[\w.-]+$/.test(name)) return 'ini';                  // .env.local, .env.production
    for (const l of languages) if ((l.filenames ?? []).some((f) => f.toLowerCase() === name)) return l.id;
    let best = null, bestLength = 0;
    for (const [ext, id] of Object.entries(EXTRA_EXTENSIONS))
        if (name.endsWith(ext) && ext.length > bestLength) { best = id; bestLength = ext.length; }
    for (const l of languages)
        for (const ext of l.extensions ?? [])
            if (name.endsWith(ext.toLowerCase()) && ext.length > bestLength) { best = l.id; bestLength = ext.length; }
    if (best) return best;
    if (!name.includes('.') || name.startsWith('.')) return languageOfShebang(firstLine) ?? 'plaintext';
    return 'plaintext';
}

/** The same with Monaco's languages, for the editor, the diff, merge, history and pull request views. */
export function languageFor(monaco, rel, text = '') {
    return languageIdFor(rel, monaco.languages.getLanguages(), text.slice(0, 200).split('\n', 1)[0]);
}

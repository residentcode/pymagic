// Emmet (the Emmet plugin of Settings > Plugins): abbreviations such as ul>li*3, div.card>h2+p or ! expanded by completion, as in VS Code and
// PyCharm. emmet-monaco-es runs the official Emmet engine; it offers an expansion only where an element may start (not inside a tag, an attribute
// or a template tag), which it tells from the tokens: the template languages name theirs as Monaco's HTML does (delimiter.html). In JavaScript and
// TypeScript it writes JSX (className), only in the files that are JSX (emmet-model.js).
import * as monaco from 'monaco-editor';
import { emmetHTML, emmetCSS, emmetJSX } from 'emmet-monaco-es';
import { isJsxFile } from './emmet-model.js';

/** The languages whose markup Emmet writes: HTML (and Vue, Svelte, Astro), Django and Jinja templates, EJS, Handlebars and Hogan.js, Twig and Nunjucks, Razor and Vash, PHP. */
export const EMMET_HTML = ['html', 'django-html', 'ejs', 'handlebars', 'twig', 'razor', 'php', 'blade'];
/** And the stylesheets: m10 is margin: 10px; */
export const EMMET_CSS = ['css', 'scss', 'less'];
/** And JSX, in the JavaScript and TypeScript files that are JSX. */
export const EMMET_JSX = ['javascript', 'typescript'];

/** Monaco as emmet-monaco-es sees it, with its completion offered only for the models <paramref name="allowed"/> says. */
function onlyFor(allowed) {
    return {
        ...monaco,
        languages: {
            ...monaco.languages,
            registerCompletionItemProvider: (language, provider) => monaco.languages.registerCompletionItemProvider(language, {
                ...provider,
                provideCompletionItems: (model, position, context, token) => (allowed(model) ? provider.provideCompletionItems(model, position, context, token) : undefined),
            }),
        },
    };
}

export function setupEmmet() {
    const html = emmetHTML(monaco, EMMET_HTML);
    const css = emmetCSS(monaco, EMMET_CSS);
    const jsx = emmetJSX(onlyFor((model) => isJsxFile(model.uri.path, model.getValue())), EMMET_JSX);
    return () => { html(); css(); jsx(); };
}

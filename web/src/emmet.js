// Emmet (the Emmet plugin of Settings > Plugins): abbreviations such as ul>li*3, div.card>h2+p or ! expanded by completion, as in VS Code and
// PyCharm. emmet-monaco-es runs the official Emmet engine; it offers an expansion only where an element may start (not inside a tag, an attribute
// or a template tag), which it tells from the tokens: the template languages name theirs as Monaco's HTML does (delimiter.html).
import * as monaco from 'monaco-editor';
import { emmetHTML, emmetCSS } from 'emmet-monaco-es';

/** The languages whose markup Emmet writes: HTML, Django and Jinja templates, EJS, Handlebars and Hogan.js, Twig and Nunjucks, Razor and Vash, PHP. */
export const EMMET_HTML = ['html', 'django-html', 'ejs', 'handlebars', 'twig', 'razor', 'php'];
/** And the stylesheets: m10 is margin: 10px; */
export const EMMET_CSS = ['css', 'scss', 'less'];

export function setupEmmet() {
    const html = emmetHTML(monaco, EMMET_HTML);
    const css = emmetCSS(monaco, EMMET_CSS);
    return () => { html(); css(); };
}

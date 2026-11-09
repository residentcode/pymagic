// HTML tag pairs (a plugin of Settings > Plugins): renaming an element's opening tag renames its closing tag, and back, as you type (the editor's
// linked editing, as VS Code and PyCharm do it). The pair is found by tag-pairs-model.js in HTML, the templates (Django, Jinja, EJS, Handlebars and
// Hogan.js, Twig and Nunjucks, Razor and Vash, PHP), XML, and the JavaScript and TypeScript files that are JSX.
import * as monaco from 'monaco-editor';
import { tagPairAt } from './tag-pairs-model.js';
import { isJsxFile } from './emmet-model.js';

export const TAG_PAIR_LANGUAGES = ['html', 'django-html', 'ejs', 'handlebars', 'twig', 'razor', 'php', 'blade', 'xml', 'javascript', 'typescript'];

export function setupTagPairs() {
    const provider = {
        provideLinkedEditingRanges(model, position) {
            const language = model.getLanguageId();
            if ((language === 'javascript' || language === 'typescript') && !isJsxFile(model.uri.path, model.getValue())) return null;
            const found = tagPairAt(model.getValue(), model.getOffsetAt(position));
            if (!found) return null;
            const range = ([start, end]) => monaco.Range.fromPositions(model.getPositionAt(start), model.getPositionAt(end));
            // what a name may become while it is typed: letters, digits and : . - (svg:path, Foo.Bar, my-element)
            return { ranges: found.map(range), wordPattern: /[A-Za-z][\w:.-]*/ };
        },
    };
    const off = TAG_PAIR_LANGUAGES.map((language) => monaco.languages.registerLinkedEditingRangeProvider(language, provider));
    return () => off.forEach((d) => d.dispose());
}

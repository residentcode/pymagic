// Typing's indentation in every kind of file (indent-rules-model.js): each language's configuration with its indentationRules and onEnterRules
// added, as VS Code has them. Monaco's languages set their own configuration when a file of them is first opened (and JSON's again when its worker
// starts), so setLanguageConfiguration itself adds the rules to whatever configuration is set, whenever it is. Also: every .html file
// (django-html, PyMagic's own) gets HTML's brackets, pairs and Enter between tags, which it had none of; Twig's and Nunjucks' {% / {{ / {# pairs,
// which left a second %} when the closing was typed, become a plain { (its } is typed over).
//
// Closings Monaco cannot move: EJS's <% } %> (Monaco does not indent a line whose typing is in embedded JavaScript), and Handlebars' {{/if}}
// and Svelte's {/if} (moved while typed, Monaco forgets the } it put after the cursor, and the } typed then is a second one). These are moved
// to their opening's indentation once complete, when the cursor has left them (watchClosings).
import * as monaco from 'monaco-editor';
import { conf as htmlConf } from 'monaco-editor/esm/vs/basic-languages/html/html.js';
import { conf as tsConf } from 'monaco-editor/esm/vs/basic-languages/typescript/typescript.js';
import { conf as cssConf } from 'monaco-editor/esm/vs/basic-languages/css/css.js';
import { conf as scssConf } from 'monaco-editor/esm/vs/basic-languages/scss/scss.js';
import { conf as lessConf } from 'monaco-editor/esm/vs/basic-languages/less/less.js';
import { conf as twigConf } from 'monaco-editor/esm/vs/basic-languages/twig/twig.js';
import { conf as liquidConf } from 'monaco-editor/esm/vs/basic-languages/liquid/liquid.js';
import { conf as handlebarsConf } from 'monaco-editor/esm/vs/basic-languages/handlebars/handlebars.js';
import { conf as razorConf } from 'monaco-editor/esm/vs/basic-languages/razor/razor.js';
import { INDENT_RULES, INDENT_OUTDENT, CLOSINGS, closingIndent, closingTag } from './indent-rules-model.js';
import { tagPairAt } from './tag-pairs-model.js';

const JSON_CONF = {
    wordPattern: /(-?\d*\.\d\w*)|([^[{\]}:"',\s]+)/g,
    comments: { lineComment: '//', blockComment: ['/*', '*/'] },
    brackets: [['{', '}'], ['[', ']']],
    autoClosingPairs: [{ open: '{', close: '}', notIn: ['string'] }, { open: '[', close: ']', notIn: ['string'] }, { open: '"', close: '"', notIn: ['string'] }],
};
const DJANGO_CONF = { ...htmlConf, comments: { blockComment: ['{#', '#}'] }, brackets: [...htmlConf.brackets, ['{%', '%}'], ['{{', '}}']] };
// pairs of more than one character ({% → %}) are not typed over when their closing is typed: a plain { instead
const singlePairs = (pairs) => [...(pairs ?? []).filter((p) => p.open.length === 1 && p.close.length === 1 && p.open !== '{'), { open: '{', close: '}' }];
const OURS = new Set(['javascript', 'typescript', 'css', 'scss', 'less', 'json', 'html', 'django-html', 'twig', 'liquid', 'handlebars', 'razor']);

function withRules(id, conf) {
    const html = ['django-html', 'twig'].includes(id) ? htmlConf.onEnterRules : [];
    return {
        ...conf,
        ...(id === 'twig' ? { autoClosingPairs: singlePairs(conf.autoClosingPairs) } : {}),
        indentationRules: INDENT_RULES[id],
        onEnterRules: [
            ...(INDENT_OUTDENT[id] ?? []).map((r) => ({ ...r, action: { indentAction: monaco.languages.IndentAction.IndentOutdent } })),
            ...(conf.onEnterRules ?? []), ...html,
        ],
    };
}

export function setupIndentRules(editor) {
    const set = monaco.languages.setLanguageConfiguration;
    monaco.languages.setLanguageConfiguration = (id, conf) => set(id, OURS.has(id) && conf ? withRules(id, conf) : conf);
    // now too, for a language whose configuration Monaco already set
    const now = { javascript: tsConf, typescript: tsConf, css: cssConf, scss: scssConf, less: lessConf, json: JSON_CONF, html: htmlConf, 'django-html': DJANGO_CONF,
        twig: twigConf, liquid: liquidConf, handlebars: handlebarsConf, razor: razorConf };
    for (const [id, conf] of Object.entries(now)) monaco.languages.setLanguageConfiguration(id, conf);
    watchClosings(editor);
}

// the languages whose closing tags (</div>) are moved: HTML, the templates and JSX (the tag pairs find their openings)
const TAG_LANGUAGES = new Set(['html', 'django-html', 'ejs', 'handlebars', 'twig', 'liquid', 'razor', 'blade', 'javascript', 'typescript']);

/** Line `at` (0-based) as a complete closing: { end, indent (its opening's, or null) }, or null. */
function closingAt(model, lines, at) {
    const language = model.getLanguageId();
    const block = CLOSINGS[language] && closingIndent(language, lines, at);
    if (block) return block;
    const tag = TAG_LANGUAGES.has(language) && closingTag(lines[at]);
    if (!tag) return null;
    const pair = tagPairAt(model.getValue(), model.getOffsetAt({ lineNumber: at + 1, column: tag.name + 1 }));
    const opening = pair && model.getPositionAt(pair[0][0]).lineNumber;
    return { end: tag.end, indent: opening && opening !== at + 1 ? /^[ \t]*/.exec(lines[opening - 1])[0] : null };
}

/**
 * A closing typed on its own line moved to its opening's indentation once complete and the cursor has left it: a closing tag (</div>), EJS's
 * <% } %>, Handlebars' {{/if}} and Svelte's {/if} (and the lines between branches). Only its indentation is changed.
 */
function watchClosings(editor) {
    let pending = null;   // { model, line }
    editor.onDidChangeModelContent((e) => {
        const model = editor.getModel();
        if (e.isUndoing || e.isRedoing || e.changes.length !== 1 || !model) return;
        const language = model.getLanguageId();
        if (!CLOSINGS[language] && !TAG_LANGUAGES.has(language)) return;
        pending = { model, line: e.changes[0].range.startLineNumber };
    });
    editor.onDidChangeCursorPosition((e) => {
        const model = editor.getModel();
        if (!pending || pending.model !== model || model.isDisposed()) { pending = null; return; }
        const { line } = pending;
        if (line > model.getLineCount()) { pending = null; return; }
        const lines = model.getLinesContent();
        const found = closingAt(model, lines, line - 1);
        if (!found) return;                                       // not a closing (yet): kept for the next keys
        if (e.position.lineNumber === line && e.position.column - 1 < found.end) return;   // still typing in it
        pending = null;
        const now = /^[ \t]*/.exec(lines[line - 1])[0];
        if (found.indent === null || now === found.indent) return;
        editor.executeEdits('indent', [{ range: new monaco.Range(line, 1, line, now.length + 1), text: found.indent }]);
    });
}

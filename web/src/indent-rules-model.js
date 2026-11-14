// What indents the next line when Enter is pressed, and what moves a line left as its first characters are typed (Monaco's indentationRules,
// with the editor's autoIndent 'full'), for each kind of file: VS Code's patterns for JavaScript/TypeScript, CSS, JSON and HTML, and each template
// engine's block tags: Django's and Jinja's {% if %}…{% endif %} (Twig, Nunjucks and Liquid too), Handlebars' and Mustache's {{#if}}…{{/if}},
// Blade's @if…@endif, EJS's <% if (a) { %>…<% } %>, Vash's (Razor's) @if (a) {…}, Svelte's {#if}…{/if}. Monaco ships these languages without
// them: a } or </div> typed on its own line stayed where it was, and in a template Enter after an opening tag did not indent. No DOM here.

const VOID = 'area|base|br|col|embed|frame|hr|html|img|input|keygen|link|menuitem|meta|param|source|track|wbr|!doctype';
const join = (parts, flags = '') => new RegExp(parts.map((p) => (p instanceof RegExp ? p.source : p)).join('|'), flags);

// HTML (VS Code's html language-configuration): an element opened and not closed on its line; a closing tag, the end of a comment
const HTML_UP = `<(?!\\?|(?:${VOID})\\b|[^>]*\\/>)([-_.A-Za-z0-9]+)(?=\\s|>)\\b[^>]*>(?!.*<\\/\\1>)|<!--(?!.*-->)`;
// A closing tag </div> is not here: Monaco moves a line by rewriting it up to the cursor, and the tag pairs' linked editing copied that into the
// opening tag (<div> became <>). indent-rules.js moves a complete closing tag to its opening's indentation instead (closingTag).
const HTML_DOWN = '^\\s*-->';
// Svelte's blocks (.svelte shows as HTML): {#if}, {:else}, {/if}
const SVELTE_UP = '\\{[#:][^}]*\\}(?:(?!\\{\\/).)*$';
// Django, Jinja, Twig, Nunjucks, Liquid: a block tag left open on its line ({% set x = 1 %} and Django's {% trans "x" %} are not blocks)
const TAG_BLOCKS = 'if|for|block|with|while|macro|call|filter|raw|verbatim|comment|autoescape|spaceless|unless|case|capture|tablerow|embed|apply|ifchanged|blocktrans|blocktranslate|else|elif|elsif|elseif|empty|when|plural|form|paginate|javascript|stylesheet|schema|style';
const TAG_UP = `\\{%-?\\s*(?:${TAG_BLOCKS})\\b(?:(?!\\{%-?\\s*end).)*$`;
const TAG_DOWN = '^\\s*\\{%-?\\s*(?:end\\w*|else|elif|elsif|elseif|empty|when|plural)\\b';
// Handlebars and Mustache: {{#if}} / {{^x}} / {{else}} open; {{/if}}, {{else}}, {{^}} close
const HBS_UP = '\\{\\{~?(?:#|\\^|else\\b)(?:(?!\\{\\{~?\\/).)*$';
// Blade: @if … @endif and its kind; @section('a', 'b') and @php(…) on one line are not blocks
const BLADE_BLOCKS = 'if|unless|isset|empty|auth|guest|foreach|forelse|for|while|switch|case|default|push|prepend|pushOnce|prependOnce|component|slot|once|production|env|error|can|cannot|canany|verbatim|else|elseif|fragment|session|hasSection|sectionMissing|teleport|persist|script|assets|volt';
const BLADE_UP = `@(?:(?:${BLADE_BLOCKS})\\b|section\\s*\\((?![^)]*,)|php\\s*$)(?:(?!@end).)*$`;
const BLADE_DOWN = '^\\s*@(?:end\\w+|else|elseif|empty|show|stop|overwrite|append|case|default)\\b';
// EJS (and Eta): a scriptlet whose code opens a { and closes the tag (<% if (a) { %>); <% } %> closes it
const EJS_UP = '<%[_=-]?(?:(?!%>).)*\\{\\s*[-_]?%>(?:(?!<%).)*$';
const EJS_DOWN = '^\\s*<%[_-]?\\s*\\}';
// a code block: { (or [ / ( ) left open at the end of the line, as VS Code's JavaScript has it; a line starting with } ] ) closes
const CODE_UP = '^(?:(?!\\/\\/).)*(?:\\{[^}"\'`]*|\\([^)"\'`]*|\\[[^\\]"\'`]*)(?:\\/\\/.*)?$';
const CODE_DOWN = '^\\s*[}\\])]';
// JSX: an element opened and not closed on its line (a .jsx/.tsx file, or React's .js)
const JSX_UP = '^\\s*<(?!\\/)([A-Za-z][\\w.:-]*)?(?:\\s[^<>]*)?>(?!.*<\\/\\1?>)\\s*$';

/** The rules of each language id: { increaseIndentPattern, decreaseIndentPattern }. */
export const INDENT_RULES = {
    javascript: { increaseIndentPattern: join([CODE_UP, JSX_UP]), decreaseIndentPattern: new RegExp(CODE_DOWN) },
    typescript: { increaseIndentPattern: join([CODE_UP, JSX_UP]), decreaseIndentPattern: new RegExp(CODE_DOWN) },
    css: { increaseIndentPattern: /^.*\{[^}]*$/, decreaseIndentPattern: /^\s*\}/ },
    scss: { increaseIndentPattern: /^.*\{[^}]*$/, decreaseIndentPattern: /^\s*\}/ },
    less: { increaseIndentPattern: /^.*\{[^}]*$/, decreaseIndentPattern: /^\s*\}/ },
    json: { increaseIndentPattern: /^.*(?:\{[^}]*|\[[^\]]*)$/, decreaseIndentPattern: /^\s*[}\]]/ },
    // Svelte's {/if} and {:else}, Handlebars' {{/if}} and {{else}}: moved by indent-rules.js once typed (CLOSINGS), not by Monaco as typed
    html: { increaseIndentPattern: join([HTML_UP, SVELTE_UP], 'i'), decreaseIndentPattern: new RegExp(HTML_DOWN, 'i') },
    'django-html': { increaseIndentPattern: join([HTML_UP, TAG_UP], 'i'), decreaseIndentPattern: join([HTML_DOWN, TAG_DOWN], 'i') },
    twig: { increaseIndentPattern: join([HTML_UP, TAG_UP], 'i'), decreaseIndentPattern: join([HTML_DOWN, TAG_DOWN], 'i') },
    liquid: { increaseIndentPattern: join([HTML_UP, TAG_UP], 'i'), decreaseIndentPattern: join([HTML_DOWN, TAG_DOWN], 'i') },
    handlebars: { increaseIndentPattern: join([HTML_UP, HBS_UP], 'i'), decreaseIndentPattern: new RegExp(HTML_DOWN, 'i') },
    blade: { increaseIndentPattern: join([HTML_UP, BLADE_UP], 'i'), decreaseIndentPattern: join([HTML_DOWN, BLADE_DOWN], 'i') },
    ejs: { increaseIndentPattern: join([HTML_UP, EJS_UP], 'i'), decreaseIndentPattern: join([HTML_DOWN, EJS_DOWN], 'i') },
    razor: { increaseIndentPattern: join([HTML_UP, '\\{\\s*$'], 'i'), decreaseIndentPattern: join([HTML_DOWN, '^\\s*\\}'], 'i') },
};

/**
 * Closings that move to their opening's indentation once typed (indent-rules.js watchClosings: Monaco cannot do these as they are typed): EJS's
 * <% } %> (and <% }) %>), Handlebars' and Mustache's {{/if}}, Svelte's {/if}; and the lines between branches: <% } else { %>, {{else}}, {{^}},
 * {:else}. { close, middle, open } of each language id.
 */
export const CLOSINGS = {
    ejs: { close: /^\s*<%[_-]?\s*\}[^%]*?[-_]?%>/, middle: /^\s*<%[_-]?\s*\}[^%]*\{\s*[-_]?%>/, open: new RegExp(EJS_UP) },
    handlebars: { close: /^\s*\{\{~?\/[^}]*\}\}/, middle: /^\s*\{\{~?(?:else\b[^}]*|\^\s*~?)\}\}/, open: /\{\{~?(?:#|\^(?=\s*[\w.@]))[^}]*\}\}(?:(?!\{\{~?\/).)*$/ },
    html: { close: /^\s*\{\/\w+\s*\}/, middle: /^\s*\{:[^}]*\}/, open: /\{#\w[^}]*\}(?:(?!\{\/).)*$/ },
};

/**
 * Whether line `at` (0-based) of `lines` is a complete closing of its language: { end (its length on the line), indent (its opening's
 * indentation, null when there is none above) }; null when it is not one.
 */
export function closingIndent(languageId, lines, at) {
    const rule = CLOSINGS[languageId];
    const m = rule && (rule.middle.exec(lines[at]) ?? rule.close.exec(lines[at]));
    if (!m) return null;
    let depth = 0;
    for (let i = at - 1; i >= 0; i--) {
        const text = lines[i];
        if (rule.middle.test(text)) continue;
        if (rule.close.test(text)) { depth++; continue; }
        if (rule.open.test(text)) {
            if (depth === 0) return { end: m[0].length, indent: /^[ \t]*/.exec(text)[0] };
            depth--;
        }
    }
    return { end: m[0].length, indent: null };
}

/**
 * Enter between an opening and its closing on one line ({% if a %}|{% endif %}, {{#if}}|{{/if}}, <% { %>|<% } %>, @if|@endif): the cursor's
 * line indented and the closing on a line of its own. [{ beforeText, afterText }] of each language id (Monaco's IndentOutdent).
 */
export const INDENT_OUTDENT = {
    'django-html': [{ beforeText: new RegExp(TAG_UP, 'i'), afterText: /^\s*\{%-?\s*end\w*/i }],
    twig: [{ beforeText: new RegExp(TAG_UP, 'i'), afterText: /^\s*\{%-?\s*end\w*/i }],
    liquid: [{ beforeText: new RegExp(TAG_UP, 'i'), afterText: /^\s*\{%-?\s*end\w*/i }],
    handlebars: [{ beforeText: new RegExp(HBS_UP, 'i'), afterText: /^\s*\{\{~?\// }],
    blade: [{ beforeText: new RegExp(BLADE_UP, 'i'), afterText: /^\s*@end\w+/i }],
    ejs: [{ beforeText: new RegExp(EJS_UP, 'i'), afterText: /^\s*<%[_-]?\s*\}/ }],
    html: [{ beforeText: new RegExp(SVELTE_UP), afterText: /^\s*\{\// }],
};

/** A closing tag alone at the start of a line (</div>, </template>, JSX's </Foo.Bar>): where its name starts on the line, and where it ends. */
export function closingTag(line) {
    const m = /^(\s*)<\/([A-Za-z][\w:.-]*)\s*>/.exec(line);
    return m ? { name: m[1].length + 2, end: m[0].length } : null;
}

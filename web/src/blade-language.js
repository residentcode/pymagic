// Blade (Laravel's templates, *.blade.php): HTML with {{ $echo }}, {!! $raw !!}, {{-- comments --}}, @directives (@if, @foreach, @extends,
// @section, @csrf, …) whose (…) hold PHP, @php … @endphp blocks, and <x-component> tags whose :prop="…" attributes are PHP. Monaco has no such
// language: the HTML is tokenized as Monaco's html tokenizer names its parts (so Emmet, tag pairs and Tailwind read it as HTML), and the PHP
// of each place is tokenized here as Monaco's php tokenizer names its tokens (variables, strings, numbers, keywords). @{{ and @@ are escapes.
import * as monaco from 'monaco-editor';

monaco.languages.register({ id: 'blade', extensions: ['.blade.php'], aliases: ['Blade', 'blade'] });

const PHP_KEYWORDS = ['as', 'and', 'or', 'new', 'fn', 'function', 'instanceof', 'true', 'false', 'null', 'array', 'isset', 'empty', 'match', 'static', 'self', 'parent', 'clone', 'echo', 'return', 'if', 'else', 'elseif', 'foreach', 'for', 'while', 'use', 'class'];

// PHP inside an echo, a directive's brackets or a block: the same rules in each, ended by its own closer
const php = (end, endToken, extra = []) => [
    ...extra,
    end,
    [/\$[A-Za-z_]\w*/, 'variable'],
    [/"([^"\\]|\\.)*"/, 'string'],
    [/'([^'\\]|\\.)*'/, 'string'],
    [/\d+(\.\d+)?/, 'number'],
    [/[A-Za-z_\\][\w\\]*(?=\s*\()/, 'function'],
    [/[A-Za-z_][\w]*/, { cases: { '@phpKeywords': 'keyword', '@default': 'identifier' } }],
    [/->|::|=>|\?\?|[-+*/%=<>!&|.?:]/, 'operator'],
    [/[[\]{}(),;]/, 'delimiter'],
    [/\s+/, ''],
];

const bladeOpen = [
    [/\{\{--/, { token: 'comment', next: '@bladeComment' }],
    [/[@]\{\{/, ''],                                                     // @{{ … }}: left for JavaScript (Vue, Alpine)
    [/\{!!/, { token: 'delimiter.blade', next: '@raw' }],
    [/\{\{/, { token: 'delimiter.blade', next: '@echo' }],
];

// a tag's attributes and their values (Blade's echoes and directives among them); the tag's own > is its state's
const attributes = () => [
    ...bladeOpen,
    [/([@][A-Za-z_]\w*)(\s*)(\()/, ['keyword.blade', '', { token: 'delimiter', next: '@directiveArgs' }]],
    [/[@][A-Za-z_]\w*/, 'keyword.blade'],
    [/(:[\w\-:.]+)(\s*)(=)(\s*)(")/, ['attribute.name', '', 'delimiter', '', { token: 'attribute.value', next: '@phpAttribute' }]],
    [/"/, 'attribute.value', '@doubleQuoted'],
    [/'/, 'attribute.value', '@singleQuoted'],
    [/[\w\-:.@]+/, 'attribute.name'],
    [/=/, 'delimiter'],
    [/[ \t\r\n]+/, ''],
];

// a fault in the grammar costs Blade its colours, not the page (Monaco checks a grammar as it is set, and an error there would stop the start)
try { monaco.languages.setMonarchTokensProvider('blade', {
    defaultToken: '',
    tokenPostfix: '.html',   // named as Monaco's HTML names its tokens (delimiter.html), which Emmet reads
    ignoreCase: false,
    phpKeywords: PHP_KEYWORDS,
    tokenizer: {
        root: [
            ...bladeOpen,
            [/[@][@]/, ''],
            // (an @ in Monarch's patterns names one of the grammar's lists: a real @ is written [@])
            [/[@]php\b(?!\s*\()/, { token: 'keyword.blade', next: '@phpBlock' }],
            [/([@][A-Za-z_]\w*)(\s*)(\()/, ['keyword.blade', '', { token: 'delimiter', next: '@directiveArgs' }]],
            [/[@][A-Za-z_]\w*/, 'keyword.blade'],
            [/<!DOCTYPE/i, 'metatag', '@doctype'],
            [/<!--/, 'comment', '@comment'],
            // <script> and <style>: their insides coloured as JavaScript and CSS, as Monaco's HTML does
            [/(<)(script)(?=[\s>])/i, ['delimiter', { token: 'tag', next: '@scriptTag' }]],
            [/(<)(style)(?=[\s>])/i, ['delimiter', { token: 'tag', next: '@styleTag' }]],
            [/(<)((?:[\w-]+:)?[\w.-]+)/, ['delimiter', { token: 'tag', next: '@tag' }]],
            [/(<\/)((?:[\w-]+:)?[\w.-]+)(\s*)(>)/, ['delimiter', 'tag', '', 'delimiter']],
            [/</, 'delimiter'],
            [/[^<@{]+/, ''],
            [/[@{]/, ''],
        ],
        doctype: [[/[^>]+/, 'metatag.content'], [/>/, 'metatag', '@pop']],
        comment: [[/-->/, 'comment', '@pop'], [/[^-]+/, 'comment.content'], [/./, 'comment.content']],
        bladeComment: [[/--\}\}/, 'comment', '@pop'], [/[^-]+/, 'comment'], [/./, 'comment']],
        // inside <tag …>: attributes, their values; :prop="…" of a component is PHP
        tag: [
            [/\/?>/, 'delimiter', '@pop'],
            ...attributes(),
        ],
        // the attributes of <script …> / <style …>, then their text as the embedded language up to the closing tag
        scriptTag: [
            [/\/>/, 'delimiter', '@pop'],
            [/>/, { token: 'delimiter', switchTo: '@scriptBody', nextEmbedded: 'text/javascript' }],
            ...attributes(),
        ],
        scriptBody: [
            [/<\/script\s*>/i, { token: '@rematch', switchTo: '@endTag', nextEmbedded: '@pop' }],
            [/[^<]+/, ''],
            [/</, ''],
        ],
        styleTag: [
            [/\/>/, 'delimiter', '@pop'],
            [/>/, { token: 'delimiter', switchTo: '@styleBody', nextEmbedded: 'text/css' }],
            ...attributes(),
        ],
        styleBody: [
            [/<\/style\s*>/i, { token: '@rematch', switchTo: '@endTag', nextEmbedded: '@pop' }],
            [/[^<]+/, ''],
            [/</, ''],
        ],
        endTag: [[/(<\/)(script|style)(\s*)(>)/i, ['delimiter', 'tag', '', { token: 'delimiter', next: '@pop' }]]],
        doubleQuoted: [...bladeOpen, [/"/, 'attribute.value', '@pop'], [/[^"{]+/, 'attribute.value'], [/\{/, 'attribute.value']],
        singleQuoted: [...bladeOpen, [/'/, 'attribute.value', '@pop'], [/[^'{]+/, 'attribute.value'], [/\{/, 'attribute.value']],
        echo: php([/\}\}/, { token: 'delimiter.blade', next: '@pop' }]),
        raw: php([/!!\}/, { token: 'delimiter.blade', next: '@pop' }]),
        phpAttribute: php([/"/, { token: 'attribute.value', next: '@pop' }]),
        // a directive's (…): PHP to the bracket that closes it, brackets inside counted
        directiveArgs: php([/\)/, { token: 'delimiter', next: '@pop' }], [[/\(/, { token: 'delimiter', next: '@directiveArgs' }]]),
        phpBlock: php([/[@]endphp\b/,{ token: 'keyword.blade', next: '@pop' }], [[/\/\/.*$/, 'comment'], [/\/\*/, { token: 'comment', next: '@phpComment' }]]),
        phpComment: [[/\*\//, 'comment', '@pop'], [/[^*]+/, 'comment'], [/\*/, 'comment']],
    },
}); } catch (e) { console.warn('Blade colours are off:', e); }

monaco.languages.setLanguageConfiguration('blade', {
    comments: { blockComment: ['{{--', '--}}'] },
    brackets: [['<!--', '-->'], ['<', '>'], ['{{', '}}'], ['{!!', '!!}'], ['{', '}'], ['(', ')'], ['[', ']']],
    autoClosingPairs: [{ open: '{{', close: ' }}' }, { open: '{!!', close: ' !!}' }, { open: '"', close: '"' }, { open: "'", close: "'" }, { open: '(', close: ')' }, { open: '[', close: ']' }],
    surroundingPairs: [{ open: '"', close: '"' }, { open: "'", close: "'" }, { open: '<', close: '>' }, { open: '(', close: ')' }],
    // @if … @endif, @foreach … @endforeach, @section … @endsection and their kind fold like tags
    folding: { markers: { start: /^\s*(<(?!\/)[\w-]+[^>]*>?$|@(if|unless|isset|empty|auth|guest|foreach|forelse|for|while|switch|section|push|prepend|component|slot|php|once|production|env|error|can|cannot|verbatim)\b)/, end: /^\s*(<\/[\w-]+>|@end\w+\b)/ } },
    wordPattern: /(-?\d*\.\d\w*)|([^`~!#%^&*()=+[{\]}\\|;:'",.<>/?\s]+)/g,
});

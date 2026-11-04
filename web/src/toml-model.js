// The colouring of TOML (pyproject.toml, ruff.toml, Cargo.toml, Pipfile, uv.lock): Monaco has no such language. Table headers as types, keys as
// variables, strings, numbers, dates, booleans and comments with the token names the themes already have, so it looks as VS Code shows TOML
// (Even Better TOML). A value's state follows it across lines: a multi-line string, an array or an inline table. No Monaco here; toml-language.js
// registers it and web/test/toml.test.mjs checks it.

const BARE = String.raw`[A-Za-z0-9_-]+`;
const BASIC = String.raw`"(?:[^"\\\n]|\\.)*"`;
const LITERAL = String.raw`'[^'\n]*'`;
const SIMPLE_KEY = `(?:${BARE}|${BASIC}|${LITERAL})`;
/** A key, dotted or not, when an = follows it. */
const KEY = new RegExp(`${SIMPLE_KEY}(?:\\s*\\.\\s*${SIMPLE_KEY})*(?=\\s*=)`);

const DATE = /\d{4}-\d\d-\d\d(?:[Tt ]\d\d:\d\d(?::\d\d(?:\.\d+)?)?(?:[Zz]|[+-]\d\d:\d\d)?)?|\d\d:\d\d(?::\d\d(?:\.\d+)?)?/;
const FLOAT = /[+-]?(?:inf|nan)|[+-]?(?:0|[1-9][\d_]*)(?:\.\d[\d_]*(?:[eE][+-]?\d[\d_]*)?|[eE][+-]?\d[\d_]*)/;
const HEX = /0x[0-9a-fA-F_]+|0o[0-7_]+|0b[01_]+/;
const INT = /[+-]?(?:0|[1-9][\d_]*)/;
const NOT_A_WORD = /(?![\w.:-])/;

const fit = (re) => new RegExp(`(?:${re.source})${NOT_A_WORD.source}`);

/** The rules of one value; `after` is what a one-line value does when it ends (pop out of `value`, or stay in an array). */
function valueRules(after) {
    const done = (token) => (after ? { token, next: after } : token);
    const into = (state) => (after ? { token: 'string', switchTo: state } : { token: 'string', next: state });
    return [
        [/"""/, into('@basicLines')],
        [/'''/, into('@literalLines')],
        [new RegExp(BASIC), done('string')],
        [/"(?:[^"\\\n]|\\.)*(?=\n|$)/, done('string.invalid')],
        [new RegExp(LITERAL), done('string')],
        [/'[^'\n]*(?=\n|$)/, done('string.invalid')],
        [fit(DATE), done('number')],
        [fit(HEX), done('number.hex')],
        [fit(FLOAT), done('number.float')],
        [fit(INT), done('number')],
        [/(?:true|false)(?![\w-])/, done('keyword')],
        [/\[/, after ? { token: 'delimiter.bracket', switchTo: '@array' } : { token: 'delimiter.bracket', next: '@array' }],
        [/\{/, after ? { token: 'delimiter.bracket', switchTo: '@inline' } : { token: 'delimiter.bracket', next: '@inline' }],
    ];
}

/** Monaco's Monarch definition of TOML. */
export function tomlTokenizer() {
    return {
        defaultToken: '',
        includeLF: true,       // each line ends with its \n, so a value ends with its line: TOML has none on the line after its =
        tokenPostfix: '.toml',
        tokenizer: {
            root: [
                [/\s+/, ''],
                [/#.*/, 'comment'],
                [/(\[\[?)([^\]#]*)(\]\]?)/, ['delimiter.bracket', 'type', 'delimiter.bracket']],
                [KEY, 'variable'],
                [/=/, { token: 'delimiter', next: '@value' }],
                [/./, ''],
            ],
            // after an =: one value, then back to where the key was
            value: [
                [/[ \t]+/, ''],
                [/\n/, { token: '', next: '@pop' }],
                ...valueRules('@pop'),
                [/\S/, { token: '@rematch', next: '@pop' }],
            ],
            array: [
                [/\s+/, ''],
                [/#.*/, 'comment'],
                [/,/, 'delimiter'],
                [/\]/, { token: 'delimiter.bracket', next: '@pop' }],
                ...valueRules(null),
                [/./, ''],
            ],
            inline: [
                [/\s+/, ''],
                [KEY, 'variable'],
                [/=/, { token: 'delimiter', next: '@value' }],
                [/,/, 'delimiter'],
                [/\}/, { token: 'delimiter.bracket', next: '@pop' }],
                [/./, ''],
            ],
            basicLines: [
                [/"{3,5}/, { token: 'string', next: '@pop' }],   // up to two quotes of the text may come before the closing three
                [/\\(?:[btnfr"\\]|u[0-9a-fA-F]{4}|U[0-9a-fA-F]{8}|[ \t]*\n)/, 'string.escape'],
                [/[^"\\]+|"|\\/, 'string'],
            ],
            literalLines: [
                [/'{3,5}/, { token: 'string', next: '@pop' }],
                [/[^']+|'/, 'string'],
            ],
        },
    };
}

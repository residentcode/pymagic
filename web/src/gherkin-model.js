// The colouring of Gherkin (.feature files) in each of its languages, made from Cucumber's keyword list (gherkin-languages.json, the same file the
// language server reads). A file is English until a `# language: de` comment before its first keyword switches it; no DOM here, so it is tested in node.

const escape = (s) => s.replace(/[.*+?^${}()|[\]\\\/]/g, '\\$&');

/** The keywords of one kind, longest first, so that "Gegeben seien" is not taken for "Gegeben sei". */
const longestFirst = (words) => [...new Set(words.map((w) => w.trim()).filter((w) => w.length > 0))].sort((a, b) => b.length - a.length);

/** The rules of one language: keywords, tags, comments, doc strings, tables and an outline's <parameters>. */
export function dialectRules(dialect) {
    const blocks = longestFirst(['feature', 'rule', 'background', 'scenarioOutline', 'scenario', 'examples'].flatMap((k) => dialect[k] ?? []));
    const stepWords = ['given', 'when', 'then', 'and', 'but'].flatMap((k) => dialect[k] ?? []);
    // a keyword written with a space after it ("Given ") needs one before the text; Chinese and Japanese ones ("假如") are written against it
    const spaced = new Set(stepWords.filter((w) => w.endsWith(' ')).map((w) => w.trim()));
    const steps = longestFirst(stepWords).map((w) => escape(w) + (spaced.has(w) ? '(?=\\s)' : ''));
    return [
        [/^\s*#.*$/, 'comment'],
        [/^\s*@.*$/, 'tag'],
        [new RegExp(`^(\\s*)(${blocks.map(escape).join('|')})(\\s*:)`), ['', 'keyword', 'delimiter']],
        [new RegExp(`^(\\s*)(${steps.join('|')})`), ['', 'keyword']],
        [/^\s*("""|```)/, 'string', '@docString'],
        [/\|/, 'delimiter'],
        [/<[^<>\s][^<>]*>/, 'variable'],
        [/"[^"]*"/, 'string'],
        [/-?\d+(\.\d+)?\b/, 'number'],
    ];
}

/** The state of a language's lines. */
export const stateOf = (code) => 'lang_' + code;

/**
 * Monarch's tokenizer for every language of the list: the first lines (comments and blank lines) are read in `root`, where `# language: xx`
 * switches to that language's state and any other line to English's, as the language server reads it.
 */
export function gherkinTokenizer(languages) {
    const codes = Object.keys(languages).sort((a, b) => b.length - a.length);
    const tokenizer = {
        root: [
            [new RegExp(`^\\s*#\\s*language\\s*:\\s*(${codes.map(escape).join('|')})\\s*$`), { token: 'comment', switchTo: '@' + stateOf('$1') }],
            [/^\s*#.*$/, 'comment'],
            [/^(?=\s*\S)/, { token: '', switchTo: '@' + stateOf('en') }],
        ],
        docString: [
            [/^\s*("""|```)\s*$/, 'string', '@pop'],
            [/.*$/, 'string'],
        ],
    };
    for (const code of codes) tokenizer[stateOf(code)] = dialectRules(languages[code]);
    return { defaultToken: '', tokenizer };
}

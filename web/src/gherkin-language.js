// Gherkin (.feature files of behave and pytest-bdd): Monaco has no such language. Keywords, tags, comments, doc strings, tables and an
// outline's <parameters> are coloured with the token names Monaco's themes already have (keyword, tag, comment, string, variable).
import * as monaco from 'monaco-editor';

monaco.languages.register({ id: 'gherkin', extensions: ['.feature'], aliases: ['Gherkin', 'gherkin'] });

monaco.languages.setLanguageConfiguration('gherkin', {
    comments: { lineComment: '#' },
    autoClosingPairs: [{ open: '"', close: '"' }, { open: '<', close: '>' }, { open: '|', close: '|' }],
});

monaco.languages.setMonarchTokensProvider('gherkin', {
    defaultToken: '',
    tokenizer: {
        root: [
            [/^\s*#.*$/, 'comment'],
            [/^\s*@.*$/, 'tag'],
            [/^(\s*)(Feature|Rule|Background|Scenario Outline|Scenario Template|Scenario|Example|Examples|Scenarios)(:)/, ['', 'keyword', 'delimiter']],
            [/^(\s*)(Given|When|Then|And|But|\*)(?=\s)/, ['', 'keyword']],
            [/^\s*("""|```)/, 'string', '@docString'],
            [/\|/, 'delimiter'],
            [/<[^<>\s][^<>]*>/, 'variable'],
            [/"[^"]*"/, 'string'],
            [/-?\d+(\.\d+)?\b/, 'number'],
        ],
        docString: [
            [/^\s*("""|```)\s*$/, 'string', '@pop'],
            [/.*$/, 'string'],
        ],
    },
});

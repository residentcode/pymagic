// EJS (Express's ejs view engine, and Eta's same syntax): HTML whose <% %>, <%= %> and <%- %> hold JavaScript. Monaco has no such language: the
// HTML is tokenized here as Monaco's html tokenizer names its parts (tag, attribute.name, attribute.value, delimiter, comment), and the code of each
// tag is Monaco's javascript, embedded, also inside an attribute's value (href="<%= url %>"); <%# %> is a comment.
import * as monaco from 'monaco-editor';
import { INDENT_RULES, INDENT_OUTDENT } from './indent-rules-model.js';

monaco.languages.register({ id: 'ejs', extensions: ['.ejs', '.eta'], aliases: ['EJS', 'ejs'] });

const ejsOpen = [/<%#/, { token: 'comment', next: '@ejsComment' }];
const codeOpen = [/<%[_=-]?/, { token: 'delimiter', next: '@ejsCode', nextEmbedded: 'javascript' }];

monaco.languages.setMonarchTokensProvider('ejs', {
    defaultToken: '',
    tokenPostfix: '.html',   // named as Monaco's HTML names its tokens (delimiter.html), which Emmet reads
    ignoreCase: true,
    tokenizer: {
        root: [
            ejsOpen,
            codeOpen,
            [/<!DOCTYPE/, 'metatag', '@doctype'],
            [/<!--/, 'comment', '@comment'],
            [/(<)((?:[\w-]+:)?[\w-]+)/, ['delimiter', { token: 'tag', next: '@tag' }]],
            [/(<\/)((?:[\w-]+:)?[\w-]+)(\s*)(>)/, ['delimiter', 'tag', '', 'delimiter']],
            [/</, 'delimiter'],
            [/[^<]+/, ''],
        ],
        doctype: [
            [/[^>]+/, 'metatag.content'],
            [/>/, 'metatag', '@pop'],
        ],
        comment: [
            [/-->/, 'comment', '@pop'],
            [/[^-]+/, 'comment.content'],
            [/./, 'comment.content'],
        ],
        // inside <tag ...>: attributes, their values, EJS tags
        tag: [
            ejsOpen,
            codeOpen,
            [/\/?>/, 'delimiter', '@pop'],
            [/"/, 'attribute.value', '@doubleQuoted'],
            [/'/, 'attribute.value', '@singleQuoted'],
            [/[\w\-:.@]+/, 'attribute.name'],
            [/=/, 'delimiter'],
            [/[ \t\r\n]+/, ''],
        ],
        doubleQuoted: [ejsOpen, codeOpen, [/"/, 'attribute.value', '@pop'], [/[^"<]+/, 'attribute.value'], [/</, 'attribute.value']],
        singleQuoted: [ejsOpen, codeOpen, [/'/, 'attribute.value', '@pop'], [/[^'<]+/, 'attribute.value'], [/</, 'attribute.value']],
        // the JavaScript of a tag, until its %>
        ejsCode: [
            [/[_-]?%>/, { token: 'delimiter', next: '@pop', nextEmbedded: '@pop' }],
        ],
        ejsComment: [
            [/[_-]?%>/, 'comment', '@pop'],
            [/[^%_-]+/, 'comment'],
            [/./, 'comment'],
        ],
    },
});

monaco.languages.setLanguageConfiguration('ejs', {
    comments: { blockComment: ['<%#', '%>'] },
    brackets: [['<%', '%>'], ['{', '}'], ['(', ')'], ['[', ']']],
    // no <% → %> and no { → }: typed as people type them (<% if (a) { %> … <% } %>), they left a second %> and a } after the tag
    autoClosingPairs: [{ open: '(', close: ')' }, { open: '[', close: ']' }, { open: '"', close: '"' }, { open: "'", close: "'" }],
    surroundingPairs: [{ open: '"', close: '"' }, { open: "'", close: "'" }, { open: '<', close: '>' }],
    // Enter after <% if (a) { %> or an element left open indents; <% } %> and a closing tag typed on their own line move left
    indentationRules: INDENT_RULES.ejs,
    onEnterRules: INDENT_OUTDENT.ejs.map((r) => ({ ...r, action: { indentAction: monaco.languages.IndentAction.IndentOutdent } })),
});

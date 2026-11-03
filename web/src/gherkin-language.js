// Gherkin (.feature files of behave and pytest-bdd): Monaco has no such language. Keywords, tags, comments, doc strings, tables and an
// outline's <parameters> are coloured with the token names Monaco's themes already have (keyword, tag, comment, string, variable), in each
// of Gherkin's languages (`# language: de` on the first line; gherkin-model.js).
/*! Gherkin's keywords in its languages: gherkin-languages.json from Cucumber, MIT License, Copyright (c) 2017 Cucumber Ltd, Gaspar Nagy,
    Björn Rasmusson, Peter Sergeant, and contributors. The licence's full text is in the app's host\Gherkin\LICENSE.txt. */
import * as monaco from 'monaco-editor';
import languages from '../../src/PyMagic.Server/Gherkin/gherkin-languages.json';
import { gherkinTokenizer } from './gherkin-model.js';

monaco.languages.register({ id: 'gherkin', extensions: ['.feature'], aliases: ['Gherkin', 'gherkin'] });

monaco.languages.setLanguageConfiguration('gherkin', {
    comments: { lineComment: '#' },
    autoClosingPairs: [{ open: '"', close: '"' }, { open: '<', close: '>' }, { open: '|', close: '|' }],
});

monaco.languages.setMonarchTokensProvider('gherkin', gherkinTokenizer(languages));

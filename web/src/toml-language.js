// TOML (pyproject.toml, ruff.toml, Cargo.toml, Pipfile, uv.lock): Monaco has no such language, so these files were plain text. The colouring is
// toml-model.js.
import * as monaco from 'monaco-editor';
import { tomlTokenizer } from './toml-model.js';

monaco.languages.register({ id: 'toml', extensions: ['.toml'], filenames: ['Pipfile', 'poetry.lock', 'uv.lock', 'pdm.lock', 'Cargo.lock'], aliases: ['TOML', 'toml'] });

monaco.languages.setLanguageConfiguration('toml', {
    comments: { lineComment: '#' },
    brackets: [['[', ']'], ['{', '}']],
    autoClosingPairs: [{ open: '[', close: ']' }, { open: '{', close: '}' }, { open: '"', close: '"', notIn: ['string'] }, { open: "'", close: "'", notIn: ['string'] }],
    surroundingPairs: [{ open: '[', close: ']' }, { open: '{', close: '}' }, { open: '"', close: '"' }, { open: "'", close: "'" }],
});

monaco.languages.setMonarchTokensProvider('toml', tomlTokenizer());

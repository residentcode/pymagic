// Which words of a file are looked at for typos, and where they are: the prose of comments and strings, the names a file defines
// (split at underscores and capitals), and the whole text of Markdown and plain text. What is spelled right is Windows' to say (spell.js asks the
// engine); what a programmer writes and no dictionary has is not asked about (TECHNICAL). No DOM here: web/test/spell.test.mjs checks it.
import { findInjections, scanPython } from './injection-model.js';

/** Words of programming that ordinary dictionaries miss. A project's own words go to its dictionary (`.pymagic/dictionary.txt`). */
export const TECHNICAL = new Set(`
args kwargs init config configs param params str int async await util utils json yaml yml toml html css http https url urls uri sql sqlite pytest unittest
django flask fastapi numpy pandas namespace namespaces boolean enum enums tuple tuples dict dicts regex regexp readme todo fixme noqa pylint ruff mypy
stdin stdout stderr env environ dev prod repo repos sync auth admin api apis cli ui ux lib libs src dir dirs tmp temp msg req resp func funcs cls idx
kwarg mixin mixins metaclass dataclass dataclasses pyi venv pip wsgi asgi orm crud uuid utf ascii unicode bytes bool repr getattr setattr hasattr
isinstance lambda iter dunder stdlib timestamp timestamps timezone datetime localhost backend frontend middleware serializer serializers queryset
querysets webhook webhooks hostname username filename pathname dirname mkdir rmdir chmod sudo cron daemon refactor refactoring refactorings dedupe
upsert tokenizer tokenize tokenizer lexer parser linter formatter autocomplete inlay typeshed foo bar baz qux spam eggs ham tmpl templ tpl
sqlalchemy psycopg pymysql postgres postgresql mysql sqlite3 yappi cprofile pstats ipython jupyter notebook notebooks conda virtualenv pyproject setuptools
wheel sdist pypi gitignore github gitlab bitbucket dockerfile docker kubernetes nginx gunicorn uvicorn celery redis redux webpack npm nodejs javascript
typescript monaco electron vscode pycharm intellij jetbrains markdown rst docutils sphinx mermaid favicon png jpg jpeg gif svg pdf csv tsv xml xsd
utf8 ascii latin ansi regexes substring substrings prefix suffixes iterable iterables iterator iterators generator generators callable callables
deserialize serialize serializes deserializes deserialization serialization boolean booleans nullable nullables enum enums kwargs varargs autouse
fixture fixtures parametrize parametrized monkeypatch mocking mock mocks stub stubs subclass subclasses superclass baseclass dunder initializer
`.split(/\s+/).filter(Boolean));

/** The words of an identifier or a chunk of prose: `parseHTTPResponse` is parse, HTTP, Response; `my_var` is my, var. */
export function splitWords(chunk) {
    const words = [];
    for (const part of chunk.split(/[^A-Za-z']+|_+|\d+/)) {
        if (!part) continue;
        for (const m of part.matchAll(/[A-Z]+(?![a-z])|[A-Z]?[a-z]+(?:'[a-z]+)?/g)) words.push({ word: m[0], at: m.index + (part === chunk ? 0 : chunk.indexOf(part)) });
    }
    return words;
}

/** Whether a word is worth asking about: not a short one, not an acronym (all capitals), not a word of programming. */
export function worthAsking(word, min) {
    if (word.length < min) return false;
    if (/^[A-Z]+$/.test(word)) return false;
    if (/[^A-Za-z']/.test(word)) return false;
    return !TECHNICAL.has(word.toLowerCase());
}

/** The words in a piece of prose (a comment, a string, a line of Markdown) with where each is: [{word, start}] (start is an offset in the piece). */
function proseWords(piece, min) {
    const found = [];
    for (const run of piece.matchAll(/\S+/g)) {
        const chunk = run[0];
        // paths, addresses, e-mails, hex numbers, anything with digits, brackets of code
        if (/[\\/@]|:\/\/|^\w+\.\w+\(|^0x/i.test(chunk) || /\d/.test(chunk) || /^[-=+*#`<>{}|~^]+$/.test(chunk)) continue;
        const code = /_|[a-z][A-Z]/.test(chunk);                          // an identifier written into the prose: its parts are names
        for (const { word, at } of splitWords(chunk))
            if (worthAsking(word, code ? 4 : min)) found.push({ word, start: run.index + at });
    }
    return found;
}

/** The words a Python source defines: functions, classes, parameters and the names assigned at the start of a line. */
function definedNames(text, strings, comments) {
    const skip = [...strings, ...comments].map((s) => [s.start, s.end]);
    const inside = (o) => skip.some(([a, b]) => o >= a && o < b);
    const found = [];
    const take = (name, base) => { for (const { word, at } of splitWords(name)) if (worthAsking(word, 4)) found.push({ word, start: base + at, kind: 'name' }); };
    for (const m of text.matchAll(/\b(?:def|class)\s+([A-Za-z_]\w*)/g)) if (!inside(m.index)) take(m[1], m.index + m[0].length - m[1].length);
    for (const m of text.matchAll(/\bdef\s+\w+\s*\(([^)]*)\)/g)) {
        if (inside(m.index)) continue;
        const base = m.index + m[0].indexOf('(') + 1;
        for (const p of m[1].matchAll(/(?:^|,)\s*\*{0,2}([A-Za-z_]\w*)/g)) take(p[1], base + p.index + p[0].lastIndexOf(p[1]));
    }
    for (const m of text.matchAll(/^[ \t]*([A-Za-z_]\w*)\s*(?::[^=\n]+)?=(?!=)/gm)) if (!inside(m.index)) take(m[1], m.index + m[0].indexOf(m[1]));
    return found;
}

/** Everything to check in a Python source: [{word, start, end, kind: 'comment' | 'string' | 'name'}]. */
export function pythonWords(text) {
    const { strings, comments } = scanPython(text);
    const injected = new Set(findInjections(text).map((i) => i.start));
    const out = [];
    for (const c of comments) {
        if (/^\s*(?:noqa|type:|pylint|ruff|fmt:|language=|lang=|-\*-|!|pragma|isort|mypy|pyright)/i.test(c.text)) continue;
        for (const w of proseWords(c.text, 3)) out.push({ ...w, start: c.start + 1 + w.start, kind: 'comment' });
    }
    for (const s of strings) {
        if (s.prefix.includes('b') || injected.has(s.bodyStart)) continue;
        let body = text.slice(s.bodyStart, s.bodyEnd);
        if (s.prefix.includes('f')) body = body.replace(/\{[^}]*\}/g, (h) => ' '.repeat(h.length));
        body = body.replace(/\\[nrt'"\\]/g, '  ');
        // a string is prose when it has more than one word: keys, paths and names are left alone
        if (!/\S+\s+\S+/.test(body.trim()) && !s.triple) continue;
        for (const w of proseWords(body, 3)) out.push({ ...w, start: s.bodyStart + w.start, kind: 'string' });
    }
    out.push(...definedNames(text, strings, comments));
    return out.map((w) => ({ ...w, end: w.start + w.word.length })).sort((a, b) => a.start - b.start);
}

/** The words of a Markdown or plain text, without code, links' addresses and inline code. */
export function textWords(text) {
    const out = [];
    let fence = false, offset = 0;
    for (const line of text.split('\n')) {
        if (/^\s*(```|~~~)/.test(line)) fence = !fence;
        else if (!fence && !/^( {4}|\t)/.test(line) || (!fence && /^\s*[-*+]/.test(line))) {
            const clean = line.replace(/`[^`]*`/g, (s) => ' '.repeat(s.length)).replace(/\]\([^)]*\)/g, (s) => ' '.repeat(s.length)).replace(/https?:\/\/\S+/g, (s) => ' '.repeat(s.length));
            for (const w of proseWords(clean, 3)) out.push({ ...w, start: offset + w.start, kind: 'text' });
        }
        offset += line.length + 1;
    }
    return out.map((w) => ({ ...w, end: w.start + w.word.length }));
}

/** The kind of file whose words are checked, by its name: 'python', 'text', or null. */
export function spellKind(rel) {
    const name = rel.toLowerCase();
    if (/\.pyi?$/.test(name)) return 'python';
    if (/\.(md|markdown|rst|txt)$/.test(name)) return 'text';
    return null;
}

/** A replacement written like the word it replaces: `Recieve` becomes `Receive`, `RECIEVE` stays shouting. */
export function likeOriginal(original, replacement) {
    if (/^[A-Z][A-Z]+$/.test(original)) return replacement.toUpperCase();
    if (/^[A-Z]/.test(original)) return replacement[0].toUpperCase() + replacement.slice(1);
    return replacement;
}

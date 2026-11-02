import { test } from 'node:test';
import assert from 'node:assert/strict';
import { languageIdFor, languageOfShebang } from '../src/file-language.js';

// a few of Monaco's languages, as monaco.languages.getLanguages() gives them
const MONACO = [
    { id: 'javascript', extensions: ['.js', '.jsx', '.mjs', '.cjs'] },
    { id: 'typescript', extensions: ['.ts', '.tsx'] },
    { id: 'handlebars', extensions: ['.handlebars', '.hbs'] },
    { id: 'pug', extensions: ['.jade', '.pug'] },
    { id: 'twig', extensions: ['.twig'] },
    { id: 'razor', extensions: ['.cshtml'] },
    { id: 'json', extensions: ['.json'] },
    { id: 'ruby', extensions: ['.rb'], filenames: ['rakefile', 'Gemfile'] },
    { id: 'dockerfile', extensions: ['.dockerfile'], filenames: ['Dockerfile'] },
    { id: 'ini', extensions: ['.ini', '.properties'] },
    { id: 'xml', extensions: ['.xml', '.svg'] },
    { id: 'kotlin', extensions: ['.kt'] },
];
const of = (rel, first) => languageIdFor(rel, MONACO, first);

test("each of Express's view engines has a language", () => {
    assert.equal(of('views/index.pug'), 'pug');
    assert.equal(of('views/index.ejs'), 'ejs');
    assert.equal(of('views/index.hbs'), 'handlebars');
    assert.equal(of('views/index.hjs'), 'handlebars');
    assert.equal(of('views/index.twig'), 'twig');
    assert.equal(of('views/index.vash'), 'razor');
    assert.equal(of('views/layout.njk'), 'twig');
    assert.equal(of('views/x.mustache'), 'handlebars');
});

test("the other kinds' sources: components, TypeScript, React Native's native files", () => {
    assert.equal(of('src/App.vue'), 'html');
    assert.equal(of('src/App.svelte'), 'html');
    assert.equal(of('src/main.tsx'), 'typescript');
    assert.equal(of('eslint.config.mjs'), 'javascript');
    assert.equal(of('site.webmanifest'), 'json');
    assert.equal(of('android/build.gradle'), 'kotlin');
    assert.equal(of('android/gradle.properties'), 'ini');
    assert.equal(of('ios/App/Info.plist'), 'xml');
    assert.equal(of('index.html'), 'django-html', 'templates as before');
});

test('files known by their name, and a script by its #! line', () => {
    assert.equal(of('Gemfile'), 'ruby');
    assert.equal(of('ios/Podfile'), 'ruby');
    assert.equal(of('Dockerfile'), 'dockerfile');
    assert.equal(of('.editorconfig'), 'ini');
    assert.equal(of('.prettierrc'), 'json');
    assert.equal(of('.env.local'), 'ini');
    assert.equal(of('bin/www', '#!/usr/bin/env node'), 'javascript');
    assert.equal(of('bin/run', '#!/usr/bin/python3'), 'python');
    assert.equal(of('bin/run', 'just text'), 'plaintext');
    assert.equal(of('notes.unknown'), 'plaintext');
    assert.equal(languageOfShebang('#!/usr/bin/env -S node --no-warnings'), 'javascript');
    assert.equal(languageOfShebang('#!/bin/bash'), 'shell');
});

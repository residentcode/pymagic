import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { gherkinTokenizer, dialectRules, stateOf } from '../src/gherkin-model.js';

const languages = JSON.parse(readFileSync(new URL('../../src/PyMagic.Server/Gherkin/gherkin-languages.json', import.meta.url), 'utf8'));

/** The keyword a language's rules colour at the start of the line, or null. */
function keywordOf(code, line) {
    for (const [re, action] of dialectRules(languages[code])) {
        if (!Array.isArray(action) || !action.includes('keyword')) continue;
        const m = re.exec(line);
        if (m) return m[2];
    }
    return null;
}

test('every language has its state, and each rule is a valid pattern', () => {
    const t = gherkinTokenizer(languages);
    assert.ok(Object.keys(languages).length >= 70);
    for (const code of Object.keys(languages)) assert.ok(Array.isArray(t.tokenizer[stateOf(code)]), code);
    assert.ok(t.tokenizer.root && t.tokenizer.docString);
});

test('English keywords, as before', () => {
    assert.equal(keywordOf('en', '  Scenario Outline: many'), 'Scenario Outline');
    assert.equal(keywordOf('en', '  Scenario: one'), 'Scenario');
    assert.equal(keywordOf('en', '    Given a shop'), 'Given');
    assert.equal(keywordOf('en', '    * a shop'), '*');
    assert.equal(keywordOf('en', '    Givenness is not a keyword'), null);
    assert.equal(keywordOf('en', '    Angenommen x'), null);
});

test('German: the longest keyword, block keywords with their colon', () => {
    assert.equal(keywordOf('de', '    Gegeben seien 3 Äpfel'), 'Gegeben seien');
    assert.equal(keywordOf('de', '    Angenommen ein Laden'), 'Angenommen');
    assert.equal(keywordOf('de', '  Szenariogrundriss: viele'), 'Szenariogrundriss');
    assert.equal(keywordOf('de', '    Beispiele:'), 'Beispiele');
    assert.equal(keywordOf('de', 'Funktionalität: Laden'), 'Funktionalität');
});

test('Chinese keywords are written against the text', () => {
    assert.equal(keywordOf('zh-CN', '    假如有一个商店'), '假如');
});

test('the language comment switches to that language, any other first line to English', () => {
    const [language, comment, other] = gherkinTokenizer(languages).tokenizer.root;
    const m = language[0].exec('# language: zh-CN');
    assert.equal(m[1], 'zh-CN');
    assert.equal(language[1].switchTo, '@lang_$1');
    assert.equal(language[0].exec('# language: xx'), null);
    assert.ok(comment[0].test('# just a comment'));
    assert.ok(other[0].test('Feature: F'));
    assert.equal(other[1].switchTo, '@lang_en');
    assert.ok(!other[0].test('   '));
});

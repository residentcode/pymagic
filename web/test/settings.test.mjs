import test from 'node:test';
import assert from 'node:assert/strict';
import { DEFAULTS, merged, editorOptions, withoutCallParens } from '../src/settings.js';

test('what is stored is laid over the defaults, and a value of the wrong kind is dropped', () => {
    const s = merged({ theme: 'light', fontSize: '20', minimap: true, unknown: 1, wordWrap: 7 });
    assert.equal(s.theme, 'light');
    assert.equal(s.minimap, true);
    assert.equal(s.fontSize, DEFAULTS.fontSize);      // a string is not a size
    assert.equal(s.wordWrap, DEFAULTS.wordWrap);       // nor is a number a wrap mode
    assert.ok(!('unknown' in s));
});

test('sizes stay within what makes sense', () => {
    assert.equal(merged({ fontSize: 3 }).fontSize, 8);
    assert.equal(merged({ fontSize: 99 }).fontSize, 40);
    assert.equal(merged({ lineHeight: 500 }).lineHeight, 80);
    assert.equal(merged({ ruler: -4 }).ruler, 0);
    assert.equal(merged({ fontSize: 13.6 }).fontSize, 14);
});

test('nothing stored gives the defaults', () => {
    assert.deepEqual(merged(undefined), DEFAULTS);
    assert.deepEqual(merged({}), DEFAULTS);
});

test('the settings become the options of the editor', () => {
    const o = editorOptions({ ...DEFAULTS, minimap: true, ruler: 100, wordWrap: 'on', ligatures: true });
    assert.deepEqual(o.minimap, { enabled: true });
    assert.deepEqual(o.rulers, [100]);
    assert.equal(o.wordWrap, 'on');
    assert.equal(o.fontLigatures, true);
    assert.deepEqual(editorOptions(DEFAULTS).rulers, []);
});

test('completing a function writes its parentheses unless the setting says not to', () => {
    const on = merged({});
    const off = merged({ callParens: false });
    assert.equal(withoutCallParens('area($0)', on), 'area($0)');
    assert.equal(withoutCallParens('area($0)', off), 'area');
    assert.equal(withoutCallParens('close()', off), 'close');
    // other snippets (a postfix template, a live template) are kept
    assert.equal(withoutCallParens('for ${1:x} in items:', off), 'for ${1:x} in items:');
});

test('code is coloured as VS Code colours it unless PyMagic\'s own palette is chosen, in the dark and in the light', async () => {
    const { themeName, VSCODE_DARK_RULES, VSCODE_LIGHT_RULES } = await import('../src/theme.js');
    assert.equal(DEFAULTS.codeColors, 'vscode');
    assert.equal(DEFAULTS.bracketColors, true);
    assert.equal(themeName('vscode', 'dark'), 'pymagic-vscode');
    assert.equal(themeName('vscode', 'light'), 'pymagic-vscode-light');
    assert.equal(themeName('pymagic', 'dark'), 'pymagic');
    assert.equal(themeName('pymagic', 'light'), 'pymagic-light');
    assert.equal(themeName(undefined, 'dark'), 'pymagic-vscode');     // an older settings file
    assert.equal(themeName('something else', 'light'), 'pymagic-vscode-light');
    // VS Code's Dark+ and Light+ colours of the main kinds of Python tokens
    const of = (rules, token) => rules.find((r) => r.token === token)?.foreground;
    assert.deepEqual(['keyword', 'keyword.control', 'string', 'number', 'comment', 'class', 'function', 'variable', 'constant'].map((t) => of(VSCODE_DARK_RULES, t)),
        ['569cd6', 'c586c0', 'ce9178', 'b5cea8', '6a9955', '4ec9b0', 'dcdcaa', '9cdcfe', '569cd6']);
    assert.deepEqual(['keyword', 'keyword.control', 'string', 'number', 'comment', 'class', 'function', 'variable', 'constant'].map((t) => of(VSCODE_LIGHT_RULES, t)),
        ['0000ff', 'af00db', 'a31515', '098658', '008000', '267f99', '795e26', '001080', '0000ff']);
    // every token PyMagic's own palette colours has a VS Code colour too
    const src = (await import('node:fs')).readFileSync(new URL('../src/theme.js', import.meta.url), 'utf8');
    const own = [...src.slice(src.indexOf('export function defineTheme')).matchAll(/token: '([^']*)'/g)].map((m) => m[1]);
    for (const t of new Set(own)) assert.ok(of(VSCODE_DARK_RULES, t) && of(VSCODE_LIGHT_RULES, t), t);
});

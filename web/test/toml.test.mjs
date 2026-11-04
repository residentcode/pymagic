import { test } from 'node:test';
import assert from 'node:assert/strict';
import { tomlTokenizer } from '../src/toml-model.js';

/** Runs the Monarch rules as Monaco does (each rule tried at the position, in order; next/switchTo/@pop/@rematch): [[text, token], ...] per line. */
function tokenize(text) {
    const rules = tomlTokenizer().tokenizer;
    const stack = ['root'];
    return text.split('\n').map((line) => {
        line += '\n';   // includeLF
        const out = [];
        let pos = 0, guard = 0;
        while (pos < line.length) {
            assert.ok(guard++ < 1000, 'no progress in ' + line);
            const state = stack[stack.length - 1];
            let matched = false;
            for (const [re, action] of rules[state]) {
                const sticky = new RegExp(re.source.replace(/\$$/, '(?![\\s\\S])'), 'y');
                sticky.lastIndex = pos;
                const m = sticky.exec(line);
                if (!m) continue;
                matched = true;
                if (Array.isArray(action)) {
                    m.slice(1).forEach((part, i) => { if (part) out.push([part, action[i]]); });
                } else {
                    const token = typeof action === 'string' ? action : action.token;
                    if (token !== '@rematch' && m[0].trim()) out.push([m[0].replace(/\n$/, ''), token]);
                    if (action.next === '@pop') stack.pop();
                    else if (action.next) stack.push(action.next.slice(1));
                    if (action.switchTo) stack[stack.length - 1] = action.switchTo.slice(1);
                    if (token === '@rematch') break;
                }
                pos += m[0].length;
                break;
            }
            assert.ok(matched, `nothing matches at ${pos} of ${line} in ${state}`);
        }
        return out;
    });
}

const tokenOf = (lines, part) => lines.flat().find(([t]) => t === part)?.[1];

test('a pyproject.toml as in the picture: headers, keys, strings, arrays, an inline table', () => {
    const lines = tokenize([
        '[project]',
        'name = "django-tailwind"',
        'readme = "README.md"',
        'authors = [',
        '    { name = "residentcode", email = "a@b.c" }',
        ']',
        'requires-python = ">=3.13"',
        'dependencies = []',
        '',
        '[project.scripts]',
        'django-tailwind = "django_tailwind:main"',
        '[[tool.uv.index]]',
        "url = 'https://x' # a comment",
    ].join('\n'));
    assert.deepEqual(lines[0], [['[', 'delimiter.bracket'], ['project', 'type'], [']', 'delimiter.bracket']]);
    assert.deepEqual(lines[1], [['name', 'variable'], ['=', 'delimiter'], ['"django-tailwind"', 'string']]);
    assert.equal(tokenOf(lines.slice(4, 5), 'email'), 'variable');
    assert.equal(tokenOf(lines.slice(4, 5), '"a@b.c"'), 'string');
    assert.equal(tokenOf(lines, 'requires-python'), 'variable');
    assert.equal(tokenOf(lines, 'project.scripts'), 'type');
    assert.equal(tokenOf(lines, 'django-tailwind'), 'variable');
    assert.equal(tokenOf(lines, 'tool.uv.index'), 'type');
    assert.equal(tokenOf(lines, "'https://x'"), 'string');
    assert.equal(tokenOf(lines, '# a comment'), 'comment');
});

test('numbers, dates, booleans and a dotted key', () => {
    const lines = tokenize('n = 3\nf = 1.5e3\nh = 0xff\nd = 1979-05-27T07:32:00Z\nok = true\nno = inf\na.b."c" = false');
    assert.equal(tokenOf(lines, '3'), 'number');
    assert.equal(tokenOf(lines, '1.5e3'), 'number.float');
    assert.equal(tokenOf(lines, '0xff'), 'number.hex');
    assert.equal(tokenOf(lines, '1979-05-27T07:32:00Z'), 'number');
    assert.equal(tokenOf(lines, 'true'), 'keyword');
    assert.equal(tokenOf(lines, 'inf'), 'number.float');
    assert.equal(tokenOf(lines, 'a.b."c"'), 'variable');
});

test('a multi-line string and a multi-line array keep their state to their end', () => {
    const lines = tokenize('s = """\nline [x] = 1\n"""\nlist = [\n  "a", # one\n  [1, 2],\n]\n[next]');
    assert.deepEqual(lines[1], [['line [x] = 1', 'string']]);
    assert.equal(tokenOf(lines.slice(4, 5), '"a"'), 'string');
    assert.equal(tokenOf(lines.slice(4, 5), '# one'), 'comment');
    assert.equal(tokenOf(lines.slice(5, 6), '1'), 'number');
    assert.deepEqual(lines[7], [['[', 'delimiter.bracket'], ['next', 'type'], [']', 'delimiter.bracket']]);
});

test('a key without a value does not swallow the next table', () => {
    const lines = tokenize('broken =\n[tool]');
    assert.equal(tokenOf(lines.slice(1), 'tool'), 'type');
});

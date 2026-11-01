// Checks of web/src/schema-model.js: `node web/test/schema.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/schema-model.js', import.meta.url), 'utf8');
const { validate, locate } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
const msgs = (data, schema) => validate(data, schema).map((p) => `${p.path.join('.')}: ${p.message}`);

const schema = {
    type: 'object',
    required: ['name'],
    additionalProperties: false,
    properties: {
        name: { type: 'string', pattern: '^[a-z][a-z0-9-]*$' },
        version: { type: 'string' },
        private: { type: 'boolean' },
        scripts: { type: 'object', additionalProperties: { type: 'string' } },
        keywords: { type: 'array', items: { type: 'string' }, uniqueItems: true },
        license: { $ref: '#/definitions/license' },
        level: { type: 'integer', minimum: 1, maximum: 3 },
        mode: { enum: ['dev', 'prod'] },
    },
    definitions: { license: { anyOf: [{ type: 'string' }, { type: 'object', required: ['type'], properties: { type: { type: 'string' } } }] } },
};

test('a good document has no problems', () => {
    assert.deepEqual(msgs({ name: 'shop', version: '1.0', scripts: { test: 'pytest' }, keywords: ['a', 'b'], license: 'MIT', level: 2, mode: 'dev' }, schema), []);
});

test('types, required, additional properties, enums, ranges', () => {
    assert.deepEqual(msgs({}, schema), ['' + ': The property name is required']);
    assert.deepEqual(msgs({ name: 5 }, schema), ['name: Should be string, not integer']);
    assert.deepEqual(msgs({ name: 'Bad Name' }, schema), ['name: Should match ^[a-z][a-z0-9-]*$']);
    assert.deepEqual(msgs({ name: 'x', extra: 1 }, schema), ['extra: The property extra is not allowed here']);
    assert.deepEqual(msgs({ name: 'x', mode: 'qa' }, schema), ['mode: Should be one of "dev", "prod"']);
    assert.deepEqual(msgs({ name: 'x', level: 4 }, schema), ['level: Should be at most 3']);
    assert.deepEqual(msgs({ name: 'x', level: 1.5 }, schema), ['level: Should be integer, not number']);
});

test('nested values, arrays and additionalProperties as a schema', () => {
    assert.deepEqual(msgs({ name: 'x', scripts: { test: 1 } }, schema), ['scripts.test: Should be string, not integer']);
    assert.deepEqual(msgs({ name: 'x', keywords: ['a', 2] }, schema), ['keywords.1: Should be string, not integer']);
    assert.deepEqual(msgs({ name: 'x', keywords: ['a', 'a'] }, schema), ['keywords: The items should be different']);
});

test('$ref and anyOf: the branch of the right type says what is wrong', () => {
    assert.deepEqual(msgs({ name: 'x', license: { type: 5 } }, schema), ['license.type: Should be string, not integer']);
    assert.deepEqual(msgs({ name: 'x', license: 7 }, schema), ['license: Does not match any allowed form (string, object)']);
    assert.deepEqual(msgs({ name: 'x', license: {} }, schema), ['license: The property type is required']);
});

test('a $ref to another document and unknown keywords are accepted', () => {
    assert.deepEqual(msgs({ a: 1 }, { type: 'object', properties: { a: { $ref: 'https://example.org/x.json' } }, format: 'whatever' }), []);
});

test('where a problem is in the text', () => {
    const text = '[project]\nname = "x"\nversion = 1\n\n[tool.poetry]\nname = "y"\n';
    assert.deepEqual(locate(text, { path: ['project', 'version'] }), { line: 3, start: 1, end: 8 });
    const json = '{\n  "scripts": {\n    "test": 1\n  }\n}\n';
    assert.deepEqual(locate(json, { path: ['scripts', 'test'] }), { line: 3, start: 5, end: 9 });
    assert.deepEqual(locate('a = 1\n', { path: ['zzz'] }), { line: 1, start: 1, end: 2 });
});

test('of several forms, the one that fits is reported (a normal job, not a reusable one)', () => {
    const jobs = { oneOf: [
        { type: 'object', required: ['runs-on'], additionalProperties: false, properties: { 'runs-on': { type: 'string' }, steps: { type: 'array' } } },
        { type: 'object', required: ['uses'], additionalProperties: false, properties: { uses: { type: 'string' } } },
    ] };
    assert.deepEqual(msgs({ 'runs-on': 5, steps: [] }, jobs), ['runs-on: Should be string, not integer']);
    assert.deepEqual(msgs({ uses: 5 }, jobs), ['uses: Should be string, not integer']);
    // a pattern that is a ${{ expression }} gets a plain message
    assert.deepEqual(msgs('x', { type: 'string', pattern: '^\\$\\{\\{.*\\}\\}$' }), ['' + ': Should be a ${{ expression }}']);
});

test('unevaluatedProperties: what no part of the schema dealt with', () => {
    const service = {
        type: 'object',
        allOf: [{ properties: { image: { type: 'string' } } }, { patternProperties: { '^x-': {} } }],
        properties: { ports: { type: 'array' } },
        unevaluatedProperties: false,
    };
    assert.deepEqual(msgs({ image: 'a', ports: [], 'x-note': 1 }, service), []);
    assert.deepEqual(msgs({ image: 'a', restart_policy: 1 }, service), ['restart_policy: The property restart_policy is not allowed here']);
    // a $ref and a fitting branch count as dealing with their properties
    const withRef = { $ref: '#/definitions/base', anyOf: [{ properties: { b: {} } }, { properties: { c: {} } }], unevaluatedProperties: false, definitions: { base: { properties: { a: {} } } } };
    assert.deepEqual(msgs({ a: 1, b: 2 }, withRef), []);
    assert.deepEqual(msgs({ a: 1, z: 2 }, withRef), ['z: The property z is not allowed here']);
});

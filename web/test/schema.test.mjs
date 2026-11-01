// Checks of web/src/schema-model.js: `node web/test/schema.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/schema-model.js', import.meta.url), 'utf8');
const { validate, locate, schemaOf, openapiSchemaOf, looksKubernetes, kubernetesSchemaOf } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
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

test('Renovate, OpenAPI and Swagger files are told by their names, the schema of an API by its version', () => {
    for (const f of ['renovate.json', '.github/renovate.json', '.renovaterc', '.renovaterc.json']) assert.equal(schemaOf(f), 'renovate', f);
    for (const f of ['openapi.yaml', 'openapi.json', 'docs/swagger.yml', 'openapi.v1.yaml']) assert.equal(schemaOf(f), 'openapi', f);
    assert.equal(schemaOf('api/spec.yaml'), null);
    assert.equal(schemaOf('package.json'), 'package');
    assert.equal(openapiSchemaOf({ openapi: '3.0.3' }), 'openapi');
    assert.equal(openapiSchemaOf({ openapi: '3.1.0' }), null, '3.1 uses $dynamicRef: not checked');
    assert.equal(openapiSchemaOf({ swagger: '2.0' }), 'swagger');
    assert.equal(openapiSchemaOf({ swagger: 2 }), 'swagger');
    assert.equal(openapiSchemaOf({ info: {} }), null);
    assert.equal(openapiSchemaOf([1]), null);
});

test('a Kubernetes manifest has the schema of its kind', () => {
    assert.equal(kubernetesSchemaOf({ apiVersion: 'apps/v1', kind: 'Deployment' }), 'k8s-deployment-apps-v1');
    assert.equal(kubernetesSchemaOf({ apiVersion: 'v1', kind: 'Service' }), 'k8s-service-v1');
    assert.equal(kubernetesSchemaOf({ apiVersion: 'networking.k8s.io/v1', kind: 'Ingress' }), 'k8s-ingress-networking-v1');
    assert.equal(kubernetesSchemaOf({ apiVersion: 'autoscaling/v2beta1', kind: 'HorizontalPodAutoscaler' }), 'k8s-horizontalpodautoscaler-autoscaling-v2beta1');
    assert.equal(kubernetesSchemaOf({ apiVersion: 'apps/v1' }), null);
    assert.equal(kubernetesSchemaOf({ apiVersion: 5, kind: 'Pod' }), null);
    assert.equal(kubernetesSchemaOf({ apiVersion: 'v1', kind: '../x' }), null);
    assert.equal(kubernetesSchemaOf({ apiVersion: 'apps/latest', kind: 'Pod' }), null);
    assert.equal(kubernetesSchemaOf(null), null);
    assert.ok(looksKubernetes('apiVersion: v1\nkind: Pod\n'));
    assert.ok(looksKubernetes('---\napiVersion: v1\nkind: Pod\n---\napiVersion: v1\nkind: Service\n'));
    assert.ok(!looksKubernetes('kind: Pod\n'));
    assert.ok(!looksKubernetes('apiVersion: v1\nkind: {{ .Values.kind }}\n'), 'a Helm template is not YAML to check');
    assert.ok(!looksKubernetes('steps:\n  - apiVersion: v1\n    kind: x\n'));
});

test('a Kubernetes kind schema of the shape the standalone files have: nullable types, int-or-string, nested wrong values', () => {
    const deployment = {
        type: 'object',
        properties: {
            apiVersion: { type: ['string', 'null'] },
            kind: { type: ['string', 'null'], enum: ['Deployment'] },
            spec: {
                type: ['object', 'null'],
                properties: {
                    replicas: { type: ['integer', 'null'], format: 'int32' },
                    strategy: { type: ['object', 'null'], properties: { rollingUpdate: { type: ['object', 'null'], properties: { maxSurge: { oneOf: [{ type: 'string' }, { type: 'integer' }] } } } } },
                    template: { type: 'object', properties: { spec: { type: 'object', properties: { containers: { type: 'array', items: { type: 'object', required: ['name'], properties: { name: { type: 'string' }, image: { type: 'string' } } } } } } } },
                },
                required: ['selector', 'template'],
            },
        },
    };
    const ok = { apiVersion: 'apps/v1', kind: 'Deployment', spec: { replicas: 2, selector: {}, strategy: { rollingUpdate: { maxSurge: '25%' } }, template: { spec: { containers: [{ name: 'c', image: 'i' }] } } } };
    assert.deepEqual(msgs(ok, deployment), []);
    const bad = { apiVersion: 'apps/v1', kind: 'Deployment', spec: { replicas: 'three', template: { spec: { containers: [{ image: 5 }] } } } };
    assert.deepEqual(msgs(bad, deployment), [
        'spec: The property selector is required',
        'spec.replicas: Should be integer or null, not string',
        'spec.template.spec.containers.0: The property name is required',
        'spec.template.spec.containers.0.image: Should be string, not integer',
    ]);
});

test('an OpenAPI 3.0 shaped schema: required info.version, responses and a wrong type', () => {
    const oas = {
        type: 'object', required: ['openapi', 'info', 'paths'],
        properties: {
            openapi: { type: 'string', pattern: '^3[.]0[.]' },
            info: { $ref: '#/definitions/Info' },
            paths: { type: 'object', patternProperties: { '^/': { $ref: '#/definitions/PathItem' } }, additionalProperties: false },
        },
        definitions: {
            Info: { type: 'object', required: ['title', 'version'], properties: { title: { type: 'string' }, version: { type: 'string' } } },
            PathItem: { type: 'object', patternProperties: { '^(get|put|post|delete)$': { $ref: '#/definitions/Operation' } } },
            Operation: { type: 'object', required: ['responses'], properties: { responses: { type: 'object', minProperties: 1 } } },
        },
    };
    assert.deepEqual(msgs({ openapi: '3.0.3', info: { title: 't', version: '1' }, paths: { '/a': { get: { responses: { 200: {} } } } } }, oas), []);
    assert.deepEqual(msgs({ openapi: '3.0.3', info: { title: 5 }, paths: { '/a': { get: {} }, bad: {} } }, oas), [
        'info: The property version is required',
        'info.title: Should be string, not integer',
        'paths./a.get: The property responses is required',
        'paths.bad: The property bad is not allowed here',
    ]);
});

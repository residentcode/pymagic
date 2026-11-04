// Checks of web/src/schema-model.js: `node web/test/schema.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/schema-model.js', import.meta.url), 'utf8');
const { validate, locate, schemaOf, openapiSchemaOf, looksKubernetes, kubernetesSchemaOf, crdSchemas, crdKeyOf, asyncapiSchemaOf, parseJsonc, frontMatterOf, globMatch, chooseSchema, declaredSchema, yamlModeline, externalRefs, normalId, storeSchemaOf } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
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
    assert.equal(openapiSchemaOf({ openapi: '3.1.0' }), 'openapi31');
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

const bundled = (name) => JSON.parse(readFileSync(new URL(`../../src/PyMagic.Server/Schemas/${name}.json`, import.meta.url), 'utf8'));

test('not, if/then/else, $anchor and $dynamicRef, dependentSchemas, propertyNames and the counts of properties', () => {
    const s = {
        $defs: { name: { $anchor: 'nm', type: 'string', minLength: 2 }, any: { $dynamicAnchor: 'meta', type: ['object', 'boolean'] } },
        type: 'object',
        properties: { n: { $ref: '#nm' }, schema: { $dynamicRef: '#meta' }, kind: { type: 'string' } },
        if: { properties: { kind: { const: 'job' } }, required: ['kind'] }, then: { required: ['cmd'] }, else: { not: { required: ['cmd'] } },
        dependentSchemas: { cert: { required: ['key'] } },
        propertyNames: { pattern: '^[a-z]+$' },
        maxProperties: 4,
    };
    assert.deepEqual(msgs({ n: 'ab', kind: 'job', cmd: 'x' }, s), []);
    assert.deepEqual(msgs({ n: 'a' }, s), ['n: Should have at least 2 characters']);                 // through an $anchor
    assert.deepEqual(msgs({ schema: 3 }, s), ['schema: Should be object or boolean, not integer']);     // through a $dynamicRef
    assert.deepEqual(msgs({ kind: 'job' }, s), [': The property cmd is required']);                     // if/then
    assert.deepEqual(msgs({ kind: 'web', cmd: 'x' }, s), ['cmd: The property cmd is not allowed here']);   // else: not
    assert.deepEqual(msgs({ cert: 'c' }, s), [': The property key is required']);                       // dependentSchemas
    assert.deepEqual(msgs({ Bad: 1 }, s), ['Bad: The name Bad: Should match ^[a-z]+$']);
    assert.deepEqual(msgs({ a: 1, b: 2, c: 3, d: 4, e: 5 }, s), [': Should have at most 4 properties']);
});

test('OpenAPI 3.1 is checked against its own schema (its $dynamicRef followed)', () => {
    const s = bundled('openapi31');
    const ok = { openapi: '3.1.0', info: { title: 'Shop', version: '1' }, paths: { '/items': { get: { responses: { 200: { description: 'ok' } } } } } };
    assert.deepEqual(msgs(ok, s), []);
    // the version is required, a path's key starts with /, and a response needs its description
    const bad = msgs({ openapi: '3.1.0', info: { title: 'Shop' }, paths: { items: {}, '/a': { get: { responses: { 200: {} } } } } }, s);
    assert.ok(bad.some((m) => /info: The property version is required/.test(m)), bad.join(' | '));
    assert.ok(bad.some((m) => /paths\.items/.test(m)), bad.join(' | '));
    assert.ok(bad.some((m) => /description is required/.test(m)), bad.join(' | '));
});

test('Kustomization files and Renovate\'s if/then and global-only options', () => {
    for (const f of ['kustomization.yaml', 'overlays/prod/kustomization.yml', 'base/Kustomization']) assert.equal(schemaOf(f), 'kustomization', f);
    const k = bundled('kustomization');
    assert.deepEqual(msgs({ apiVersion: 'kustomize.config.k8s.io/v1beta1', kind: 'Kustomization', resources: ['a.yaml'], namePrefix: 'dev-' }, k), []);
    assert.ok(msgs({ resources: 'a.yaml' }, k).some((m) => /^resources: Should be array/.test(m)), msgs({ resources: 'a.yaml' }, k).join(' | '));
    const r = bundled('renovate');
    // a jsonata custom manager needs its fileFormat (if/then)
    const manager = { customType: 'jsonata', managerFilePatterns: ['/x/'], matchStrings: ['a'] };
    assert.ok(msgs({ customManagers: [manager] }, r).some((m) => /fileFormat is required/.test(m)), msgs({ customManagers: [manager] }, r).join(' | '));
    // an option of the global config is not allowed in a repository's file (not of required)
    assert.deepEqual(msgs({ allowedHeaders: ['X-*'] }, r), ['allowedHeaders: The property allowedHeaders is not allowed here']);
    assert.deepEqual(msgs({ extends: ['config:recommended'] }, r), []);
});

test('a custom resource is checked against the CustomResourceDefinition that declares its kind', () => {
    const crd = {
        apiVersion: 'apiextensions.k8s.io/v1', kind: 'CustomResourceDefinition',
        spec: { group: 'shop.example.com', names: { kind: 'Shelf' }, versions: [{ name: 'v1', schema: { openAPIV3Schema: { type: 'object', properties: { spec: { type: 'object', required: ['size'], properties: { size: { type: 'integer', minimum: 1 } } } } } } }] },
    };
    const found = crdSchemas([crd, { kind: 'Deployment' }, null]);
    assert.deepEqual([...found.keys()], ['shop.example.com/v1:Shelf']);
    const doc = { apiVersion: 'shop.example.com/v1', kind: 'Shelf', spec: { size: 0 } };
    assert.equal(crdKeyOf(doc), 'shop.example.com/v1:Shelf');
    assert.deepEqual(msgs(doc, found.get(crdKeyOf(doc))), ['spec.size: Should be at least 1']);
    assert.equal(crdKeyOf({ apiVersion: 'v1', kind: 'Pod' }), null);   // the core group has no definitions of its own
});

test('YAML against the other bundled schemas: a small good file passes, a broken one is marked', () => {
    const cases = [
        ['compose', { services: { web: { image: 'nginx', ports: ['80:80'] } } }, { services: { web: { imagee: 'nginx' } } }],
        ['gitlab', { test: { script: ['pytest'] } }, { test: { script: ['pytest'], when: 'later' } }],
        ['precommit', { repos: [{ repo: 'local', hooks: [{ id: 'a', name: 'a', entry: 'a', language: 'system' }] }] }, { repos: 'all' }],
        ['mkdocs', { site_name: 'Docs', nav: [{ Home: 'index.md' }] }, { site_name: 3 }],
        ['dependabot', { version: 2, updates: [{ 'package-ecosystem': 'pip', directory: '/', schedule: { interval: 'weekly' } }] }, { version: 2, updates: [{ 'package-ecosystem': 'pip', directory: '/', schedule: { interval: 'hourly' } }] }],
        ['codecov', { coverage: { precision: 2 } }, { coverage: { precision: 'two' } }],
    ];
    for (const [name, good, broken] of cases) {
        const s = bundled(name);
        assert.deepEqual(msgs(good, s), [], name + ' (good)');
        assert.ok(msgs(broken, s).length > 0, name + ' (broken) is not marked');
    }
});


// ---------------------------------------------------------------- the schemas PyCharm lists, mappings and "$schema"

test('the files of the schemas PyCharm lists are known by their names', () => {
    const cases = {
        'asyncapi.yaml': 'asyncapi', 'docs/my-asyncapi.json': 'asyncapi', '.devcontainer/devcontainer.json': 'devcontainer', '.devcontainer/py/devcontainer.json': 'devcontainer',
        '.devcontainer.json': 'devcontainer', 'action.yml': 'github-action', '.github/actions/setup/action.yaml': 'github-action', '.babelrc': 'babelrc', 'babel.config.json': 'babelrc',
        '.eslintrc': 'eslintrc', '.eslintrc.yml': 'eslintrc', 'jest.config.json': 'jest', 'jsconfig.json': 'jsconfig', 'tsconfig.json': 'tsconfig', 'tsconfig.build.json': 'tsconfig',
        '.prettierrc': 'prettierrc', '.stylelintrc.json': 'stylelintrc', 'tslint.json': 'tslint', 'tsd.json': 'tsd', 'typings.json': 'typings', 'mermaid.config.json': 'mermaid',
        'http-client.env.json': 'http-client-env', 'http-client.private.env.json': 'http-client-private-env', 'README.md': 'markdown-frontmatter', 'other.json': null,
    };
    for (const [rel, want] of Object.entries(cases)) assert.equal(schemaOf(rel), want, rel);
    assert.equal(asyncapiSchemaOf({ asyncapi: '2.6.0' }), 'asyncapi2');
    assert.equal(asyncapiSchemaOf({ asyncapi: '3.0.0' }), 'asyncapi3');
    assert.equal(asyncapiSchemaOf({ openapi: '3.0.0' }), null);
});

test('JSON with comments and trailing commas is read (tsconfig); front matter is found at the top of Markdown only', () => {
    assert.deepEqual(parseJsonc('{\n  // the target\n  "a": "http://x//y", /* b */ "b": [1, 2,],\n}'), { a: 'http://x//y', b: [1, 2] });
    assert.throws(() => parseJsonc('{ "a": }'));
    assert.deepEqual(frontMatterOf('---\ntitle: T\n---\n# Head\n'), { text: 'title: T', line: 1 });
    assert.equal(frontMatterOf('# Head\n---\ntitle: T\n---\n'), null);
});

test('a mapping\'s pattern: a path from the root, * in a name, ** any folders, a bare name in any folder', () => {
    const cases = [['config/*.json', 'config/a.json', true], ['config/*.json', 'config/x/a.json', false], ['**/*.json', 'a/b/c.json', true], ['**/*.json', 'c.json', true],
        ['*.json', 'deep/a.json', true], ['a.json', 'x/a.json', true], ['a.json', 'xa.json', false], ['src/**/conf.yaml', 'src/conf.yaml', true],
        ['src/**/conf.yaml', 'src/a/b/conf.yaml', true], ['src/**', 'src/a/b.json', true], ['src/**', 'other/a.json', false], ['conf?.json', 'conf1.json', true]];
    for (const [pattern, rel, want] of cases) assert.equal(globMatch(pattern, rel), want, `${pattern} ${rel}`);
});

test('which schema a file gets: ignored, then a mapping, then its own "$schema", then its name', () => {
    const project = { mappings: [{ name: 'Mine', schema: 'file:schemas/app.json', patterns: ['config/*.json'] }, { name: 'TS', schema: 'tsconfig', patterns: ['base.json'] }], ignored: ['tsconfig.json'] };
    assert.deepEqual(chooseSchema('tsconfig.json', project, null), { ignored: true, why: 'ignored' });
    assert.deepEqual(chooseSchema('config/a.json', project, { name: 'jest' }), { file: 'schemas/app.json', why: 'mapping', mapping: 'Mine' });
    assert.deepEqual(chooseSchema('pkg/base.json', project, null), { name: 'tsconfig', why: 'mapping', mapping: 'TS' });
    assert.deepEqual(chooseSchema('package.json', project, { name: 'jest' }), { name: 'jest', why: 'declared' });
    assert.deepEqual(chooseSchema('package.json', project, null), { name: 'package', why: 'name' });
    assert.equal(chooseSchema('data.json', project, null), null);
});

test('a file\'s "$schema": a built-in schema by its $id, a .json path relative to the file, nothing for an address PyMagic has not got', () => {
    const ids = { 'https://json-schema.org/draft-07/schema': 'jsonschema-7', 'https://json.schemastore.org/tsconfig': 'tsconfig' };
    assert.deepEqual(declaredSchema('http://json-schema.org/draft-07/schema#', ids, 'a.json'), { name: 'jsonschema-7' });
    assert.deepEqual(declaredSchema('https://json.schemastore.org/tsconfig', ids, 'x/tsconfig.base.json'), { name: 'tsconfig' });
    assert.deepEqual(declaredSchema('./schemas/my.json', ids, 'conf/app.json'), { file: 'conf/schemas/my.json' });
    assert.deepEqual(declaredSchema('../s.json', ids, 'conf/app.json'), { file: 's.json' });
    assert.equal(declaredSchema('../../s.json', ids, 'conf/app.json'), null);            // outside the project
    assert.equal(declaredSchema('https://example.com/other.json', ids, 'a.json'), null);   // not downloaded
    assert.equal(yamlModeline('# yaml-language-server: $schema=https://json.schemastore.org/tsconfig\na: 1\n'), 'https://json.schemastore.org/tsconfig');
    assert.equal(yamlModeline('a: 1\n'), null);
    assert.equal(normalId('http://json-schema.org/draft-07/schema#'), 'https://json-schema.org/draft-07/schema');
});

test('$refs: to a definition under its own $id (AsyncAPI), to another document by its id, and draft-06/07 "$id": "#name" anchors', () => {
    // AsyncAPI keeps each definition under an absolute $id, and $refs it by that address
    const asyncapi = {
        $id: 'http://asyncapi.com/definitions/3.0.0/asyncapi.json', type: 'object', properties: { info: { $ref: 'http://asyncapi.com/definitions/3.0.0/info.json' } },
        definitions: { 'http://asyncapi.com/definitions/3.0.0/info.json': { $id: 'http://asyncapi.com/definitions/3.0.0/info.json', type: 'object', required: ['title'], properties: { title: { type: 'string' }, contact: { $ref: '#/definitions/contact' } }, definitions: { contact: { type: 'object', properties: { email: { type: 'string' } } } } } },
    };
    assert.deepEqual(msgs({ info: {} }, asyncapi), ['info: The property title is required']);
    assert.deepEqual(msgs({ info: { title: 'T', contact: { email: 3 } } }, asyncapi), ['info.contact.email: Should be string, not integer']);
    // another document: the registry gives it by its normalized id; without one the $ref is accepted
    const meta = { $id: 'https://example.org/meta/core', type: 'object', properties: { $id: { type: 'string' } } };
    const root = { $id: 'https://example.org/schema', allOf: [{ $ref: 'meta/core' }] };
    assert.deepEqual([...externalRefs(root)], ['https://example.org/meta/core']);
    assert.deepEqual(validate({ $id: 3 }, root, 200, (id) => (id === 'https://example.org/meta/core' ? meta : null)).map((p) => p.message), ['Should be string, not integer']);
    assert.deepEqual(validate({ $id: 3 }, root).map((p) => p.message), []);
    // "$id": "#positive" names an anchor
    const anchored = { definitions: { p: { $id: '#positive', type: 'integer', minimum: 1 } }, properties: { n: { $ref: '#positive' } } };
    assert.deepEqual(msgs({ n: 0 }, anchored), ['n: Should be at least 1']);
});

test('draft-04\'s boolean exclusive bounds, the later numeric ones, and $recursiveRef', () => {
    assert.deepEqual(msgs(0, { minimum: 0, exclusiveMinimum: true }), [': Should be more than 0']);
    assert.deepEqual(msgs(0, { minimum: 0 }), []);
    assert.deepEqual(msgs(5, { maximum: 5, exclusiveMaximum: true }), [': Should be less than 5']);
    assert.deepEqual(msgs(5, { exclusiveMaximum: 5 }), [': Should be less than 5']);
    assert.deepEqual(msgs(1, { exclusiveMinimum: 0 }), []);
    // 2019-09: a subschema checked against the outermost schema
    const recursive = { $recursiveAnchor: true, type: 'object', properties: { child: { $recursiveRef: '#' } }, additionalProperties: false };
    assert.deepEqual(msgs({ child: { child: { x: 1 } } }, recursive), ['child.child.x: The property x is not allowed here']);
});


test('SchemaStore\'s addresses are one: json., www. or no host part, with .json or without, http or https', () => {
    for (const a of ['https://json.schemastore.org/tsconfig', 'https://json.schemastore.org/tsconfig.json', 'http://json.schemastore.org/tsconfig.json',
        'https://www.schemastore.org/tsconfig.json', 'https://schemastore.org/tsconfig', 'https://json.schemastore.org/tsconfig.json#'])
        assert.equal(normalId(a), 'https://json.schemastore.org/tsconfig', a);
    assert.equal(normalId('https://example.com/a.json'), 'https://example.com/a.json');   // other addresses keep their .json
    assert.deepEqual(declaredSchema('https://json.schemastore.org/tsconfig.json', { 'https://json.schemastore.org/tsconfig': 'tsconfig' }, 'a.json'), { name: 'tsconfig' });
});

test('SchemaStore\'s catalog picks a schema by its fileMatch when PyMagic\'s own names have none; ! leaves files out', () => {
    const store = [
        { name: 'store:nodemon', fileMatch: ['nodemon.json'] },
        { name: 'store:ruff', fileMatch: ['ruff.toml', '.ruff.toml'] },
        { name: 'store:helmfile', fileMatch: ['helmfile.d/**/*.yaml', '!helmfile.d/**/values*.yaml'] },
    ];
    assert.equal(storeSchemaOf('nodemon.json', store), 'store:nodemon');
    assert.equal(storeSchemaOf('web/nodemon.json', store), 'store:nodemon');
    assert.equal(storeSchemaOf('ruff.toml', store), 'store:ruff');
    assert.equal(storeSchemaOf('deploy/helmfile.d/apps/a.yaml', store), 'store:helmfile');
    assert.equal(storeSchemaOf('deploy/helmfile.d/apps/values-a.yaml', store), null);
    assert.equal(storeSchemaOf('data.json', store), null);
    // PyMagic's own name comes first; SchemaStore's after it; a mapping or "$schema" before both
    assert.deepEqual(chooseSchema('package.json', null, null, [{ name: 'store:package', fileMatch: ['package.json'] }]), { name: 'package', why: 'name' });
    assert.deepEqual(chooseSchema('nodemon.json', null, null, store), { name: 'store:nodemon', why: 'store' });
    assert.deepEqual(chooseSchema('nodemon.json', null, { name: 'jest' }, store), { name: 'jest', why: 'declared' });
});

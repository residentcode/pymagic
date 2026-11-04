import { test } from 'node:test';
import assert from 'node:assert/strict';
import { dependencyEntries, problems, isPackageJson, folderOf } from '../src/npm-model.js';

const PACKAGE = `{
  "name": "shop",
  "version": "1.0.0",
  "scripts": { "start": "node app.js" },
  "dependencies": {
    "express": "^4.18.2",
    "@types/node": "^20.0.0"
  },
  "devDependencies": {
    "jest": "29.7.0"
  },
  "peerDependencies": { "react": "*" },
  "config": { "dependencies": { "not": "one" } }
}
`;

test('the dependencies and their places: both sections, scoped names, not peers, not a key of the same name deeper down', () => {
    const entries = dependencyEntries(PACKAGE);
    assert.deepEqual(entries.map((e) => `${e.section}:${e.name}@${e.spec}`), ['dependencies:express@^4.18.2', 'dependencies:@types/node@^20.0.0', 'devDependencies:jest@29.7.0']);
    const express = entries[0];
    assert.equal(express.line, 6);
    assert.equal(PACKAGE.split('\n')[5].slice(express.start - 1, express.end - 1), '"express"');
});

test('what node_modules has is not marked; the rest is, and without node_modules all of it', () => {
    const entries = dependencyEntries(PACKAGE);
    const some = problems(entries, { express: '4.18.2', '@types/node': '20.1.0' });
    assert.deepEqual(some.map((p) => p.message), ["Package 'jest' is not installed: npm install puts it in node_modules"]);
    assert.equal(problems(entries, null).length, 3);
    assert.ok(problems(entries, null)[0].message.includes('no node_modules'));
    assert.equal(problems(entries, { express: '', '@types/node': '', jest: '' }).length, 0);
});

test('a broken or empty text has no dependencies; escapes and comments are read past', () => {
    assert.deepEqual(dependencyEntries(''), []);
    assert.deepEqual(dependencyEntries('{"dependencies": {"a": '), []);
    const entries = dependencyEntries('{\n  // a comment\n  "description": "say \\"hi\\"",\n  "dependencies": { "left-pad": "1.3.0" }\n}');
    assert.deepEqual(entries.map((e) => e.name), ['left-pad']);
    assert.equal(entries[0].line, 4);
});

test("only a project's own package.json, and its folder", () => {
    assert.ok(isPackageJson('package.json') && isPackageJson('web/package.json'));
    assert.ok(!isPackageJson('node_modules/express/package.json') && !isPackageJson('package.json.bak'));
    assert.equal(folderOf('package.json'), '');
    assert.equal(folderOf('web/app/package.json'), 'web/app');
});

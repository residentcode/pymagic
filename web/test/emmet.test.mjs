import { test } from 'node:test';
import assert from 'node:assert/strict';
import { isJsxFile } from '../src/emmet-model.js';

test('JSX files: .jsx and .tsx always', () => {
    assert.ok(isJsxFile('/src/App.jsx'));
    assert.ok(isJsxFile('/src/App.TSX', ''));
});

test('a .js or .ts file is JSX when it is React or has JSX in it', () => {
    assert.ok(isJsxFile('/src/App.js', "import React from 'react';\n"));
    assert.ok(isJsxFile('/src/App.js', "import { View } from 'react-native';\n"));
    assert.ok(isJsxFile('/src/Card.ts', 'export const Card = () => <div className="card"></div>;\n'));
    assert.ok(isJsxFile('/src/a.mjs', 'const x = <Icon name="x" />;\n'));
    assert.ok(isJsxFile('/src/a.js', 'function A() {\n  return (\n    <main>'));
    assert.ok(isJsxFile('/src/a.js', "const React = require('react');\n"));
});

test('plain JavaScript, TypeScript and other files are not', () => {
    assert.ok(!isJsxFile('/app.js', "var express = require('express');\nif (a < b && c > d) {}\n"));
    assert.ok(!isJsxFile('/bin/www', ''));
    assert.ok(!isJsxFile('/src/util.ts', 'export function f<T>(x: Array<T>): T { return x[0]; }\n'));
    assert.ok(!isJsxFile('/index.html', '<div></div>'));
});

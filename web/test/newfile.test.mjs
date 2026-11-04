// Checks of web/src/newfile-model.js (the project tree's New submenu): `node --test web/test/newfile.test.mjs`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { NEW_KINDS, IGNORE_FILES, newFileName, newFileText } from '../src/newfile-model.js';

const kind = (label) => NEW_KINDS.find((k) => k !== '-' && k.label === label);

test('the entries are PyCharm\'s, in its order', () => {
    assert.deepEqual(NEW_KINDS.filter((k) => k !== '-').map((k) => k.label), ['Python File', 'Jupyter Notebook', 'File', 'Scratch File', 'Directory', 'Python Package',
        'JavaScript File', 'TypeScript File', 'HTML File', 'Stylesheet', 'Dockerfile', 'Dev Container Config…', 'Docker Compose File', 'HTTP Request', 'OpenAPI Specification',
        'EditorConfig File', 'Resource Bundle', '.ignore File', 'Data Source in Path']);
    assert.deepEqual(IGNORE_FILES.map(([name]) => name), ['.gitignore', '.dockerignore', '.npmignore', '.prettierignore', '.eslintignore']);
});

test('a typed name gets the kind\'s extension when it has none of its own', () => {
    assert.equal(newFileName(kind('Python File'), 'models'), 'models.py');
    assert.equal(newFileName(kind('Python File'), 'stubs.pyi'), 'stubs.pyi');
    assert.equal(newFileName(kind('Python File'), 'pkg/views'), 'pkg/views.py');
    assert.equal(newFileName(kind('Jupyter Notebook'), 'Analysis'), 'Analysis.ipynb');
    assert.equal(newFileName(kind('HTTP Request'), 'api'), 'api.http');
    assert.equal(newFileName(kind('File'), 'notes'), 'notes');
    assert.equal(newFileName(kind('Dockerfile'), 'Dockerfile'), 'Dockerfile');
});

test('the starting text: a notebook that Jupyter reads, the name in it where the kind has one', () => {
    const nb = JSON.parse(newFileText(kind('Jupyter Notebook'), 'Analysis.ipynb'));
    assert.equal(nb.nbformat, 4);
    assert.equal(nb.cells.length, 1);
    assert.match(newFileText(kind('OpenAPI Specification'), 'shop-api.yaml'), /title: shop-api/);
    assert.match(newFileText(kind('HTTP Request'), 'login.http'), /^### login\nGET /);
    assert.equal(JSON.parse(newFileText(kind('Dev Container Config…'), '.devcontainer/devcontainer.json')).image.startsWith('mcr.microsoft.com/devcontainers/python'), true);
    assert.equal(newFileText(kind('Python File'), 'a.py'), '');
});

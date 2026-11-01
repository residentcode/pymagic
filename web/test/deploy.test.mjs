// Checks of web/src/deploy-model.js: `node web/test/deploy.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { defaultAction, actionsFor, defaultPort, deleteQuestion, describeServer, mappingText, parseMappings, sizeText, parentOf } from '../src/deploy-model.js';

test('what a sync proposes by default', () => {
    assert.equal(defaultAction('local-only'), 'upload');
    assert.equal(defaultAction('local-newer'), 'upload');
    assert.equal(defaultAction('remote-only'), 'download');
    assert.equal(defaultAction('remote-newer'), 'download');
    assert.equal(defaultAction('same'), 'skip');
    assert.deepEqual(actionsFor('local-only').map((a) => a[0]), ['upload', 'skip']);
    assert.deepEqual(actionsFor('remote-newer').map((a) => a[0]), ['upload', 'download', 'skip']);
});

test('a server in words', () => {
    assert.equal(describeServer({ type: 'sftp', host: 'example.org', port: 22, user: 'me' }), 'SFTP me@example.org');
    assert.equal(describeServer({ type: 'sftp', host: 'example.org', port: 2222, user: 'me' }), 'SFTP me@example.org:2222');
    assert.equal(describeServer({ type: 'ftp', host: 'ftp.site.org', port: 21, user: '' }), 'FTP ftp.site.org');
    assert.equal(describeServer({ type: 'folder', root: 'D:/share' }), 'a folder D:/share');
    assert.equal(describeServer({ type: 'ftps', host: 'ftp.site.org', port: 21, user: 'me' }), 'FTPS me@ftp.site.org');
    assert.equal(describeServer({ type: 'ftps', host: 'ftp.site.org', port: 990, user: '' }), 'FTPS ftp.site.org:990');
    assert.equal(defaultPort('ftps'), 21);
    assert.equal(defaultPort('ftp'), 21);
    assert.equal(defaultPort('sftp'), 22);
});

test('the question before a delete on a server says what goes', () => {
    assert.equal(deleteQuestion({ name: 'a.py', isDir: false }, 'live', 'site/pkg'), 'Delete /site/pkg/a.py on live? This cannot be undone.');
    assert.equal(deleteQuestion({ name: 'pkg', isDir: true }, 'live', ''), 'Delete the folder /pkg on live, with everything in it? This cannot be undone.');
});

test('mappings as lines, and back', () => {
    assert.equal(mappingText({ local: 'web', remote: 'public' }), 'web → public');
    assert.equal(mappingText({ local: '', remote: '' }), '. → .');
    assert.deepEqual(parseMappings('web -> public\n\n./ = /app/\nsrc\n'), [{ local: 'web', remote: 'public' }, { local: '', remote: 'app' }, { local: 'src', remote: 'src' }]);
    assert.deepEqual(parseMappings(''), [{ local: '', remote: '' }]);
    assert.deepEqual(parseMappings('. → .'), [{ local: '', remote: '' }]);
});

test('sizes and parent folders', () => {
    assert.equal(sizeText(12), '12 B');
    assert.equal(sizeText(2048), '2.0 KB');
    assert.equal(sizeText(3 * 1024 * 1024), '3.0 MB');
    assert.equal(parentOf('a/b/c'), 'a/b');
    assert.equal(parentOf('a'), '');
    assert.equal(parentOf(''), '');
});

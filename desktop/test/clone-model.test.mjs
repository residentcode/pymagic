// Checks of desktop/clone-model.js: `node --test desktop/test/clone-model.test.mjs`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { cloneAddressOk, repositoryName } = require('../clone-model.js');

test('the addresses Get from VCS clones', () => {
    for (const ok of ['https://github.com/residentcode/pymagic.git', 'http://localhost:3000/me/x', 'ssh://git@host:22/me/x.git', 'git://host/x', 'git@github.com:owner/name.git', '  https://gitlab.com/a/b  '])
        assert.equal(cloneAddressOk(ok), true, ok);
    // never an option of git, a local path, or nothing
    for (const no of ['--upload-pack=calc', '-c core.x=y', 'C:\\repo', 'file:///C:/repo', 'https://host/a b', '', null])
        assert.equal(cloneAddressOk(no), false, String(no));
});

test('the folder a repository is suggested in is named after it', () => {
    assert.equal(repositoryName('https://github.com/residentcode/pymagic.git'), 'pymagic');
    assert.equal(repositoryName('git@github.com:owner/My.Repo.git'), 'My.Repo');
    assert.equal(repositoryName('https://gitlab.com/group/sub/project/'), 'project');
    assert.equal(repositoryName('https://host/x?ref=main'), 'x');
    assert.equal(repositoryName(''), '');
});

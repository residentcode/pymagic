import { test } from 'node:test';
import assert from 'node:assert/strict';
import { libraryPathOf, pathOfLibraryUri, libraryTitle } from '../src/libview-model.js';

test('a location outside the project is a library file, with its path; one in the project is not', () => {
    const root = 'file:///C:/work/shop/';
    assert.equal(libraryPathOf('file:///C:/Python313/Lib/json/__init__.py', root), 'C:/Python313/Lib/json/__init__.py');
    assert.equal(libraryPathOf('file:///C:/Program%20Files/PyMagic/typeshed/stdlib/json/__init__.pyi', root), 'C:/Program Files/PyMagic/typeshed/stdlib/json/__init__.pyi');
    assert.equal(libraryPathOf('file:///usr/lib/python3.12/json/__init__.py', 'file:///home/me/shop/'), '/usr/lib/python3.12/json/__init__.py');
    // the project's own files, whatever the case of the drive letter, and what is not a file
    assert.equal(libraryPathOf('file:///C:/work/shop/app.py', root), null);
    assert.equal(libraryPathOf('file:///c:/Work/Shop/app.py', root), null);
    assert.equal(libraryPathOf('untitled:1', root), null);
    assert.equal(libraryPathOf(undefined, root), null);
});

test('the path back from a library model, and the window title', () => {
    assert.equal(pathOfLibraryUri('/C:/Python313/Lib/json/__init__.py'), 'C:/Python313/Lib/json/__init__.py');
    assert.equal(pathOfLibraryUri('/usr/lib/python3.12/json/__init__.py'), '/usr/lib/python3.12/json/__init__.py');
    assert.equal(libraryTitle('C:/Python313/Lib/json/__init__.py'), 'Lib/json/__init__.py');
});

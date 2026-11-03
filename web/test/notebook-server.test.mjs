// Checks of web/src/notebook-model.js `isLocalServer` and `debugRoute`: how the debugger of a cell on a Jupyter server reaches the IDE.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { debugRoute, isLocalServer } from '../src/notebook-model.js';

test('a server on this computer is one whose address is localhost or a loopback address', () => {
    for (const url of ['http://localhost:8888', 'http://LOCALHOST:8888/user/me', 'http://127.0.0.1:9000', 'http://127.1.2.3', 'https://localhost', 'http://[::1]:8888'])
        assert.equal(isLocalServer({ url }), true, url);
});

test('any other address, a name that only starts like it, and nothing at all are not local', () => {
    for (const url of ['http://192.168.1.10:8888', 'http://example.org', 'http://localhost.example.org:8888', 'http://127.0.0.1.example.org', 'http://10.0.0.1', '', 'not an address'])
        assert.equal(isLocalServer({ url }), false, url);
    assert.equal(isLocalServer(null), false);
    assert.equal(isLocalServer({}), false);
});

test('a cell on a server elsewhere is debugged over the kernel\'s channels; here and built in, by connecting back', () => {
    assert.equal(debugRoute({ url: 'http://192.168.1.10:8888' }), 'kernel');
    assert.equal(debugRoute({ url: 'https://hub.example.org/user/me' }), 'kernel');
    assert.equal(debugRoute({ url: 'http://localhost:8888' }), undefined);
    assert.equal(debugRoute(null), undefined);      // the built-in kernel
});

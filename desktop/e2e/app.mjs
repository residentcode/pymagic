// Starts the built PyMagic.exe on a project folder of its own, with a user-data folder of its own (so the user's settings,
// recent projects and the page's storage are not touched), and drives its page over the Chrome DevTools Protocol
// with Node's own WebSocket: no browser driver to download.
import { spawn, spawnSync } from 'node:child_process';
import { chmodSync, existsSync, mkdtempSync, readdirSync, rmSync, statSync, writeFileSync, mkdirSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';

const desktop = resolve(import.meta.dirname, '..');
export const EXE = process.env.PYMAGIC_EXE ?? join(desktop, 'out', 'PyMagic-win32-x64', 'PyMagic.exe');

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function freePort() {
    return new Promise((ok, fail) => {
        const server = createServer();
        server.on('error', fail);
        server.listen(0, '127.0.0.1', () => { const { port } = server.address(); server.close(() => ok(port)); });
    });
}

/** A folder with the given files ({ 'pkg/a.py': 'text' }), removed by `dispose`. */
export function project(files) {
    const root = mkdtempSync(join(tmpdir(), 'pymagic-e2e-'));
    for (const [rel, text] of Object.entries(files)) {
        mkdirSync(dirname(join(root, rel)), { recursive: true });
        writeFileSync(join(root, rel), text);
    }
    return root;
}

/** The page of a PyMagic window: evaluate runs an expression in it (promises awaited, the value returned as JSON). */
class Page {
    constructor(ws) {
        this.ws = ws;
        this.id = 0;
        this.pending = new Map();
        ws.addEventListener('message', (e) => {
            const m = JSON.parse(e.data);
            if (this.pending.has(m.id)) {
                this.pending.get(m.id)(m);
                this.pending.delete(m.id);
            }
        });
    }
    send(method, params = {}) {
        return new Promise((ok) => { const n = ++this.id; this.pending.set(n, ok); this.ws.send(JSON.stringify({ id: n, method, params })); });
    }
    async evaluate(expression) {
        const r = await this.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
        if (r.result?.exceptionDetails)
            throw new Error('in the page: ' + (r.result.exceptionDetails.exception?.description ?? r.result.exceptionDetails.text));
        return r.result?.result?.value;
    }
    /** Waits until the expression gives a true value, which it returns; throws after `ms`. */
    async waitFor(expression, ms = 20000, what = expression) {
        const end = Date.now() + ms;
        while (Date.now() < end) {
            const v = await this.evaluate(expression).catch(() => null);
            if (v)
                return v;
            await sleep(200);
        }
        throw new Error(`timed out waiting for ${what}`);
    }
    /** A click of the left button at a point of the page (CSS pixels). */
    async click(x, y) {
        await this.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
        await this.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 });
        await this.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 });
    }
    /** A key pressed in the focused element: code is the Windows virtual-key code (F9 is 120). */
    async key(code, key, modifiers = 0) {
        await this.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', windowsVirtualKeyCode: code, key, code: key, modifiers });
        await this.send('Input.dispatchKeyEvent', { type: 'keyUp', windowsVirtualKeyCode: code, key, code: key, modifiers });
    }
}

/** Starts PyMagic.exe on `root`; resolves when its page has the editor. */
export async function launch(root) {
    if (!existsSync(EXE))
        throw new Error(`${EXE} is not built: run node desktop/package.mjs --no-installer first`);
    const port = await freePort();
    const profile = mkdtempSync(join(tmpdir(), 'pymagic-e2e-profile-'));
    const child = spawn(EXE, [`--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, root], { stdio: 'ignore' });
    let target = null;
    for (let i = 0; i < 120 && !target; i++) {
        await sleep(500);
        try {
            const list = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
            target = list.find((t) => t.type === 'page' && /^http:\/\/127\.0\.0\.1:\d+\/$/.test(t.url));
        }
        catch { /* not listening yet */ }
    }
    if (!target) {
        stop(child, profile);
        throw new Error('PyMagic did not show its page');
    }
    const ws = new WebSocket(target.webSocketDebuggerUrl);
    await new Promise((ok, fail) => { ws.addEventListener('open', ok); ws.addEventListener('error', fail); });
    const page = new Page(ws);
    // the page behaves as focused even when other windows are in front, so typing and clicks reach the editor every time
    await page.send('Page.bringToFront');
    await page.send('Emulation.setFocusEmulationEnabled', { enabled: true });
    await page.waitFor('!!(window.pymagic && window.pymagic.openFile && window.pymagic.editor.getModel())', 60000, 'the editor');
    return {
        page, root,
        async close() {
            ws.close();
            stop(child, profile);
            removeTree(root);
        },
    };
}

/** Removes a folder of a test run. A git repository keeps read-only files (its packs), which Windows will not delete: they are made writable first, and a file that is still held by a closing process is tried again. */
export function removeTree(dir) {
    const writable = (p) => {
        try {
            const st = statSync(p);
            if (st.isDirectory()) for (const name of readdirSync(p)) writable(join(p, name));
            chmodSync(p, 0o666);
        }
        catch { /* gone already, or not ours to change: the removal below says */ }
    };
    for (let i = 0; i < 6; i++) {
        try { rmSync(dir, { recursive: true, force: true, maxRetries: 3, retryDelay: 200 }); return; }
        catch (e) {
            if (i === 5) throw e;
            writable(dir);
            Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 500);
        }
    }
}

function stop(child, profile) {
    // the window, its host (the C# server) and the programs it started: only the process tree this test launched
    if (child.exitCode === null)
        spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' });
    for (let i = 0; i < 10; i++) {
        try {
            rmSync(profile, { recursive: true, force: true });
            return;
        }
        catch { spawnSync(process.execPath, ['-e', 'setTimeout(() => {}, 300)']); }
    }
}

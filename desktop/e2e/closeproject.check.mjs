// Items 379 and 380 in the built PyMagic.exe: a Django view and a Celery task with Django and Celery installed in the project's .venv (no false
// "Cannot find reference" on `Address.objects` or `.delay`, and `await ....afirst()` an Address | None), then Close project from the project
// folder's menu: the last window shows the welcome screen, and the project opens again from its recent projects.
// Run: node desktop/e2e/closeproject.check.mjs   (needs uv on the PATH, and the internet the first time for Django and Celery)
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { launch, project, sleep } from './app.mjs';

const root = project({
    'shop/__init__.py': '',
    'shop/models.py': 'from django.conf import settings\nfrom django.db import models\n\n\nclass Address(models.Model):\n    user = models.ForeignKey(settings.AUTH_USER_MODEL, on_delete=models.CASCADE)\n    line_id = models.CharField(max_length=20)\n',
    'shop/tools.py': 'from celery import shared_task\nfrom django.conf import settings\nfrom django.core.mail import EmailMessage\n\n\n@shared_task\ndef send_to_admin(subject, message):\n    email_msg = EmailMessage(subject, message, settings.SERVER_EMAIL, [settings.SALES_EMAIL])\n    email_msg.content_subtype = "html"\n    email_msg.send()\n',
    'shop/views.py': 'from . import models, tools\n\n\nasync def view(request, line_id):\n    address = await models.Address.objects.filter(user=request.user, line_id=line_id).afirst()\n    tools.send_to_admin.delay("subject", "message")\n    return address\n',
});
for (const step of [['venv', '-q', '.venv'], ['pip', 'install', '-q', '-p', join(root, '.venv', 'Scripts', 'python.exe'), 'django', 'celery']]) {
    const r = spawnSync('uv', step, { cwd: root, encoding: 'utf8' });
    assert.equal(r.status, 0, `uv ${step.join(' ')}: ${r.stderr}`);
}

const app = await launch(root);
const { page } = app;
try {
    await page.evaluate(`window.pymagic.openFile('shop/views.py')`);
    await page.waitFor(`window.pymagic.editor.getModel()?.uri.path.endsWith('views.py')`, 20000, 'views.py open');
    await sleep(8000);   // the interpreter's libraries are read and the file checked
    const problems = await page.evaluate(`window.pymagic.monaco.editor.getModelMarkers({ resource: window.pymagic.editor.getModel().uri }).map((x) => x.startLineNumber + ': ' + x.message)`);
    console.log('problems of views.py:', problems);
    assert.ok(!problems.some((p) => /Cannot find reference/.test(p)), 'no Cannot find reference');
    // the hover shown at a place, asked again every few seconds until it says `expected`
    const hover = async (line, column, expected) => {
        let text = '';
        for (let i = 0; i < 6 && !expected.test(text); i++) {
            await page.key(27, 'Escape');
            await sleep(300);
            await page.evaluate(`(() => { const ed = window.pymagic.editor; ed.setPosition({ lineNumber: ${line}, column: ${column} }); ed.focus(); ed.trigger('check', 'editor.action.showHover', {}); })()`);
            text = await page.waitFor(`(() => { const t = [...document.querySelectorAll('.monaco-hover-content')].filter((h) => h.offsetParent !== null && h.getClientRects().length).map((h) => h.textContent).join(' '); return ${expected}.test(t) ? t : null; })()`, 4000, 'the hover').catch(() => '') ?? '';
        }
        return text;
    };
    const address = await hover(5, 6, /address: Address \| None/);
    console.log('hover address:', address.slice(0, 120));
    assert.match(address, /Address \| None/);
    const delay = await hover(6, 26, /def delay/);
    if (!delay) console.log('hover at delay says:', (await hover(6, 26, /./)).slice(0, 300), '| at send_to_admin:', (await hover(6, 14, /./)).slice(0, 300));
    console.log('hover delay:', delay.slice(0, 120));
    assert.match(delay, /def delay/);

    // Close project at the end of the project folder's menu
    await page.evaluate(`(() => { const li = document.querySelector('#files li.root'); const r = li.getBoundingClientRect(); li.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, clientX: r.left + 20, clientY: r.top + 5 })); })()`);
    const labels = await page.waitFor(`(() => { const m = document.querySelector('.ctx-menu'); return m ? [...m.querySelectorAll('.ctx-item .ctx-label')].map((x) => x.textContent) : null; })()`, 5000, 'the menu');
    console.log('last rows of the menu:', labels.slice(-3));
    assert.equal(labels.at(-1), 'Close project');
    // the page goes away as the window turns to the welcome screen: the click is sent without waiting for an answer
    page.ws.send(JSON.stringify({ id: 99999, method: 'Runtime.evaluate', params: { expression: `[...document.querySelector('.ctx-menu').querySelectorAll('.ctx-item')].find((r) => r.querySelector('.ctx-label').textContent === 'Close project').click()` } }));

    let welcome = null;
    for (let i = 0; i < 60 && !welcome; i++) {
        await sleep(500);
        const list = await (await fetch(`http://127.0.0.1:${app.port}/json`)).json().catch(() => []);
        welcome = list.find((t) => t.type === 'page' && t.url.startsWith('file:') && t.url.endsWith('welcome.html'));
    }
    assert.ok(welcome, 'the welcome screen');
    const ws = new WebSocket(welcome.webSocketDebuggerUrl);
    await new Promise((ok, fail) => { ws.addEventListener('open', ok); ws.addEventListener('error', fail); });
    let n = 0;
    const ask = (expression) => new Promise((ok) => {
        const id = ++n;
        const on = (e) => { const m = JSON.parse(e.data); if (m.id === id) { ws.removeEventListener('message', on); ok(m.result?.result?.value); } };
        ws.addEventListener('message', on);
        ws.send(JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, awaitPromise: true, returnByValue: true } }));
    });
    let rows = null;
    for (let i = 0; i < 40 && !rows?.length; i++) { await sleep(250); rows = await ask(`[...document.querySelectorAll('#recent li .path')].map((x) => x.textContent)`); }
    console.log('welcome screen, recent projects:', rows);
    assert.ok(rows.some((p) => p.toLowerCase() === root.toLowerCase()), 'the closed project is in the recent projects');
    // and it opens again from there, in this window
    ws.send(JSON.stringify({ id: 9999, method: 'Runtime.evaluate', params: { expression: `[...document.querySelectorAll('#recent li')].find((li) => li.title.toLowerCase() === ${JSON.stringify(root.toLowerCase())}).click()` } }));
    let again = null;
    for (let i = 0; i < 120 && !again; i++) {
        await sleep(500);
        const list = await (await fetch(`http://127.0.0.1:${app.port}/json`)).json().catch(() => []);
        again = list.find((t) => t.type === 'page' && /^http:\/\/127\.0\.0\.1:\d+\/$/.test(t.url));
    }
    assert.ok(again, 'the project open again');
    console.log('the project opened again from the welcome screen:', again.url);
    ws.close();
    console.log('OK');
} finally {
    await app.close();
}

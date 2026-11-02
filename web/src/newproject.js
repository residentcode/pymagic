// File > New project…: where it goes, its kind (pure Python, FastAPI, Flask, Django, Jupyter), Git, a welcome script, and its
// environment (a project venv, uv, an existing interpreter such as conda's base, or none). The engine makes it (/api/newproject)
// and the desktop app opens it; in a browser the page says where it is.
import { toast } from './toast';
import { pluginsAtStart } from './settings';
import { kindsOff, pluginOn } from './plugin-model.js';

const KINDS = [
    ['python', 'Pure Python', 'A folder with a welcome script, if you want one.'],
    ['fastapi', 'FastAPI', 'main.py with two endpoints, requests to try them, and requirements.txt.'],
    ['flask', 'Flask', 'app.py, templates and static folders, and requirements.txt.'],
    ['django', 'Django', 'manage.py and the project package (settings, urls, wsgi, asgi), a templates folder, a first app if you name one, and requirements.txt.'],
    ['jupyter', 'Jupyter', 'A first notebook and requirements.txt.'],
];

/** The folder a new project is suggested in: beside the open one. */
export const suggestedLocation = (root, name = 'untitled') => {
    const sep = root.includes('\\') ? '\\' : '/';
    const parent = root.replace(/[\\/]+$/, '').replace(/[\\/][^\\/]*$/, '');
    return `${parent}${sep}${name}`;
};

export async function openNewProject() {
    const desktop = window.pymagicDesktop;
    const info = await fetch('/api/info').then((r) => r.json()).catch(() => ({}));
    const interpreters = await fetch('/api/interpreters').then((r) => r.json()).catch(() => []);
    const local = interpreters.filter((i) => !i.target || i.target === 'local');

    const shade = document.createElement('div');
    shade.className = 'settings-shade';
    const box = document.createElement('div');
    box.className = 'np-box';
    box.innerHTML = `
      <h3>New project</h3>
      <label class="np-row">Location <span class="np-loc"><input id="np-location" spellcheck="false"><button id="np-browse" type="button">Browse…</button></span></label>
      <div class="np-kinds" id="np-kinds"></div>
      <p class="set-note" id="np-kind-note"></p>
      <label class="np-check"><input type="checkbox" id="np-git" checked> Create a Git repository</label>
      <label class="np-check" id="np-welcome-row"><input type="checkbox" id="np-welcome" checked> Create a welcome script (main.py)</label>
      <div id="np-django" class="np-django" style="display:none">
        <label class="np-check">Template language <select id="np-dj-lang"><option value="django">Django</option><option value="jinja2">Jinja2</option></select></label>
        <label class="np-check">Templates folder <input id="np-dj-folder" value="templates" spellcheck="false"></label>
        <label class="np-check">First app <input id="np-dj-app" placeholder="none (an app's name, such as shop)" spellcheck="false"></label>
        <label class="np-check"><input type="checkbox" id="np-dj-admin" checked> The admin (django.contrib.admin and its URL)</label>
      </div>
      <h4>Interpreter</h4>
      <label class="np-check"><input type="radio" name="np-env" value="venv" checked> A project venv (.venv), made by <select id="np-base"></select></label>
      <label class="np-check"><input type="radio" name="np-env" value="uv"> uv: <input id="np-uv" value="uv" spellcheck="false" title="The uv program, or its path"> Python <input id="np-version" placeholder="3.13" spellcheck="false" size="6"></label>
      <label class="np-check"><input type="radio" name="np-env" value="interpreter"> An existing interpreter (base conda, or any other) <select id="np-existing"></select></label>
      <label class="np-check"><input type="radio" name="np-env" value="none"> None for now</label>
      <p class="set-note" id="np-status"></p>
      <div class="set-buttons"><button id="np-cancel" type="button">Cancel</button><button id="np-create" class="primary" type="button">Create</button></div>`;
    shade.append(box);
    document.body.append(shade);
    const $ = (id) => box.querySelector('#' + id);
    const location = $('np-location');
    location.value = info.root ? suggestedLocation(info.root) : '';
    let kind = 'python';
    // Settings > Plugins: the kinds of the plugins that are off (Django, Jupyter) are not offered, nor Git when its plugin is off
    const offKinds = kindsOff(pluginsAtStart());
    if (!pluginOn(pluginsAtStart(), 'git')) {
        $('np-git').checked = false;
        $('np-git').closest('label').style.display = 'none';
    }
    for (const [id, label, note] of KINDS.filter(([k]) => !offKinds.includes(k))) {
        const b = document.createElement('button');
        b.type = 'button';
        b.textContent = label;
        b.dataset.kind = id;
        b.onclick = () => {
            kind = id;
            for (const x of $('np-kinds').children) x.classList.toggle('active', x === b);
            $('np-kind-note').textContent = note;
            $('np-welcome-row').style.display = id === 'python' ? '' : 'none';
            $('np-django').style.display = id === 'django' ? '' : 'none';
        };
        $('np-kinds').append(b);
    }
    $('np-kinds').firstElementChild.click();
    for (const select of [$('np-base'), $('np-existing')])
        for (const i of local)
            select.add(new Option(`${i.label ?? i.version}: ${i.path}`, i.path));
    // the base of a venv is better not a venv; conda's base is a good existing one
    const notVenv = local.find((i) => !i.isVenv);
    if (notVenv)
        $('np-base').value = notVenv.path;
    const conda = local.find((i) => i.kind === 'conda');
    if (conda)
        $('np-existing').value = conda.path;
    if (local.length === 0) {
        $('np-base').add(new Option('no Python found', ''));
        box.querySelector('input[value="none"]').checked = true;
    }
    $('np-browse').style.display = desktop?.pickFolder ? '' : 'none';
    $('np-browse').onclick = async () => {
        const chosen = await desktop.pickFolder(location.value);
        if (chosen)
            location.value = chosen;
    };
    const close = () => shade.remove();
    $('np-cancel').onclick = close;
    shade.onmousedown = (e) => { if (e.target === shade) close(); };
    box.onkeydown = (e) => { if (e.key === 'Escape') close(); };
    $('np-create').onclick = async () => {
        const environment = box.querySelector('input[name="np-env"]:checked').value;
        const body = {
            location: location.value.trim(), kind, git: $('np-git').checked, welcome: $('np-welcome').checked, environment,
            python: environment === 'venv' ? $('np-base').value : environment === 'interpreter' ? $('np-existing').value : undefined,
            uvPath: $('np-uv').value, pythonVersion: $('np-version').value,
            django: kind === 'django' ? { templateLanguage: $('np-dj-lang').value, templatesFolder: $('np-dj-folder').value.trim() || 'templates', appName: $('np-dj-app').value.trim() || undefined, admin: $('np-dj-admin').checked } : undefined,
        };
        $('np-create').disabled = true;
        $('np-status').textContent = environment === 'none' ? 'Creating…' : 'Creating the project and its environment…';
        let res;
        try {
            res = await fetch('/api/newproject', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
        }
        catch (e) {
            res = null;
        }
        const answer = res ? await res.json().catch(() => ({})) : {};
        if (!res?.ok) {
            $('np-status').textContent = answer.error ?? 'The project could not be made.';
            $('np-create').disabled = false;
            return;
        }
        close();
        for (const note of answer.notes ?? [])
            toast(note);
        if (desktop?.openProject)
            await desktop.openProject(answer.folder);
        else
            toast(`The project is in ${answer.folder}; start PyMagic there to open it.`);
    };
    location.focus();
    location.select();
}

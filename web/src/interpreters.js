// Interpreter picker: lists what the host found, switches the project's interpreter, creates a .venv.
const $ = (id) => document.getElementById(id);
export function setupInterpreters() {
    const select = $('interp');
    const note = $('interp-note');
    const venvButton = $('b-venv');
    let flash;
    function say(text, bad = false) {
        note.textContent = text;
        note.className = bad ? 'bad' : '';
        clearTimeout(flash);
        if (text)
            flash = window.setTimeout(() => { note.textContent = ''; }, 6000);
    }
    async function refresh(retries = 8) {
        select.disabled = true;
        select.textContent = '';
        const loading = document.createElement('option');
        loading.textContent = 'Detecting Python…';
        select.appendChild(loading);
        let list = [];
        try {
            list = await (await fetch('/api/interpreters')).json();
        }
        catch { /* host not reachable */ }
        select.textContent = '';
        if (list.length === 0) {
            const none = document.createElement('option');
            none.textContent = 'No Python found';
            select.appendChild(none);
            venvButton.disabled = true;
            return;
        }
        const hasCurrent = list.some((i) => i.current);
        if (!hasCurrent) {
            const pick = document.createElement('option');
            pick.textContent = 'Choose an interpreter…';
            pick.value = '';
            pick.selected = true;
            select.appendChild(pick);
            // the host picks one in the background right after it starts
            if (retries > 0)
                window.setTimeout(() => void refresh(retries - 1), 1500);
        }
        for (const i of list) {
            const option = document.createElement('option');
            option.value = i.path;
            option.textContent = i.label;
            option.title = i.path;
            option.selected = i.current;
            select.appendChild(option);
        }
        select.disabled = false;
        venvButton.disabled = false;
        select.title = list.find((i) => i.current)?.path ?? 'Python interpreter';
    }
    async function choose(path) {
        say('Switching…');
        select.disabled = true;
        const res = await fetch('/api/interpreter', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path }) });
        const body = await res.json();
        if (!res.ok) {
            say(body.error ?? 'Could not switch the interpreter', true);
            await refresh(0);
            return;
        }
        say(`Using ${body.label}`);
        window.dispatchEvent(new Event('pymagic:interpreter'));
        await refresh(0);
    }
    select.onchange = () => { if (select.value)
        void choose(select.value); };
    venvButton.onclick = async () => {
        venvButton.disabled = true;
        say('Creating .venv…');
        try {
            const res = await fetch('/api/venv', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
            const body = await res.json();
            if (!res.ok) {
                say(body.error ?? 'Could not create the environment', true);
                return;
            }
            await choose(body.path);
        }
        finally {
            venvButton.disabled = false;
        }
    };
    // ---- an interpreter from a WSL distribution or a Docker image
    $('b-remote').onclick = () => openRemoteDialog();
    function openRemoteDialog() {
        const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; };
        const shade = el('div', 'ask-shade');
        const box = el('div', 'ask-box');
        const kind = el('select');
        for (const [value, label] of [['wsl', 'WSL distribution'], ['docker', 'Docker image'], ['compose', 'Docker Compose service'], ['ssh', 'SSH host']]) { const o = el('option', '', label); o.value = value; kind.append(o); }
        const nameLabel = el('div', 'ask-hint');
        const nameSelect = el('select');
        const nameInput = el('input');
        nameInput.spellcheck = false;
        nameInput.autocomplete = 'off';
        const python = el('input');
        python.spellcheck = false;
        python.autocomplete = 'off';
        const identityLabel = el('div', 'ask-hint', 'Key file (optional; the ssh agent and ~/.ssh keys are used otherwise)');
        const identity = el('input');
        identity.spellcheck = false;
        identity.autocomplete = 'off';
        identity.placeholder = 'C:\\Users\\you\\.ssh\\id_ed25519';
        const composeFileLabel = el('div', 'ask-hint', 'Compose file (optional; relative to the project, the usual names are looked for otherwise)');
        const composeFile = el('input');
        composeFile.spellcheck = false;
        composeFile.autocomplete = 'off';
        composeFile.placeholder = 'compose.yaml';
        const execLabel = el('label', 'ask-hint');
        const execBox = el('input');
        execBox.type = 'checkbox';
        execLabel.append(execBox, document.createTextNode(' Use the running container (docker compose exec) instead of a new one'));
        const composeDir = el('input');
        composeDir.spellcheck = false;
        composeDir.autocomplete = 'off';
        composeDir.placeholder = 'Where the service mounts the project (default /workspace)';
        const hint = el('div', 'ask-hint');
        const message = el('div', 'ask-hint');
        message.style.color = '#e5534b';
        const add = el('button', '', 'Add and use');
        const cancel = el('button', '', 'Cancel');
        const row = el('div', 'ask-buttons');
        row.append(cancel, add);
        box.append(el('div', 'ask-title', 'Add an interpreter'), el('div', 'ask-hint', 'Where does it run?'), kind, nameLabel, nameSelect, nameInput,
            el('div', 'ask-hint', 'The interpreter in it'), python, identityLabel, identity, composeFileLabel, composeFile, execLabel, composeDir, hint, message, row);
        shade.append(box);
        document.body.append(shade);
        const done = () => shade.remove();
        let targets = { wsl: [], docker: { available: false }, dockerImages: [], compose: { file: null, services: [] }, ssh: { installed: false, hosts: [] } };
        function paint() {
            const wsl = kind.value === 'wsl';
            const ssh = kind.value === 'ssh';
            const compose = kind.value === 'compose';
            composeFileLabel.style.display = composeFile.style.display = execLabel.style.display = compose ? '' : 'none';
            composeDir.style.display = compose && execBox.checked ? '' : 'none';
            if (compose && !composeFile.value && targets.compose.file) composeFile.placeholder = targets.compose.file;
            nameLabel.textContent = compose ? 'Service' : wsl ? 'Distribution' : ssh ? 'Address (user@host or user@host:port, or a name from ~/.ssh/config)' : 'Image (for example python:3.12-slim)';
            nameSelect.style.display = wsl ? '' : 'none';
            nameInput.style.display = wsl ? 'none' : '';
            identityLabel.style.display = identity.style.display = ssh ? '' : 'none';
            python.placeholder = wsl || ssh ? 'python3' : 'python';
            hint.textContent = wsl
                ? (targets.wsl.length ? 'The project folder is shared with the distribution; libraries are read from it. Debugging needs the Windows firewall to let it connect back.' : 'No WSL distribution was found on this computer.')
                : compose
                    ? (targets.compose.services.length ? 'Each run is docker compose run --rm of the service (and what it depends on), with the project folder as /workspace. Libraries of the image are not read by the editor. Nothing here has been run against a Docker engine yet.' : 'No compose file with services was found in the project: write the service name, and the file if it is not compose.yaml.')
                : ssh
                    ? (targets.ssh.installed ? 'Nothing is shared with that machine: the changed files of the project are copied to ~/.pymagic there before each run, and the libraries are copied here once so the editor can read them. It signs in with a key or the ssh agent (no password prompt), trusts a new host once and refuses one whose key changed.' : 'The ssh program was not found: it is an optional Windows feature.')
                    : (targets.docker.available ? 'Each run starts a container from the image with the project folder as /workspace. Libraries are copied out of the image once.' : 'The docker command did not answer: install Docker or start it.');
            nameSelect.replaceChildren(...targets.wsl.map((n) => { const o = el('option', '', n); o.value = n; return o; }));
            if (kind.value === 'compose' && !document.getElementById('compose-services')) {
                const list = el('datalist');
                list.id = 'compose-services';
                box.append(list);
            }
            const services = document.getElementById('compose-services');
            if (services) services.replaceChildren(...targets.compose.services.map((n) => { const o = el('option'); o.value = n; return o; }));
            if (kind.value === 'compose')
                nameInput.setAttribute('list', 'compose-services');
            else if (kind.value === 'ssh' && targets.ssh.hosts.length && !document.getElementById('ssh-hosts')) {
                const hosts = el('datalist');
                hosts.id = 'ssh-hosts';
                hosts.append(...targets.ssh.hosts.map((h) => { const o = el('option'); o.value = h; return o; }));
                box.append(hosts);
            }
            if (kind.value === 'ssh')
                nameInput.setAttribute('list', 'ssh-hosts');
            else if (kind.value === 'docker' && targets.dockerImages.length && !document.getElementById('docker-images')) {
                const list = el('datalist');
                list.id = 'docker-images';
                list.append(...targets.dockerImages.map((i) => { const o = el('option'); o.value = i; return o; }));
                box.append(list);
                nameInput.setAttribute('list', 'docker-images');
            }
            else if (kind.value === 'docker')
                nameInput.setAttribute('list', 'docker-images');
        }
        kind.onchange = paint;
        execBox.onchange = paint;
        paint();
        async function loadTargets() {
            try {
                const file = composeFile.value.trim();
                targets = await (await fetch('/api/interpreter/targets' + (file ? '?composeFile=' + encodeURIComponent(file) : ''))).json();
            } catch { /* host not reachable */ }
            paint();
        }
        composeFile.onchange = loadTargets;     // the services of another file
        void loadTargets();
        cancel.onclick = done;
        shade.onmousedown = (e) => { if (e.target === shade) done(); };
        shade.onkeydown = (e) => { if (e.key === 'Escape') done(); };
        add.onclick = async () => {
            message.textContent = '';
            const name = kind.value === 'wsl' ? nameSelect.value : nameInput.value.trim();
            if (!name) { message.textContent = kind.value === 'wsl' ? 'Choose a distribution.' : kind.value === 'compose' ? 'Type the name of a service.' : kind.value === 'ssh' ? 'Type user@host.' : 'Type the name of an image.'; return; }
            add.disabled = true;
            say('Asking the interpreter…');
            try {
                const res = await fetch('/api/interpreter/remote', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ kind: kind.value, name, python: python.value.trim(), identity: identity.value.trim(), composeFile: composeFile.value.trim(), exec: execBox.checked, composeDir: composeDir.value.trim() }) });
                const body = await res.json();
                if (!res.ok) { message.textContent = body.error ?? 'It could not be added.'; say('', false); return; }
                done();
                say(`Using ${body.label}`);
                window.dispatchEvent(new Event('pymagic:interpreter'));
                await refresh(0);
            }
            finally { add.disabled = false; }
        };
        (nameSelect.style.display === 'none' ? nameInput : nameSelect).focus();
    }
    void refresh();
}

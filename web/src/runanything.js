// Run Anything (Control pressed twice, or Ctrl+Shift+A): one box that runs a run configuration, a script of the project, `python -m module`,
// a `manage.py` command, `pip ...`, or opens a recent project, with the matching things listed as it is typed. Enter runs, Shift+Enter
// debugs (where that makes sense), Tab completes the one chosen. runanything-model.js says what a line means and suggests.
import { doubleTap, parseCommand, suggest } from './runanything-model';
import { toast } from './toast';

const $ = (id) => document.getElementById(id);

export function setupRunAnything({ runConfigs, runHooks, editor }) {
    let shade = null;

    async function context() {
        const [files, django, recents] = await Promise.all([
            fetch('/api/files').then((r) => (r.ok ? r.json() : [])).catch(() => []),
            fetch('/api/django').then((r) => (r.ok ? r.json() : null)).catch(() => null),
            Promise.resolve(window.pymagicDesktop?.recentProjects?.()).then((r) => r ?? []).catch(() => []),
        ]);
        const base = (p) => String(p.path ?? p).split(/[\\/]/).filter(Boolean).pop();
        return {
            configs: runConfigs.names(),
            scripts: files.filter((f) => !/(^|\/)(\.venv|venv|node_modules|site-packages)\//.test(f)),
            manage: django?.found !== false && django?.manage ? django.manage.replace(/\\/g, '/') : null,
            recents: recents.map((p) => ({ name: typeof p === 'string' ? base(p) : (p.name ?? base(p)), path: typeof p === 'string' ? p : p.path })),
        };
    }

    /** Does what a line says; false (with a message) when it says nothing the box can do. */
    async function execute(text, ctx, debug) {
        const cmd = parseCommand(text);
        switch (cmd.kind) {
            case 'module': void runHooks.runModule(cmd.module, cmd.args, debug); return true;
            case 'script': void runHooks.runScript(cmd.file, cmd.args, debug); return true;
            case 'manage':
                if (!ctx.manage) { toast('This project has no manage.py.', true); return false; }
                if (!cmd.args.length) { toast('Which command? (migrate, runserver, ...)', true); return false; }
                void runHooks.runScript(ctx.manage, cmd.args, debug);
                return true;
            case 'pip': {
                const [verb, ...rest] = cmd.args;
                const names = rest.filter((a) => !a.startsWith('-'));
                if ((verb === 'install' || verb === 'uninstall') && names.length === rest.length && names.length) {
                    window.dispatchEvent(new CustomEvent(verb === 'install' ? 'pymagic:installPackages' : 'pymagic:uninstallPackages', { detail: names }));
                    return true;
                }
                void runHooks.runModule('pip', cmd.args, false);
                return true;
            }
            case 'project': {
                const hit = ctx.recents.find((p) => p.name.toLowerCase() === cmd.name.toLowerCase()) ?? ctx.recents.find((p) => p.name.toLowerCase().includes(cmd.name.toLowerCase()));
                if (!hit || !window.pymagicDesktop?.openProject) { toast(`No recent project named ${cmd.name}.`, true); return false; }
                await window.pymagicDesktop.openProject(hit.path);
                return true;
            }
            default: {
                const config = runConfigs.byName(cmd.text) ?? runConfigs.names().map((n) => runConfigs.byName(n)).find((c) => c.name.toLowerCase() === cmd.text.toLowerCase());
                if (config) { runConfigs.choose(config.name); $(debug ? 'b-debug' : 'b-run').click(); return true; }
                toast(cmd.text ? `Nothing to run for '${cmd.text}'.` : 'Type what to run.', true);
                return false;
            }
        }
    }

    async function open() {
        if (shade) return;
        const ctx = await context();
        shade = document.createElement('div');
        shade.className = 'ask-shade ra-shade';
        const box = document.createElement('div');
        box.className = 'ask-box ra-box';
        const title = document.createElement('div');
        title.className = 'ask-title';
        title.textContent = 'Run anything';
        const input = document.createElement('input');
        input.placeholder = 'a run configuration, a script, python -m …, manage.py …, pip …, open …';
        input.spellcheck = false;
        input.autocomplete = 'off';
        const list = document.createElement('div');
        list.className = 'ra-list';
        const hint = document.createElement('div');
        hint.className = 'ask-hint';
        hint.textContent = 'Enter runs · Shift+Enter debugs · Tab completes · Esc closes';
        box.append(title, input, list, hint);
        shade.append(box);
        document.body.append(shade);
        let items = [], at = 0;
        const close = () => { shade?.remove(); shade = null; editor.focus(); };
        const draw = () => {
            items = suggest(input.value, ctx);
            at = Math.min(at, Math.max(0, items.length - 1));
            list.replaceChildren(...items.map((s, i) => {
                const row = document.createElement('div');
                row.className = 'ra-row' + (i === at ? ' active' : '');
                const label = document.createElement('span');
                label.className = 'ra-label';
                label.textContent = s.label;
                const detail = document.createElement('span');
                detail.className = 'ra-detail';
                detail.textContent = s.detail;
                row.append(label, detail);
                row.onmousedown = (e) => { e.preventDefault(); at = i; void go(false); };
                return row;
            }));
            list.querySelector('.active')?.scrollIntoView({ block: 'nearest' });
        };
        const go = async (debug) => {
            const chosen = items[at];
            const line = input.value.trim();
            // a suggestion that wants more typed ("pip install ", "python -m ") is completed, not run
            if (chosen && /\s$/.test(chosen.fill) && chosen.fill.trim() !== line) { input.value = chosen.fill; at = 0; draw(); return; }
            // a line with arguments of its own runs as it is; a part of a name runs the suggestion chosen
            const own = parseCommand(line);
            const whole = own.kind !== 'name' && own.kind !== 'project' && (own.args?.length > 0 || own.kind === 'script' || own.kind === 'module');
            const target = whole && !(own.kind === 'manage' && own.args.length === 1 && chosen) ? line : (chosen ? chosen.fill : line);
            if (await execute(target, ctx, debug)) close();
        };
        input.oninput = () => { at = 0; draw(); };
        input.onkeydown = (e) => {
            e.stopPropagation();
            if (e.key === 'Escape') { e.preventDefault(); close(); }
            else if (e.key === 'ArrowDown') { e.preventDefault(); at = Math.min(items.length - 1, at + 1); draw(); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); at = Math.max(0, at - 1); draw(); }
            else if (e.key === 'Tab') { e.preventDefault(); if (items[at]) { input.value = items[at].fill; at = 0; draw(); } }
            else if (e.key === 'Enter') { e.preventDefault(); void go(e.shiftKey); }
        };
        shade.onmousedown = (e) => { if (e.target === shade) close(); };
        draw();
        input.focus();
    }

    // Control pressed and let go twice in a row
    const tap = {};
    for (const type of ['keydown', 'keyup'])
        window.addEventListener(type, (e) => { if (doubleTap(tap, { type, key: e.key, repeat: e.repeat }, performance.now())) { e.preventDefault(); void open(); } }, true);
    editor.addAction({ id: 'pymagic.runAnything', label: 'Run Anything…', keybindings: [2048 | 1024 | 31], run: () => void open() });   // Ctrl+Shift+A
    return { open, execute };
}

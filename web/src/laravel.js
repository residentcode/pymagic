// Laravel in PyMagic: Run Artisan Command… (Ctrl+Shift+X, as PhpStorm's command-line tools), in a Laravel project only: the project's own
// commands (php artisan list) filtered as they are typed, each with its description; Enter (or a click) asks for its arguments, with its usage,
// and runs `php artisan command arguments` in the Run panel. The commands run last come first.
import { defineCommand } from './commands.js';
import { toast } from './toast';
import { splitArguments } from './django-model.js';

import { artisanMatches } from './laravel-model.js';

const RECENT_KEY = 'pymagic.artisanRecent';

export function setupLaravel(deps) {
    let laravel = null;
    const ready = fetch('/api/php').then((r) => r.json()).then((p) => { laravel = p.laravel ? p : null; }).catch(() => { });
    let recent = [];
    try { recent = JSON.parse(localStorage.getItem(RECENT_KEY) ?? '[]'); } catch { /* none */ }

    async function open() {
        await ready;
        if (!laravel) { toast('This is not a Laravel project (it has no artisan file and laravel/framework).', true); return; }
        const res = await fetch('/api/artisan/commands');
        const commands = res.ok ? await res.json() : null;
        if (!commands) { toast((await res.json().catch(() => null))?.error ?? 'Artisan did not answer.', true); return; }
        const shade = document.createElement('div');
        shade.className = 'ask-shade';
        const box = document.createElement('div');
        box.className = 'ask-box artisan-box';
        const title = document.createElement('div');
        title.className = 'ask-title';
        title.textContent = `Run Artisan command — Laravel ${laravel.laravel.version ?? ''}`;
        const filter = document.createElement('input');
        filter.placeholder = 'make:model, migrate, route:list…';
        filter.spellcheck = false;
        const list = document.createElement('div');
        list.className = 'artisan-list';
        box.append(title, filter, list);
        shade.append(box);
        document.body.append(shade);
        let shown = [];
        let at = 0;
        const done = () => shade.remove();
        shade.onmousedown = (e) => { if (e.target === shade) done(); };
        const paint = () => {
            shown = artisanMatches(commands, filter.value.split(/\s/)[0], recent).slice(0, 200);
            at = Math.min(at, Math.max(0, shown.length - 1));
            list.replaceChildren(...shown.map((c, i) => {
                const row = document.createElement('div');
                row.className = 'artisan-row' + (i === at ? ' sel' : '');
                const name = document.createElement('span');
                name.className = 'artisan-name';
                name.textContent = c.name;
                const about = document.createElement('span');
                about.className = 'artisan-about';
                about.textContent = c.description ?? '';
                row.append(name, about);
                row.onmousedown = (e) => { e.preventDefault(); choose(c); };
                return row;
            }));
            list.querySelector('.sel')?.scrollIntoView({ block: 'nearest' });
        };
        // the command chosen: its arguments asked for (what was typed after the name is kept), then run
        const choose = async (c) => {
            done();
            const typedRest = filter.value.trim().split(/\s+/).slice(1).join(' ');
            const needs = [...(c.arguments ?? []).map((a) => (a.required ? `<${a.name}>` : `[${a.name}]`)), ...(c.options ?? []).slice(0, 6).map((o) => o.name)].join(' ');
            const rest = (c.arguments ?? []).length || typedRest ? await deps.askText(`php artisan ${c.name}`, typedRest, needs ? `${c.description ?? ''} — ${needs}` : c.description ?? '') : '';
            if (rest === null || rest === undefined) return;
            recent = [c.name, ...recent.filter((n) => n !== c.name)].slice(0, 8);
            try { localStorage.setItem(RECENT_KEY, JSON.stringify(recent)); } catch { /* not kept */ }
            if (!(await deps.run.runArtisan([c.name, ...splitArguments(rest)], false)))
                toast('A program is running in the Run panel: stop it first.', true);
        };
        filter.oninput = () => { at = 0; paint(); };
        filter.onkeydown = (e) => {
            if (e.key === 'ArrowDown') { e.preventDefault(); at = Math.min(shown.length - 1, at + 1); paint(); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); at = Math.max(0, at - 1); paint(); }
            else if (e.key === 'Enter' && shown[at]) { e.preventDefault(); void choose(shown[at]); }
            else if (e.key === 'Escape') { e.preventDefault(); done(); }
        };
        paint();
        filter.focus();
    }

    defineCommand({ id: 'pymagic.laravel.artisan', label: 'Run Artisan command…', group: 'Tools', keys: ['Ctrl+Shift+X'], everywhere: true, run: () => void open() });
    return { open, ready: () => ready.then(() => !!laravel) };
}

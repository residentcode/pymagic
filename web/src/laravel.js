// Laravel in PyMagic: Run Artisan Command… (Ctrl+Shift+X, as PhpStorm's command-line tools), in a Laravel project only: the project's own
// commands (php artisan list) filtered as they are typed, each with its description; Enter (or a click) asks for its arguments, with its usage,
// and runs `php artisan command arguments` in the Run panel. The commands run last come first. Run Composer Command… does the same for
// Composer's commands and the scripts of composer.json.
import * as monaco from 'monaco-editor';
import { defineCommand } from './commands.js';
import { toast } from './toast';
import { splitArguments } from './django-model.js';

import { artisanMatches } from './laravel-model.js';

const RECENT_KEY = 'pymagic.artisanRecent';
const K = monaco.languages.CompletionItemKind;

export function setupLaravel(deps) {
    let laravel = null;
    const ready = fetch('/api/php').then((r) => r.json()).then((p) => { laravel = p.laravel ? p : null; }).catch(() => { });
    const recentOf = (key) => { try { return JSON.parse(localStorage.getItem(key) ?? '[]'); } catch { return []; } };

    /** A list of commands to choose from, filtered as it is typed (the ones run last first); the one chosen asks for its arguments and runs. */
    function picker({ title, placeholder, commands, prefix, recentKey, run }) {
        let recent = recentOf(recentKey);
        const shade = document.createElement('div');
        shade.className = 'ask-shade';
        const box = document.createElement('div');
        box.className = 'ask-box artisan-box';
        const head = document.createElement('div');
        head.className = 'ask-title';
        head.textContent = title;
        const filter = document.createElement('input');
        filter.placeholder = placeholder;
        filter.spellcheck = false;
        const list = document.createElement('div');
        list.className = 'artisan-list';
        box.append(head, filter, list);
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
            const rest = (c.arguments ?? []).length || typedRest ? await deps.askText(`${prefix} ${c.name}`, typedRest, needs ? `${c.description ?? ''} — ${needs}` : c.description ?? '') : '';
            if (rest === null || rest === undefined) return;
            recent = [c.name, ...recent.filter((n) => n !== c.name)].slice(0, 8);
            try { localStorage.setItem(recentKey, JSON.stringify(recent)); } catch { /* not kept */ }
            if (!(await run([...c.name.split(' '), ...splitArguments(rest)])))
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

    async function open() {
        await ready;
        if (!laravel) { toast('This is not a Laravel project (it has no artisan file and laravel/framework).', true); return; }
        const res = await fetch('/api/artisan/commands');
        const commands = res.ok ? await res.json() : null;
        if (!commands) { toast((await res.json().catch(() => null))?.error ?? 'Artisan did not answer.', true); return; }
        picker({
            title: `Run Artisan command — Laravel ${laravel.laravel.version ?? ''}`, placeholder: 'make:model, migrate, route:list…', commands,
            prefix: 'php artisan', recentKey: RECENT_KEY, run: (argv) => deps.run.runArtisan(argv, false),
        });
    }

    /** Run Composer Command: install, update, require …, remove …, dump-autoload, and the scripts of composer.json, in the Run panel. */
    async function openComposer() {
        const res = await fetch('/api/composer/commands');
        const commands = res.ok ? await res.json() : null;
        if (!commands) { toast((await res.json().catch(() => null))?.error ?? 'Composer did not answer.', true); return; }
        picker({
            title: 'Run Composer command', placeholder: 'require, update, dump-autoload, run-script…', commands,
            prefix: 'composer', recentKey: 'pymagic.composerRecent', run: (argv) => deps.run.runComposer(argv),
        });
    }

    defineCommand({ id: 'pymagic.laravel.artisan', label: 'Run Artisan command…', group: 'Tools', keys: ['Ctrl+Shift+X'], everywhere: true, run: () => void open() });
    defineCommand({ id: 'pymagic.laravel.composer', label: 'Run Composer command…', group: 'Tools', everywhere: true, run: () => void openComposer() });
    return { open, openComposer, ready: () => ready.then(() => !!laravel) };
}

/**
 * Laravel's names in .php files and Blade templates (LaravelLinks.cs): Ctrl+click (Go to Definition) on a view's name, a route's name, a config
 * key, an environment variable, a translation, an asset or an <x-component> opens where it is; completion in those quotes offers the project's own.
 */
export function setupLaravelLinks({ modelFor, relOf }) {
    const ask = (path, model, position) => fetch(path, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ file: relOf(model), text: model.getValue(), offset: model.getOffsetAt(position) }),
    }).then((r) => (r.ok ? r.json() : null)).catch(() => null);
    monaco.languages.registerDefinitionProvider(['php', 'blade'], {
        async provideDefinition(model, position) {
            const link = await ask('/api/laravel/link', model, position);
            if (!link) return null;
            const target = await modelFor(link.file);
            if (!target) return null;
            const s = model.getPositionAt(link.start), e = model.getPositionAt(link.end);
            return [{
                uri: target.uri, range: new monaco.Range(link.line + 1, 1, link.line + 1, 1),
                originSelectionRange: new monaco.Range(s.lineNumber, s.column, e.lineNumber, e.column),
            }];
        },
    });
    monaco.languages.registerCompletionItemProvider(['php', 'blade'], {
        triggerCharacters: ["'", '"', '.', '-', '/'],
        async provideCompletionItems(model, position) {
            const found = await ask('/api/laravel/complete', model, position);
            if (!found?.items?.length) return { suggestions: [] };
            const s = model.getPositionAt(found.start), e = model.getPositionAt(found.end);
            const range = new monaco.Range(s.lineNumber, s.column, e.lineNumber, e.column);
            const kind = { view: K.File, component: K.Class, route: K.Reference, config: K.Property, env: K.Variable, lang: K.Text, asset: K.File, column: K.Field }[found.kind] ?? K.Text;
            return { suggestions: found.items.map((i, n) => ({ label: i.label, kind, detail: i.detail, insertText: i.label, range, sortText: String(n).padStart(4, '0') })) };
        },
    });
}

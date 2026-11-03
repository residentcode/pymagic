// Find in files (the Search tab of the bottom panel, Ctrl+Shift+F), replace in files, and go to symbol in the workspace.
import * as monaco from 'monaco-editor';
import { showLibraryFile as showLibrary } from './libview.js';
import { showTab } from './panel';
import { fileScore } from './filescore';
import { openMenu } from './menu';
import { readTrace } from './stacktrace-model.js';
import { rememberSearch, multilinePattern, SYMBOL_FILTERS, filterSymbols } from './search-model.js';
import { defineCommand } from './commands.js';
const $ = (id) => document.getElementById(id);
const KINDS = { 2: 'module', 5: 'class', 6: 'method', 7: 'property', 8: 'field', 9: 'constructor', 12: 'function', 13: 'variable', 14: 'constant' };
export function setupSearch(deps) {
    const { editor, lsp } = deps;
    let running = 0;
    let last = null;
    const query = $('fs-query');
    const replace = $('fs-replace');
    const include = $('fs-include');
    const exclude = $('fs-exclude');
    const caseBox = $('fs-case');
    const wordBox = $('fs-word');
    const regexBox = $('fs-regex');
    const structBox = $('fs-struct');
    const spattern = $('fs-spattern');
    const sreplace = $('fs-sreplace');
    // what is searched for and what replaces it, in the boxes of the mode that is on
    const what = () => (structBox.checked ? spattern.value : query.value);
    const withWhat = () => (structBox.checked ? sreplace.value : replace.value);
    const summary = $('fs-summary');
    const results = $('fs-results');
    const replaceAll = $('fs-replace-all');
    const libsBox = $('fs-libs');
    const recentList = $('fs-recent');
    // the results left out of Replace all (their checkboxes cleared), by file, line, column and text
    const skippedMatches = new Set();
    const matchKey = (path, m) => `${path}\u0000${m.line}\u0000${m.column}\u0000${m.text}`;
    const options = () => ({
        query: query.value, regex: regexBox.checked, caseSensitive: caseBox.checked, wholeWord: wordBox.checked,
        include: include.value, exclude: exclude.value, unsaved: deps.unsavedTexts(), libraries: libsBox.checked,
    });
    // ---------------------------------------------------------------- recent searches, offered under the query box
    const RECENT_KEY = 'pymagic.recentSearches';
    function recentSearches() {
        try {
            const list = JSON.parse(localStorage.getItem(RECENT_KEY) ?? '[]');
            return Array.isArray(list) ? list.filter((q) => typeof q === 'string') : [];
        }
        catch {
            return [];
        }
    }
    function showRecent() {
        recentList.replaceChildren(...recentSearches().map((q) => Object.assign(document.createElement('option'), { value: q })));
    }
    function remember(q) {
        const list = rememberSearch(recentSearches(), q);
        try {
            localStorage.setItem(RECENT_KEY, JSON.stringify(list));
        }
        catch { /* storage unavailable: the list lasts as long as the page */ }
        showRecent();
    }
    showRecent();
    // text of several lines pasted into the box is searched for as it is, line breaks included
    query.addEventListener('paste', (e) => {
        const text = e.clipboardData?.getData('text/plain') ?? '';
        if (!/\r?\n/.test(text.replace(/\r?\n$/, '')))
            return;
        e.preventDefault();
        regexBox.checked = true;
        query.value = multilinePattern(text);
        void search();
    });
    async function search() {
        if (!what()) {
            summary.textContent = '';
            results.replaceChildren();
            last = null;
            replaceAll.disabled = true;
            return;
        }
        const ticket = ++running;
        summary.textContent = 'Searching…';
        if (structBox.checked) {
            await structural(ticket);
            return;
        }
        let res;
        try {
            res = await fetch('/api/search', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ ...options(), replace: replace.value || undefined }) });
        }
        catch (e) {
            summary.textContent = String(e.message ?? e);
            return;
        }
        if (ticket !== running)
            return; // a newer search is under way
        if (!res.ok) {
            const body = await res.json().catch(() => ({}));
            summary.textContent = body.error ?? 'The search failed.';
            summary.className = 'bad';
            results.replaceChildren();
            last = null;
            replaceAll.disabled = true;
            return;
        }
        summary.className = '';
        last = await res.json();
        render();
        remember(query.value);
    }
    /** Structural search: the engine matches the pattern against the syntax of the project's Python files. */
    async function structural(ticket) {
        let found;
        try {
            found = await lsp.request('pymagic/structuralSearch', { pattern: spattern.value, replacement: sreplace.value || undefined });
        }
        catch (e) {
            if (ticket !== running)
                return;
            summary.textContent = e.message ?? 'The search failed.';
            summary.className = 'bad';
            results.replaceChildren();
            last = null;
            replaceAll.disabled = true;
            return;
        }
        if (ticket !== running)
            return;
        const byFile = new Map();
        for (const m of found) {
            const path = relOf(m.uri);
            if (!byFile.has(path))
                byFile.set(path, []);
            const { start, end } = m.range;
            const length = end.line === start.line ? end.character - start.character : m.text.length - start.character;
            byFile.get(path).push({ line: start.line, column: start.character, length, text: m.text, replacement: m.replacement ?? null, endLine: end.line, endColumn: end.character });
        }
        summary.className = '';
        last = { total: found.length, files: [...byFile].map(([path, matches]) => ({ path, matches })), filesSearched: 0, truncated: found.length >= 2000 };
        render();
        if (found.length === 0)
            summary.textContent = 'Nothing has that shape in the Python files of the project';
    }
    function render() {
        results.replaceChildren();
        if (!last)
            return;
        const r = last;
        summary.textContent = r.total === 0
            ? `No results in ${r.filesSearched} files`
            : `${r.total}${r.truncated ? '+' : ''} result${r.total === 1 ? '' : 's'} in ${r.files.length} file${r.files.length === 1 ? '' : 's'}${r.truncated ? ' (showing the first ones; narrow the search)' : ''}`;
        replaceAll.disabled = r.total === 0 || !withWhat() || r.files.every((f) => !deps.isEditable(f.path));
        for (const file of r.files) {
            const group = document.createElement('div');
            group.className = 'fs-file';
            const head = document.createElement('div');
            head.className = 'fs-head';
            const arrow = document.createElement('span');
            arrow.textContent = '▾ ';
            const name = document.createElement('span');
            name.textContent = file.path;
            const count = document.createElement('span');
            count.className = 'fs-count';
            count.textContent = String(file.matches.length);
            // with something to replace with, each result of a file that can be edited has a box: Replace all leaves out those cleared
            const choosing = !!withWhat() && !file.library && deps.isEditable(file.path) && r.replaceable !== false;
            const boxes = [];
            if (choosing) {
                const all = document.createElement('input');
                all.type = 'checkbox';
                all.title = 'Replace in this file';
                all.checked = file.matches.some((m) => !skippedMatches.has(matchKey(file.path, m)));
                all.onclick = (e) => {
                    e.stopPropagation();
                    for (const [m, box] of boxes) {
                        box.checked = all.checked;
                        if (all.checked)
                            skippedMatches.delete(matchKey(file.path, m));
                        else
                            skippedMatches.add(matchKey(file.path, m));
                    }
                };
                head.append(all);
            }
            head.append(arrow, name, count);
            if (file.library) {
                const tag = document.createElement('span');
                tag.className = 'fs-count';
                tag.textContent = 'library';
                head.append(tag);
            }
            const body = document.createElement('div');
            head.onclick = () => { const hidden = body.style.display === 'none'; body.style.display = hidden ? '' : 'none'; arrow.textContent = hidden ? '▾ ' : '▸ '; };
            for (const m of file.matches) {
                const row = document.createElement('div');
                row.className = 'fs-match';
                if (choosing) {
                    const box = document.createElement('input');
                    box.type = 'checkbox';
                    box.title = 'Replace this one';
                    box.checked = !skippedMatches.has(matchKey(file.path, m));
                    box.onclick = (e) => {
                        e.stopPropagation();
                        if (box.checked)
                            skippedMatches.delete(matchKey(file.path, m));
                        else
                            skippedMatches.add(matchKey(file.path, m));
                    };
                    boxes.push([m, box]);
                    row.append(box);
                }
                const num = document.createElement('span');
                num.className = 'fs-line';
                num.textContent = m.line === null ? '' : String(m.line + 1);
                const text = document.createElement('span');
                const lead = m.text.slice(0, m.column).trimStart();
                const hit = document.createElement('mark');
                hit.textContent = m.text.slice(m.column, m.column + m.length);
                text.append(lead, hit);
                if (m.replacement !== null) {
                    const to = document.createElement('ins');
                    to.textContent = m.replacement;
                    text.appendChild(to);
                }
                text.append(m.text.slice(m.column + m.length));
                row.append(num, text);
                // a row can name its own file (duplicates, a stack trace); one with no file of the project is only text
                const target = m.path === undefined ? file.path : m.path;
                if (file.library)
                    row.onclick = () => void showLibraryFile(file.path, m.line, m.column);
                else if (target)
                    row.onclick = () => void deps.openFile(target, new monaco.Range(m.line + 1, m.column + 1, (m.endLine ?? m.line) + 1, m.endLine === undefined ? m.column + 1 + m.length : m.endColumn + 1));
                else
                    row.classList.add('fs-plain');
                body.appendChild(row);
            }
            group.append(head, body);
            results.appendChild(group);
        }
    }
    async function replaceEverywhere() {
        if (!last || !withWhat())
            return;
        // search again so that the matches carry exactly the replacement that is typed now
        await search();
        if (!last)
            return;
        const changes = {};
        let skipped = 0, unchosen = 0;
        for (const file of last.files) {
            if (file.library || !deps.isEditable(file.path)) {
                skipped += file.matches.length;
                continue;
            }
            const chosen = file.matches.filter((m) => !skippedMatches.has(matchKey(file.path, m)));
            unchosen += file.matches.length - chosen.length;
            if (chosen.length === 0)
                continue;
            changes[deps.uriFor(file.path)] = chosen.map((m) => ({
                range: { start: { line: m.line, character: m.column }, end: m.endLine === undefined ? { line: m.line, character: m.column + m.length } : { line: m.endLine, character: m.endColumn } },
                newText: m.replacement ?? '',
            }));
        }
        if (Object.keys(changes).length === 0)
            return;
        const done = await deps.applyEdits(`Replace "${what()}" with "${withWhat()}"`, changes, { alwaysPreview: true });
        if (done) {
            summary.textContent = `Replaced${unchosen ? `, except the ${unchosen} left out` : ''}${skipped ? ` (${skipped} matches in files that cannot be edited here were left alone)` : ''}. The files are changed in the editor; save them with Ctrl+S.`;
            results.replaceChildren();
            skippedMatches.clear();
            last = null;
            replaceAll.disabled = true;
        }
    }
    /** Results that are not a text search (Find Usages of a file): shown as a search's are, under their own summary; they cannot be replaced. */
    function showFound(title, files) {
        showTab('search');
        last = { total: files.reduce((n, f) => n + f.matches.length, 0), files, filesSearched: 0, truncated: false, replaceable: false };
        render();
        summary.className = '';
        summary.textContent = title;
        replaceAll.disabled = true;
    }
    let timer;
    const later = () => { clearTimeout(timer); timer = window.setTimeout(() => void search(), 300); };
    query.oninput = later;
    replace.oninput = () => { if (last)
        later(); };
    include.onchange = exclude.onchange = () => void search();
    for (const box of [caseBox, wordBox, regexBox, libsBox])
        box.onchange = () => void search();
    /** A library's file, read only, in a window over the page at the line of a result (the editor opens only the project's files). */
    async function showLibraryFile(path, line, column) {
        if (!(await showLibrary(path, line, column, editor)))
            summary.textContent = `${path} cannot be read here.`;
    }
    const structural_ = () => {
        const on = structBox.checked;
        for (const box of [caseBox, wordBox, regexBox])
            box.disabled = on;
        query.style.display = replace.style.display = on ? 'none' : '';
        spattern.style.display = sreplace.style.display = on ? '' : 'none';
    };
    structBox.onchange = () => { structural_(); void search(); };
    // patterns to start from, as PyCharm's templates are
    const PATTERNS = [
        ['Comparisons with None by ==', '$x$ == None', '$x$ is None'],
        ['Empty test by len', 'len($x$) == 0', 'not $x$'],
        ['print calls', 'print($args*$)', ''],
        ['Test functions', 'def $f:^test_$($p*$):\n    $body*$', ''],
        ['Bare except', 'try:\n    $body*$\nexcept:\n    $handler*$', ''],
        ['open() without with', '$f$ = open($args*$)', ''],
        ['dict.keys() in a loop', 'for $k$ in $d$.keys():\n    $body*$', 'for $k$ in $d$:\n    $body*$'],
        ['isinstance with a tuple', 'isinstance($x$, ($types*$))', ''],
        ['Assignments to self', 'self.$a$ = $v$', ''],
    ];
    $('fs-patterns').onclick = (e) => openMenu(e.currentTarget, PATTERNS.map(([label, pattern, replacement]) => ({
        label,
        run: () => {
            structBox.checked = true;
            structural_();
            spattern.value = pattern;
            sreplace.value = replacement;
            spattern.focus();
            void search();
        },
    })));
    /** Code written more than once: each group is a heading with the places under it. */
    async function duplicates() {
        showTab('search');
        summary.className = '';
        summary.textContent = 'Looking for duplicated code…';
        results.replaceChildren();
        let groups;
        try {
            groups = await lsp.request('pymagic/duplicates', { minLines: 6 });
        }
        catch (e) {
            summary.textContent = e.message ?? 'The search failed.';
            return;
        }
        const files = groups.map((g) => ({
            path: `${g.lines} lines in ${g.places.length} places: ${g.preview}`,
            matches: g.places.map((p) => ({ line: p.range.start.line, column: 0, length: 0, text: `${relOf(p.uri)}  lines ${p.range.start.line + 1}-${p.range.end.line + 1}`, replacement: null, path: relOf(p.uri), endLine: p.range.end.line, endColumn: p.range.end.character })),
        }));
        showFound(groups.length === 0 ? 'No code of 6 lines or more is written twice in the project' : `${groups.length} fragment${groups.length === 1 ? ' is' : 's are'} written more than once`, files);
    }
    /** A stack trace pasted from elsewhere: shown with its lines, those that name a project file open it. */
    function analyzeTrace() {
        const shade = document.createElement('div');
        shade.className = 'settings-shade';
        const box = document.createElement('div');
        box.className = 'trace-box';
        const head = document.createElement('h3');
        head.textContent = 'Analyze a stack trace';
        const area = document.createElement('textarea');
        area.placeholder = 'Paste a traceback, a pytest report or a log here';
        area.spellcheck = false;
        const buttons = document.createElement('div');
        buttons.className = 'set-buttons';
        const cancel = document.createElement('button');
        cancel.textContent = 'Cancel';
        const ok = document.createElement('button');
        ok.className = 'primary';
        ok.textContent = 'Show';
        buttons.append(cancel, ok);
        box.append(head, area, buttons);
        shade.append(box);
        document.body.append(shade);
        navigator.clipboard?.readText?.().then((t) => { if (!area.value && /line \d+|\.py:\d+/.test(t)) area.value = t; }).catch(() => { /* no clipboard */ });
        area.focus();
        const close = () => shade.remove();
        cancel.onclick = close;
        shade.onmousedown = (e) => { if (e.target === shade) close(); };
        area.onkeydown = (e) => { if (e.key === 'Escape') close(); };
        ok.onclick = async () => {
            const all = await fetch('/api/files').then((r) => r.json()).catch(() => []);
            const lines = readTrace(area.value, all.map((f) => (typeof f === 'string' ? f : f.path)).filter(Boolean));
            close();
            const linked = lines.filter((l) => l.rel).length, named = lines.filter((l) => l.path).length;
            showFound(`Stack trace: ${linked} of ${named} places are in the project`, [{
                path: 'Stack trace',
                matches: lines.filter((l) => l.text.trim()).map((l) => ({ line: l.rel ? l.line - 1 : null, column: 0, length: 0, text: l.text, replacement: null, path: l.rel })),
            }]);
        };
    }
    for (const box of [spattern, sreplace]) {
        box.onkeydown = (e) => {
            if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                e.preventDefault();
                void search();
            }
            else if (e.key === 'Escape')
                editor.focus();
        };
        box.oninput = () => { if (box === sreplace && last) later(); };
    }
    $('fs-dups').onclick = () => void duplicates();
    $('fs-trace').onclick = () => analyzeTrace();
    for (const input of [query, replace, include, exclude]) {
        input.onkeydown = (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                clearTimeout(timer);
                void search();
            }
            else if (e.key === 'Escape')
                editor.focus();
        };
    }
    $('fs-go').onclick = () => void search();
    replaceAll.onclick = () => void replaceEverywhere();
    $('t-search').onclick = () => { showTab('search'); query.focus(); };
    /** Shows Find in Files; `scope` comes from the menus of the project tree: { include: 'folder/**', replace: true } searches that folder and goes to Replace. */
    function open(prefill, scope) {
        showTab('search');
        if (scope) include.value = scope.include ?? '';
        const sel = editor.getModel() && editor.getSelection() && !editor.getSelection().isEmpty()
            ? editor.getModel().getValueInRange(editor.getSelection()) : '';
        const text = prefill ?? (sel && !sel.includes('\n') ? sel : '');
        if (text) {
            query.value = text;
            void search();
        }
        if (scope?.replace && query.value) { replace.focus(); replace.select(); return; }
        query.focus();
        query.select();
    }
    // in the editors and outside them (commands.js answers there too)
    defineCommand({ id: 'pymagic.search.inFiles', label: 'Find in files', group: 'Navigate', keys: ['Ctrl+Shift+F'], everywhere: true, run: () => open() });
    defineCommand({ id: 'pymagic.search.symbol', label: 'Go to symbol', group: 'Navigate', keys: ['Ctrl+Alt+O'], everywhere: true, run: () => void symbolPicker() });
    // ---------------------------------------------------------------- go to symbol in the workspace
    function symbolPicker() {
        return new Promise((resolve) => {
            const overlay = document.createElement('div');
            overlay.id = 'sigdialog';
            const box = document.createElement('div');
            box.className = 'box';
            const title = document.createElement('h2');
            title.textContent = 'Go to symbol';
            const input = document.createElement('input');
            input.placeholder = 'function, class, method or variable name';
            input.spellcheck = false;
            const list = document.createElement('div');
            list.className = 'picklist';
            // the kinds to show: a row of choices, the last one kept for next time
            let kind = 'All';
            try {
                kind = localStorage.getItem('pymagic.symbolKind') ?? 'All';
            }
            catch { /* storage unavailable */ }
            const kinds = document.createElement('div');
            kinds.className = 'fs-kinds';
            for (const [name] of SYMBOL_FILTERS) {
                const chip = document.createElement('button');
                chip.textContent = name;
                chip.className = name === kind ? 'on' : '';
                chip.onmousedown = (e) => e.preventDefault();   // the input keeps the focus
                chip.onclick = () => {
                    kind = name;
                    for (const c of kinds.children)
                        c.className = c.textContent === name ? 'on' : '';
                    try {
                        localStorage.setItem('pymagic.symbolKind', name);
                    }
                    catch { /* storage unavailable */ }
                    void refresh();
                };
                kinds.appendChild(chip);
            }
            box.append(title, input, kinds, list);
            overlay.appendChild(box);
            let shown = [];
            let index = 0;
            let ticket = 0;
            const finish = (target) => {
                overlay.remove();
                if (target) {
                    const rel = decodeURIComponent(target.location.uri).split('/').slice(-1)[0];
                    const r = target.location.range;
                    void deps.openFile(relOf(target.location.uri) ?? rel, new monaco.Range(r.start.line + 1, r.start.character + 1, r.end.line + 1, r.end.character + 1));
                }
                else
                    editor.focus();
                resolve();
            };
            function draw() {
                list.replaceChildren();
                if (shown.length === 0) {
                    const none = document.createElement('div');
                    none.className = 'dim';
                    none.textContent = 'No symbol matches.';
                    list.appendChild(none);
                }
                shown.forEach((s, i) => {
                    const row = document.createElement('div');
                    row.className = i === index ? 'pick sel' : 'pick';
                    const name = document.createElement('span');
                    name.textContent = s.name;
                    const detail = document.createElement('span');
                    detail.className = 'pick-detail';
                    detail.textContent = `${KINDS[s.kind] ?? ''}  ${s.containerName ?? ''}`;
                    row.append(name, detail);
                    row.onclick = () => finish(s);
                    list.appendChild(row);
                });
                list.querySelector('.sel')?.scrollIntoView({ block: 'nearest' });
            }
            async function refresh() {
                const mine = ++ticket;
                const found = (await lsp.request('workspace/symbol', { query: input.value })) ?? [];
                if (mine !== ticket)
                    return;
                shown = filterSymbols(found, kind).slice(0, 100);
                index = 0;
                draw();
            }
            input.oninput = () => void refresh();
            overlay.onkeydown = (e) => {
                if (e.key === 'Escape') {
                    e.preventDefault();
                    finish();
                }
                else if (e.key === 'ArrowDown') {
                    e.preventDefault();
                    index = Math.min(index + 1, shown.length - 1);
                    draw();
                }
                else if (e.key === 'ArrowUp') {
                    e.preventDefault();
                    index = Math.max(index - 1, 0);
                    draw();
                }
                else if (e.key === 'Enter') {
                    e.preventDefault();
                    if (shown[index])
                        finish(shown[index]);
                }
            };
            overlay.onmousedown = (e) => { if (e.target === overlay)
                finish(); };
            document.body.appendChild(overlay);
            input.focus();
            void refresh();
        });
    }
    function relOf(uri) {
        const root = deps.uriFor('');
        return uri.toLowerCase().startsWith(root.toLowerCase()) ? decodeURIComponent(uri.slice(root.length)) : undefined;
    }
    // ---------------------------------------------------------------- search everywhere: the files of the project and its symbols
    function anythingPicker() {
        return new Promise((resolve) => {
            const overlay = document.createElement('div');
            overlay.id = 'sigdialog';
            const box = document.createElement('div');
            box.className = 'box';
            const title = document.createElement('h2');
            title.textContent = 'Search everywhere';
            const input = document.createElement('input');
            input.placeholder = 'a file, function, class, method or variable name';
            input.spellcheck = false;
            const list = document.createElement('div');
            list.className = 'picklist';
            box.append(title, input, list);
            overlay.appendChild(box);
            let files = [];
            let symbols = [];
            let shown = [];
            let index = 0;
            let ticket = 0;
            const finish = (target) => {
                overlay.remove();
                if (target?.file)
                    void deps.openFile(target.file);
                else if (target?.symbol) {
                    const r = target.symbol.location.range;
                    void deps.openFile(relOf(target.symbol.location.uri) ?? decodeURIComponent(target.symbol.location.uri).split('/').slice(-1)[0], new monaco.Range(r.start.line + 1, r.start.character + 1, r.end.line + 1, r.end.character + 1));
                }
                else
                    editor.focus();
                resolve();
            };
            function matches() {
                const q = input.value.trim();
                const byFile = files.map((f) => ({ file: f, score: fileScore(f, q) })).filter((x) => x.score >= 0)
                    .sort((a, b) => a.score - b.score || a.file.length - b.file.length).slice(0, 30);
                return [...byFile.map((x) => ({ file: x.file })), ...symbols.slice(0, 40).map((s) => ({ symbol: s }))];
            }
            function draw() {
                list.replaceChildren();
                if (shown.length === 0) {
                    const none = document.createElement('div');
                    none.className = 'dim';
                    none.textContent = 'Nothing matches.';
                    list.appendChild(none);
                }
                let heading = '';
                shown.forEach((entry, i) => {
                    const group = entry.file ? 'Files' : 'Symbols';
                    if (group !== heading) {
                        heading = group;
                        const head = document.createElement('div');
                        head.className = 'pick-head';
                        head.textContent = group;
                        list.appendChild(head);
                    }
                    const row = document.createElement('div');
                    row.className = i === index ? 'pick sel' : 'pick';
                    const name = document.createElement('span');
                    const detail = document.createElement('span');
                    detail.className = 'pick-detail';
                    if (entry.file) {
                        name.textContent = entry.file.slice(entry.file.lastIndexOf('/') + 1);
                        detail.textContent = entry.file.includes('/') ? entry.file.slice(0, entry.file.lastIndexOf('/')) : '';
                    }
                    else {
                        name.textContent = entry.symbol.name;
                        detail.textContent = `${KINDS[entry.symbol.kind] ?? ''}  ${entry.symbol.containerName ?? ''}`;
                    }
                    row.append(name, detail);
                    row.onclick = () => finish(entry);
                    list.appendChild(row);
                });
                list.querySelector('.sel')?.scrollIntoView({ block: 'nearest' });
            }
            async function refresh() {
                const mine = ++ticket;
                shown = matches();
                index = 0;
                draw();
                const q = input.value.trim();
                const found = q ? (await lsp.request('workspace/symbol', { query: q })) ?? [] : [];
                if (mine !== ticket)
                    return;
                symbols = found;
                shown = matches();
                draw();
            }
            input.oninput = () => void refresh();
            overlay.onkeydown = (e) => {
                if (e.key === 'Escape') {
                    e.preventDefault();
                    finish();
                }
                else if (e.key === 'ArrowDown') {
                    e.preventDefault();
                    index = Math.min(index + 1, shown.length - 1);
                    draw();
                }
                else if (e.key === 'ArrowUp') {
                    e.preventDefault();
                    index = Math.max(index - 1, 0);
                    draw();
                }
                else if (e.key === 'Enter') {
                    e.preventDefault();
                    if (shown[index])
                        finish(shown[index]);
                }
            };
            overlay.onmousedown = (e) => { if (e.target === overlay)
                finish(); };
            document.body.appendChild(overlay);
            input.focus();
            void fetch('/api/files').then((r) => r.json()).then((all) => {
                files = all.map((f) => (typeof f === 'string' ? f : f.path)).filter(Boolean);
                void refresh();
            });
        });
    }
    return { open, symbolPicker, anythingPicker, showFound, duplicates, analyzeTrace, structural: () => { showTab('search'); structBox.checked = true; structural_(); spattern.focus(); } };
}

// The MCP server's tools, carried out in the project's page (desktop/mcp-main.js sends each call here: window.pymagic.mcp.call(name, args)).
// They use what the editor uses: the open files' texts, the project's files and search (/api/tree, /api/search), the language server (problems,
// hover, rename, formatting), the run configurations and the Run panel. Each answers { text, isError? }.
import { projectPath, truncateLines, replaceText, filesByName, filesByGlob, treeText, findSymbol, problemsText, matchesText, includeOf, hoverText, relativeToRoot } from './mcp-model.js';

/**
 * deps: { monaco, lsp, root, openModel(rel), ensureModel(rel), uriOf(model), relOfUri(uri), modelForUri(uri), openFile(rel, range), saveFile(rel),
 *         applyTextEdits(model, edits), tidyFile(rel, kind), tabs(), runConfigs(), runHooks() }
 */
export function setupMcpTools(deps) {
    const fileUrl = (rel) => '/api/file?path=' + encodeURIComponent(rel);
    const rel = (p, opts) => projectPath(relativeToRoot(p, deps.root), opts);
    const say = (text) => ({ text });
    const json = (value) => ({ text: JSON.stringify(value, null, 2) });

    async function textOf(r) {
        const m = deps.openModel(r);
        if (m) return m.getValue();
        const res = await fetch(fileUrl(r));
        if (res.status === 404) throw new Error(`${r} is not a file of the project`);
        if (res.status === 413) throw new Error(`${r} is too large to read here`);
        if (res.status === 415) throw new Error(`${r} is a binary file`);
        if (!res.ok) throw new Error(`${r} cannot be read (${res.status})`);
        return res.text();
    }
    async function exists(r) {
        if (deps.openModel(r)) return true;
        const res = await fetch(fileUrl(r)).catch(() => null);
        return !!res && res.status !== 404;
    }
    /** Writes a file: an open one through its model (one undo step), then saved. */
    async function write(r, text) {
        const m = deps.openModel(r);
        if (m) {
            if (m.getValue() !== text) m.pushEditOperations([], [{ range: m.getFullModelRange(), text }], () => null);
            await deps.saveFile(r);
            return;
        }
        const res = await fetch(fileUrl(r), { method: 'PUT', body: text });
        if (!res.ok) throw new Error(`${r} cannot be written: ${await res.text().catch(() => res.status)}`);
        window.dispatchEvent(new CustomEvent('pymagic:diskChanged', { detail: { paths: [r] } }));
    }
    let tree = null, treeAt = 0;
    async function projectTree() {
        // read once for the calls that come together (an agent often lists, then searches)
        if (!tree || Date.now() - treeAt > 2000) {
            tree = await (await fetch('/api/tree')).json();
            treeAt = Date.now();
        }
        return tree;
    }
    const filesOf = async () => (await projectTree()).files.map((f) => f.path);
    const unsavedTexts = () => {
        const out = {};
        for (const m of deps.monaco.editor.getModels()) {
            const r = deps.relOfUri(deps.uriOf(m) ?? '');
            if (r && !r.startsWith('.scratches/')) out[r] = m.getValue();
        }
        return out;
    };
    const isPython = (r) => /\.pyi?$/i.test(r);
    async function pythonModel(r) {
        if (!isPython(r)) throw new Error(`${r} is not a Python file: this tool works on .py files`);
        if (!(await exists(r))) throw new Error(`${r} is not a file of the project`);
        const m = await deps.ensureModel(r);
        if (!m) throw new Error(`${r} cannot be opened`);
        const uri = deps.uriOf(m);
        deps.lsp.notify('textDocument/didChange', { textDocument: { uri, version: m.getVersionId() }, contentChanges: [{ text: m.getValue() }] });
        return { m, uri };
    }

    async function search(args, regex) {
        const query = regex ? args.regexPattern : args.searchText;
        const res = await fetch('/api/search', {
            method: 'POST', headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ query, regex, caseSensitive: args.caseSensitive ?? true, include: includeOf(args.directoryToSearch, args.fileMask) || undefined, unsaved: unsavedTexts() }),
        });
        const body = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(body.error ?? 'the search failed');
        return say(matchesText(body, args.maxUsageCount ?? 1000));
    }

    const tools = {
        async get_file_text_by_path(a) {
            const r = rel(a.pathInProject);
            return say(truncateLines(await textOf(r), a.maxLinesCount ?? 5000, a.truncateMode ?? 'END').text);
        },
        async replace_text_in_file(a) {
            const r = rel(a.pathInProject);
            const before = await textOf(r);
            const { text, count } = replaceText(before, a.oldText, a.newText, { all: a.replaceAll ?? true, caseSensitive: a.caseSensitive ?? true });
            if (!count) return { text: `The text was not found in ${r}; nothing changed.`, isError: true };
            await write(r, text);
            return say(`Replaced ${count} occurrence${count === 1 ? '' : 's'} in ${r}.`);
        },
        async create_new_file(a) {
            const r = rel(a.pathInProject);
            if (!a.overwrite && (await exists(r))) return { text: `${r} is there already (overwrite: true writes over it).`, isError: true };
            await write(r, a.text ?? '');
            tree = null;
            return say(`Made ${r}.`);
        },
        async find_files_by_name_keyword(a) {
            const { files, more } = filesByName(await filesOf(), a.nameKeyword, a.fileCountLimit ?? 1000);
            return say(files.length ? files.join('\n') + (more ? `\n(${more} more)` : '') : 'No file has that in its name.');
        },
        async find_files_by_glob(a) {
            const under = a.subDirectoryRelativePath ? rel(a.subDirectoryRelativePath, { allowRoot: true }) : '';
            const { files, more } = filesByGlob(await filesOf(), a.globPattern, under, a.fileCountLimit ?? 1000);
            return say(files.length ? files.join('\n') + (more ? `\n(${more} more)` : '') : 'No file matches.');
        },
        search_in_files_by_text: (a) => search(a, false),
        search_in_files_by_regex: (a) => search(a, true),
        async list_directory_tree(a) {
            const t = await projectTree();
            return say(treeText(t.dirs, t.files.map((f) => f.path), a.directoryPath ? rel(a.directoryPath, { allowRoot: true }) : '', a.maxDepth ?? 5));
        },
        async get_file_problems(a) {
            const r = rel(a.filePath);
            if (isPython(r)) {
                const { uri } = await pythonModel(r);
                const found = await deps.lsp.request('pymagic/fileProblems', { textDocument: { uri } });
                return say(problemsText(r, (found ?? []).map((d) => ({ line: d.range.start.line + 1, column: d.range.start.character + 1, severity: d.severity, message: d.message, code: d.code })), a.errorsOnly));
            }
            const m = deps.openModel(r);
            if (!m) return { text: `${r} is not open in the editor: its problems are known only while it is (open_file_in_editor opens it; Python files are checked always).`, isError: true };
            const S = deps.monaco.MarkerSeverity;
            const level = (s) => s === S.Error ? 1 : s === S.Warning ? 2 : s === S.Info ? 3 : 4;
            const markers = deps.monaco.editor.getModelMarkers({ resource: m.uri });
            return say(problemsText(r, markers.map((k) => ({ line: k.startLineNumber, column: k.startColumn, severity: level(k.severity), message: k.message, code: typeof k.code === 'object' ? k.code?.value : k.code })), a.errorsOnly));
        },
        async get_project_problems(a) {
            const found = await deps.lsp.request('pymagic/problems', { leastSevere: a.errorsOnly ? 1 : 3 }) ?? [];
            const max = a.maxCount ?? 500;
            const lines = found.slice(0, max).map((p) => `${deps.relOfUri(p.uri) ?? p.uri}:${p.line + 1}:${p.column + 1} ${({ 1: 'error', 2: 'warning', 3: 'weak warning', 4: 'hint' })[p.severity] ?? 'problem'}: ${p.message}${p.code ? ` [${p.code}]` : ''}`);
            return say(lines.length ? lines.join('\n') + (found.length > max ? `\n(${found.length - max} more)` : '') : 'No problems in the project\'s Python files.');
        },
        async get_symbol_info(a) {
            const r = rel(a.filePath);
            const { uri } = await pythonModel(r);
            const position = { line: a.line - 1, character: a.column - 1 };
            const [hover, def] = await Promise.all([
                deps.lsp.request('textDocument/hover', { textDocument: { uri }, position }).catch(() => null),
                deps.lsp.request('textDocument/definition', { textDocument: { uri }, position }).catch(() => null),
            ]);
            const where = (Array.isArray(def) ? def : def ? [def] : []).map((d) => {
                const u = d.uri ?? d.targetUri, range = d.range ?? d.targetSelectionRange;
                return `${deps.relOfUri(u) ?? decodeURIComponent(String(u).replace(/^file:\/\/\//, ''))}:${range.start.line + 1}:${range.start.character + 1}`;
            });
            const text = hoverText(hover);
            if (!text && !where.length) return say(`Nothing is known at ${r}:${a.line}:${a.column}.`);
            return say([text, where.length ? `Defined at: ${where.join(', ')}` : ''].filter(Boolean).join('\n\n'));
        },
        async rename_refactoring(a) {
            const r = rel(a.pathInProject);
            const { uri } = await pythonModel(r);
            const symbols = await deps.lsp.request('textDocument/documentSymbol', { textDocument: { uri } });
            const s = findSymbol(symbols, a.symbolName);
            if (!s) return { text: `${a.symbolName} is not defined in ${r}.`, isError: true };
            const position = (s.selectionRange ?? s.range).start;
            const edit = await deps.lsp.request('textDocument/rename', { textDocument: { uri }, position, newName: a.newName });
            const changed = [];
            for (const [target, list] of Object.entries(edit?.changes ?? {})) {
                const m = await deps.modelForUri(target);
                const tr = deps.relOfUri(target);
                if (!m || !tr) continue;
                deps.applyTextEdits(m, list);
                await deps.saveFile(tr);
                changed.push(`${tr} (${list.length})`);
            }
            return say(changed.length ? `Renamed ${a.symbolName} to ${a.newName} in: ${changed.join(', ')}.` : 'Nothing was renamed.');
        },
        async reformat_file(a) {
            const r = rel(a.path);
            if (!(await exists(r))) return { text: `${r} is not a file of the project`, isError: true };
            if (!/\.(pyi?|php)$/i.test(r)) return { text: `${r}: PyMagic's formatter formats Python files (and PHP ones with Pint or PHP-CS-Fixer).`, isError: true };
            const m = await deps.ensureModel(r);
            const before = m?.getValue();
            const done = await deps.tidyFile(r, 'format');
            if (done && deps.openModel(r)) await deps.saveFile(r);
            return say(done ? `Formatted ${r}.` : before == null ? `${r} cannot be formatted.` : `${r} is formatted already.`);
        },
        async open_file_in_editor(a) {
            const r = rel(a.filePath);
            if (!(await exists(r))) return { text: `${r} is not a file of the project`, isError: true };
            const range = a.line ? new deps.monaco.Range(a.line, a.column ?? 1, a.line, a.column ?? 1) : undefined;
            await deps.openFile(r, range);
            return say(`Opened ${r}${a.line ? ` at line ${a.line}` : ''}.`);
        },
        async get_all_open_file_paths() {
            const t = deps.tabs();
            return json({ activeFile: t?.active() || null, openFiles: t?.list() ?? [] });
        },
        async get_run_configurations() {
            const rc = deps.runConfigs();
            if (!rc) return say('The project has no run configurations.');
            const list = rc.names().map((n) => rc.byName(n)).filter(Boolean).map((c) => ({
                name: c.name, kind: c.kind, ...(c.script ? { script: c.script } : {}), ...(c.module ? { module: c.module } : {}), ...(c.args ? { parameters: c.args } : {}),
                ...(c.cwd ? { workingDirectory: c.cwd } : {}), ...(c.app ? { app: c.app } : {}),
            }));
            return json({ selected: rc.selectedName(), configurations: list });
        },
        async execute_run_configuration(a) {
            const rc = deps.runConfigs(), run = deps.runHooks();
            const c = rc?.byName(a.configurationName);
            if (!c) return { text: `There is no run configuration ${a.configurationName}; get_run_configurations lists them.`, isError: true };
            if (!run?.runForAgent) return { text: 'Running is not available in this window.', isError: true };
            const r = await run.runForAgent(c, a.timeout ?? 120000);
            if (!r.started) return { text: r.note ?? 'It did not start.', isError: true };
            const { text } = truncateLines(r.output.replace(/\s+$/, ''), a.maxLinesCount ?? 1000, 'START');
            const head = r.timedOut ? `Still running after ${a.timeout ?? 120000} ms (it goes on; the Run panel shows it).` : `Exit code: ${r.exitCode ?? 'unknown'}`;
            return { text: `${head}\n${text}`, isError: r.timedOut || (r.exitCode ?? 0) !== 0 };
        },
        async get_project_dependencies() {
            const res = await fetch('/api/packages');
            const body = await res.json().catch(() => ({}));
            if (body.error && !(body.packages ?? []).length) return { text: `The packages are not known: ${body.error}`, isError: true };
            return say((body.packages ?? []).map((p) => `${p.name}==${p.version}`).join('\n') || 'No packages are installed.');
        },
    };

    return {
        names: Object.keys(tools),
        async call(name, args) {
            const fn = tools[name];
            if (!fn) return { text: `The page has no tool ${name}.`, isError: true };
            try { return await fn(args ?? {}); }
            catch (e) { return { text: String(e?.message ?? e), isError: true }; }
        },
    };
}

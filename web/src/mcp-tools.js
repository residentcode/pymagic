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
        if (!isPython(r)) throw new Error(`${r} is not a Python file: this works on .py files (and, for symbols and renaming, JavaScript, TypeScript and PHP ones)`);
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
            return more.problemsOfOther(r, a.errorsOnly);
        },
        async get_project_problems(a) {
            const found = await deps.lsp.request('pymagic/problems', { leastSevere: a.errorsOnly ? 1 : 3 }) ?? [];
            const max = a.maxCount ?? 500;
            const lines = found.slice(0, max).map((p) => `${deps.relOfUri(p.uri) ?? p.uri}:${p.line + 1}:${p.column + 1} ${({ 1: 'error', 2: 'warning', 3: 'weak warning', 4: 'hint' })[p.severity] ?? 'problem'}: ${p.message}${p.code ? ` [${p.code}]` : ''}`);
            return say(lines.length ? lines.join('\n') + (found.length > max ? `\n(${found.length - max} more)` : '') : 'No problems in the project\'s Python files.');
        },
        async get_symbol_info(a) {
            const r = rel(a.filePath);
            if (isScript(r)) return more.symbolOfScript(r, a);
            if (isPhp(r)) return more.symbolOfPhp(r, a);
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
            if (isScript(r)) return more.renameInScript(r, a);
            if (isPhp(r)) return more.renameInPhp(r, a);
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


    // ---- other languages than Python: JavaScript and TypeScript through the editor's own service, PHP through its language server

    const isScript = (r) => /\.(tsx?|mts|cts|jsx?|mjs|cjs)$/i.test(r);
    const isPhp = (r) => /\.php$/i.test(r);
    /** A JavaScript or TypeScript file with the service that checks it: { m, worker, name }. */
    async function scriptService(r) {
        if (!(await exists(r))) throw new Error(`${r} is not a file of the project`);
        const m = await deps.ensureModel(r);
        if (!m) throw new Error(`${r} cannot be opened`);
        const ts = deps.monaco.languages.typescript;
        const worker = await (await (m.getLanguageId() === 'typescript' ? ts.getTypeScriptWorker() : ts.getJavaScriptWorker()))(m.uri);
        return { m, worker, name: m.uri.toString() };
    }
    const said = (parts) => (parts ?? []).map((p) => p.text).join('');
    /** A file the service names (pymagic:/src/a.ts) as a project path, and a place in it as line:column. */
    const scriptPath = (fileName) => { try { return decodeURIComponent(deps.monaco.Uri.parse(fileName).path.replace(/^\//, '')); } catch { return fileName; } };
    function scriptPlace(fileName, start) {
        const ts = deps.monaco.languages.typescript;
        const text = deps.monaco.editor.getModel(deps.monaco.Uri.parse(fileName))?.getValue() ?? ts.typescriptDefaults.getExtraLibs()[fileName]?.content ?? ts.javascriptDefaults.getExtraLibs()[fileName]?.content;
        if (text === undefined) return scriptPath(fileName);
        const before = text.slice(0, start).split('\n');
        return `${scriptPath(fileName)}:${before.length}:${before[before.length - 1].length + 1}`;
    }
    /** A symbol of a file's outline by its name (Class.method for a member): its name's place, or null. */
    function scriptSymbol(tree, wanted) {
        const path = wanted.split('.');
        const find = (nodes, at) => {
            for (const n of nodes ?? []) {
                if (n.text === path[at]) {
                    if (at === path.length - 1) return n;
                    const deeper = find(n.childItems, at + 1);
                    if (deeper) return deeper;
                }
                else if (at === 0) { const deeper = find(n.childItems, 0); if (deeper) return deeper; }
            }
            return null;
        };
        return find(tree?.childItems, 0);
    }
    /** A file's findings once the editor's checks have settled: the same twice, a little apart (a file just opened is checked a moment later). */
    async function settledMarkers(m, ms = 9000) {
        const read = () => deps.monaco.editor.getModelMarkers({ resource: m.uri });
        const key = (list) => JSON.stringify(list.map((k) => [k.startLineNumber, k.startColumn, k.message, k.owner]));
        const end = Date.now() + ms;
        let last = null, same = 0;
        await new Promise((r) => setTimeout(r, 1200));
        while (Date.now() < end) {
            const now = key(read());
            same = now === last ? same + 1 : 0;
            if (same >= 2) break;
            last = now;
            await new Promise((r) => setTimeout(r, 500));
        }
        return read();
    }
    /** The model of a file with the language server's address (Python and PHP), its text told to the server. */
    async function serverModel(r) {
        if (!(await exists(r))) throw new Error(`${r} is not a file of the project`);
        const m = await deps.ensureModel(r);
        const uri = m && deps.uriOf(m);
        if (!uri) throw new Error(`${r} cannot be opened`);
        deps.lsp.notify('textDocument/didChange', { textDocument: { uri, version: m.getVersionId() }, contentChanges: [{ text: m.getValue() }] });
        return { m, uri };
    }
    const placeOf = (d) => {
        const u = d.uri ?? d.targetUri, range = d.range ?? d.targetSelectionRange;
        return `${deps.relOfUri(u) ?? decodeURIComponent(String(u).replace(/^file:\/\/\//, ''))}:${range.start.line + 1}:${range.start.character + 1}`;
    };

    const more = {
        /** get_file_problems of a file that is not Python: opened for the editor's checks when it is not open. */
        async problemsOfOther(r, errorsOnly) {
            if (!(await exists(r))) return { text: `${r} is not a file of the project`, isError: true };
            const m = deps.openModel(r) ?? await deps.ensureModel(r);
            if (!m) return { text: `${r} cannot be opened`, isError: true };
            const S = deps.monaco.MarkerSeverity;
            const level = (s) => s === S.Error ? 1 : s === S.Warning ? 2 : s === S.Info ? 3 : 4;
            const markers = await settledMarkers(m);
            return say(problemsText(r, markers.map((k) => ({ line: k.startLineNumber, column: k.startColumn, severity: level(k.severity), message: k.message, code: typeof k.code === 'object' ? k.code?.value : k.code })), errorsOnly));
        },
        async symbolOfScript(r, a) {
            const { m, worker, name } = await scriptService(r);
            const offset = m.getOffsetAt({ lineNumber: a.line, column: a.column });
            const [info, defs] = await Promise.all([worker.getQuickInfoAtPosition(name, offset).catch(() => null), worker.getDefinitionAtPosition(name, offset).catch(() => null)]);
            const where = (defs ?? []).filter((d) => !/^lib\.[\w.]*d\.ts$/.test(d.fileName)).map((d) => scriptPlace(d.fileName, d.textSpan.start));
            if (!info && !where.length) return say(`Nothing is known at ${r}:${a.line}:${a.column}.`);
            return say([info ? said(info.displayParts) : '', info ? said(info.documentation) : '', where.length ? `Defined at: ${where.join(', ')}` : ''].filter(Boolean).join('\n\n'));
        },
        async symbolOfPhp(r, a) {
            const { uri } = await serverModel(r);
            const position = { line: a.line - 1, character: a.column - 1 };
            const [hover, def] = await Promise.all([
                deps.lsp.request('textDocument/hover', { textDocument: { uri }, position }).catch(() => null),
                deps.lsp.request('textDocument/definition', { textDocument: { uri }, position }).catch(() => null),
            ]);
            const where = (Array.isArray(def) ? def : def ? [def] : []).map(placeOf);
            const text = hoverText(hover);
            if (!text && !where.length) return say(`Nothing is known at ${r}:${a.line}:${a.column}.`);
            return say([text, where.length ? `Defined at: ${where.join(', ')}` : ''].filter(Boolean).join('\n\n'));
        },
        async renameInScript(r, a) {
            const { worker, name } = await scriptService(r);
            const s = scriptSymbol(await worker.getNavigationTree(name), a.symbolName);
            if (!s?.nameSpan) return { text: `${a.symbolName} is not defined in ${r}.`, isError: true };
            const can = await worker.getRenameInfo(name, s.nameSpan.start, { allowRenameOfImportPath: false });
            if (can && can.canRename === false) return { text: `${a.symbolName} cannot be renamed: ${can.localizedErrorMessage ?? 'the service says no'}.`, isError: true };
            const places = await worker.findRenameLocations(name, s.nameSpan.start, false, false, false) ?? [];
            const byFile = new Map();
            for (const p of places) {
                const file = scriptPath(p.fileName);
                if (/(^|\/)node_modules\//.test(file)) continue;
                if (!byFile.has(file)) byFile.set(file, []);
                byFile.get(file).push(p);
            }
            const changed = [];
            for (const [file, list] of byFile) {
                const m = deps.openModel(file) ?? await deps.ensureModel(file);
                if (!m) continue;
                const edits = list.map((p) => {
                    const from = m.getPositionAt(p.textSpan.start), to = m.getPositionAt(p.textSpan.start + p.textSpan.length);
                    return { range: new deps.monaco.Range(from.lineNumber, from.column, to.lineNumber, to.column), text: (p.prefixText ?? '') + a.newName + (p.suffixText ?? '') };
                });
                m.pushEditOperations([], edits, () => null);
                await deps.saveFile(file);
                changed.push(`${file} (${list.length})`);
            }
            return say(changed.length ? `Renamed ${a.symbolName} to ${a.newName} in: ${changed.join(', ')}.` : 'Nothing was renamed.');
        },
        async renameInPhp(r, a) {
            const { m, uri } = await serverModel(r);
            // the place of the name where it is declared: class Name, function name, $name, const NAME
            const last = a.symbolName.split(/::|\.|->/).pop().replace(/^\$/, '');
            const escaped = last.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
            const declared = new RegExp(`\\b(?:class|interface|trait|enum|function|const)\\s+(${escaped})\\b|\\$(${escaped})\\b`).exec(m.getValue());
            if (!declared) return { text: `${a.symbolName} is not defined in ${r}.`, isError: true };
            const at = m.getPositionAt(declared.index + declared[0].length - last.length);
            const edit = await deps.lsp.request('textDocument/rename', { textDocument: { uri }, position: { line: at.lineNumber - 1, character: at.column - 1 }, newName: a.newName });
            return applyWorkspaceEdit(edit, `Renamed ${a.symbolName} to ${a.newName} in: `, 'Nothing was renamed (PHP\'s language server, Phpactor, gave no change).');
        },
    };

    /** A language server's edit carried out: each file changed in its model and saved. */
    async function applyWorkspaceEdit(edit, done, none) {
        const changed = [];
        const all = { ...(edit?.changes ?? {}) };
        for (const d of edit?.documentChanges ?? []) if (d.textDocument?.uri && d.edits) all[d.textDocument.uri] = [...(all[d.textDocument.uri] ?? []), ...d.edits];
        for (const [target, list] of Object.entries(all)) {
            const m = await deps.modelForUri(target);
            const tr = deps.relOfUri(target);
            if (!m || !tr) continue;
            deps.applyTextEdits(m, list);
            await deps.saveFile(tr);
            changed.push(`${tr} (${list.length})`);
        }
        return say(changed.length ? done + changed.join(', ') + '.' : none);
    }

    // ---- quick fixes, version control, breakpoints

    /** The quick fixes of a Python file's line (or of every line with a finding): [{ line, title, edit }]. */
    async function quickFixes(r, line) {
        const { m, uri } = await pythonModel(r);
        let lines = [line];
        if (!line) {
            const found = await deps.lsp.request('pymagic/fileProblems', { textDocument: { uri } }) ?? [];
            lines = [...new Set(found.map((d) => d.range.start.line + 1))].slice(0, 200);
        }
        const out = [];
        for (const l of lines) {
            if (l < 1 || l > m.getLineCount()) continue;
            const range = { start: { line: l - 1, character: 0 }, end: { line: l - 1, character: m.getLineMaxColumn(l) - 1 } };
            const found = await deps.lsp.request('textDocument/codeAction', { textDocument: { uri }, range, context: { diagnostics: [] } }).catch(() => null) ?? [];
            for (const f of found) if (f.edit && !out.some((x) => x.line === l && x.title === f.title)) out.push({ line: l, title: f.title, edit: f.edit });
        }
        return out;
    }
    const STATUS = { M: 'modified', A: 'added', D: 'deleted', R: 'renamed', C: 'copied', U: 'conflict', '?': 'not tracked', '!': 'ignored', T: 'type changed' };

    Object.assign(tools, {
        async get_quick_fixes(a) {
            const r = rel(a.filePath);
            const fixes = await quickFixes(r, a.line);
            return say(fixes.length ? fixes.map((f) => `${r}:${f.line} ${f.title}`).join('\n') : a.line ? `No quick fix at ${r}:${a.line}.` : `No quick fix in ${r}.`);
        },
        async apply_quick_fix(a) {
            const r = rel(a.filePath);
            const fixes = await quickFixes(r, a.line);
            const wanted = String(a.title).trim().toLowerCase();
            const fix = fixes.find((f) => f.title.toLowerCase() === wanted) ?? fixes.find((f) => f.title.toLowerCase().includes(wanted));
            if (!fix) return { text: `No quick fix named "${a.title}" at ${r}:${a.line}${fixes.length ? ` (there: ${fixes.map((f) => f.title).join('; ')})` : ''}.`, isError: true };
            return applyWorkspaceEdit(fix.edit, `Applied "${fix.title}" in: `, 'The fix changed nothing.');
        },
        async get_project_vcs_status() {
            const s = await (await fetch('/api/git/status')).json().catch(() => null);
            if (!s?.available) return say(`The project is not under Git${s?.reason ? ` (${s.reason})` : ''}.`);
            const files = (s.files ?? []).map((f) => {
                const code = f.index && f.index !== ' ' && f.index !== '?' ? f.index : f.worktree;
                return `${f.path} — ${STATUS[code] ?? code}${f.index && f.index !== ' ' && f.index !== '?' ? ' (staged)' : ''}${f.originalPath ? `, was ${f.originalPath}` : ''}`;
            });
            const head = `Branch: ${s.detached ? 'detached HEAD' : s.branch ?? 'none yet'}${s.upstream ? `, tracking ${s.upstream} (${s.ahead} ahead, ${s.behind} behind)` : ''}`;
            return say([head, files.length ? files.join('\n') : 'No changes.'].join('\n'));
        },
        async find_commit_by_message(a) {
            const res = await fetch('/api/git/log?limit=' + Math.min(200, a.maxCount ?? 50) + '&text=' + encodeURIComponent(a.text));
            const body = await res.json().catch(() => null);
            if (!res.ok || !body) return { text: body?.error ?? 'The project is not under Git.', isError: true };
            const list = Array.isArray(body) ? body : body.commits ?? body.entries ?? [];
            return say(list.length ? list.map((c) => `${String(c.hash ?? c.id ?? '').slice(0, 10)} ${c.date ?? c.when ?? ''} ${c.author ?? c.authorName ?? ''}: ${c.subject ?? c.message ?? ''}`.replace(/\s+/g, ' ').trim()).join('\n') : 'No commit has that in its message.');
        },
        async toggle_debugger_breakpoint(a) {
            const r = rel(a.filePathInProject);
            const run = deps.runHooks();
            if (!run?.toggleBreakpoint) return { text: 'Breakpoints are not available in this window.', isError: true };
            if (!(await exists(r))) return { text: `${r} is not a file of the project`, isError: true };
            const m = deps.openModel(r) ?? await deps.ensureModel(r);
            if (!m || a.line < 1 || a.line > m.getLineCount()) return { text: `${r} has no line ${a.line}.`, isError: true };
            run.toggleBreakpoint(m, a.line);
            const on = run.breakpointsOf(m).some((b) => b.line === a.line);
            return say(`${on ? 'Set' : 'Removed'} the breakpoint at ${r}:${a.line}.`);
        },
        async get_debugger_breakpoints() {
            const run = deps.runHooks();
            if (!run?.breakpointsOf) return { text: 'Breakpoints are not available in this window.', isError: true };
            const out = [];
            for (const m of deps.monaco.editor.getModels()) {
                const r = deps.relOfUri(deps.uriOf(m) ?? '') ?? (m.uri.scheme === 'pymagic' ? decodeURIComponent(m.uri.path.replace(/^\//, '')) : null);
                if (!r) continue;
                for (const b of run.breakpointsOf(m)) out.push({ file: r, line: b.line, ...(b.condition ? { condition: b.condition } : {}), ...(b.enabled === false ? { enabled: false } : {}), ...(b.logMessage ? { logMessage: b.logMessage } : {}) });
            }
            return json(out);
        },
    });

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

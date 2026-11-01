// Markers on the files that are not Python while they are open (TOML, YAML, .env, Dockerfile, Markdown) and on the regular expressions
// of Python's re module (filecheck-model.js says what is wrong).
import * as monaco from 'monaco-editor';
import { kindOf, check, markdownTargets, headingAnchors } from './filecheck-model';

const OWNER = 'pymagic-filecheck';

export function setupFileChecks(deps) {
    const timers = new Map();
    let files = null, filesAt = 0;

    /** The project's files, for Markdown links (null when the tree cannot be read: then no link is reported). */
    async function projectFiles() {
        if (files && Date.now() - filesAt < 5000) return files;
        try {
            const res = await fetch('/api/tree');
            files = res.ok ? new Set((await res.json()).files.map((f) => f.path)) : null;
        } catch { files = null; }
        filesAt = Date.now();
        return files;
    }

    const anchorCache = new Map();        // a Markdown file's project path -> {at, anchors}

    /** The anchors of the Markdown files a text links into with a #heading (read from the project, kept for a few seconds). */
    async function anchorsOf(text, rel) {
        const anchors = new Map();
        await Promise.all(markdownTargets(text, rel).map(async (path) => {
            const hit = anchorCache.get(path);
            if (hit && Date.now() - hit.at < 5000) { if (hit.anchors) anchors.set(path, hit.anchors); return; }
            let found = null;
            try {
                const res = await fetch('/api/file?path=' + encodeURIComponent(path));
                if (res.ok) found = headingAnchors(await res.text());
            } catch { /* not readable: its anchors are not checked */ }
            anchorCache.set(path, { at: Date.now(), anchors: found });
            if (found) anchors.set(path, found);
        }));
        return anchors;
    }

    const severities = { error: monaco.MarkerSeverity.Error, warning: monaco.MarkerSeverity.Warning, info: monaco.MarkerSeverity.Info };

    async function run(model) {
        const rel = deps.relOf(model);
        const kind = rel && kindOf(rel);
        if (!kind || model.isDisposed()) return;
        const context = kind === 'markdown' ? { rel, files: await projectFiles(), anchors: await anchorsOf(model.getValue(), rel) } : {};
        if (model.isDisposed()) return;
        monaco.editor.setModelMarkers(model, OWNER, check(kind, model.getValue(), context).map((p) => ({
            startLineNumber: p.line, startColumn: p.start, endLineNumber: p.line, endColumn: p.end,
            severity: severities[p.severity] ?? monaco.MarkerSeverity.Warning,
            message: p.message, code: p.code, source: 'PyMagic',
        })));
    }

    function watch(model) {
        const rel = deps.relOf(model);
        if (!rel || !kindOf(rel)) return;
        void run(model);
        model.onDidChangeContent(() => {
            clearTimeout(timers.get(model));
            timers.set(model, setTimeout(() => void run(model), 800));
        });
    }

    monaco.editor.onDidCreateModel((model) => setTimeout(() => watch(model), 0));
    for (const model of monaco.editor.getModels()) watch(model);
}

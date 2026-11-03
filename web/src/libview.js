// A library file (the standard library, site-packages, typeshed's stubs) in a read-only window of its own, at a line: Find in files opens it
// for a match in a library, and go to definition (F12, Ctrl+click) for a name defined there. The engine reads it (/api/library-file), only
// from the interpreter's library folders and typeshed.
import * as monaco from 'monaco-editor';
import { LIB_SCHEME, libraryTitle } from './libview-model.js';

const texts = new Map();   // path -> its text, read once

/** The text of a library file, or null when the engine does not give it. */
export async function libraryText(path) {
    if (texts.has(path)) return texts.get(path);
    const res = await fetch('/api/library-file?path=' + encodeURIComponent(path)).catch(() => null);
    const text = res?.ok ? await res.text() : null;
    if (text !== null) texts.set(path, text);
    return text;
}

/** The read-only model of a library file (for peek), made once; null when it cannot be read. */
export async function libraryModel(path) {
    const uri = monaco.Uri.from({ scheme: LIB_SCHEME, path: '/' + path.replace(/^\//, '') });
    const open = monaco.editor.getModel(uri);
    if (open) return open;
    const text = await libraryText(path);
    return text === null ? null : monaco.editor.createModel(text, 'python', uri);
}

/** Shows the file read only at a line and column (0-based); false when it cannot be read. `editor` is the main editor (its font, its focus back). */
export async function showLibraryFile(path, line, column, editor) {
    const text = await libraryText(path);
    if (text === null) return false;
    const overlay = document.createElement('div');
    overlay.id = 'sigdialog';
    const box = document.createElement('div');
    box.className = 'box fs-libview';
    const title = document.createElement('h2');
    title.textContent = libraryTitle(path);
    title.title = path + ' (read only)';
    const host = document.createElement('div');
    host.className = 'fs-libview-editor';
    box.append(title, host);
    overlay.appendChild(box);
    document.body.appendChild(overlay);
    const model = monaco.editor.createModel(text, 'python');
    const view = monaco.editor.create(host, { model, readOnly: true, minimap: { enabled: false }, scrollBeyondLastLine: false, automaticLayout: true, fontSize: editor.getOption(monaco.editor.EditorOption.fontSize) });
    const at = new monaco.Range(line + 1, column + 1, line + 1, column + 1);
    view.setSelection(at);
    view.revealLineInCenter(line + 1);
    view.focus();
    const close = () => { view.dispose(); model.dispose(); overlay.remove(); editor.focus(); };
    overlay.onkeydown = (e) => { if (e.key === 'Escape') { e.preventDefault(); close(); } };
    overlay.onmousedown = (e) => { if (e.target === overlay) close(); };
    return true;
}

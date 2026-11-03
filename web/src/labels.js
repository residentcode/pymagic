// A label of Local History put by PyMagic before something big (a refactoring, a test run): the project as it is, to go back to. The
// server puts its own before Git updates. A failed request does not stop what the label was for. A label put from the page keeps the open
// files with their changes not saved yet as the editor has them (the server reads the others from the disk).
let unsavedTexts = () => ({});

/** Where the texts of the open files with unsaved changes come from: rel -> text (history.js sets it). */
export function setUnsavedTexts(fn) { unsavedTexts = fn; }

/** The open files with unsaved changes, as a label is given them: [{path, text}]. */
export function unsavedFiles() {
    try { return Object.entries(unsavedTexts() ?? {}).map(([path, text]) => ({ path, text })); } catch { return []; }
}

export async function putLabel(name) {
    try {
        await fetch('/api/history/label', { method: 'POST', body: JSON.stringify({ name, auto: true, unsaved: unsavedFiles() }) });
        window.dispatchEvent(new CustomEvent('pymagic:labelPut'));
    } catch { /* the thing goes on without its label */ }
}

// A label of Local History put by PyMagic before something big (a refactoring, a test run): the project as it is, to go back to. The
// server puts its own before Git updates. A failed request does not stop what the label was for.
export async function putLabel(name) {
    try {
        await fetch('/api/history/label', { method: 'POST', body: JSON.stringify({ name, auto: true }) });
        window.dispatchEvent(new CustomEvent('pymagic:labelPut'));
    } catch { /* the thing goes on without its label */ }
}

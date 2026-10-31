// The text of open files shared between PyMagic's windows on one project (Open in a new window, Shift+F4): what is typed in one
// shows in the other before it is saved, and a save in one is a save in both. The windows load the page from the same engine, so a
// BroadcastChannel joins them; nothing leaves this computer.
const CHANNEL = 'pymagic-text';

export function setupSharedText({ modelOf, markSaved }) {
    if (typeof BroadcastChannel !== 'function')
        return { watch() { }, saved() { } };
    const channel = new BroadcastChannel(CHANNEL + ':' + location.host);
    const me = Math.random().toString(36).slice(2);
    let applying = false;
    const timers = new Map();

    channel.onmessage = (e) => {
        const m = e.data;
        if (!m || m.from === me || typeof m.rel !== 'string')
            return;
        if (m.type === 'hello') {
            // a window that just opened asks for the text of a file it has: one that holds unsaved text answers
            const model = modelOf(m.rel);
            if (model && model.__pmDirty?.())
                channel.postMessage({ type: 'text', from: me, rel: m.rel, text: model.getValue() });
            return;
        }
        const model = modelOf(m.rel);
        if (!model)
            return;
        if (m.type === 'text' && model.getValue() !== m.text) {
            applying = true;
            try {
                // one edit for the whole text, as an edit of the person here would be, so undo still works
                model.pushEditOperations([], [{ range: model.getFullModelRange(), text: m.text }], () => null);
            }
            finally {
                applying = false;
            }
        }
        else if (m.type === 'saved')
            markSaved(m.rel, m.text);
    };

    return {
        /** Shares a model's changes from now on, and asks the other windows for its unsaved text. */
        watch(rel, model, isDirty) {
            model.__pmDirty = isDirty;
            model.onDidChangeContent(() => {
                if (applying)
                    return;
                clearTimeout(timers.get(rel));
                timers.set(rel, window.setTimeout(() => channel.postMessage({ type: 'text', from: me, rel, text: model.getValue() }), 150));
            });
            channel.postMessage({ type: 'hello', from: me, rel });
        },
        /** This window saved a file: the others take that text as saved too. */
        saved(rel, text) {
            channel.postMessage({ type: 'saved', from: me, rel, text });
        },
    };
}

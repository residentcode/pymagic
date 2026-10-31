// Minimal JSON-RPC client for the language server's WebSocket endpoint (one JSON message per frame).

/** The error a cancelled request rejects with (named as Monaco names its own, so it is not reported). */
function cancelled() {
    const e = new Error('Canceled');
    e.name = 'Canceled';
    return e;
}
export class LspClient {
    ws;
    nextId = 1;
    pending = new Map();
    handlers = new Map();
    onStatus = () => { };
    connect(url) {
        return new Promise((resolve, reject) => {
            this.ws = new WebSocket(url);
            this.ws.onopen = () => { this.onStatus(true); resolve(); };
            this.ws.onerror = () => reject(new Error('cannot reach the language server'));
            this.ws.onclose = () => {
                this.onStatus(false);
                for (const p of this.pending.values())
                    p.reject(new Error('connection closed'));
                this.pending.clear();
            };
            this.ws.onmessage = (ev) => this.dispatch(JSON.parse(ev.data));
        });
    }
    on(method, handler) { this.handlers.set(method, handler); }
    /**
     * A request; with a cancellation token (Monaco's, or anything with isCancellationRequested and onCancellationRequested), the engine is told
     * when it is no longer wanted ($/cancelRequest) and the promise rejects at once: typing on drops the completions and hovers still queued.
     */
    request(method, params, token) {
        const id = this.nextId++;
        return new Promise((resolve, reject) => {
            if (token?.isCancellationRequested) {
                reject(cancelled());
                return;
            }
            this.pending.set(id, { resolve, reject });
            this.send({ jsonrpc: '2.0', id, method, params });
            token?.onCancellationRequested?.(() => {
                if (!this.pending.has(id))
                    return;
                this.pending.delete(id);
                this.notify('$/cancelRequest', { id });
                reject(cancelled());
            });
        });
    }
    notify(method, params) { this.send({ jsonrpc: '2.0', method, params }); }
    send(msg) {
        if (this.ws.readyState === WebSocket.OPEN)
            this.ws.send(JSON.stringify(msg));
    }
    dispatch(msg) {
        if (msg.id !== undefined && (msg.result !== undefined || msg.error)) {
            const p = this.pending.get(msg.id);
            if (!p)
                return;
            this.pending.delete(msg.id);
            msg.error ? p.reject(new Error(msg.error.message)) : p.resolve(msg.result);
        }
        else if (msg.method) {
            this.handlers.get(msg.method)?.(msg.params);
        }
    }
}

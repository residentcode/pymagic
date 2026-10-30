// WebSocket client for the host's /run endpoint (programs, the debugger and the test runner share it).
export class RunClient {
    ws;
    nextId = 1;
    waiting = new Map();
    onMessage = () => { };
    onClose = () => { };
    constructor(first) {
        const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
        this.ws = new WebSocket(`${scheme}://${location.host}/run`);
        this.ws.onopen = () => this.ws.send(JSON.stringify(first));
        this.ws.onclose = () => this.onClose();
        this.ws.onmessage = (ev) => {
            const m = JSON.parse(ev.data);
            const waiter = m.id !== undefined ? this.waiting.get(m.id) : undefined;
            if (waiter && ['scopes', 'variables', 'values', 'evaluated', 'variableSet', 'jumped'].includes(m.type)) {
                this.waiting.delete(m.id);
                waiter(m);
            }
            else
                this.onMessage(m);
        };
    }
    send(msg) { if (this.ws.readyState === WebSocket.OPEN)
        this.ws.send(JSON.stringify(msg)); }
    ask(msg) {
        const id = this.nextId++;
        return new Promise((resolve) => { this.waiting.set(id, resolve); this.send({ ...msg, id }); });
    }
    close() { try {
        this.ws.close();
    }
    catch { /* already closed */ } }
}

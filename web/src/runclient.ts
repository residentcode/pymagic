// WebSocket client for the host's /run endpoint (programs, the debugger and the test runner share it).

export class RunClient {
  private ws: WebSocket;
  private nextId = 1;
  private waiting = new Map<number, (m: any) => void>();
  onMessage: (m: any) => void = () => {};
  onClose: () => void = () => {};

  constructor(first: object) {
    const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
    this.ws = new WebSocket(`${scheme}://${location.host}/run`);
    this.ws.onopen = () => this.ws.send(JSON.stringify(first));
    this.ws.onclose = () => this.onClose();
    this.ws.onmessage = (ev) => {
      const m = JSON.parse(ev.data as string);
      const waiter = m.id !== undefined ? this.waiting.get(m.id) : undefined;
      if (waiter && (m.type === 'scopes' || m.type === 'variables' || m.type === 'evaluated')) {
        this.waiting.delete(m.id);
        waiter(m);
      } else this.onMessage(m);
    };
  }

  send(msg: object) { if (this.ws.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(msg)); }

  ask(msg: object): Promise<any> {
    const id = this.nextId++;
    return new Promise((resolve) => { this.waiting.set(id, resolve); this.send({ ...msg, id }); });
  }

  close() { try { this.ws.close(); } catch { /* already closed */ } }
}

// Minimal JSON-RPC client for the language server's WebSocket endpoint (one JSON message per frame).

type Pending = { resolve: (v: any) => void; reject: (e: Error) => void };

export class LspClient {
  private ws!: WebSocket;
  private nextId = 1;
  private pending = new Map<number, Pending>();
  private handlers = new Map<string, (params: any) => void>();
  onStatus: (connected: boolean) => void = () => {};

  connect(url: string): Promise<void> {
    return new Promise((resolve, reject) => {
      this.ws = new WebSocket(url);
      this.ws.onopen = () => { this.onStatus(true); resolve(); };
      this.ws.onerror = () => reject(new Error('cannot reach the language server'));
      this.ws.onclose = () => {
        this.onStatus(false);
        for (const p of this.pending.values()) p.reject(new Error('connection closed'));
        this.pending.clear();
      };
      this.ws.onmessage = (ev) => this.dispatch(JSON.parse(ev.data as string));
    });
  }

  on(method: string, handler: (params: any) => void) { this.handlers.set(method, handler); }

  request<T = any>(method: string, params: unknown): Promise<T> {
    const id = this.nextId++;
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.send({ jsonrpc: '2.0', id, method, params });
    });
  }

  notify(method: string, params: unknown) { this.send({ jsonrpc: '2.0', method, params }); }

  private send(msg: object) {
    if (this.ws.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(msg));
  }

  private dispatch(msg: any) {
    if (msg.id !== undefined && (msg.result !== undefined || msg.error)) {
      const p = this.pending.get(msg.id);
      if (!p) return;
      this.pending.delete(msg.id);
      msg.error ? p.reject(new Error(msg.error.message)) : p.resolve(msg.result);
    } else if (msg.method) {
      this.handlers.get(msg.method)?.(msg.params);
    }
  }
}

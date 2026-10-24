// The Endpoints tab: the routes of the project (FastAPI, Starlette, Flask) with the prefixes their routers and blueprints add;
// a click goes to the function that answers the route.
import type * as monaco from 'monaco-editor';
import type { LspClient } from './lsp';
import { showTab } from './panel';
import { toast } from './toast';

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

interface RouteEntry { methods: string; path: string; name: string; framework: string; uri: string; line: number }

export interface RoutesDeps {
  editor: monaco.editor.IStandaloneCodeEditor;
  lsp: LspClient;
  relOfUri(uri: string): string | undefined;
  openFile(rel: string, reveal?: monaco.IRange): Promise<void>;
}

export function setupRoutes(deps: RoutesDeps) {
  const list = $('routes-list');
  const summary = $('routes-summary');
  const filter = $<HTMLInputElement>('routes-filter');
  let routes: RouteEntry[] = [];
  let ticket = 0;

  function paint() {
    const needle = filter.value.trim().toLowerCase();
    const shown = routes.filter((r) => !needle || `${r.methods} ${r.path} ${r.name} ${deps.relOfUri(r.uri) ?? ''}`.toLowerCase().includes(needle));
    summary.textContent = routes.length === 0
      ? 'No routes found. Routes are functions decorated with @app.get("/path"), @router.post(...) or @bp.route(...).'
      : `${shown.length} of ${routes.length} routes`;
    list.replaceChildren(...shown.map((r) => {
      const row = document.createElement('div');
      row.className = 'route';
      const methods = document.createElement('span');
      methods.className = `verb verb-${r.methods.split(',')[0].trim().toLowerCase()}`;
      methods.textContent = r.methods;
      const path = document.createElement('span'); path.className = 'rpath'; path.textContent = r.path;
      const name = document.createElement('span'); name.className = 'detail'; name.textContent = `${r.name}()`;
      const file = document.createElement('span'); file.className = 'detail'; file.textContent = `${deps.relOfUri(r.uri) ?? r.uri}:${r.line + 1}`;
      row.append(methods, path, name, file);
      row.onclick = async () => {
        const rel = deps.relOfUri(r.uri);
        if (!rel) { toast('That place is outside the project.', true); return; }
        await deps.openFile(rel, { startLineNumber: r.line + 1, startColumn: 1, endLineNumber: r.line + 1, endColumn: 1 });
      };
      return row;
    }));
  }

  async function refresh() {
    const mine = ++ticket;
    try {
      const found = (await deps.lsp.request('pymagic/routes', {})) as RouteEntry[] | null;
      if (mine !== ticket) return;
      routes = (found ?? []).slice().sort((a, b) => a.path.localeCompare(b.path) || a.methods.localeCompare(b.methods));
      paint();
    } catch (e: any) { summary.textContent = String(e.message ?? e); }
  }

  $('t-routes').onclick = () => { showTab('routes'); void refresh(); };
  $('routes-refresh').onclick = () => void refresh();
  filter.oninput = paint;
  window.addEventListener('pymagic:saved', () => { if ($('routes-tab').classList.contains('active')) void refresh(); });
}

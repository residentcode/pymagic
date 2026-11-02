# Making a plugin for PyMagic

A PyMagic plugin is a feature that comes with the app and can be switched on and off in **Settings > Plugins**, like PyCharm's bundled
plugins (items 289 to 291). Nothing is downloaded or loaded from outside: a plugin is code in this repository. When it is off:

- **In the window**, its setup does not run when the window opens. Its commands, keys, checks and editor features are not there, and its panel
  tab, buttons and menu entries are hidden.
- **In the engine**, the work it does on its own is skipped: inspections, completion, navigation, and external programs it would run.

Every plugin is on until the person switches it off. The setting keeps only the plugins that are off (`"plugins": { "docker": false }` in the
person's settings), and Settings > Plugins is the only place that writes it. A change takes effect when the window reloads: the page says so and
offers **Reload PyMagic**. The engine follows the setting as soon as it is saved.

The plugins that exist are listed in [`web/src/plugin-model.js`](../web/src/plugin-model.js): Django, Flask, FastAPI, JavaScript projects, Emmet, HTML
tag pairs, Jupyter Notebooks, Docker, WSL, SSH, Git, JSON Schema, HTTP Client, Database Tools and SQL, Coverage, Diagrams, Profiler, Pull Requests, Deployment, Markdown and Macros.
Any of them is a working example.

## 1. Register the plugin

Add an entry to `PLUGINS` in `web/src/plugin-model.js`. Settings > Plugins lists it, finds it by its name and description, and gives it its switch.

```js
{ id: 'todo', name: 'TODO', about: 'TODO, FIXME and XXX comments listed in the TODO panel.', tab: 'todo', owns: ['b-todo'] },
```

| Field | What it is |
|---|---|
| `id` | The key in the setting and in the engine (`Plugins.On("todo")`): lower case, never changed once released. |
| `name`, `about` | What Settings > Plugins shows. Say what the person loses when it is off. |
| `tab` | Optional. The panel tool window it owns: the tab button `t-<tab>` and the panel `<tab>-tab` are hidden while it is off, and its entries leave the tool-window lists of the main menu. |
| `owns` | Optional. The ids of other elements of `index.html` that are hidden while it is off (toolbar buttons, rail buttons). |
| `kind` | Optional. The New Project kind it brings (`'django'`, `'jupyter'`, `'flask'`, `'fastapi'`), or a list of them (JavaScript projects brings seven); New Project does not offer them while the plugin is off. |
| `requires` | Optional. Another plugin it needs (`prs` needs `git`). It is off whenever that one is, and its row says so. |

The functions beside `PLUGINS` are what everything else uses: `pluginOn(setting, id)`, `pluginsOff(setting)`, `hiddenElements(setting)`,
`toolOff(setting, tab)`, `kindsOff(setting)` and `switchPlugin(setting, id, on)`. They take the `plugins` object of the settings, so they can be tested
without a page.

## 2. Leave it out of the window

The plugins set up are the ones in force when the window opened: `pluginsAtStart()` from `web/src/settings.js`. In `web/src/main.js`, guard the
plugin's setup with `plugin(id)`:

```js
if (plugin('todo'))
    setupTodo({ editor, lsp, openFile, relOfUri });
```

If other code uses what the setup returns, keep the value and make it `null` when the plugin is off, and use `?.` where it is used:

```js
const coverage = plugin('coverage') ? setupCoverage(common) : null;
// ...
coverage?.attach(e);
```

Sometimes a module has to stay because others need part of it. The Git panel also hosts the diff viewer, which tests and comparisons use. In that
case, pass a flag and let the module do nothing else: `setupGit({ ..., enabled: plugin('git') })`.

What is hidden and where:

- **Elements of `index.html`**: `tab` and `owns` cover them. `main.js` gives each one the class `plugin-off` (`display: none`) when the window opens.
- **Elements made later** (dialogs, New Project, the remote interpreter window): ask when they are built.
  ```js
  import { pluginsAtStart } from './settings';
  import { pluginOn } from './plugin-model.js';
  if (!pluginOn(pluginsAtStart(), 'todo')) return;
  ```
- **Main menu entries** (`web/src/mainmenu.js`): filter them with `toolOff(pluginsAtStart(), tab)` or `pluginOn(pluginsAtStart(), id)`, as the
  Profiler, Diagrams, Coverage and Git entries do.

Commands made with `defineCommand` inside the setup are simply not defined when the plugin is off, so their keys and Settings > Keymap rows go
with it.

## 3. Leave it out of the engine

The engine reads the setting through `PyMagic.Server.Plugins` ([`src/PyMagic.Server/Plugins.cs`](../src/PyMagic.Server/Plugins.cs)). It is
loaded at start and again each time the settings are saved (`PUT /api/settings`). Ask `Plugins.On("todo")` where the plugin's work starts:

- **Inspections**: in `InspectionDiagnostics` (`LanguageService.InspectionFixes.cs`), as Django and FastAPI do:
  ```cs
  if (Plugins.On("todo")) foreach (var d in TodoDiagnostics(m, ev)) yield return d;
  ```
- **Hover, go to definition, find usages, rename, completion**: at the entry points in `LanguageService.cs`, `LanguageService.References.cs` and
  `LanguageService.Completion.cs`, before the plugin's own provider is asked.
- **What the plugin reads from the project**: where the index is built. Flask and FastAPI routes are skipped in `RoutesIn`, so their checks,
  completion and Endpoints rows all go together.
- **Endpoints of `src/PyMagic.Web/Program.cs`** that run an external program: do not run it, and answer as if there were nothing. With Docker
  off, `docker` is never started to list images.
- **Interpreters of a target** (Docker, WSL, SSH): `Plugins.OfTarget(kind)` names the plugin of a target, and `Plugins.TargetOn(kind)` decides
  whether an interpreter of that target may be listed, chosen or used.

The engine has one language service per window, so the caches it builds are new after a reload.

## 4. Data the plugin needs

Ship it with the app, never download it when the feature is used (the JSON Schemas, item 288). Put the files in the engine project and embed them:

```xml
<EmbeddedResource Include="Schemas\*.json" LogicalName="schema-%(Filename).json" />
```

Read them with `GetManifestResourceStream`, and write down in a README beside them where each file comes from and when it was taken.

## 5. Settings of its own

If the plugin has settings, they go on a Settings page in the app (`web/src/settings-ui.js`). The person never edits a `.json` file. Add a field
to `DEFAULTS` in `web/src/settings.js`, and a row to a page of `PAGES` or a page of its own in `SPECIAL`.

## 6. Tests

- **Page tests** (`web/test/plugins.test.mjs`): what the plugin hides (`hiddenElements`), its New Project kind, what it requires.
- **Engine tests** (`tests/PyMagic.Tests`): switch it off with `Plugins.Load`, check its findings are gone, and always switch it back on in a
  `finally`. Other tests share the same process:
  ```cs
  try
  {
      Plugins.Load("{\"plugins\": {\"todo\": false}}");
      Assert.Equal(0, Found(svc, "todo").Count);
  }
  finally { Plugins.Load("{}"); }
  ```
- **Browser test** (`desktop/e2e/page.e2e.mjs`, `describe('Settings > Plugins')`): switch it off with
  `window.pymagic.changeSetting({ plugins: ... })`, reload, and check that its parts are gone. The suite's `after` switches every plugin back on.

Run all three, rebuild the app (`node desktop/package.mjs --no-installer`) and run the browser suite in it before calling the plugin done.

## Checklist

1. An entry in `PLUGINS`, with what the person loses when it is off.
2. Its setup guarded in `main.js`, and what it returns used with `?.`.
3. Its elements in `tab` / `owns`, and its menu entries and dialogs asking `pluginOn`.
4. `Plugins.On(id)` where the engine starts its work, and no external program run while it is off.
5. Data shipped with the app, settings on a page of the app.
6. Page, engine and browser tests; an item in `work_done.md`.

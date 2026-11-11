// The welcome screen's Customize page without Electron: which of the person's settings it shows and changes (theme, code colours, the
// editor's font and size, the keymap), each checked before it is written, in the engine's own settings file (the one Settings writes:
// PYMAGIC_SETTINGS_FILE, else %LOCALAPPDATA%\PyMagic\settings.json), so the next project opened has them.
const path = require('path');
const os = require('os');

/** The engine's settings file of the person (UserSettings.cs's DefaultFile). */
function engineSettingsFile(env = process.env) {
  if (env.PYMAGIC_SETTINGS_FILE) return env.PYMAGIC_SETTINGS_FILE;
  const local = env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
  return path.join(local, 'PyMagic', 'settings.json');
}

/** What Customize shows, and what each may be (settings.js's DEFAULTS and choices). */
const FIELDS = {
  theme: { default: 'dark', choices: ['dark', 'light', 'system'] },
  codeColors: { default: 'vscode', choices: ['vscode', 'pymagic'] },
  fontFamily: { default: '"Cascadia Code", "Cascadia Mono", Consolas, "Courier New", monospace', text: true },
  fontSize: { default: 14, min: 8, max: 40 },
  keymap: { default: 'pymagic', choices: ['pymagic', 'pycharm', 'vscode'] },
};

/** The Customize fields of the settings (their defaults where the file has none or has a value that is not one of them). */
function customizeOf(stored) {
  const s = stored && typeof stored === 'object' ? stored : {};
  const out = {};
  for (const [key, f] of Object.entries(FIELDS)) out[key] = valid(key, s[key]) ? s[key] : f.default;
  return out;
}

function valid(key, value) {
  const f = FIELDS[key];
  if (!f) return false;
  if (f.choices) return f.choices.includes(value);
  if (f.text) return typeof value === 'string' && value.length > 0 && value.length < 300 && !/[\u0000-\u001f;{}<>]/.test(value);
  return Number.isInteger(value) && value >= f.min && value <= f.max;
}

/** The settings with a change of Customize's made: only its fields, each checked; the rest of the settings kept as they are. */
function withCustomize(stored, patch) {
  const next = stored && typeof stored === 'object' && !Array.isArray(stored) ? { ...stored } : {};
  for (const [key, value] of Object.entries(patch ?? {})) {
    const v = key === 'fontSize' ? Number(value) : value;
    if (!valid(key, v)) throw new Error(`${key} cannot be ${JSON.stringify(value)}`);
    next[key] = v;
  }
  return next;
}

module.exports = { engineSettingsFile, customizeOf, withCustomize, FIELDS };

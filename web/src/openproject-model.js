// Where a project is opened (Settings > System, "Open project in", as PyCharm's System Settings has it): in this window, replacing its project, in
// a new window, or asked each time. No DOM here: web/test/openproject.test.mjs checks it.

export const OPEN_PROJECT_IN = [['ask', 'Ask'], ['this', 'This window'], ['new', 'New window']];

/** Where to open without asking ('this' or 'new'), or 'ask'; an unknown setting asks. */
export function openWhere(setting) {
    return setting === 'this' || setting === 'new' ? setting : 'ask';
}

/** The setting after the dialog: with "Don't ask again" ticked, the answer becomes the setting; Cancel never changes it. */
export function settingAfter(setting, answer, dontAsk) {
    return dontAsk && (answer === 'this' || answer === 'new') ? answer : setting;
}

/** Whether a folder is the project of this window (opening it again does nothing). Windows paths: case and the last \ do not count. */
export function sameFolder(a, b) {
    const norm = (p) => String(p ?? '').replace(/[\\/]+$/, '').replace(/\//g, '\\').toLowerCase();
    return !!a && !!b && norm(a) === norm(b);
}

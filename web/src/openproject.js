// Opening a project in the desktop app (the menu's Open folder, the recent projects, the demo, New Project, Run Anything's "open"): as PyCharm
// does, the person is asked whether it replaces the project of this window or opens in a new window of its own, with "Don't ask again" (which
// sets Settings > System > Open project in). Each window has its engine; a project already open in a window brings that window to the front.
import { askChoice } from './ask.js';
import { settings, change } from './settings.js';
import { openWhere, settingAfter } from './openproject-model.js';

/** The desktop app's functions (preload.js) with the opening of a project asking first; null in a browser. */
export function projectDesktop() {
    const desktop = window.pymagicDesktop;
    if (!desktop) return null;
    const openProject = async (folder) => {
        if (!folder) return false;
        let where = openWhere(settings().openProjectIn);
        if (where === 'ask') {
            const answer = await askChoice('Open Project',
                'Projects can either be opened in a new window, or replace the project in the current window. How would you like to open the project?',
                [['This Window', 'this'], ['New Window', 'new'], ['Cancel', 'cancel']], { check: "Don't ask again" });
            if (answer.value === 'cancel') return false;
            const next = settingAfter(settings().openProjectIn, answer.value, answer.checked);
            if (next !== settings().openProjectIn) change({ openProjectIn: next });
            where = answer.value;
        }
        await desktop.openProject(folder, where);
        return true;
    };
    return {
        ...desktop,
        openProject,
        openFolder: async () => openProject(await desktop.chooseFolder()),
        openDemo: async () => openProject(await desktop.demoFolder()),
    };
}

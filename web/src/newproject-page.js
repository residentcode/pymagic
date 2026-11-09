// The welcome screen's New project (desktop/welcome.html): the New project dialog alone, on an engine started for it in a folder of PyMagic's
// own. Cancel goes back to the welcome screen; the project made opens in this window.
import { loadSettings, themeNow } from './settings';
import { openNewProject } from './newproject';

void (async () => {
    await loadSettings();
    document.documentElement.dataset.theme = themeNow();
    const near = new URLSearchParams(window.location.search).get('near');
    await openNewProject({ near, openHere: true, onCancel: () => void window.pymagicDesktop?.showWelcome?.() });
})();

// The welcome screen's New project and Plugins (desktop/welcome.html): their dialog alone, on an engine started for it in a folder of PyMagic's
// own. ?tool=newproject (the default): the New project dialog, the project made opening in this window. ?tool=plugins: Settings at its
// Plugins page (?tool=settings: at Appearance, Customize's All settings…) (the person's settings, which the next project opened uses). Closing either goes back to the welcome screen.
import { loadSettings, themeNow } from './settings';
import { openNewProject } from './newproject';
import { openSettings } from './settings-ui';

void (async () => {
    await loadSettings();
    document.documentElement.dataset.theme = themeNow();
    const params = new URLSearchParams(window.location.search);
    const back = () => void window.pymagicDesktop?.showWelcome?.();
    if (params.get('tool') === 'plugins')
        openSettings('plugins', { onClose: back });
    // Customize's All settings…: every page of Settings, from Appearance
    else if (params.get('tool') === 'settings')
        openSettings('appearance', { onClose: back });
    else
        await openNewProject({ near: params.get('near'), openHere: true, onCancel: back });
})();

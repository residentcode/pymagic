// Saving without asking: when the window is left, or a second after the last change, as the settings say (the command is the editor's own saveAll).
import { settings } from './settings';

export function setupAutoSave({ editor, saveAll }) {
    let timer = 0;
    editor.onDidChangeModelContent(() => {
        window.clearTimeout(timer);
        if (settings().autoSave === 'delay') timer = window.setTimeout(() => { if (settings().autoSave === 'delay') void saveAll(); }, 1000);
    });
    window.addEventListener('blur', () => { if (settings().autoSave === 'blur') void saveAll(); });
    document.addEventListener('visibilitychange', () => { if (document.hidden && settings().autoSave === 'blur') void saveAll(); });
}

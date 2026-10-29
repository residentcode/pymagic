// The bottom panel: tabs are buttons "t-<name>" and sections "<name>-tab". Its top edge can be dragged to make it taller or shorter.
const $ = (id) => document.getElementById(id);
export function showTab(name) {
    $('panel').classList.add('open');
    document.querySelectorAll('#tabs button[id^="t-"]').forEach((b) => b.classList.toggle('active', b.id === `t-${name}`));
    document.querySelectorAll('#panel .tab').forEach((t) => t.classList.toggle('active', t.id === `${name}-tab`));
}

/** The height the panel may have in a window this tall: at least 120 px, and the editor keeps at least 160 px. */
export function clampPanelHeight(height, windowHeight) {
    return Math.round(Math.max(120, Math.min(height, windowHeight - 48 - 26 - 160)));
}

function setupResize() {
    const panel = $('panel');
    const grip = $('panel-resize');
    if (!panel || !grip) return;
    const apply = (h) => { if (h) panel.style.height = `${clampPanelHeight(h, window.innerHeight)}px`; else panel.style.removeProperty('height'); };
    let saved = 0;
    try { saved = Number(localStorage.getItem('pymagic.panel.height')) || 0; } catch { /* storage unavailable */ }
    apply(saved);
    grip.addEventListener('mousedown', (e) => {
        e.preventDefault();
        const startY = e.clientY, startH = panel.getBoundingClientRect().height;
        const move = (ev) => apply(startH + (startY - ev.clientY));
        const up = () => {
            window.removeEventListener('mousemove', move);
            window.removeEventListener('mouseup', up);
            document.body.classList.remove('row-resizing');
            try { localStorage.setItem('pymagic.panel.height', String(Math.round(panel.getBoundingClientRect().height))); } catch { /* storage unavailable */ }
        };
        document.body.classList.add('row-resizing');
        window.addEventListener('mousemove', move);
        window.addEventListener('mouseup', up);
    });
    grip.addEventListener('dblclick', () => { apply(0); try { localStorage.removeItem('pymagic.panel.height'); } catch { /* storage unavailable */ } });
    // a smaller window keeps the editor visible
    window.addEventListener('resize', () => { if (panel.style.height) apply(parseFloat(panel.style.height)); });
}
if (typeof document !== 'undefined') {
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', setupResize);
    else setupResize();
}

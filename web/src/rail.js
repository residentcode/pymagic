// The frame around the editor that only shows and forwards: the rail of tools on the left, the explorer's visibility, and the project and
// branch in the command bar. Nothing here decides what a tool does; each rail button presses the button the tool already has.
const $ = (id) => document.getElementById(id);

const SIDEBAR_KEY = 'pymagic.sidebar';

/** The rail buttons that show something in the sidebar, and what. */
const VIEWS = { 'r-explorer': ['files', 'Files'], 'r-structure': ['structure', 'Structure'], 'r-bookmarks': ['bookmarks', 'Bookmarks'] };

/** Which dock tab (or command bar button) each rail button stands for. */
const TOOLS = {
    'r-search': 't-search',
    'r-git': 't-git',
    'r-tests': 'b-tests',
    'r-problems': 't-problems',
    'r-terminal': 't-terminal',
    'r-packages': 't-packages',
};

function remembered() {
    try {
        const v = localStorage.getItem(SIDEBAR_KEY) ?? 'files';
        return v === 'hidden' ? { shown: false, view: 'files' } : { shown: true, view: Object.values(VIEWS).some(([k]) => k === v) ? v : 'files' };
    } catch { return { shown: true, view: 'files' }; }
}

export function setupChrome() {
    const app = $('app');

    let view = 'files';
    const shown = () => !app.classList.contains('no-side');
    /** Shows the sidebar with this view, or hides it. */
    const showSidebar = (isShown, name = view) => {
        view = name;
        app.classList.toggle('no-side', !isShown);
        $('sidebar').dataset.view = view;
        for (const [id, [v, title]] of Object.entries(VIEWS)) {
            $(id).classList.toggle('active', isShown && v === view);
            if (v === view) $('side-title').textContent = title;
        }
        try { localStorage.setItem(SIDEBAR_KEY, isShown ? view : 'hidden'); } catch { /* the choice is only not remembered */ }
        if (isShown) window.dispatchEvent(new CustomEvent('pymagic:sidebar', { detail: view }));
    };
    const first = remembered();
    showSidebar(first.shown, first.view);
    for (const [id, [v]] of Object.entries(VIEWS)) $(id).onclick = () => showSidebar(!(shown() && view === v), v);

    for (const [from, to] of Object.entries(TOOLS)) $(from).onclick = () => $(to).click();

    // a rail button is lit while its tool is the one open in the dock
    const mark = () => {
        const open = $('panel').classList.contains('open');
        const current = document.querySelector('#tabs button.active')?.id;
        for (const [from, to] of Object.entries(TOOLS))
            $(from).classList.toggle('active', open && (current === to || (to === 'b-tests' && current === 't-tests')));
    };
    const watch = new MutationObserver(mark);
    watch.observe($('panel'), { attributes: true, attributeFilter: ['class'] });
    for (const b of document.querySelectorAll('#tabs button')) watch.observe(b, { attributes: true, attributeFilter: ['class'] });
    mark();

    // the folder the project is, and the branch the Git panel reports
    fetch('/api/info').then((r) => r.json()).then((info) => {
        const root = String(info.root ?? '');
        $('project').textContent = root.split(/[\\/]/).filter(Boolean).pop() ?? '';
        $('project').title = root;
    }).catch(() => { /* the name stays empty */ });
    const branch = $('git-branch');
    const chip = $('branch-chip');
    const sync = () => {
        const name = branch?.textContent?.trim() ?? '';
        chip.classList.toggle('on', name.length > 0);
        chip.lastElementChild.textContent = name;
    };
    if (branch) {
        new MutationObserver(sync).observe(branch, { childList: true, characterData: true, subtree: true });
        sync();
    }
    chip.onclick = () => $('t-git').click();
    return { showView: (name) => showSidebar(true, name), view: () => view, shown };
}

// The bottom panel: tabs are buttons "t-<name>" and sections "<name>-tab".
const $ = (id) => document.getElementById(id);
export function showTab(name) {
    $('panel').classList.add('open');
    document.querySelectorAll('#tabs button[id^="t-"]').forEach((b) => b.classList.toggle('active', b.id === `t-${name}`));
    document.querySelectorAll('#panel .tab').forEach((t) => t.classList.toggle('active', t.id === `${name}-tab`));
}

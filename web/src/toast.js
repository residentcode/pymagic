// A short message at the bottom of the page.
let timer;
export function toast(message, bad = false) {
    let el = document.getElementById('toast');
    if (!el) {
        el = document.createElement('div');
        el.id = 'toast';
        document.body.appendChild(el);
    }
    el.textContent = message;
    el.className = bad ? 'bad' : '';
    el.style.display = 'block';
    clearTimeout(timer);
    timer = window.setTimeout(() => { el.style.display = 'none'; }, bad ? 7000 : 2500);
}

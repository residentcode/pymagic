// A question in a small box over the page, with one button for each answer (the first has the focus). Escape or a press outside it cancels.
/**
 * Resolves with the value of the button pressed, or 'cancel'. choices: [[label, value], ...]. With options.check (the label of a box to tick, as
 * "Don't ask again"), it resolves with { value, checked } instead.
 */
export function askChoice(title, hint, choices, options = {}) {
    return new Promise((resolve) => {
        const shade = document.createElement('div');
        shade.className = 'ask-shade';
        const box = document.createElement('div');
        box.className = 'ask-box';
        const head = document.createElement('div');
        head.className = 'ask-title';
        head.textContent = title;
        const text = document.createElement('div');
        text.className = 'ask-hint';
        text.textContent = hint;
        let tick = null;
        const check = document.createElement('label');
        if (options.check) {
            check.className = 'ask-check';
            tick = document.createElement('input');
            tick.type = 'checkbox';
            check.append(tick, ' ' + options.check);
        }
        const row = document.createElement('div');
        row.className = 'ask-buttons';
        const done = (value) => { shade.remove(); resolve(options.check ? { value, checked: !!tick?.checked } : value); };
        for (const [label, value] of choices) {
            const b = document.createElement('button');
            b.textContent = label;
            b.dataset.value = value;
            b.onclick = () => done(value);
            row.append(b);
        }
        box.append(head, text, ...(options.check ? [check] : []), row);
        shade.append(box);
        shade.onmousedown = (e) => { if (e.target === shade)
            done('cancel'); };
        shade.onkeydown = (e) => { if (e.key === 'Escape')
            done('cancel'); };
        document.body.append(shade);
        row.firstChild.focus();
    });
}

// A question in a small box over the page, with one button for each answer (the first has the focus). Escape or a press outside it cancels.
/** Resolves with the value of the button pressed, or 'cancel'. choices: [[label, value], ...]. */
export function askChoice(title, hint, choices) {
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
        const row = document.createElement('div');
        row.className = 'ask-buttons';
        const done = (value) => { shade.remove(); resolve(value); };
        for (const [label, value] of choices) {
            const b = document.createElement('button');
            b.textContent = label;
            b.onclick = () => done(value);
            row.append(b);
        }
        box.append(head, text, row);
        shade.append(box);
        shade.onmousedown = (e) => { if (e.target === shade)
            done('cancel'); };
        shade.onkeydown = (e) => { if (e.key === 'Escape')
            done('cancel'); };
        document.body.append(shade);
        row.firstChild.focus();
    });
}

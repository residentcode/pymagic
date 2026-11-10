// `/* … */` written in a template's HTML: it is no comment there (the browser shows it as text, and the tags inside still count). Found
// outside <script> and <style>, tags, HTML comments and the template's own code, to be marked and changed into the template's comment.

/** The comment that does the job in each kind of template. */
export const COMMENT_OF = {
    blade: ['{{--', '--}}'],
    html: ['<!--', '-->'],
    'django-html': ['<!--', '-->'],
    twig: ['<!--', '-->'],
};

/** The places of a template where a `/*` is not HTML's text: script and style blocks, tags, comments, the template's own code. */
function codeRanges(text) {
    const ranges = [];
    const add = (re) => { for (const m of text.matchAll(re)) ranges.push([m.index, m.index + m[0].length]); };
    add(/<script\b[\s\S]*?(?:<\/script\s*>|$)/gi);
    add(/<style\b[\s\S]*?(?:<\/style\s*>|$)/gi);
    add(/<!--[\s\S]*?(?:-->|$)/g);
    add(/\{\{--[\s\S]*?(?:--\}\}|$)/g);
    add(/\{\{[\s\S]*?\}\}/g);
    add(/\{!![\s\S]*?!!\}/g);
    add(/\{%[\s\S]*?%\}/g);
    add(/\{#[\s\S]*?#\}/g);
    add(/@php\b(?!\s*\()[\s\S]*?(?:@endphp\b|$)/g);
    add(/<\?(?:php|=)?[\s\S]*?(?:\?>|$)/g);
    add(/<\/?[A-Za-z][^<>]*>/g);
    return ranges.sort((a, b) => a[0] - b[0]);
}

/** Each `/* … *\/` of a template's HTML: { start, end } (end after the `*\/`). One that is not closed is left alone. */
export function strayBlockComments(text) {
    const code = codeRanges(text);
    const inCode = (i) => code.some(([s, e]) => i >= s && i < e);
    const found = [];
    let from = 0;
    for (;;) {
        let open = text.indexOf('/*', from);
        while (open >= 0 && inCode(open)) open = text.indexOf('/*', open + 2);
        if (open < 0) break;
        let close = text.indexOf('*/', open + 2);
        while (close >= 0 && inCode(close)) close = text.indexOf('*/', close + 2);
        if (close < 0) break;
        found.push({ start: open, end: close + 2 });
        from = close + 2;
    }
    return found;
}

/** The text that replaces a `/* … *\/` of a template: the same content in the template's own comment (a space inside each end kept or added). */
export function asTemplateComment(inner, language) {
    const [open, close] = COMMENT_OF[language] ?? COMMENT_OF.html;
    return open + (/^\s/.test(inner) ? '' : ' ') + inner + (/\s$/.test(inner) ? '' : ' ') + close;
}

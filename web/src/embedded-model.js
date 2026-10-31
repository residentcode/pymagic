// The CSS and JavaScript inside a template, as documents of their own that keep the template's offsets: everything outside the
// <style> and <script> blocks (and style="" attributes, for CSS) is blank, line breaks kept, so a finding of the CSS or JavaScript
// service at a place of the document is at the same place of the template. A {{ … }} or {% … %} inside a block becomes a value of the
// same length, so it is not reported as broken code.

const blankOut = (text) => text.replace(/[^\n]/g, ' ');

/** A template expression inside code: a value as long as it (`0` and spaces), or spaces when it stands alone on its line. */
function neutral(code) {
    return code.replace(/\{\{[\s\S]*?\}\}|\{%[\s\S]*?%\}|\{#[\s\S]*?#\}/g, (m) => (m.startsWith('{{') ? '0' + blankOut(m.slice(1)) : blankOut(m)));
}

/** The regions of a language in a template: [start, end) of each block's content (and of each style="" value for CSS). */
export function regionsOf(text, language) {
    const out = [];
    const tag = language === 'css' ? 'style' : 'script';
    const open = new RegExp(`<${tag}\\b[^>]*>`, 'gi');
    let m;
    while ((m = open.exec(text))) {
        // a script that is not JavaScript (a template, JSON data) is left out
        if (tag === 'script' && /\btype\s*=\s*["']?(?!(text|application)\/(javascript|ecmascript)|module)[\w/+-]+/i.test(m[0])) continue;
        const start = m.index + m[0].length;
        const close = text.toLowerCase().indexOf(`</${tag}`, start);
        const end = close < 0 ? text.length : close;
        if (end > start) out.push([start, end]);
        open.lastIndex = end;
    }
    if (language === 'css') {
        const attr = /\sstyle\s*=\s*(["'])([^"']*)\1/gi;
        while ((m = attr.exec(text))) {
            const start = m.index + m[0].indexOf(m[1]) + 1;
            out.push([start, start + m[2].length]);
        }
    }
    return out.sort((a, b) => a[0] - b[0]);
}

/** The document of one language: the template's text blank outside its regions. A style="" value is wrapped as a rule so CSS reads it (`*{…}`). */
export function embeddedDocument(text, language) {
    const regions = regionsOf(text, language);
    let out = '';
    let at = 0;
    for (const [start, end] of regions) {
        out += blankOut(text.slice(at, start));
        const code = neutral(text.slice(start, end));
        // an attribute's declarations become a rule: two of the blanked characters before it carry "*{" and the closing quote "}"
        const isAttribute = language === 'css' && /=\s*["']$/.test(text.slice(Math.max(0, start - 3), start));
        if (isAttribute && out.length >= 2 && out.endsWith('  ')) out = out.slice(0, -2) + '*{';
        out += code;
        at = end;
        if (isAttribute && at < text.length) { out += '}'; at += 1; }
    }
    out += blankOut(text.slice(at));
    return out;
}

/** Whether an offset is inside one of a language's regions (for completion). */
export function inRegion(text, offset, language) {
    return regionsOf(text, language).some(([s, e]) => offset >= s && offset <= e);
}

/** CSS property names offered inside a style block or attribute (the common ones; the CSS service checks the rest). */
export const CSS_PROPERTIES = ('align-content align-items align-self animation animation-delay animation-duration animation-name background background-color background-image background-position '
    + 'background-repeat background-size border border-bottom border-collapse border-color border-left border-radius border-right border-style border-top border-width bottom box-shadow '
    + 'box-sizing clear color column-gap columns content cursor display filter flex flex-basis flex-direction flex-flow flex-grow flex-shrink flex-wrap float font font-family font-size '
    + 'font-style font-weight gap grid grid-area grid-column grid-gap grid-row grid-template-areas grid-template-columns grid-template-rows height inset justify-content justify-items '
    + 'justify-self left letter-spacing line-height list-style margin margin-bottom margin-left margin-right margin-top max-height max-width min-height min-width object-fit opacity '
    + 'order outline overflow overflow-x overflow-y padding padding-bottom padding-left padding-right padding-top place-items pointer-events position right row-gap text-align '
    + 'text-decoration text-overflow text-shadow text-transform top transform transition transition-duration user-select vertical-align visibility white-space width word-break z-index').split(' ');

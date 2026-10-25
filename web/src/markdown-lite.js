// A small Markdown reader for notebook cells. It returns a plain structure (never HTML), so the page builds elements
// from it with textContent and nothing in a cell can inject markup. No DOM here: web/test/markdown.test.mjs checks it.
//
// Blocks:  { type: 'heading', level, inline } | { type: 'paragraph', inline } | { type: 'code', lang, text }
//          | { type: 'list', ordered, items: [inline] } | { type: 'quote', blocks } | { type: 'rule' }
// Inline:  [{ t: 'text' | 'bold' | 'italic' | 'code' | 'link' | 'image', text, href? }]

/** `**bold**`, `*italic*` / `_italic_`, `` `code` ``, [text](href) and ![alt](src). Links keep only http(s), mailto and relative targets. */
export function inlineOf(text) {
    const out = [];
    const push = (t, s, extra) => { if (s !== '' || t === 'image') out.push({ t, text: s, ...extra }); };
    let plain = '';
    const flush = () => { push('text', plain); plain = ''; };
    for (let i = 0; i < text.length;) {
        const rest = text.slice(i);
        let m;
        if ((m = /^`([^`\n]+)`/.exec(rest))) { flush(); push('code', m[1]); i += m[0].length; continue; }
        if ((m = /^\*\*([^\n]+?)\*\*/.exec(rest)) || (m = /^__([^\n]+?)__/.exec(rest))) { flush(); push('bold', m[1]); i += m[0].length; continue; }
        if ((m = /^\*([^*\s][^*\n]*?)\*/.exec(rest)) || (m = /^_([^_\s][^_\n]*?)_(?![A-Za-z0-9])/.exec(rest))) {
            if (m[0].startsWith('_') && i > 0 && /[A-Za-z0-9]/.test(text[i - 1])) { plain += text[i]; i++; continue; }
            flush(); push('italic', m[1]); i += m[0].length; continue;
        }
        if ((m = /^!\[([^\]\n]*)\]\(([^)\s]+)\)/.exec(rest))) { flush(); push('image', m[1], { href: m[2] }); i += m[0].length; continue; }
        if ((m = /^\[([^\]\n]+)\]\(([^)\s]+)\)/.exec(rest))) {
            flush();
            if (safeHref(m[2])) push('link', m[1], { href: m[2] }); else push('text', m[1]);
            i += m[0].length;
            continue;
        }
        if (text[i] === '\\' && i + 1 < text.length && /[\\`*_{}[\]()#+\-.!>]/.test(text[i + 1])) { plain += text[i + 1]; i += 2; continue; }
        plain += text[i];
        i++;
    }
    flush();
    return out;
}

/** http, https, mailto and anything relative; never javascript:, data: and the like. */
export function safeHref(href) {
    const scheme = /^([A-Za-z][A-Za-z0-9+.-]*):/.exec(href.trim());
    return !scheme || ['http', 'https', 'mailto'].includes(scheme[1].toLowerCase());
}

export function blocksOf(text) {
    const lines = text.replace(/\r\n?/g, '\n').split('\n');
    const blocks = [];
    let paragraph = [];
    const endParagraph = () => {
        if (paragraph.length) blocks.push({ type: 'paragraph', inline: inlineOf(paragraph.join(' ')) });
        paragraph = [];
    };
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        let m;
        if ((m = /^(`{3,}|~{3,})\s*([\w+-]*)\s*$/.exec(line))) {
            endParagraph();
            const body = [];
            for (i++; i < lines.length && !lines[i].startsWith(m[1]); i++) body.push(lines[i]);
            blocks.push({ type: 'code', lang: m[2], text: body.join('\n') });
        } else if (/^\s*$/.test(line)) endParagraph();
        else if ((m = /^(#{1,6})\s+(.*?)\s*#*\s*$/.exec(line))) { endParagraph(); blocks.push({ type: 'heading', level: m[1].length, inline: inlineOf(m[2]) }); }
        else if (/^\s{0,3}([-*_])(\s*\1){2,}\s*$/.test(line)) { endParagraph(); blocks.push({ type: 'rule' }); }
        else if (/^\s{0,3}>/.test(line)) {
            endParagraph();
            const quoted = [];
            for (; i < lines.length && /^\s{0,3}>/.test(lines[i]); i++) quoted.push(lines[i].replace(/^\s{0,3}>\s?/, ''));
            i--;
            blocks.push({ type: 'quote', blocks: blocksOf(quoted.join('\n')) });
        } else if ((m = /^\s*([-*+]|\d+[.)])\s+(.*)$/.exec(line))) {
            endParagraph();
            const ordered = /\d/.test(m[1]);
            const items = [];
            for (; i < lines.length; i++) {
                const item = /^\s*([-*+]|\d+[.)])\s+(.*)$/.exec(lines[i]);
                if (item && /\d/.test(item[1]) === ordered) items.push(inlineOf(item[2]));
                else if (items.length && /^\s{2,}\S/.test(lines[i])) items[items.length - 1] = items[items.length - 1].concat(inlineOf(' ' + lines[i].trim()));
                else break;
            }
            i--;
            blocks.push({ type: 'list', ordered, items });
        } else paragraph.push(line.trim());
    }
    endParagraph();
    return blocks;
}

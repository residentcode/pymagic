// The two names of an element, its opening tag's and its closing tag's, so that renaming one renames the other (the HTML tag pairs plugin; the
// editor's linked editing). The tags are read as a browser reads them: comments, template tags ({% %}, {{ }}, {# #}, <% %>, <? ?>) and the text of
// <script> and <style> are not tags; void elements (br, img, input, ...) and self-closing tags (<Icon />) have no partner; a closing tag closes the
// nearest open element of its name, or the innermost one when none has it (while one of the two names is being typed). No DOM here:
// web/test/tag-pairs.test.mjs checks it.

export const VOID_ELEMENTS = new Set(['area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr', 'keygen', 'command']);

const SKIPPED = [['<!--', '-->'], ['<![CDATA[', ']]>'], ['{#', '#}'], ['{%', '%}'], ['{{', '}}'], ['<%', '%>'], ['<?', '?>']];
const NAME = /[A-Za-z][\w:.-]*/y;

/** The tags of a text: { name, start, end (the name's), closing, selfClosing }, in order. */
export function scanTags(text) {
    const tags = [];
    let i = 0;
    const n = text.length;
    while (i < n) {
        const lt = text.indexOf('<', i);
        const brace = text.indexOf('{', i);
        const next = lt < 0 ? brace : brace < 0 ? lt : Math.min(lt, brace);
        if (next < 0) break;
        i = next;
        const skip = SKIPPED.find(([open]) => text.startsWith(open, i));
        if (skip) {
            const close = text.indexOf(skip[1], i + skip[0].length);
            i = close < 0 ? n : close + skip[1].length;
            continue;
        }
        if (text[i] !== '<') { i++; continue; }
        const closing = text[i + 1] === '/';
        NAME.lastIndex = i + (closing ? 2 : 1);
        const m = NAME.exec(text);
        if (!m) { i++; continue; }
        const start = m.index, end = start + m[0].length;
        // the end of the tag: the > that is not in a quoted value or a JSX {expression}
        let j = end, quote = '', depth = 0;
        for (; j < n; j++) {
            const c = text[j];
            if (quote) { if (c === quote) quote = ''; continue; }
            if (c === '"' || c === "'") quote = c;
            else if (c === '{') depth++;
            else if (c === '}') depth = Math.max(0, depth - 1);
            else if (c === '>' && depth === 0) break;
        }
        const selfClosing = !closing && text[j - 1] === '/';
        tags.push({ name: m[0], start, end, closing, selfClosing });
        i = j + 1;
        // the text of <script> and <style> is not markup
        const lower = m[0].toLowerCase();
        if (!closing && !selfClosing && (lower === 'script' || lower === 'style')) {
            const stop = text.toLowerCase().indexOf('</' + lower, i);
            i = stop < 0 ? n : stop;
        }
    }
    return tags;
}

/** Each opening tag with its closing tag: Map from a tag's index in scanTags to its partner's. */
export function pairTags(tags) {
    const partner = new Map();
    const open = [];
    tags.forEach((t, index) => {
        if (!t.closing) {
            if (!t.selfClosing && !VOID_ELEMENTS.has(t.name.toLowerCase())) open.push(index);
            return;
        }
        if (open.length === 0) return;
        // the nearest open element of this name; when none has it, the innermost (its name is being changed)
        let at = -1;
        for (let k = open.length - 1; k >= 0; k--) if (tags[open[k]].name === t.name) { at = k; break; }
        if (at < 0) for (let k = open.length - 1; k >= 0; k--) if (tags[open[k]].name.toLowerCase() === t.name.toLowerCase()) { at = k; break; }
        if (at < 0) at = open.length - 1;
        const opener = open[at];
        open.length = at;
        partner.set(opener, index);
        partner.set(index, opener);
    });
    return partner;
}

/** The ranges ([start, end] offsets) of the two names of the element whose tag name the offset is in or at the end of, or null. */
export function tagPairAt(text, offset) {
    const tags = scanTags(text);
    const index = tags.findIndex((t) => t.start <= offset && offset <= t.end);
    if (index < 0) return null;
    const other = pairTags(tags).get(index);
    if (other === undefined) return null;
    const a = tags[index], b = tags[other];
    return a.start < b.start ? [[a.start, a.end], [b.start, b.end]] : [[b.start, b.end], [a.start, a.end]];
}

// The syntax checks of HTML and YAML while typing (web/src/htmlcheck.js puts them on the editor): an HTML file's or template's tags (one never
// ended, an element never closed, a closing tag that matches nothing, a comment never ended), and YAML's errors from its parser. No DOM, no
// Monaco here: web/test/htmlcheck.test.mjs checks it. Each problem is { start, end, message } (offsets of the text).
import { parseAllDocuments } from 'yaml';

/** Elements without an end tag. */
const VOID = new Set(['area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr', 'keygen', 'command', 'basefont', 'frame', 'isindex']);
/** Elements HTML lets end without their end tag (closed by what follows them, or by their parent's end). */
const OPTIONAL_END = new Set(['p', 'li', 'dt', 'dd', 'option', 'optgroup', 'rp', 'rt', 'rb', 'rtc', 'tr', 'td', 'th', 'thead', 'tbody', 'tfoot', 'colgroup', 'caption', 'html', 'head', 'body']);
/** Elements whose text is not markup: up to their end tag. */
const RAW = new Set(['script', 'style', 'textarea', 'title', 'xmp', 'iframe', 'noembed', 'noframes', 'plaintext']);
/** An element that, opened, ends the open ones of these names first (as HTML's parser does). */
const ENDS = {
    li: ['li'], dt: ['dt', 'dd'], dd: ['dt', 'dd'], option: ['option'], optgroup: ['optgroup', 'option'], tr: ['tr', 'td', 'th'], td: ['td', 'th'], th: ['td', 'th'],
    thead: ['tbody', 'tfoot', 'tr', 'td', 'th'], tbody: ['thead', 'tbody', 'tfoot', 'tr', 'td', 'th'], tfoot: ['thead', 'tbody', 'tr', 'td', 'th'], rt: ['rt', 'rp'], rp: ['rt', 'rp'],
};
/** Elements that end an open <p>. */
const ENDS_P = new Set(['address', 'article', 'aside', 'blockquote', 'details', 'dialog', 'div', 'dl', 'fieldset', 'figcaption', 'figure', 'footer', 'form', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
    'header', 'hgroup', 'hr', 'main', 'menu', 'nav', 'ol', 'p', 'pre', 'section', 'table', 'ul']);

/** A template's {% %}, {{ }} and {# #} as spaces (their lines kept), so the HTML around them is read alone. */
export function blankTemplate(text) {
    return text.replace(/\{%[\s\S]*?%\}|\{\{[\s\S]*?\}\}|\{#[\s\S]*?#\}/g, (m) => m.replace(/[^\n]/g, ' '));
}

/** Whether a template has branches ({% else %}, {% elif %}, {% empty %}), where each may open its own tag: then only broken syntax is told. */
export function hasBranches(text) {
    return /\{%[-+]?\s*(else|elif|empty)\b/.test(text);
}

/**
 * The HTML problems of a text (a template's tags blanked first). `balance`: also elements never closed and closing tags matching nothing
 * (false for a template with branches).
 */
export function htmlProblems(source, { balance = true } = {}) {
    const text = blankTemplate(source);
    const lower = text.toLowerCase();
    const problems = [];
    const stack = [];   // { name, start, end }
    const isNameStart = (c) => /[A-Za-z]/.test(c ?? '');
    let i = 0;
    const close = (k) => { stack.length = k; };
    while (i < text.length) {
        const lt = text.indexOf('<', i);
        if (lt < 0) break;
        i = lt + 1;
        if (text.startsWith('<!--', lt)) {
            const end = text.indexOf('-->', lt + 4);
            if (end < 0) { problems.push({ start: lt, end: lt + 4, message: 'Comment is not closed: add -->' }); break; }
            i = end + 3;
            continue;
        }
        if (text[lt + 1] === '!' || text[lt + 1] === '?') {   // <!DOCTYPE …>, <![CDATA[…]]>, <?xml …?>
            const end = text.indexOf('>', lt);
            i = end < 0 ? text.length : end + 1;
            continue;
        }
        const closing = text[lt + 1] === '/';
        const nameAt = lt + (closing ? 2 : 1);
        if (!isNameStart(text[nameAt])) continue;   // a < of text (a < b)
        let j = nameAt;
        while (j < text.length && /[\w:.-]/.test(text[j])) j++;
        const name = lower.slice(nameAt, j);
        // to the tag's > (quoted values may hold one); another < first means this tag was never ended
        let quote = null, end = -1, broken = -1;
        for (let k = j; k < text.length; k++) {
            const c = text[k];
            if (quote) { if (c === quote) quote = null; continue; }
            if (c === '"' || c === "'") { if (/[=\s]/.test(text[k - 1] ?? '') || text[k - 1] === '=') quote = c; continue; }
            if (c === '>') { end = k; break; }
            if (c === '<' && isNameStart(text[k + 1] ?? '') || c === '<' && text[k + 1] === '/') { broken = k; break; }
        }
        if (end < 0) {
            problems.push({ start: lt, end: j, message: quote ? `Tag <${closing ? '/' : ''}${name}> is not ended: a quote of an attribute is not closed` : `Tag <${closing ? '/' : ''}${name}> is not ended: add >` });
            if (broken < 0) break;
            i = broken;
            continue;
        }
        i = end + 1;
        if (closing) {
            if (!balance) continue;
            const k = stack.map((s) => s.name).lastIndexOf(name);
            if (k < 0) {
                // </p> and </br> HTML takes as <p></p> and <br>; still a mistake to write
                problems.push({ start: lt, end: end + 1, message: `Closing tag </${name}> matches nothing` });
                continue;
            }
            for (let m = stack.length - 1; m > k; m--)
                if (!OPTIONAL_END.has(stack[m].name)) problems.push({ start: stack[m].start, end: stack[m].end, message: `Element <${stack[m].name}> is not closed: add </${stack[m].name}> before </${name}>` });
            close(k);
            continue;
        }
        const selfClosed = text[end - 1] === '/';
        if (RAW.has(name) && !selfClosed) {
            const endTag = lower.indexOf('</' + name, end + 1);
            if (endTag < 0) { problems.push({ start: lt, end: j, message: `Element <${name}> is not closed: add </${name}>` }); break; }
            const gt = text.indexOf('>', endTag);
            i = gt < 0 ? text.length : gt + 1;
            continue;
        }
        if (!balance || VOID.has(name) || selfClosed) continue;
        // what HTML ends by itself when this element opens
        if (ENDS_P.has(name)) { const k = stack.map((s) => s.name).lastIndexOf('p'); if (k >= 0 && stack.slice(k + 1).every((s) => OPTIONAL_END.has(s.name) || !ENDS_P.has(s.name))) close(k); }
        if (ENDS[name] && stack.length && ENDS[name].includes(stack[stack.length - 1].name)) {
            let k = stack.length - 1;
            while (k > 0 && ENDS[name].includes(stack[k - 1].name)) k--;
            close(k);
        }
        stack.push({ name, start: lt, end: j });
    }
    if (balance)
        for (const s of stack)
            if (!OPTIONAL_END.has(s.name)) problems.push({ start: s.start, end: s.end, message: `Element <${s.name}> is not closed: add </${s.name}>` });
    return problems.sort((a, b) => a.start - b.start);
}

/** A template's or HTML file's problems as the editor shows them: balance left out where the template has branches. */
export function templateProblems(text) {
    return htmlProblems(text, { balance: !hasBranches(text) });
}

/** Whether a YAML file is a template of another tool (Helm's templates/*.yaml with {{ }}): not YAML until it is filled. */
export function isYamlTemplate(path, text) {
    return /(^|\/)templates\//i.test(path) && text.includes('{{');
}

/** YAML's syntax errors (its parser's; not its warnings, such as an unknown !Tag of CloudFormation). */
export function yamlProblems(text) {
    let docs;
    try { docs = parseAllDocuments(text, { prettyErrors: false, uniqueKeys: true }); }
    catch (e) { return [{ start: 0, end: 1, message: String(e.message ?? e) }]; }
    const list = Array.isArray(docs) ? docs : [docs];   // an empty text gives an empty stream
    const out = [];
    for (const d of list)
        for (const e of d.errors ?? []) {
            const [start, end] = e.pos ?? [0, 1];
            out.push({ start, end: Math.max(end, start + 1), message: String(e.message).split('\n')[0].replace(/ at line \d+, column \d+:?$/, '') });
        }
    return out;
}

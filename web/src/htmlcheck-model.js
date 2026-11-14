// The syntax checks of HTML and YAML while typing (web/src/htmlcheck.js puts them on the editor): an HTML file's or template's tags (one never
// ended, an element never closed, a closing tag that matches nothing, a comment never ended), and YAML's errors from its parser. No DOM, no
// Monaco here: web/test/htmlcheck.test.mjs checks it. Each problem is { start, end, message } (offsets of the text).
import { parseAllDocuments } from 'yaml';

/** Elements without an end tag. */
export const VOID = new Set(['area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr', 'keygen', 'command', 'basefont', 'frame', 'isindex']);
/** Elements HTML lets end without their end tag (closed by what follows them, or by their parent's end). */
export const OPTIONAL_END = new Set(['p', 'li', 'dt', 'dd', 'option', 'optgroup', 'rp', 'rt', 'rb', 'rtc', 'tr', 'td', 'th', 'thead', 'tbody', 'tfoot', 'colgroup', 'caption', 'html', 'head', 'body']);
/** Elements whose text is not markup: up to their end tag. */
export const RAW = new Set(['script', 'style', 'textarea', 'title', 'xmp', 'iframe', 'noembed', 'noframes', 'plaintext']);
/** An element that, opened, ends the open ones of these names first (as HTML's parser does). */
export const ENDS = {
    li: ['li'], dt: ['dt', 'dd'], dd: ['dt', 'dd'], option: ['option'], optgroup: ['optgroup', 'option'], tr: ['tr', 'td', 'th'], td: ['td', 'th'], th: ['td', 'th'],
    thead: ['tbody', 'tfoot', 'tr', 'td', 'th'], tbody: ['thead', 'tbody', 'tfoot', 'tr', 'td', 'th'], tfoot: ['thead', 'tbody', 'tr', 'td', 'th'], rt: ['rt', 'rp'], rp: ['rt', 'rp'],
};
/** Elements that end an open <p>. */
export const ENDS_P = new Set(['address', 'article', 'aside', 'blockquote', 'details', 'dialog', 'div', 'dl', 'fieldset', 'figcaption', 'figure', 'footer', 'form', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
    'header', 'hgroup', 'hr', 'main', 'menu', 'nav', 'ol', 'p', 'pre', 'section', 'table', 'ul']);

/** A template's {% %}, {{ }} and {# #} as spaces (their lines kept), so the HTML around them is read alone. */
export function blankTemplate(text) {
    // (what {% comment %} holds is not written either)
    return text.replace(/\{%[-+]?\s*comment\b[\s\S]*?\{%[-+]?\s*endcomment\b[\s\S]*?%\}/g, (m) => m.replace(/[^\n]/g, ' '))
        .replace(/\{%[\s\S]*?%\}|\{\{[\s\S]*?\}\}|\{#[\s\S]*?#\}/g, (m) => m.replace(/[^\n]/g, ' '));
}

/** Whether a template has branches ({% else %}, {% elif %}, {% empty %}), where each may open its own tag: then only broken syntax is told. */
export function hasBranches(text) {
    return /\{%[-+]?\s*(else|elif|empty)\b/.test(text);
}

const spaces = (m) => m.replace(/[^\n]/g, ' ');

/**
 * A Django or Jinja template's block tags, in the order they come: [{ index, kind: 'open' | 'else' | 'close', name, branching, last }]. Every
 * tag opens (one that is no block is dropped when the block around it ends); {% else %} and {% empty %} are a block's last branch,
 * {% elif %} one more; {% endif %} closes the nearest if. What is inside {# #} and {% comment %} is no tag.
 */
export function djangoBlocks(source) {
    const text = source.replace(/\{#[\s\S]*?#\}/g, spaces).replace(/(\{%[-+]?\s*comment\b[\s\S]*?%\})([\s\S]*?)(?=\{%[-+]?\s*endcomment\b)/g, (m, tag, inside) => tag + spaces(inside));
    const out = [];
    for (const m of text.matchAll(/\{%[-+]?\s*([A-Za-z_]\w*)[\s\S]*?%\}/g)) {
        const name = m[1];
        if (name === 'else' || name === 'empty') out.push({ index: m.index, kind: 'else', last: true });
        else if (name === 'elif') out.push({ index: m.index, kind: 'else', last: false });
        else if (name.startsWith('end') && name.length > 3) out.push({ index: m.index, kind: 'close', name: name.slice(3) });
        else out.push({ index: m.index, kind: 'open', name, branching: /^(if|ifchanged|ifequal|ifnotequal|for)$/.test(name) });
    }
    return out;
}

/**
 * The HTML problems of a text: tags never ended, comments never closed and, with `balance`, elements never closed and closing tags matching
 * nothing. `blank` takes the text of another language out first (a template's tags). `blocks` are that language's blocks (djangoBlocks'
 * form): each branch of one starts with the elements open where the block starts, and when its branches leave different elements open
 * (an {% if %} that opens a <div> another {% if %} closes) balance is not told from there on, only what was sure before. `strict`: no
 * element ends by itself (a component's markup: Vue and Svelte want every </p> and </li>).
 */
export function htmlProblems(source, { balance = true, blank = blankTemplate, blocks = [], strict = false } = {}) {
    const text = blank(source);
    const lower = text.toLowerCase();
    const problems = [];
    const stack = [];   // { name, start, end }
    const isNameStart = (c) => /[A-Za-z]/.test(c ?? '');
    const optional = (name) => !strict && OPTIONAL_END.has(name);
    let i = 0;
    const close = (k) => { stack.length = k; };

    // the blocks of the template around the tags
    const frames = [];      // the open ones: { name, branching, start: the elements open at its start, ends: those at each branch's end, other }
    let unsure = false, b = 0;
    // (an element HTML ends by itself, a <li> left open by one branch, makes no difference between branches)
    const names = (s) => s.filter((x) => !optional(x.name)).map((x) => x.name).join(' ');
    const setStack = (to) => { stack.length = 0; stack.push(...to); };
    // a component's block holds whole elements: what a branch opened and left open is told there, and the branch ends as it started
    const whole = (f) => {
        for (const s of stack.slice(f.start.length)) problems.push({ start: s.start, end: s.end, message: `Element <${s.name}> is not closed: add </${s.name}>` });
        setStack(f.start);
    };
    function blocksUntil(pos) {
        for (; b < blocks.length && blocks[b].index < pos; b++) {
            const e = blocks[b];
            if (e.kind === 'open') frames.push({ name: e.name, branching: !!e.branching, start: stack.slice(), ends: [], other: false });
            else if (e.kind === 'else') {
                let k = frames.length - 1;
                while (k >= 0 && !frames[k].branching) k--;
                if (k < 0) continue;
                frames.length = k + 1;
                const f = frames[k];
                if (strict) { whole(f); continue; }
                f.ends.push(stack.slice());
                if (e.last) f.other = true;
                setStack(f.start);
            }
            else {
                let k = frames.length - 1;
                while (k >= 0 && frames[k].name !== e.name) k--;
                if (k < 0) continue;
                const f = frames[k];
                frames.length = k;
                if (strict) { whole(f); continue; }
                if (!f.branching) continue;
                f.ends.push(stack.slice());
                if (!f.other) f.ends.push(f.start);      // no {% else %}: the branch that writes nothing
                if (f.ends.some((x) => names(x) !== names(f.ends[0]))) unsure = true;
                setStack(f.ends[0]);
            }
        }
    }
    const blocksSkip = (pos) => { while (b < blocks.length && blocks[b].index < pos) b++; };   // tags inside an HTML tag, a comment, a script

    while (i < text.length) {
        const lt = text.indexOf('<', i);
        if (lt < 0) break;
        blocksUntil(lt);
        i = lt + 1;
        if (text.startsWith('<!--', lt)) {
            const end = text.indexOf('-->', lt + 4);
            if (end < 0) { problems.push({ start: lt, end: lt + 4, message: 'Comment is not closed: add -->' }); break; }
            i = end + 3;
            blocksSkip(i);
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
        blocksSkip(i);
        if (closing) {
            if (!balance) continue;
            const k = stack.map((s) => s.name).lastIndexOf(name);
            if (k < 0) {
                // </p> and </br> HTML takes as <p></p> and <br>; still a mistake to write
                if (!unsure) problems.push({ start: lt, end: end + 1, message: `Closing tag </${name}> matches nothing` });
                continue;
            }
            for (let m = stack.length - 1; m > k; m--)
                if (!optional(stack[m].name) && !unsure) problems.push({ start: stack[m].start, end: stack[m].end, message: `Element <${stack[m].name}> is not closed: add </${stack[m].name}> before </${name}>` });
            close(k);
            continue;
        }
        const selfClosed = text[end - 1] === '/';
        if (RAW.has(name) && !selfClosed) {
            const endTag = lower.indexOf('</' + name, end + 1);
            if (endTag < 0) { problems.push({ start: lt, end: j, message: `Element <${name}> is not closed: add </${name}>` }); break; }
            const gt = text.indexOf('>', endTag);
            i = gt < 0 ? text.length : gt + 1;
            blocksSkip(i);
            continue;
        }
        if (!balance || VOID.has(name) || selfClosed) continue;
        // what HTML ends by itself when this element opens
        if (!strict && ENDS_P.has(name)) { const k = stack.map((s) => s.name).lastIndexOf('p'); if (k >= 0 && stack.slice(k + 1).every((s) => OPTIONAL_END.has(s.name) || !ENDS_P.has(s.name))) close(k); }
        if (!strict && ENDS[name] && stack.length && ENDS[name].includes(stack[stack.length - 1].name)) {
            let k = stack.length - 1;
            while (k > 0 && ENDS[name].includes(stack[k - 1].name)) k--;
            close(k);
        }
        stack.push({ name, start: lt, end: j });
    }
    blocksUntil(Infinity);
    if (balance && !unsure)
        for (const s of stack)
            if (!optional(s.name)) problems.push({ start: s.start, end: s.end, message: `Element <${s.name}> is not closed: add </${s.name}>` });
    return problems.sort((x, y) => x.start - y.start);
}

/** A template's or HTML file's problems as the editor shows them: each branch of an {% if %} read with the elements open before it. */
export function templateProblems(text) {
    return htmlProblems(text, { blocks: djangoBlocks(text) });
}

// ------------------------------------------------------------ components and Blade: markup with a syntax of its own around it

/** The end of what starts at an opening bracket: its matching closing one (quoted text and brackets inside passed over), or -1. */
function matching(text, at, open, shut) {
    let depth = 0, quote = null;
    for (let k = at; k < text.length; k++) {
        const c = text[k];
        if (quote) { if (c === '\\') k++; else if (c === quote) quote = null; continue; }
        if (c === '"' || c === "'" || c === '`') quote = c;
        else if (c === open) depth++;
        else if (c === shut && --depth === 0) return k;
    }
    return -1;
}

/** The places of <script> and <style> elements' text: [[start, end]], not markup. */
function rawRanges(text) {
    const out = [];
    for (const m of text.matchAll(/<(script|style)\b[^>]*>/gi)) {
        const end = text.toLowerCase().indexOf('</' + m[1].toLowerCase(), m.index + m[0].length);
        out.push([m.index + m[0].length, end < 0 ? text.length : end]);
    }
    return out;
}

/**
 * A Vue component (.vue): every element closed (Vue's compiler wants each end tag), {{ }} left out, a <template lang="pug"> not read.
 */
export const vueText = (text) => text.replace(/(<template\b[^>]*\blang=["']?pug\b[^>]*>)([\s\S]*)(?=<\/template>)/i, (m, tag, inside) => tag + spaces(inside))
    .replace(/\{\{[\s\S]*?\}\}/g, spaces);
export function vueProblems(source) {
    return htmlProblems(source, { blank: vueText, strict: true });
}

/**
 * A Svelte component (.svelte): every element closed, its { } expressions left out (they may hold < and >), and its blocks followed as a
 * template's: {#if} {:else if} {:else} {/if}, {#each} {:else} {/each}, {#await} {:then} {:catch} {/await}, {#key}, {#snippet}.
 */
export function svelteProblems(source) {
    const { text, blocks } = svelteParts(source);
    return htmlProblems(source, { blank: () => text, blocks, strict: true });
}

/** A Svelte component's markup with its { } blanked, and its blocks: { text, blocks }. */
export function svelteParts(source) {
    const raw = rawRanges(source);
    const blocks = [];
    let text = '', at = 0;
    for (let i = 0; i < source.length; i++) {
        if (source[i] !== '{') continue;
        const inside = raw.find(([s, e]) => i >= s && i < e);
        if (inside) { i = inside[1]; continue; }
        const end = matching(source, i, '{', '}');
        if (end < 0) break;
        const m = /^\{\s*([#:/])\s*(\w+)(\s+if\b)?/.exec(source.slice(i, end + 1));
        if (m) {
            if (m[1] === '#') blocks.push({ index: i, kind: 'open', name: m[2], branching: true });
            else if (m[1] === ':') blocks.push({ index: i, kind: 'else', last: !m[3] });
            else blocks.push({ index: i, kind: 'close', name: m[2] });
        }
        text += source.slice(at, i) + spaces(source.slice(i, end + 1));
        at = end + 1;
        i = end;
    }
    text += source.slice(at);
    return { text, blocks };
}

const BLADE_BRANCHING = /^(if|unless|isset|auth|guest|can|cannot|canany|env|production|foreach|for|while|forelse|hasSection|sectionMissing|error|switch|empty)$/;

/**
 * A Blade view: HTML's rules, with {{ }}, {!! !!}, {{-- --}}, @php … @endphp and each directive's (…) left out, and the directives' blocks
 * followed as a template's: @if @elseif @else @endif, @foreach, @forelse @empty @endforelse, @switch @case @default, @section … @endsection
 * (or @show, @stop, @append, @overwrite).
 */
export function bladeProblems(source) {
    const { text, blocks } = bladeParts(source);
    return htmlProblems(source, { blank: () => text, blocks });
}

/** A Blade view's markup with its echoes and directives' arguments blanked, and its directives as blocks: { text, blocks }. */
export function bladeParts(source) {
    let text = source.replace(/\{\{--[\s\S]*?--\}\}|\{\{[\s\S]*?\}\}|\{!![\s\S]*?!!\}/g, spaces)
        .replace(/(@php\b(?!\s*\())([\s\S]*?)(?=@endphp\b)/g, (m, tag, inside) => tag + spaces(inside))
        .replace(/(@verbatim\b)([\s\S]*?)(?=@endverbatim\b)/g, (m, tag, inside) => tag + spaces(inside));
    const blocks = [];
    const re = /@([A-Za-z]\w*)/g;
    let m;
    while ((m = re.exec(text))) {
        const before = text[m.index - 1] ?? '';
        if (/[\w@.]/.test(before)) continue;                        // an address, @@escaped
        const name = m[1];
        let end = m.index + m[0].length, args = false;
        const paren = /^\s*\(/.exec(text.slice(end));
        if (paren) {
            const shut = matching(text, end + paren[0].length - 1, '(', ')');
            if (shut > 0) {
                args = true;
                text = text.slice(0, end) + spaces(text.slice(end, shut + 1)) + text.slice(shut + 1);
                end = shut + 1;
            }
        }
        re.lastIndex = end;
        if (name === 'else' || name === 'default' || (name === 'empty' && !args)) blocks.push({ index: m.index, kind: 'else', last: true });
        else if (name === 'elseif' || name === 'case') blocks.push({ index: m.index, kind: 'else', last: false });
        else if (/^(show|stop|append|overwrite)$/.test(name)) blocks.push({ index: m.index, kind: 'close', name: 'section' });
        else if (name.startsWith('end') && name.length > 3) blocks.push({ index: m.index, kind: 'close', name: name.slice(3) });
        else blocks.push({ index: m.index, kind: 'open', name, branching: BLADE_BRANCHING.test(name) });
    }
    return { text, blocks };
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

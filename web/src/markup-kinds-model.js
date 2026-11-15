// More kinds of markup for the syntax check (htmlcheck-model.js's htmlProblems) and the indentation marks (html-indent-model.js): Handlebars
// and Mustache templates, EJS pages and Astro components. Each gives its markup alone (its own syntax as spaces, the lines kept) and its
// blocks in djangoBlocks' form, so the elements are read around them as in a Django template. No DOM, no Monaco here.
import { htmlProblems } from './htmlcheck-model.js';

const spaces = (m) => m.replace(/[^\n]/g, ' ');

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

// ------------------------------------------------------------ Handlebars and Mustache

/**
 * A Handlebars template's markup with its {{ }} blanked, and its blocks: {{#if}} {{else if}} {{else}} {{/if}}, {{#each}}, {{#with}}, any
 * helper's {{#name}} … {{/name}}, Mustache's inverted {{^name}} … {{/name}} and a block's {{^}}, a partial block {{#> layout}} … {{/layout}};
 * what a raw block {{{{raw}}}} … {{{{/raw}}}} and a comment hold is not read.
 */
export function handlebarsParts(source) {
    let text = source.replace(/(\{\{\{\{\s*([\w.-]+)[^}]*\}\}\}\})([\s\S]*?)(?=\{\{\{\{\s*\/\2\s*\}\}\}\})/g, (m, tag, name, inside) => tag + spaces(inside));
    const blocks = [];
    text = text.replace(/\{\{!--[\s\S]*?--\}\}|\{\{\{\{[\s\S]*?\}\}\}\}|\{\{\{[\s\S]*?\}\}\}|\{\{[\s\S]*?\}\}/g, (m, index) => {
        const tag = /^\{\{~?\s*([#^/])?\s*(>\s*|\*\s*)?([\w.@/-]*)\s*([\s\S]*?)~?\}\}$/.exec(m);
        if (tag && !m.startsWith('{{{') && !m.startsWith('{{!')) {
            const [, sign, , name, rest] = tag;
            if (sign === '#' || (sign === '^' && name)) blocks.push({ index, kind: 'open', name, branching: true });
            else if (sign === '/') blocks.push({ index, kind: 'close', name });
            else if (sign === '^') blocks.push({ index, kind: 'else', last: true });
            else if (name === 'else') blocks.push({ index, kind: 'else', last: !rest.trim() });
        }
        return spaces(m);
    });
    return { text, blocks };
}
export function handlebarsProblems(source) {
    const { text, blocks } = handlebarsParts(source);
    return htmlProblems(source, { blank: () => text, blocks });
}

// ------------------------------------------------------------ EJS (and Eta)

/** How many more braces a scriptlet's code opens than it closes (quoted text and comments not counted). */
function braces(code) {
    const plain = code.replace(/'(?:[^'\\\n]|\\.)*'|"(?:[^"\\\n]|\\.)*"|`(?:[^`\\]|\\.)*`|\/\/[^\n]*|\/\*[\s\S]*?\*\//g, '');
    return (plain.match(/\{/g) ?? []).length - (plain.match(/\}/g) ?? []).length;
}

/**
 * An EJS page's markup with its <% %> blanked, and its blocks: a scriptlet that leaves a brace open (<% if (x) { %>, <% items.forEach((i) => { %>)
 * opens one, <% } %> closes it, and <% } else { %> is its next branch.
 */
export function ejsParts(source) {
    const blocks = [];
    const text = source.replace(/<%%|<%([_=#-]?)([\s\S]*?)[-_]?%>/g, (m, sign, code, index) => {
        if (m === '<%%') return m.replace(/./g, ' ');
        if (sign !== '=' && sign !== '-' && sign !== '#') {
            const body = String(code).trim(), net = braces(body);
            if (/^\}/.test(body) && /\{$/.test(body) && net === 0) blocks.push({ index, kind: 'else', last: !/\belse\s+if\b|\bcase\b|\bcatch\b/.test(body) });
            else if (net > 0) blocks.push({ index, kind: 'open', name: 'block', branching: true });
            else if (net < 0) blocks.push({ index, kind: 'close', name: 'block' });
        }
        return spaces(m);
    });
    return { text, blocks };
}
export function ejsProblems(source) {
    const { text, blocks } = ejsParts(source);
    return htmlProblems(source, { blank: () => text, blocks });
}

// ------------------------------------------------------------ Astro

const ATTRS = /^(?:\s+[\w:@.\-[\]]+(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s"'<>=]+))?)*\s*\/?$/;

/**
 * An Astro component's markup alone: its front matter (--- … ---) and its { } expressions as spaces. An expression that holds elements
 * ({items.map((i) => <li>{i}</li>)}) keeps them, so they are read with the rest; `elements: false` leaves out what an expression of several
 * lines holds (for the indentation, which is the expression's own there). A fragment's <> and </> are no elements.
 */
export function astroText(source, { elements = true } = {}) {
    const out = source.split('');
    const blank = (from, to) => { for (let i = from; i < to; i++) if (out[i] !== '\n' && out[i] !== '\r') out[i] = ' '; };
    const front = /^\s*---[ \t]*\r?\n[\s\S]*?\r?\n---[ \t]*(?=\r?\n|$)/.exec(source);
    if (front) blank(0, front[0].length);
    const lower = source.toLowerCase();

    /** The end of a tag that starts at `at` (its >), braces and quoted values passed over; -1 when it is no tag. */
    function tagEnd(at, inExpression) {
        const name = /^<\/?[A-Za-z][\w:.-]*/.exec(source.slice(at, at + 80));
        if (!name) return -1;
        // (in an expression a < after a name or a bracket is a comparison or a type's argument: twice<number>(2), a <b)
        if (inExpression && name[0][1] !== '/' && /[\w$)\].]/.test(source[at - 1] ?? '')) return -1;
        let attrs = '';
        for (let i = at + name[0].length; i < source.length; i++) {
            const c = source[i];
            if (c === '>') return !inExpression || ATTRS.test(attrs) ? i : -1;
            if (c === '{') { const end = matching(source, i, '{', '}'); if (end < 0) return -1; attrs += /=\s*$/.test(attrs) ? '"x"' : ' x'; i = end; continue; }
            if (c === '"' || c === "'") { const end = source.indexOf(c, i + 1); if (end < 0) return -1; attrs += '"x"'; i = end; continue; }
            if (c === '<') return -1;
            attrs += c;
        }
        return -1;
    }
    /** Goes through [from, to): markup as it is; inside an expression everything but its elements goes. */
    function walk(from, to, inExpression, keep) {
        for (let i = from; i < to; i++) {
            const c = source[i];
            if (c === '<' && source.startsWith('<!--', i)) {
                const end = source.indexOf('-->', i + 4);
                const stop = end < 0 ? to : Math.min(to, end + 3);
                if (inExpression && !keep) blank(i, stop);
                i = stop - 1;
                continue;
            }
            if (c === '<' && (source[i + 1] === '>' || source.startsWith('</>', i))) { const n = source[i + 1] === '>' ? 2 : 3; blank(i, i + n); i += n - 1; continue; }
            if (c === '<') {
                const end = tagEnd(i, inExpression);
                if (end >= 0 && end < to) {
                    // the tag itself: its { } values go; a <script>'s or <style>'s text is not markup
                    for (let k = i; k < end; k++) {
                        if (source[k] === '{') { const z = matching(source, k, '{', '}'); blank(k, z + 1); k = z; }
                        else if (source[k] === '"' || source[k] === "'") k = source.indexOf(source[k], k + 1);
                    }
                    if (!keep) blank(i, end + 1);
                    const raw = /^<(script|style)\b/i.exec(source.slice(i, i + 8));
                    if (raw && source[end - 1] !== '/') {
                        const shut = lower.indexOf('</' + raw[1].toLowerCase(), end + 1);
                        const stop = shut < 0 ? to : Math.min(to, shut);
                        if (!keep) blank(end + 1, stop);
                        i = stop - 1;
                        continue;
                    }
                    i = end;
                    continue;
                }
                if (inExpression) blank(i, i + 1);
                continue;
            }
            if (c === '{') {
                const end = matching(source, i, '{', '}');
                if (end < 0 || end >= to) { blank(i, to); return; }
                blank(i, i + 1);
                blank(end, end + 1);
                walk(i + 1, end, true, keep && (elements || !source.slice(i, end).includes('\n')));
                i = end;
                continue;
            }
            if (inExpression) {
                // quoted text of the expression is not markup
                if (c === '"' || c === "'" || c === '`') {
                    let z = i + 1;
                    while (z < to && source[z] !== c) z += source[z] === '\\' ? 2 : 1;
                    const stop = Math.min(to, z + 1);
                    blank(i, stop);
                    i = stop - 1;
                    continue;
                }
                blank(i, i + 1);
            }
        }
    }
    walk(front ? front[0].length : 0, source.length, false, true);
    return out.join('');
}
export function astroProblems(source) {
    return htmlProblems(source, { blank: (text) => astroText(text) });
}

// A Markdown reader for the preview beside the editor: headings (# and underlined), paragraphs with hard breaks, emphasis, strikethrough, code,
// links, images and <autolinks>, fenced and indented code, quotes, lists with nesting and task boxes, tables with alignment, rules. It returns a
// tree (never HTML), each block with the line it starts on (1-based), so the page builds elements with textContent and the preview can follow
// the editor. No DOM here: web/test/markdowndoc.test.mjs checks it.
//
// Blocks:  {type: 'heading', level, inline, line, id} | {type: 'paragraph', inline, line} | {type: 'code', lang, text, line} | {type: 'rule', line}
//          | {type: 'quote', blocks, line} | {type: 'list', ordered, start, items: [{blocks, task: null|true|false}], line}
//          | {type: 'table', head: [inline], align: [null|'left'|'center'|'right'], rows: [[inline]], line}
// Inline:  [{t: 'text'|'code'|'br'|'bold'|'italic'|'strike'|'link'|'image', text?, children?, href?}]

import { safeHref } from './markdown-lite.js';

/** GitHub's anchor for a heading text: lower case, punctuation dropped, spaces as hyphens. */
export function slugOf(text) {
    return text.trim().toLowerCase().replace(/`/g, '').replace(/[^\p{L}\p{N}\s_-]/gu, '').replace(/\s/g, '-');
}

export function inlineDoc(text) {
    const out = [];
    let plain = '';
    const flush = () => { if (plain) out.push({ t: 'text', text: plain }); plain = ''; };
    for (let i = 0; i < text.length;) {
        const rest = text.slice(i);
        let m;
        if ((m = /^(`+)([^`]|[^`][\s\S]*?[^`])\1(?!`)/.exec(rest))) { flush(); out.push({ t: 'code', text: m[2].trim() }); i += m[0].length; continue; }
        if (text[i] === '\\' && i + 1 < text.length) {
            if (text[i + 1] === '\n') { flush(); out.push({ t: 'br' }); i += 2; continue; }
            if (/[\\`*_{}[\]()#+\-.!>~|<]/.test(text[i + 1])) { plain += text[i + 1]; i += 2; continue; }
        }
        if ((m = /^ {2,}\n/.exec(rest))) { flush(); out.push({ t: 'br' }); i += m[0].length; continue; }
        if ((m = /^(\*\*|__)(?=\S)([\s\S]*?\S)\1/.exec(rest))) { flush(); out.push({ t: 'bold', children: inlineDoc(m[2]) }); i += m[0].length; continue; }
        if ((m = /^~~(?=\S)([\s\S]*?\S)~~/.exec(rest))) { flush(); out.push({ t: 'strike', children: inlineDoc(m[1]) }); i += m[0].length; continue; }
        if ((m = /^\*(?=[^\s*])([\s\S]*?[^\s*])\*/.exec(rest)) || (m = /^_(?=[^\s_])([\s\S]*?[^\s_])_(?![A-Za-z0-9])/.exec(rest))) {
            if (!(m[0][0] === '_' && i > 0 && /[A-Za-z0-9]/.test(text[i - 1]))) { flush(); out.push({ t: 'italic', children: inlineDoc(m[1]) }); i += m[0].length; continue; }
        }
        if ((m = /^!\[([^\]\n]*)\]\(\s*<?([^)\s>]+)>?(?:\s+"([^"]*)")?\s*\)/.exec(rest))) { flush(); out.push({ t: 'image', text: m[1], href: m[2], title: m[3] }); i += m[0].length; continue; }
        if ((m = /^\[((?:[^\[\]\n]|\[[^\]\n]*\])+)\]\(\s*<?([^)\s>]*)>?(?:\s+"([^"]*)")?\s*\)/.exec(rest))) {
            flush();
            if (safeHref(m[2])) out.push({ t: 'link', href: m[2], title: m[3], children: inlineDoc(m[1]) }); else out.push(...inlineDoc(m[1]));
            i += m[0].length;
            continue;
        }
        if ((m = /^<((?:https?:\/\/|mailto:)[^\s>]+)>/.exec(rest))) { flush(); out.push({ t: 'link', href: m[1], children: [{ t: 'text', text: m[1] }] }); i += m[0].length; continue; }
        if ((m = /^https?:\/\/[^\s<>)\]]+[^\s<>)\].,;:!?'"]/.exec(rest)) && (i === 0 || /[\s(]/.test(text[i - 1]))) { flush(); out.push({ t: 'link', href: m[0], children: [{ t: 'text', text: m[0] }] }); i += m[0].length; continue; }
        plain += text[i];
        i++;
    }
    flush();
    return out;
}

const FENCE = /^\s{0,3}(`{3,}|~{3,})\s*([\w+#.-]*)[^`]*$/;
const ATX = /^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$/;
const RULE = /^\s{0,3}([-*_])(\s*\1){2,}\s*$/;
const LIST_ITEM = /^(\s*)([-*+]|\d{1,9}[.)])(\s+)(\S.*)?$/;
const TABLE_DELIMITER = /^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$/;

const isBlank = (l) => /^\s*$/.test(l);
const cells = (row) => row.trim().replace(/^\|/, '').replace(/\|$/, '').split(/(?<!\\)\|/).map((c) => c.trim().replace(/\\\|/g, '|'));

export function blocksDoc(text, firstLine = 1) {
    const lines = text.replace(/\r\n?/g, '\n').split('\n');
    return parse(lines, firstLine);
}

function parse(lines, base) {
    const blocks = [];
    let i = 0;
    const startsBlock = (j) => FENCE.test(lines[j]) || ATX.test(lines[j]) || RULE.test(lines[j]) || /^\s{0,3}>/.test(lines[j]) || (LIST_ITEM.test(lines[j]) && !RULE.test(lines[j]));
    while (i < lines.length) {
        const line = lines[i];
        if (isBlank(line)) { i++; continue; }
        let m;
        if ((m = FENCE.exec(line))) {
            const body = [];
            const at = i;
            for (i++; i < lines.length && !new RegExp('^\\s{0,3}' + m[1][0] + '{' + m[1].length + ',}\\s*$').test(lines[i]); i++) body.push(lines[i]);
            i++;
            blocks.push({ type: 'code', lang: m[2], text: body.join('\n'), line: base + at });
            continue;
        }
        if ((m = ATX.exec(line))) { blocks.push({ type: 'heading', level: m[1].length, inline: inlineDoc(m[2]), id: slugOf(m[2]), line: base + i }); i++; continue; }
        if (RULE.test(line)) { blocks.push({ type: 'rule', line: base + i }); i++; continue; }
        if (/^\s{0,3}>/.test(line)) {
            const at = i, quoted = [];
            for (; i < lines.length && (/^\s{0,3}>/.test(lines[i]) || (!isBlank(lines[i]) && quoted.length && !startsBlock(i))); i++) quoted.push(lines[i].replace(/^\s{0,3}>\s?/, ''));
            blocks.push({ type: 'quote', blocks: parse(quoted, base + at), line: base + at });
            continue;
        }
        if ((m = LIST_ITEM.exec(line)) && !RULE.test(line)) {
            const at = i;
            const ordered = /\d/.test(m[2]);
            const indent = m[1].length;
            const items = [];
            while (i < lines.length) {
                const item = LIST_ITEM.exec(lines[i]);
                if (!item || item[1].length !== indent || /\d/.test(item[2]) !== ordered || RULE.test(lines[i])) break;
                const width = item[1].length + item[2].length + item[3].length;
                const content = [item[4] ?? ''];
                const first = i;
                i++;
                // what belongs to the item: lines indented to its text, and blank lines between them
                while (i < lines.length) {
                    if (isBlank(lines[i])) {
                        let k = i;
                        while (k < lines.length && isBlank(lines[k])) k++;
                        if (k < lines.length && lines[k].match(/^\s*/)[0].length >= width) { for (; i < k; i++) content.push(''); continue; }
                        break;
                    }
                    if (lines[i].match(/^\s*/)[0].length >= width) { content.push(lines[i].slice(width)); i++; continue; }
                    if (!startsBlock(i) && !isBlank(lines[i - 1])) { content.push(lines[i].trim()); i++; continue; }   // a lazy continuation of the paragraph
                    break;
                }
                let task = null;
                const t = /^\[([ xX])\]\s+/.exec(content[0]);
                if (t) { task = t[1] !== ' '; content[0] = content[0].slice(t[0].length); }
                items.push({ blocks: parse(content, base + first), task });
                while (i < lines.length && isBlank(lines[i]) && i + 1 < lines.length && !isBlank(lines[i + 1]) && LIST_ITEM.test(lines[i + 1]) && LIST_ITEM.exec(lines[i + 1])[1].length === indent) i++;
            }
            blocks.push({ type: 'list', ordered, start: ordered ? parseInt(m[2], 10) : 1, items, line: base + at });
            continue;
        }
        if (line.includes('|') && i + 1 < lines.length && TABLE_DELIMITER.test(lines[i + 1]) && lines[i + 1].includes('-') && cells(line).length === cells(lines[i + 1]).length) {
            const at = i;
            const head = cells(line).map(inlineDoc);
            const align = cells(lines[i + 1]).map((c) => (c.startsWith(':') && c.endsWith(':') ? 'center' : c.endsWith(':') ? 'right' : c.startsWith(':') ? 'left' : null));
            const rows = [];
            for (i += 2; i < lines.length && !isBlank(lines[i]) && lines[i].includes('|'); i++) rows.push(cells(lines[i]).map(inlineDoc));
            blocks.push({ type: 'table', head, align, rows, line: base + at });
            continue;
        }
        if (/^( {4}|\t)/.test(line)) {
            const at = i, body = [];
            for (; i < lines.length && (/^( {4}|\t)/.test(lines[i]) || isBlank(lines[i])); i++) body.push(lines[i].replace(/^( {4}|\t)/, ''));
            while (body.length && body[body.length - 1] === '') body.pop();
            blocks.push({ type: 'code', lang: '', text: body.join('\n'), line: base + at });
            continue;
        }
        // a paragraph, or a heading when the line below is = or - underneath
        const at = i, para = [];
        for (; i < lines.length && !isBlank(lines[i]) && (para.length === 0 || !startsBlock(i)); i++) {
            para.push(lines[i]);
            if (i + 1 < lines.length && /^\s{0,3}(=+|-+)\s*$/.test(lines[i + 1]) && para.length >= 1) {
                const level = lines[i + 1].includes('=') ? 1 : 2;
                const title = para.join(' ').trim();
                blocks.push({ type: 'heading', level, inline: inlineDoc(title), id: slugOf(title), line: base + at });
                i += 2;
                para.length = 0;
                break;
            }
            if (i + 1 < lines.length && para.length && line.includes('|') && TABLE_DELIMITER.test(lines[i + 1]) && para.length === 1 && false) break;
        }
        if (para.length) blocks.push({ type: 'paragraph', inline: inlineDoc(para.map((l, k) => (k < para.length - 1 && / {2,}$/.test(l) ? l + '\n' : l.trim())).join(para.some((l) => / {2,}$/.test(l)) ? '' : ' ')), line: base + at });
    }
    return blocks;
}

/** The headings of a text for the Structure view: [{level, text, line, id}], not those inside code fences. */
export function headingsOf(text) {
    const found = [];
    const walk = (blocks) => { for (const b of blocks) { if (b.type === 'heading') found.push({ level: b.level, text: plain(b.inline), line: b.line, id: b.id }); else if (b.type === 'quote') walk(b.blocks); } };
    walk(blocksDoc(text));
    // equal headings get -1, -2 as GitHub gives them
    const seen = new Map();
    for (const h of found) { const n = seen.get(h.id) ?? 0; seen.set(h.id, n + 1); if (n) h.id = `${h.id}-${n}`; }
    return found;
}

/** The text of inline nodes without their marks. */
export function plain(inline) {
    return inline.map((n) => (n.children ? plain(n.children) : n.t === 'br' ? ' ' : n.text ?? '')).join('');
}

/** The headings as symbols of the Structure view: nested by level, a heading under the nearest one above it that is shallower. */
export function markdownSymbols(text) {
    const roots = [];
    const stack = [];
    for (const h of headingsOf(text)) {
        const at = { line: h.line - 1, character: 0 };
        const symbol = { name: h.text || '(empty)', detail: '', kind: 'md-heading', range: { start: at, end: at }, selectionRange: { start: at, end: at }, children: [], level: h.level };
        while (stack.length && stack[stack.length - 1].level >= h.level) stack.pop();
        (stack.length ? stack[stack.length - 1].children : roots).push(symbol);
        stack.push(symbol);
    }
    return roots;
}

/**
 * The tables of a Markdown text laid out: the pipes under each other, each column as wide as its widest cell, the alignment marks kept
 * (`:--`, `:-:`, `--:`). Code fences are left as they are. Returns the whole text.
 */
export function formatTables(text) {
    const nl = text.includes('\r\n') ? '\r\n' : '\n';
    const lines = text.replace(/\r\n/g, '\n').split('\n');
    const out = [];
    let fence = false;
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        if (/^\s{0,3}(```|~~~)/.test(line)) { fence = !fence; out.push(line); continue; }
        const isTable = !fence && line.includes('|') && i + 1 < lines.length && TABLE_DELIMITER.test(lines[i + 1]) && lines[i + 1].includes('-') && cells(line).length === cells(lines[i + 1]).length;
        if (!isTable) { out.push(line); continue; }
        const indent = /^\s*/.exec(line)[0];
        const rows = [cells(line)];
        const align = cells(lines[i + 1]).map((c) => (c.startsWith(':') && c.endsWith(':') ? 'c' : c.endsWith(':') ? 'r' : c.startsWith(':') ? 'l' : ''));
        let j = i + 2;
        for (; j < lines.length && !isBlank(lines[j]) && lines[j].includes('|'); j++) rows.push(cells(lines[j]));
        const n = align.length;
        const norm = rows.map((r) => Array.from({ length: n }, (_, k) => (r[k] ?? '').replace(/\|/g, '\\|').replace(/\\\\\|/g, '\\|')));
        const width = Array.from({ length: n }, (_, k) => Math.max(3, ...norm.map((r) => [...r[k]].length)));
        const pad = (s, k, a) => { const gap = width[k] - [...s].length; return a === 'r' ? ' '.repeat(gap) + s : a === 'c' ? ' '.repeat(gap >> 1) + s + ' '.repeat(gap - (gap >> 1)) : s + ' '.repeat(gap); };
        const row = (r) => `${indent}| ${r.map((c, k) => pad(c, k, align[k])).join(' | ')} |`;
        const rule = `${indent}| ${align.map((a, k) => (a === 'c' ? ':' + '-'.repeat(width[k] - 2) + ':' : a === 'r' ? '-'.repeat(width[k] - 1) + ':' : a === 'l' ? ':' + '-'.repeat(width[k] - 1) : '-'.repeat(width[k]))).join(' | ')} |`;
        out.push(row(norm[0]), rule, ...norm.slice(1).map(row));
        i = j - 1;
    }
    return out.join(nl);
}

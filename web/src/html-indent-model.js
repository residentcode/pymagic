// Indentation that does not follow the elements and blocks of markup, as data (html-indent.js puts it on the editor): an HTML file or a Django /
// Jinja / Nunjucks template, a Blade view, a Vue or Svelte component. A line that starts with a tag or a block's tag is told when it is not
// where its place asks: a closing tag (and {% else %}, {% endif %}, @endif, {/if}) under the line that opened it, and the lines inside an
// element or block all at one indentation, either one step in from the opening line or level with it (a file that keeps <body>'s or
// {% block %}'s lines at the left is as right as one that moves them in). The step is the file's own, the one most of its elements use.
// Left alone: lines of text, the lines of a tag spread over several, what is inside <script>, <style>, <pre>, <textarea> and comments, a file
// whose elements do not balance (the syntax check tells that), and a file where most lines differ (its own layout). No DOM, no Monaco here.
import { VOID, OPTIONAL_END, ENDS, ENDS_P, blankTemplate, djangoBlocks, templateProblems, vueText, vueProblems, svelteParts, svelteProblems, bladeParts, bladeProblems } from './htmlcheck-model.js';
import { handlebarsParts, handlebarsProblems, ejsParts, ejsProblems, astroText, astroProblems } from './markup-kinds-model.js';

/** The kind of markup a file is, by the editor's language and the file's name; null for none. */
export function markupKind(languageId, path = '') {
    if (languageId === 'django-html' || languageId === 'twig') return 'django';
    if (languageId === 'blade') return 'blade';
    if (languageId === 'handlebars') return 'handlebars';
    if (languageId === 'ejs') return 'ejs';
    if (languageId === 'html') return /\.vue$/i.test(path) ? 'vue' : /\.svelte$/i.test(path) ? 'svelte' : /\.astro$/i.test(path) ? 'astro' : null;
    return null;
}

const KEEP = new Set(['script', 'style', 'pre', 'textarea']);       // what they hold is not markup to indent
const TAG = /<!--[\s\S]*?(?:-->|$)|<(\/?)([A-Za-z][\w:.-]*)((?:"[^"]*"|'[^']*'|[^<>"'])*)>/g;

/** The markup alone and the blocks around it: { text, blocks, strict, sound }. */
function parts(source, kind) {
    if (kind === 'vue') return { text: vueText(source), blocks: [], strict: true, sound: vueProblems(source).length === 0 };
    if (kind === 'svelte') return { ...svelteParts(source), strict: true, sound: svelteProblems(source).length === 0 };
    if (kind === 'blade') return { ...bladeParts(source), strict: false, sound: bladeProblems(source).length === 0 };
    if (kind === 'handlebars') return { ...handlebarsParts(source), strict: false, sound: handlebarsProblems(source).length === 0 };
    if (kind === 'ejs') return { ...ejsParts(source), strict: false, sound: ejsProblems(source).length === 0 };
    // (what an expression of several lines holds is laid out as code: its elements are not judged)
    if (kind === 'astro') return { text: astroText(source, { elements: false }), blocks: [], strict: false, sound: astroProblems(source).length === 0 };
    return { text: blankTemplate(source), blocks: djangoBlocks(source), strict: false, sound: templateProblems(source).length === 0 };
}

/** A block's tags that have their end (a tag that is no block, {% load %}, @csrf, opens nothing): [{ index, type: 'open' | 'mid' | 'close', name }]. */
function pairedBlocks(blocks) {
    const open = [], keep = new Set();
    blocks.forEach((b, i) => {
        if (b.kind === 'open') open.push(i);
        else if (b.kind === 'close') {
            let k = open.length - 1;
            while (k >= 0 && blocks[open[k]].name !== b.name) k--;
            if (k < 0) return;
            keep.add(open[k]);
            keep.add(i);
            open.length = k;
        }
    });
    // an else belongs to the nearest block that is still open and has its end
    const out = [], live = [];
    blocks.forEach((b, i) => {
        if (b.kind === 'open' && keep.has(i)) { live.push(b.name); out.push({ index: b.index, type: 'open', name: b.name }); }
        else if (b.kind === 'close' && keep.has(i)) { live.pop(); out.push({ index: b.index, type: 'close', name: b.name }); }
        else if (b.kind === 'else' && live.length) out.push({ index: b.index, type: 'mid', name: live[live.length - 1] });
    });
    return out;
}

const describe = (n, tabs) => (tabs ? `${n} tab${n === 1 ? '' : 's'}` : `${n} space${n === 1 ? '' : 's'}`);

/**
 * The scan: null when the file is not one to tell (not balanced, not markup), else { found: [{ line (1-based), actual, expected, message }],
 * checked, tabs, blockEnd: for a line that opens a block, the line that closes it (0-based) }.
 */
function scan(source, kind, givenUnit) {
    const { text, blocks, strict, sound } = parts(source, kind);
    if (!sound) return null;
    const lines = text.split('\n');
    const starts = [];
    let offset = 0;
    for (const l of lines) { starts.push(offset); offset += l.length + 1; }
    const lineAt = (index) => { let lo = 0, hi = starts.length - 1; while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (starts[mid] <= index) lo = mid; else hi = mid - 1; } return lo; };

    // the tags, and the lines that are not to be judged
    const events = [], skip = new Set(), covered = [];
    const lower = text.toLowerCase();
    TAG.lastIndex = 0;
    let m;
    while ((m = TAG.exec(text))) {
        const a = lineAt(m.index), z = lineAt(m.index + m[0].length - 1);
        for (let l = a + 1; l <= z; l++) skip.add(l);                 // a comment's or a tag's further lines
        covered.push([m.index, m.index + m[0].length]);
        if (m[2] === undefined) continue;                             // a comment
        const name = m[2].toLowerCase();
        if (m[1]) { events.push({ index: m.index, type: 'close', kind: 'html', name }); continue; }
        if (VOID.has(name) || /\/\s*$/.test(m[3])) { events.push({ index: m.index, type: 'void', kind: 'html', name }); continue; }
        events.push({ index: m.index, type: 'open', kind: 'html', name });
        if (KEEP.has(name)) {
            const end = lower.indexOf('</' + name, m.index + m[0].length);
            if (end < 0) return null;
            for (let l = z + 1; l < lineAt(end); l++) skip.add(l);
            if (lineAt(end) > z && lines[lineAt(end)].slice(0, end - starts[lineAt(end)]).trim()) skip.add(lineAt(end));
            covered.push([m.index + m[0].length, end]);
            TAG.lastIndex = end;
        }
    }
    for (const b of pairedBlocks(blocks))
        if (!covered.some(([s, e]) => b.index >= s && b.index < e)) events.push({ ...b, kind: 'block' });
    events.sort((x, y) => x.index - y.index);

    // the indentation of the lines: the file's own character, a line with the other one is not judged
    const source_lines = source.split('\n');
    const lead = source_lines.map((l) => /^[ \t]*/.exec(l)[0]);
    const tabs = lead.filter((s) => s.startsWith('\t')).length > lead.filter((s) => s.startsWith(' ')).length;
    const plain = (i) => !lead[i].includes(tabs ? ' ' : '\t');
    const indent = (i) => lead[i].length;

    const byLine = new Map();
    for (const e of events) { const l = lineAt(e.index); if (!byLine.has(l)) byLine.set(l, []); byLine.get(l).push(e); }

    const judged = [];                // { line, expected: a number, or { parent: line, first: bool } resolved with the unit }
    const steps = [];
    const blockEnd = new Map();
    const firstKid = new Map();       // the opening line (or -1 for the file) -> its first judged child's line
    const stack = [];                 // { kind, name, line }
    let broken = false;
    const pop = (e) => {
        let k = stack.length - 1;
        while (k >= 0 && !(stack[k].kind === e.kind && stack[k].name === e.name)) k--;
        if (k < 0) { broken = true; return null; }
        // what is dropped on the way must be elements HTML ends by itself
        for (let j = stack.length - 1; j > k; j--)
            if (stack[j].kind !== 'html' || strict || !OPTIONAL_END.has(stack[j].name)) { broken = true; return null; }
        const entry = stack[k];
        stack.length = k;
        return entry;
    };
    for (let i = 0; i < lines.length && !broken; i++) {
        const evs = byLine.get(i) ?? [];
        // (a block's tag is blanked in the text: where the line starts is read from the file itself)
        const startsWith = evs.length && evs[0].index === starts[i] + lead[i].length ? evs[0] : null;
        let n = 0;
        const openers = new Set();
        if (startsWith) {
            while (n < evs.length && (evs[n].type === 'close' || evs[n].type === 'mid')) {
                if (evs[n].type === 'mid') {
                    let k = stack.length - 1;
                    while (k >= 0 && !(stack[k].kind === 'block' && stack[k].name === evs[n].name)) k--;
                    if (k < 0) { broken = true; break; }
                    // (an element left open by the branch before, a <li>, ends with it)
                    for (let j = stack.length - 1; j > k; j--) if (stack[j].kind !== 'html' || strict || !OPTIONAL_END.has(stack[j].name)) broken = true;
                    stack.length = k + 1;
                    openers.add(stack[k].line);
                }
                else { const entry = pop(evs[n]); if (!entry) break; openers.add(entry.line); blockEnd.set(entry.line, i); }
                n++;
            }
        }
        if (broken) break;
        if (startsWith && !skip.has(i) && plain(i)) {
            if (n > 0) { if (openers.size === 1) judged.push({ line: i, expected: [...openers][0], closing: true }); }
            else {
                const parent = stack.length ? stack[stack.length - 1].line : -1;
                if (!firstKid.has(parent)) { firstKid.set(parent, i); if (parent >= 0 && plain(parent) && indent(i) > indent(parent)) steps.push(indent(i) - indent(parent)); }
                judged.push({ line: i, parent, first: firstKid.get(parent) === i });
            }
        }
        for (; n < evs.length && !broken; n++) {
            const e = evs[n];
            if (e.type === 'void') continue;
            if (e.type === 'mid') {
                let k = stack.length - 1;
                while (k >= 0 && !(stack[k].kind === 'block' && stack[k].name === e.name)) k--;
                if (k < 0) broken = true; else stack.length = k + 1;
                continue;
            }
            if (e.type === 'close') { const entry = pop(e); if (entry && entry.line !== i) blockEnd.set(entry.line, i); continue; }
            if (e.kind === 'html' && !strict && stack.length && stack[stack.length - 1].kind === 'html') {
                const top = stack[stack.length - 1].name;
                if (ENDS[e.name]?.includes(top) || (top === 'p' && ENDS_P.has(e.name))) stack.pop();      // HTML ends it here
            }
            stack.push({ kind: e.kind, name: e.name, line: i });
        }
    }
    if (broken) return null;

    // the step: the one most elements move their lines in by
    let unit = givenUnit;
    if (!unit) {
        const count = new Map();
        for (const s of steps) count.set(s, (count.get(s) ?? 0) + 1);
        unit = [...count].sort((a, b) => b[1] - a[1] || a[0] - b[0])[0]?.[0] ?? (tabs ? 1 : 4);
    }
    const found = [];
    const right = new Map();          // an opening line -> the indentation its lines are to have
    for (const j of judged) {
        const actual = indent(j.line);
        let expected;
        if (j.closing) expected = plain(j.expected) ? indent(j.expected) : actual;
        else if (j.parent < 0) {
            // the file's own lines: all where the first of them is
            if (j.first) right.set(-1, actual);
            expected = right.get(-1);
        }
        else if (!plain(j.parent)) expected = actual;
        else if (j.first) {
            const base = indent(j.parent);
            expected = actual >= base ? actual : base + unit;            // level with it or further in, by the lines' own step
            right.set(j.parent, expected);
        }
        else expected = right.get(j.parent) ?? actual;
        if (expected !== actual) found.push({ line: j.line + 1, actual, expected, message: `Indentation is ${describe(actual, tabs)}, expected ${describe(expected, tabs)} (Fix indentation: Alt+Enter)` });
    }
    return { found, checked: judged.length, tabs, unit, blockEnd };
}

/** The lines whose indentation does not follow the markup: [{ line (1-based), actual, expected, message }]; none for a file with a layout of its own. */
export function htmlIndentProblems(source, kind, limit = 200) {
    const r = scan(source.replace(/\r\n/g, '\n'), kind);
    if (!r || (r.found.length > 3 && r.found.length > r.checked * 0.3)) return [];
    return r.found.slice(0, limit);
}

/** The text with those lines where they belong: each moved with what it holds (the lines down to its closing tag), nothing else changed. */
export function fixHtmlIndentation(source, kind) {
    const eol = source.includes('\r\n') ? '\r\n' : '\n';
    let text = source.replace(/\r\n/g, '\n');
    const first = scan(text, kind);
    if (!first || !first.found.length || (first.found.length > 3 && first.found.length > first.checked * 0.3)) return source;
    for (let pass = 0; pass < 12; pass++) {
        const r = scan(text, kind, first.unit);
        if (!r || !r.found.length) break;
        const lines = text.split('\n');
        const delta = new Array(lines.length).fill(0);
        let until = -1;
        for (const f of r.found) {
            const i = f.line - 1;
            if (i <= until) continue;                              // inside a block that moves already: told again on the next pass
            const end = r.blockEnd.get(i) ?? i;
            for (let l = i; l <= end; l++) delta[l] = f.expected - f.actual;
            until = end;
        }
        const ch = r.tabs ? '\t' : ' ';
        text = lines.map((l, i) => {
            if (!delta[i] || !l.trim()) return l;
            const lead = /^[ \t]*/.exec(l)[0];
            return ch.repeat(Math.max(0, lead.length + delta[i])) + l.slice(lead.length);
        }).join('\n');
    }
    return eol === '\n' ? text : text.replace(/\n/g, eol);
}

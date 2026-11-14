// The CSS and JavaScript inside a template, as documents of their own that keep the template's offsets: everything outside the
// <style> and <script> blocks (and style="" attributes, for CSS) is blank, line breaks kept, so a finding of the CSS or JavaScript
// service at a place of the document is at the same place of the template. A {{ … }} or {% … %} inside a block becomes a value of the
// same length, so it is not reported as broken code.

const blankOut = (text) => text.replace(/[^\n]/g, ' ');

/** A value as long as a template expression: `0` and spaces. */
const value = (m) => '0' + blankOut(m.slice(1));

/** A template expression inside code: a value as long as it (`0` and spaces), or spaces when it stands alone on its line. */
function neutral(code, template) {
    // a component's (.vue, .svelte) script is code as it is
    if (template === 'component') return code;
    if (template === 'blade') {
        // Blade: {{-- comments --}} go; {{ }}, {!! !!}, @json(…) and @js(…) are values; other directives (@if(…), @endif, @csrf) go; @{{ is JavaScript's
        return code.replace(/\{\{--[\s\S]*?--\}\}|@\{\{|\{\{[\s\S]*?\}\}|\{!![\s\S]*?!!\}|(?<![\w.@])@(\w+)(\s*\((?:[^()'"]|'[^']*'|"[^"]*"|\((?:[^()'"]|'[^']*'|"[^"]*")*\))*\))?/g, (m, directive) => {
            if (m === '@{{') return m;
            if (m.startsWith('{{--')) return blankOut(m);
            if (m.startsWith('{{') || m.startsWith('{!!')) return value(m);
            return directive === 'json' || directive === 'js' ? value(m) : blankOut(m);
        });
    }
    return code.replace(/\{\{[\s\S]*?\}\}|\{%[\s\S]*?%\}|\{#[\s\S]*?#\}/g, (m) => (m.startsWith('{{') ? value(m) : blankOut(m)));
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
        // a component's block in another language (lang="scss", "less", "stylus", "tsx", "coffee") is left out too
        if (tag === 'script' && /\blang\s*=\s*["']?(?!(js|ts|javascript|typescript)\b)\w+/i.test(m[0])) continue;
        if (tag === 'style' && /\blang\s*=\s*["']?(?!css\b)\w+/i.test(m[0])) continue;
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
export function embeddedDocument(text, language, template = 'django') {
    const regions = regionsOf(text, language);
    let out = '';
    let at = 0;
    for (const [start, end] of regions) {
        out += blankOut(text.slice(at, start));
        const code = neutral(text.slice(start, end), template);
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

/** Whether a component's (.vue, .svelte) script is TypeScript: a <script lang="ts">. */
export const scriptIsTs = (text) => /<script\b[^>]*\blang\s*=\s*["']?(ts|typescript)\b/i.test(text);

/**
 * What a component's script may use without declaring it: Vue's compiler macros and Svelte's runes (written after the document's end, so the
 * offsets stay the template's).
 */
export function componentPrelude(kind, script = '') {
    // as Vue and Svelte type them, simpler: what a type argument says comes back (defineProps<{ title: string }>() is { title: string })
    const vue = [
        'declare function defineProps<T = any>(props?: any): T;', 'declare function defineEmits<T = any>(emits?: any): any;',
        'declare function defineExpose(exposed?: any): void;', 'declare function defineModel<T = any>(...args: any[]): { value: T };',
        'declare function defineOptions(options?: any): void;', 'declare function defineSlots<T = any>(): T;',
        'declare function withDefaults<T, D = any>(props: T, defaults?: D): T;',
    ];
    const svelte = [
        'declare function $state<T>(initial?: T): T;', 'declare const $derived: { <T>(value: T): T; by<T>(f: () => T): T };',
        'declare const $effect: { (f: () => void | (() => void)): void; pre(f: () => void | (() => void)): void; root(f: () => void | (() => void)): () => void; tracking(): boolean };',
        'declare function $props<T = any>(): T;', 'declare function $bindable<T>(fallback?: T): T;', 'declare function $inspect(...values: any[]): any;',
        'declare function $host<T = any>(): T;',
    ];
    // Svelte's `$: name = …` declares name (unless the script does), with the type of what it is given: written again here as
    // `var name = (…);` (a var, so the script may use it above); what is found in these lines is not shown (embedded.js)
    if (kind === 'svelte') {
        const seen = new Set();
        for (const m of String(script).matchAll(/^[ \t]*\$:\s*([A-Za-z_$][\w$]*)\s*=(?!=)/gm)) {
            const name = m[1];
            const declared = new RegExp(`\\b(let|const|var|function|class)\\s+${name.replace(/\$/g, '\\$')}(?![\\w$])`).test(script);
            if (declared || seen.has(name)) continue;
            seen.add(name);
            const value = statementValue(script, m.index + m[0].length);
            // (a store's $name is its value: any)
            svelte.push(value && !name.startsWith('$') ? `var ${name} = (${value});` : `declare let ${name}: any;`);
        }
    }
    return '\n;' + (kind === 'vue' ? vue : svelte).join(' ') + '\n';
}

/** The value of an assignment that starts at an offset: up to its ; or the end of its line, brackets and quoted text kept whole; '' when not found. */
function statementValue(code, from) {
    let depth = 0, quote = null;
    for (let i = from; i < code.length; i++) {
        const c = code[i];
        if (quote) { if (c === '\\') i++; else if (c === quote) quote = null; continue; }
        if (c === '"' || c === "'" || c === '`') quote = c;
        else if ('([{'.includes(c)) depth++;
        else if (')]}'.includes(c)) { if (--depth < 0) return code.slice(from, i).trim(); }
        else if ((c === ';' || c === '\n') && depth === 0) {
            const value = code.slice(from, i).trim();
            // a line that goes on (an operator at its end, or at the next line's start)
            if (c === '\n' && (/[=+\-*/%&|^?:,.<>(]$/.test(value) || /^\s*[.?:+\-*/%&|^)\]]/.test(code.slice(i + 1)))) continue;
            return value;
        }
    }
    return quote || depth ? '' : code.slice(from).trim();
}

const IDENT = /[A-Za-z_$][\w$]*/g;

/** The end of what starts at an opening brace: its closing one (quoted text and braces inside passed over), or -1. */
function closingBrace(text, at) {
    let depth = 0, quote = null;
    for (let k = at; k < text.length; k++) {
        const c = text[k];
        if (quote) { if (c === '\\') k++; else if (c === quote) quote = null; continue; }
        if (c === '"' || c === "'" || c === '`') quote = c;
        else if (c === '{') depth++;
        else if (c === '}' && --depth === 0) return k;
    }
    return -1;
}

/**
 * A component's document with the expressions of its markup written as code at their own places, so they are checked against the script's
 * names and types and a finding is where the expression is: Svelte's {expr} as ;expr; and Vue's {{ expr }}, :prop="expr", v-if="expr",
 * @event="expr" as ;(expr). An expression that uses a name the markup itself gives (an {#each} item, a v-for's, a slot's, $event) is left
 * out: only the script knows no such name. What is told of these places is narrowed by keepFinding (inMarkup).
 */
export function withMarkupCode(text, doc, kind) {
    if (kind !== 'vue' && kind !== 'svelte') return doc;
    const out = doc.split('');
    // not markup: the scripts and styles (of any language) and the comments
    const skip = [];
    for (const m of text.matchAll(/<(script|style)\b[^>]*>[\s\S]*?(?:<\/\1\s*>|$)|<!--[\s\S]*?(?:-->|$)/gi)) skip.push([m.index, m.index + m[0].length]);
    const skipped = (i) => skip.find(([s, e]) => i >= s && i < e);
    // a statement of the markup before a script must not run into it
    for (const [start] of regionsOf(text, 'javascript')) if (start > 0 && out[start - 1] === ' ') out[start - 1] = ';';
    const put = (from, to) => { for (let i = from; i < to; i++) out[i] = text[i]; };

    if (kind === 'svelte') {
        const bound = new Set(['$$props', '$$restProps', '$$slots']);
        const spots = [];
        for (let i = 0; i < text.length; i++) {
            if (text[i] !== '{') continue;
            const s = skipped(i);
            if (s) { i = s[1] - 1; continue; }
            const end = closingBrace(text, i);
            if (end < 0) break;
            const inner = text.slice(i + 1, end);
            const tag = /^\s*([#:/@])\s*(\w+)/.exec(inner);
            if (tag) {
                // the names a block gives: {#each xs as x, i (key)}, {:then v}, {#await p then v}, {#snippet f(a)}, {@const n = …}
                const rest = inner.slice(tag[0].length);
                const names = tag[2] === 'each' ? rest.split(/\bas\b/).slice(1).join(' ').replace(/\([^)]*\)\s*$/, '')
                    : tag[2] === 'then' || tag[2] === 'catch' ? rest
                    : tag[2] === 'await' ? (/\b(?:then|catch)\b([\s\S]*)$/.exec(rest)?.[1] ?? '')
                    : tag[2] === 'snippet' ? rest : tag[2] === 'const' ? rest.split('=')[0] : '';
                for (const n of names.match(IDENT) ?? []) bound.add(n);
            }
            else if (inner.trim() && !/^\s*\.\.\./.test(inner)) spots.push([i, end]);
            i = end;
        }
        for (const m of text.matchAll(/\slet:([A-Za-z_$][\w$]*)(?:\s*=\s*\{([^}]*)\})?/g)) for (const n of (m[2] ?? m[1]).match(IDENT) ?? []) bound.add(n);
        for (const [a, z] of spots) {
            if ((text.slice(a + 1, z).match(IDENT) ?? []).some((n) => bound.has(n))) continue;
            out[a] = ';';
            put(a + 1, z);
            out[z] = ';';
        }
        return out.join('');
    }

    // Vue: inside <template> … </template> (one written in another language, lang="pug", is left out)
    const open = /<template\b([^>]*)>/i.exec(text);
    const close = text.lastIndexOf('</template');
    if (!open || close < 0 || /\blang\s*=/.test(open[1])) return out.join('');
    const from = open.index + open[0].length;
    const bound = new Set(['$event', '$slots', '$attrs', '$refs', '$emit', '$props', '$el', '$t', '$route', '$router', '$store']);
    const spots = [];   // [start of the code, its end, the place of ";(", the place of ")"]
    const body = text.slice(from, close);
    for (const m of body.matchAll(/\s(v-for|v-slot(?::[\w.-]+)?|#[\w.-]+)\s*=\s*(["'])([\s\S]*?)\2/g)) {
        const names = m[1] === 'v-for' ? m[3].split(/\s(?:in|of)\s/)[0] : m[3];
        for (const n of names.match(IDENT) ?? []) bound.add(n);
    }
    for (const m of body.matchAll(/\{\{([\s\S]*?)\}\}/g)) {
        const a = from + m.index;
        // (in an attribute's value {{ }} is text to Vue)
        const before = body.slice(0, m.index);
        if (before.lastIndexOf('<') > before.lastIndexOf('>')) continue;
        if (m[1].trim()) spots.push([a + 2, a + 2 + m[1].length, a, a + 2 + m[1].length]);
    }
    for (const m of body.matchAll(/\s(?:v-bind(?::[\w.[\]-]+)?|v-on:[\w.[\]-]+|[:@][\w.[\]-]+|v-if|v-else-if|v-show|v-html|v-text|v-model(?:[:.][\w.-]+)?)=(["'])([\s\S]*?)\1/g)) {
        const q = from + m.index + m[0].length - m[2].length - 2;      // the opening quote
        if (m[2].trim()) spots.push([q + 1, q + 1 + m[2].length, q - 1, q + 1 + m[2].length]);
    }
    for (const [a, z, at, shut] of spots) {
        if ((text.slice(a, z).match(IDENT) ?? []).some((n) => bound.has(n))) continue;
        out[at] = ';';
        out[at + 1] = '(';
        put(a, z);
        out[shut] = ')';
    }
    return out.join('');
}

/**
 * Which findings of a block's checker a template shows: in a template's <script> its syntax errors only (its names are the page's, not
 * checked against the page's other scripts); in a component's, the type errors too, but not a name the markup uses (declared but not read)
 * nor, in Svelte, a store's $name.
 */
export function keepFinding(code, message, kind, inMarkup = false) {
    const n = Number(typeof code === 'object' && code ? code.value : code);
    // an expression of the markup: only a property or method that is not there, and (Svelte) a call with the wrong arguments. A name
    // not found is the markup's own to give; Vue reads a ref's value by itself, so nothing about a Ref is told
    if (inMarkup) {
        if (!(kind === 'vue' ? [2339, 2551] : [2339, 2551, 2554, 2345]).includes(n)) return false;
        return !(kind === 'vue' && /Ref</.test(String(message))) && !/'\$\w+'/.test(String(message));
    }
    if (!Number.isFinite(n) || n < 2000) return true;
    if (kind !== 'vue' && kind !== 'svelte') return false;
    if ([6133, 6138, 6192, 6196, 6198, 6199, 6205].includes(n)) return false;
    // Svelte: a store's $name (Cannot find name, perhaps with "Did you mean"), and the $: label TypeScript sees as not used
    if (kind === 'svelte' && (n === 2304 || n === 2552) && /^Cannot find name '\$\w+'/.test(String(message))) return false;
    if (kind === 'svelte' && n === 7028) return false;
    return true;
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

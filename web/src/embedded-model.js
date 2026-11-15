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

    for (const [a, z, at, shut] of vueSpots(text)) {
        out[at] = ';';
        out[at + 1] = '(';
        put(a, z);
        out[shut] = ')';
    }
    return out.join('');
}

/**
 * The expressions of a Vue component's markup: [start of the code, its end, the place for ";(", the place for ")"] of each {{ expr }},
 * :prop="expr", v-if="expr", @event="expr" inside <template> … </template> (one written in another language, lang="pug", is left out).
 * An expression that uses a name the markup itself gives (a v-for's, a slot's, $event) is left out; `empty` keeps the ones with nothing in
 * them yet (for completion).
 */
export function vueSpots(text, empty = false, declared = null) {
    const open = /<template\b([^>]*)>/i.exec(text);
    const close = text.lastIndexOf('</template');
    if (!open || close < 0 || /\blang\s*=/.test(open[1])) return [];
    const from = open.index + open[0].length;
    const bound = new Set(['$event', '$slots', '$attrs', '$refs', '$emit', '$props', '$el', '$t', '$route', '$router', '$store']);
    const spots = [];
    const body = text.slice(from, close);
    const comments = [...body.matchAll(/<!--[\s\S]*?(?:-->|$)/g)].map((m) => [m.index, m.index + m[0].length]);
    const commented = (i) => comments.some(([s, e]) => i >= s && i < e);
    for (const m of body.matchAll(/\s(v-for|v-slot(?::[\w.-]+)?|#[\w.-]+)\s*=\s*(["'])([\s\S]*?)\2/g)) {
        const names = m[1] === 'v-for' ? m[3].split(/\s(?:in|of)\s/)[0] : m[3];
        for (const n of names.match(IDENT) ?? []) if (!declared?.has(n)) bound.add(n);
    }
    for (const m of body.matchAll(/\{\{([\s\S]*?)\}\}/g)) {
        const a = from + m.index;
        // (in an attribute's value {{ }} is text to Vue)
        const before = body.slice(0, m.index);
        if (before.lastIndexOf('<') > before.lastIndexOf('>') || commented(m.index)) continue;
        if (m[1].trim() || empty) spots.push([a + 2, a + 2 + m[1].length, a, a + 2 + m[1].length]);
    }
    for (const m of body.matchAll(/\s(?:v-bind(?::[\w.[\]-]+)?|v-on:[\w.[\]-]+|[:@][\w.[\]-]+|v-if|v-else-if|v-show|v-html|v-text|v-model(?:[:.][\w.-]+)?)=(["'])([\s\S]*?)\1/g)) {
        const q = from + m.index + m[0].length - m[2].length - 2;      // the opening quote
        if (commented(m.index)) continue;
        if (m[2].trim() || empty) spots.push([q + 1, q + 1 + m[2].length, q - 1, q + 1 + m[2].length]);
    }
    return spots.filter(([a, z]) => !(text.slice(a, z).match(IDENT) ?? []).some((n) => bound.has(n)));
}

const KEYWORDS = new Set(('true false null undefined this typeof instanceof in of new void delete await async function return if else for while do switch case break '
    + 'continue var let const class extends super import export default try catch finally throw yield as is keyof NaN Infinity').split(' '));

/** The names an expression takes from outside it: not a member (a.b's b), not an object's key ({ on: x }'s on), not a word of the language or of quoted text. */
export function freeNames(code) {
    const plain = String(code).replace(/'(?:[^'\\\n]|\\.)*'|"(?:[^"\\\n]|\\.)*"/g, (m) => ' '.repeat(m.length));
    const out = [];
    for (const m of plain.matchAll(/(?<![\w$.])[A-Za-z_$][\w$]*/g)) {
        const name = m[0];
        if (KEYWORDS.has(name) || out.includes(name)) continue;
        const before = plain.slice(0, m.index).trimEnd().slice(-1), after = plain.slice(m.index + name.length);
        if (/^\s*:(?!:)/.test(after) && (before === '{' || before === ',')) continue;
        out.push(name);
    }
    return out;
}

/** The names a script declares at its top: const, let, var (a destructuring's too), function, class, enum, and what it imports. */
export function topLevelNames(script) {
    const names = new Set();
    const add = (text) => { for (const part of String(text).split(',')) { const n = /([A-Za-z_$][\w$]*)\s*(?:=[^,]*)?$/.exec(part.replace(/^\s*\.\.\./, '').trim()); if (n && !KEYWORDS.has(n[1])) names.add(n[1]); } };
    let depth = 0;
    const re = /'(?:[^'\\\n]|\\.)*'|"(?:[^"\\\n]|\\.)*"|`(?:[^`\\]|\\.)*`|\/\/[^\n]*|\/\*[\s\S]*?\*\/|\bimport\s+(?:type\s+)?([^'"`;]*?)\s+from\b|\b(?:const|let|var)\s+(\{[^}=]*\}|\[[^\]=]*\]|[A-Za-z_$][\w$]*)|\b(?:function\*?|class|enum)\s+([A-Za-z_$][\w$]*)|[{}()[\]]/g;
    for (const m of String(script).matchAll(re)) {
        const t = m[0];
        if (t.length === 1 && '{(['.includes(t)) depth++;
        else if (t.length === 1 && '})]'.includes(t)) depth = Math.max(0, depth - 1);
        else if (depth > 0) continue;
        else if (m[1] !== undefined) add(m[1].replace(/\*\s*as\s+/g, '').replace(/[{}]/g, ',').replace(/\btype\s+/g, '').replace(/[\w$]+\s+as\s+/g, ''));
        else if (m[2] !== undefined) add(/^[{[]/.test(m[2]) ? m[2].slice(1, -1).replace(/[\w$]+\s*:\s*(?=[A-Za-z_$])/g, '') : m[2]);
        else if (m[3] !== undefined) names.add(m[3]);
    }
    return [...names];
}

/** The type argument or the argument of a call that starts at `at` (just after its name): its text between the brackets, or ''. */
function bracketed(code, at, open, shut) {
    if (code[at] !== open) return '';
    let depth = 0, quote = null;
    for (let i = at; i < code.length; i++) {
        const c = code[i];
        if (quote) { if (c === '\\') i++; else if (c === quote) quote = null; continue; }
        if (c === '"' || c === "'" || c === '`') quote = c;
        else if (c === open) depth++;
        else if (c === '=' && code[i + 1] === '>') i++;      // (an arrow is not a closing bracket)
        else if (c === shut && --depth === 0) return code.slice(at + 1, i);
    }
    return '';
}

const VUE_THIS = 'type __pm_PP<P> = P extends readonly (infer K extends string)[] ? { [X in K]: any } : { [K in keyof P]: any }; '
    + 'type __pm_CC<C> = { [K in keyof C]: C[K] extends (...a: any[]) => infer R ? R : C[K] extends { get: (...a: any[]) => infer R } ? R : any }; '
    + 'type __pm_Vm = { $emit: (event: string, ...args: any[]) => void; $refs: any; $el: any; $props: any; $attrs: any; $slots: any; $data: any; $options: any; $parent: any; $root: any; '
    + '$nextTick: (f?: () => void) => Promise<void>; $watch: (...a: any[]) => any; $forceUpdate: () => void; [k: `$${string}`]: any }; '
    + 'type __pm_This<P, D, C, M> = __pm_PP<P> & D & __pm_CC<C> & M & __pm_Vm; '
    + 'declare function __pm_options<P = {}, D = {}, C = {}, M = {}>(o: { props?: P; data?: (this: __pm_PP<P> & __pm_Vm, vm: any) => D; computed?: C & ThisType<__pm_This<P, D, C, M>>; '
    + 'methods?: M & ThisType<__pm_This<P, D, C, M>>; [k: string]: any } & ThisType<__pm_This<P, D, C, M>>): { props: P; data: () => D; computed: C; methods: M }; '
    + 'declare const _o_: typeof __pm_options; ';
const VUE_TYPES = VUE_THIS + 'declare function __pm_props<P>(p: P): P extends readonly (infer K extends string)[] ? { [X in K]: any } : { [K in keyof P]: any }; '
    + 'declare function __pm_inst<O>(o: O): O extends new (...a: any) => infer I ? I : (O extends { data: (...a: any[]) => infer D } ? D : {}) '
    + '& (O extends { computed: infer C } ? { [K in keyof C]: C[K] extends (...a: any[]) => infer R ? R : C[K] extends { get: (...a: any[]) => infer R } ? R : any } : {}) '
    + '& (O extends { methods: infer M } ? M : {}) '
    + '& (O extends { props: infer P } ? (P extends readonly (infer K extends string)[] ? { [X in K]: any } : { [K in keyof P]: any }) : {}) '
    + '& (O extends { setup: (...a: any[]) => infer S } ? S : {});';

/**
 * A Vue component's script as the document the TypeScript service checks: the script at its own places, and after the document's end what
 * the markup may use, for the markup's own document (vueMarkupDocument) to import: `__pm_s`, the names of a <script setup> (the ones it
 * declares and the ones the markup uses) or the instance an options object makes (its data, computed, methods, props and setup's), and
 * `__pm_p`, a <script setup>'s props. An options object's `export default` is written as a constant of the same length, to be read.
 */
export function vueScriptDocument(text, doc, ts) {
    const setup = /<script\b[^>]*\bsetup\b/i.test(text);
    let out = doc, tail;
    if (setup) {
        const used = vueSpots(text).flatMap(([a, z]) => freeNames(text.slice(a, z)));
        const names = [...new Set([...topLevelNames(doc), ...used])].filter((n) => !n.startsWith('__pm_'));
        const at = /\bdefineProps\s*/.exec(doc);
        const after = at ? at.index + at[0].length : -1;
        const type = at ? bracketed(doc, after, '<', '>') : '', arg = at && !type ? bracketed(doc, after, '(', ')') : '';
        const props = type && ts ? `export declare const __pm_p: ${type};` : arg.trim() ? `export const __pm_p = __pm_props(${arg});` : 'export const __pm_p = {};';
        tail = `export const __pm_s = { ${names.join(', ')} }; ${props}`;
    }
    else {
        const m = /\bexport default(?=\s*(?:\{|[A-Za-z_$][\w$.]*\s*\())/.exec(doc);
        // a plain object (export default { ... }): given to __pm_options in as many characters, so that its methods' this is the instance
        const wrap = m && /^ \{/.test(doc.slice(m.index + 14)) ? closingBrace(doc, m.index + 15) : -1;
        // (its ")" takes the place of the first space or ";" after the brace, past line ends, which stay: every line keeps its number)
        let shut = wrap + 1;
        while (shut > 0 && (doc[shut] === '\n' || doc[shut] === '\r')) shut++;
        if (wrap > 0 && /[ \t;]/.test(doc[shut] ?? '')) out = doc.slice(0, m.index) + 'let __pm_o=_o_(' + doc.slice(m.index + 15, shut) + ')' + doc.slice(shut + 1);
        else if (m) out = doc.slice(0, m.index) + 'const __pm_o =' + doc.slice(m.index + 14);
        tail = `export const __pm_s = ${m ? '__pm_inst(__pm_o)' : '{}'}; export const __pm_p = {};`;
    }
    return out + componentPrelude('vue', out) + VUE_TYPES + '\n' + tail + '\n';
}

/**
 * The names a component's markup gives (v-for="(item, i) in list", v-slot="{ row }", #name="props"): [{ names, source, kind }] with the
 * names' text as written (a destructuring's braces kept) and, for a v-for, the list's expression. Only names given once in the file are
 * declared for the markup's document (one given twice may stand for two kinds of value: its expressions are left out, as before).
 */
export function vueBindings(text) {
    const open = /<template\b([^>]*)>/i.exec(text);
    const close = text.lastIndexOf('</template');
    if (!open || close < 0 || /\blang\s*=/.test(open[1])) return { list: [], declared: new Set() };
    const body = text.slice(open.index + open[0].length, close).replace(/<!--[\s\S]*?(?:-->|$)/g, (m) => ' '.repeat(m.length));
    const list = [];
    for (const m of body.matchAll(/\s(v-for|v-slot(?::[\w.-]+)?|#[\w.-]+)\s*=\s*(["'])([\s\S]*?)\2/g)) {
        if (m[1] === 'v-for') {
            const f = /^\s*([\s\S]*?)\s+(?:in|of)\s+([\s\S]+?)\s*$/.exec(m[3]);
            if (f) list.push({ kind: 'for', names: f[1].replace(/^\(([\s\S]*)\)$/, '$1').trim(), source: f[2] });
        }
        else list.push({ kind: 'slot', names: m[3].trim(), source: '' });
    }
    const count = new Map();
    for (const b of list) for (const n of b.names.match(IDENT) ?? []) count.set(n, (count.get(n) ?? 0) + 1);
    const declared = new Set([...count].filter(([n, k]) => k === 1 && !KEYWORDS.has(n)).map(([n]) => n));
    return { list: list.filter((b) => (b.names.match(IDENT) ?? []).every((n) => declared.has(n))), declared };
}

/** A v-for's or a slot's names as declarations of the markup's document: an item is its list's element, an index or key what it is, a slot's any. */
function bindingCode(b, ts) {
    const any = ts ? '(0 as any)' : '/** @type {any} */ (0)';
    if (b.kind === 'slot') return `var ${b.names} = ${any};`;
    if (/^[{[]/.test(b.names)) return `var ${b.names} = __pm_each((${b.source}));`;
    const [item, key, index] = b.names.split(',').map((x) => x.trim());
    return [item && `var ${item} = __pm_each((${b.source}));`, key && `var ${key} = __pm_key((${b.source}));`, index && `var ${index} = 0;`].filter(Boolean).join(' ');
}
const EACH_TS = 'declare function __pm_each<T>(x: T): T extends readonly (infer E)[] ? E : T extends number ? number : T extends string ? string : T extends Iterable<infer E> ? E : T extends object ? T[keyof T] : any; '
    + 'declare function __pm_key<T>(x: T): T extends readonly any[] ? number : T extends number ? number : T extends object ? keyof T & string : any;';
const EACH_JS = '/** @type {<T>(x: T) => (T extends readonly (infer E)[] ? E : T extends number ? number : T extends string ? string : T extends Iterable<infer E> ? E : T extends object ? T[keyof T] : any)} */ var __pm_each; '
    + '/** @type {<T>(x: T) => (T extends readonly any[] ? number : T extends number ? number : T extends object ? keyof T & string : any)} */ var __pm_key;';

// ------------------------------------------------------------ the props of a component another one uses

const CONSTRUCTORS = { String: 'string', Number: 'number', Boolean: 'boolean', Array: 'any[]', Object: 'Record<string, any>', Function: '(...args: any[]) => any', Date: 'Date', Symbol: 'symbol', BigInt: 'bigint' };
/** A runtime prop's type (String, [String, Number], { type: Number, required: true }) as a type: { type, required }. */
function runtimeProp(value) {
    const v = String(value ?? '').trim();
    const one = (t) => CONSTRUCTORS[t.trim()] ?? 'any';
    if (/^\[/.test(v)) return { type: v.slice(1, -1).split(',').map(one).join(' | ') || 'any', required: false };
    if (/^\{/.test(v)) {
        const type = /\btype\s*:\s*(\[[^\]]*\]|[A-Za-z_$][\w$]*)/.exec(v)?.[1];
        return { type: type ? runtimeProp(type).type : 'any', required: /\brequired\s*:\s*true\b/.test(v) && !/\bdefault\s*:/.test(v) };
    }
    return { type: CONSTRUCTORS[v] ?? 'any', required: false };
}
/** An object literal's members at its top: [[key, value text]]. */
function members(objectText) {
    const inner = objectText.trim().replace(/^\{/, '').replace(/\}$/, '');
    const out = [];
    let depth = 0, quote = null, start = 0;
    const push = (end) => {
        const part = inner.slice(start, end).trim();
        const m = /^(?:['"]?)([A-Za-z_$][\w$-]*)(?:['"]?)\s*(?:\??:\s*([\s\S]*))?$/.exec(part);
        if (m) out.push([m[1], m[2] ?? m[1]]);
        start = end + 1;
    };
    for (let i = 0; i < inner.length; i++) {
        const c = inner[i];
        if (quote) { if (c === '\\') i++; else if (c === quote) quote = null; continue; }
        if (c === '"' || c === "'" || c === '`') quote = c;
        else if ('{[('.includes(c)) depth++;
        else if ('}])'.includes(c)) depth--;
        else if ((c === ',' || c === ';' || c === '\n') && depth === 0) push(i);
    }
    push(inner.length);
    return out.filter(([k]) => k);
}

/**
 * The props a component declares, from its file's text: { type, required } with `type` an object type's text ({ label: string; count?: number })
 * and `required` the names that must be given; null when it declares none that can be read. defineProps<T>() (T written there, or an
 * interface or type of the file), defineProps({ ... }) and defineProps([...]) of a <script setup>, an options object's props.
 */
export function vueProps(text) {
    const script = [...String(text).matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/gi)].map((m) => m[1]).join('\n');
    if (!script) return null;
    const at = /\bdefineProps\s*/.exec(script);
    let typeText = '', runtime = '';
    if (at) {
        const after = at.index + at[0].length;
        typeText = bracketed(script, after, '<', '>').trim();
        if (!typeText) runtime = bracketed(script, after, '(', ')').trim();
        if (/^[A-Za-z_$][\w$]*$/.test(typeText)) {
            const named = new RegExp(`\\b(?:interface\\s+${typeText}\\s*(?:extends[^{]*)?(?=\\{)|type\\s+${typeText}\\s*=\\s*(?=\\{))`).exec(script);
            typeText = named ? bracketed(script, named.index + named[0].length, '{', '}') : '';
            if (typeText) typeText = '{' + typeText + '}';
        }
    }
    else {
        const m = /\bprops\s*:\s*(?=[{[])/.exec(script.slice(script.search(/\bexport\s+default\b/) >= 0 ? script.search(/\bexport\s+default\b/) : 0));
        if (m) {
            const base = script.search(/\bexport\s+default\b/) >= 0 ? script.search(/\bexport\s+default\b/) : 0;
            const from = base + m.index + m[0].length;
            runtime = script[from] === '[' ? '[' + bracketed(script, from, '[', ']') + ']' : '{' + bracketed(script, from, '{', '}') + '}';
        }
    }
    const props = [];
    if (typeText.startsWith('{')) {
        for (const [name, value] of members(typeText)) {
            const optional = new RegExp(`(?:^|[{;,\\n])\\s*['"]?${name.replace(/\$/g, '\\$')}['"]?\\s*\\?\\s*:`).test(typeText);
            props.push({ name, type: value === name ? 'any' : value.trim(), required: !optional });
        }
        // withDefaults(defineProps<...>(), { count: 1 }): a prop with a default need not be given
        const defaults = /\bwithDefaults\s*\(/.exec(script);
        if (defaults) {
            const call = bracketed(script, defaults.index + defaults[0].length - 1, '(', ')');
            const second = call.slice(call.lastIndexOf('{'));
            for (const [name] of members(second)) for (const p of props) if (p.name === name) p.required = false;
        }
    }
    else if (runtime.startsWith('[')) for (const m of runtime.matchAll(/['"]([\w$-]+)['"]/g)) props.push({ name: m[1], type: 'any', required: false });
    else if (runtime.startsWith('{')) for (const [name, value] of members(runtime)) props.push({ name, ...runtimeProp(value === name ? '' : value) });
    if (!props.length) return null;
    return { type: '{ ' + props.map((p) => `${JSON.stringify(p.name)}${p.required ? '' : '?'}: ${p.type};`).join(' ') + ' }', required: props.filter((p) => p.required).map((p) => p.name) };
}

const camel = (s) => s.replace(/-([a-z0-9])/g, (m, c) => c.toUpperCase());
const pascal = (s) => camel(s).replace(/^[a-z]/, (c) => c.toUpperCase());

/** The components a script imports from .vue files: { Name: './Name.vue' } (default imports). */
export function vueImports(text) {
    const out = {};
    const script = [...String(text).matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/gi)].map((m) => m[1]).join('\n');
    for (const m of script.matchAll(/\bimport\s+([A-Za-z_$][\w$]*)\s+from\s+(["'])([^"']+\.vue)\2/g)) out[m[1]] = m[3];
    return out;
}

/**
 * Where a component's markup uses the components it imports, and what it gives them: [{ component, from, tag: [start, end], attrs: [{ prop,
 * value, bound, place: [start, end] }] }] (offsets in the file; a bound value is code, a plain one text; v-model gives modelValue).
 */
export function vueComponentUses(text) {
    const imports = vueImports(text);
    const names = Object.keys(imports);
    const open = /<template\b([^>]*)>/i.exec(text);
    const close = text.lastIndexOf('</template');
    if (!names.length || !open || close < 0 || /\blang\s*=/.test(open[1])) return [];
    const from = open.index + open[0].length;
    const body = text.slice(from, close).replace(/<!--[\s\S]*?(?:-->|$)/g, (m) => ' '.repeat(m.length));
    const uses = [];
    for (const m of body.matchAll(/<([A-Za-z][\w-]*)((?:\s+(?:[^\s"'>/=]+)(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]+))?)*)\s*\/?>/g)) {
        const component = names.find((n) => n === m[1] || n === pascal(m[1]));
        if (!component) continue;
        const tagAt = from + m.index + 1;
        const attrs = [];
        const attrsAt = from + m.index + 1 + m[1].length;
        for (const a of m[2].matchAll(/([^\s"'>/=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?/g)) {
            const name = a[1];
            const value = a[2] ?? a[3] ?? a[4];
            let prop = null, bound = false;
            if (/^(?::|v-bind:)/.test(name)) { prop = name.replace(/^(?::|v-bind:)/, '').replace(/\.(?:prop|camel|attr)$/, ''); bound = true; }
            else if (/^v-model(?::|$)/.test(name)) { prop = name.includes(':') ? name.slice(8).split('.')[0] : 'modelValue'; bound = true; }
            else if (!/^(?:v-|@|#|ref$|key$|class$|style$|is$|slot$)/.test(name)) prop = name;
            if (!prop || /[[\]]/.test(prop)) continue;
            const start = attrsAt + a.index;
            attrs.push({ prop: camel(prop), value: value ?? null, bound, place: [start, start + name.length] });
        }
        uses.push({ component, from: imports[component], tag: [tagAt, tagAt + m[1].length], attrs });
    }
    return uses;
}

/**
 * The checks of the props a markup gives its components (`props`: the import's path -> vueProps of its file): `code`, lines for the end of
 * the markup's document (each prop given, assigned to a value of the component's props' type), `places`, each line's place in the file (null
 * for a line that only declares), and `missing`, the required props a use leaves out: [{ place, message }].
 */
export function vuePropChecks(text, props, ts) {
    const code = [], places = [], missing = [];
    vueComponentUses(text).forEach((use, k) => {
        const declared = props?.[use.from];
        if (!declared) return;
        const known = new Set((declared.type.match(/"([^"]+)"\??:/g) ?? []).map((x) => x.replace(/"\??:$/, '').replace(/^"/, '')));
        const given = new Set(use.attrs.map((a) => a.prop));
        for (const name of declared.required) if (!given.has(name) && !use.attrs.some((a) => a.prop === 'vBind')) missing.push({ place: use.tag, message: `${use.component} needs the prop ${name}.` });
        const checked = use.attrs.filter((a) => known.has(a.prop));
        if (!checked.length) return;
        code.push(ts ? `declare const __pm_c${k}: ${declared.type};` : `/** @type {${declared.type}} */ var __pm_c${k};`);
        places.push(null);
        for (const a of checked) {
            const value = a.bound ? `(${a.value ?? 'true'})` : a.value === null ? 'true' : JSON.stringify(a.value);
            code.push(`__pm_c${k}[${JSON.stringify(a.prop)}] = ${value};`);
            places.push(a.place);
        }
    });
    return { code, places, missing };
}
/** The line of the markup's document after which its props' checks follow (vuePropChecks' places are of the lines after it). */
export const PROPS_AT = '//__pm_props';

/**
 * The document of a Vue component's markup: blank but for its expressions, each written as ;(expr) at its own place, and after the
 * document's end the names they use, taken from the script's document (`module`, its file's name without the extension): a ref is its
 * value here, as Vue reads it in a template, and a prop is a name of its own. A v-for's and a slot's names are declared there too (an item
 * is its list's element), and with `props` (vuePropChecks) the props given to the components it uses are checked after PROPS_AT.
 */
export function vueMarkupDocument(text, module, ts, props = null) {
    const out = blankOut(text).split('');
    const { list: bindings, declared } = vueBindings(text);
    const spots = vueSpots(text, false, declared);
    for (const [a, z, at, shut] of spots) {
        out[at] = ';';
        out[at + 1] = '(';
        for (let i = a; i < z; i++) out[i] = text[i];
        out[shut] = ')';
    }
    const checks = props ? vuePropChecks(text, props, ts) : { code: [], places: [] };
    const given = new Set(bindings.flatMap((b) => b.names.match(IDENT) ?? []));
    const used = [...spots.map(([a, z]) => text.slice(a, z)), ...bindings.map((b) => b.source), ...checks.code.filter((c) => c.startsWith('__pm_c')).map((c) => c.slice(c.indexOf('=') + 1))];
    const names = [...new Set(used.flatMap((c) => freeNames(c)))].filter((n) => !n.startsWith('__pm_') && !given.has(n));
    const values = ts
        ? 'type __pm_U<T> = 0 extends 1 & import("vue").Ref ? T : T extends import("vue").Ref<infer V> ? V : T; '
            + 'declare const __pm_v: { [K in keyof typeof __pm.__pm_s as 0 extends 1 & (typeof __pm.__pm_s)[K] ? never : K]: __pm_U<(typeof __pm.__pm_s)[K]> } & typeof __pm.__pm_p;'
        : 'const __pm_v = { ...__pm.__pm_p, ...__pm.__pm_s };';
    return out.join('') + `\n;import * as __pm from ${JSON.stringify('./' + module)}; ${values}\n`
        + names.map((n) => `var ${n} = __pm_v.${n};`).join(' ') + ` ${ts ? EACH_TS : EACH_JS} ${bindings.map((b) => bindingCode(b, ts)).join(' ')}`
        + `\n;(__pm_v.${MEMBERS_AT});\n${PROPS_AT}\n${checks.code.join('\n')}\n`;
}
/** The word after which the markup's document lists every name the markup may use (asked for completion). */
export const MEMBERS_AT = '__pm_all';

/** Where a name of the markup's document is taken from the script (its `var name = __pm_v.name;`): the offset of the member, or -1. */
export function memberOffset(markupDoc, name) {
    const at = markupDoc.lastIndexOf(`var ${name} = __pm_v.${name};`);
    return at < 0 ? -1 : at + `var ${name} = __pm_v.`.length;
}
/** Where a <script setup>'s name is listed for the markup (in `__pm_s = { … }` after the script's document): its offset, or -1. */
export function listedOffset(scriptDoc, name) {
    const start = scriptDoc.lastIndexOf('export const __pm_s = {');
    if (start < 0) return -1;
    const end = scriptDoc.indexOf('}', start);
    const m = new RegExp(`(?<![\\w$])${name.replace(/\$/g, '\\$')}(?![\\w$])`).exec(scriptDoc.slice(start + 22, end));
    return m ? start + 22 + m.index : -1;
}

/**
 * Which findings of a block's checker a template shows: in a template's <script> its syntax errors only (its names are the page's, not
 * checked against the page's other scripts); in a component's, the type errors too, but not a name the markup uses (declared but not read)
 * nor, in Svelte, a store's $name.
 */
export function keepFinding(code, message, kind, inMarkup = false) {
    const n = Number(typeof code === 'object' && code ? code.value : code);
    // an expression of the markup: only a property or method that is not there, and a call with the wrong arguments. A name not found
    // is the markup's own to give; a ref inside another value Vue does not read by itself, so nothing about a Ref is told
    if (inMarkup) {
        if (![2339, 2551, 2554, 2345].includes(n)) return false;
        return !(kind === 'vue' && /Ref</.test(String(message))) && !/'\$\w+'/.test(String(message));
    }
    if (!Number.isFinite(n) || n < 2000) return true;
    if (kind !== 'vue' && kind !== 'svelte') return false;
    if ([6133, 6138, 6192, 6196, 6198, 6199, 6205].includes(n)) return false;
    // an options object's computed that uses another one through this: TypeScript cannot tell its type, which Vue does not ask for
    if (kind === 'vue' && [7022, 7023, 7024].includes(n)) return false;
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

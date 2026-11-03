// Tailwind CSS class completion without the editor: where a class is being typed, its variants (md:hover:), which classes and variants to offer,
// and the colour of a colour class (its swatch). No DOM here: web/test/tailwind.test.mjs checks it.

// class="..." and className="..." (also className={`...`} and className={"..."}), Svelte's class:list, Vue's :class, and the helpers React
// projects join classes with (clsx, cn, classNames, twMerge, cva, tw)
const CONTEXTS = [
    /(?:\bclass(?:Name)?|\bclass:list|:class|\bngClass)\s*=\s*\{?\s*(["'`])([^"'`]*)$/,
    // the arguments before it: closed strings and anything but brackets (active && "...", { "...": x })
    /\b(?:clsx|cn|classNames|classnames|twMerge|twJoin|cva|tw)\((?:[^()"'`]|"[^"]*"|'[^']*'|`[^`]*`)*?(["'`])([^"'`]*)$/,
];

/** The class being typed at the end of the text before the caret (within a class attribute or a class helper), or null when it is not in one. */
export function classAt(textBefore) {
    const tail = textBefore.slice(-2000);
    for (const re of CONTEXTS) {
        const m = re.exec(tail);
        // a class starts after a space or a template tag ({% if a %}ring-2, {{ x }}p-4)
        if (m) return m[2].split(/[\s{}]+/).pop();
    }
    return null;
}

/** A class split into its variants (md:hover:, with their colons) and the utility (ml-au); a colon inside [...] is not a variant's. */
export function splitVariants(word) {
    let depth = 0, cut = 0;
    for (let i = 0; i < word.length; i++) {
        const c = word[i];
        if (c === '[' || c === '(') depth++;
        else if (c === ']' || c === ')') depth--;
        else if (c === ':' && depth === 0) cut = i + 1;
    }
    return { variants: word.slice(0, cut), leaf: word.slice(cut) };
}

/** What to offer for a class being typed: the utilities and the variants (with their colon) that start with the same letter; all of them for none typed. */
export function suggestions(word, classes, variants) {
    const { variants: before, leaf } = splitVariants(word);
    const first = leaf.replace(/^[-!]/, '')[0];
    const fits = (name) => !first || name.replace(/^[-!]/, '')[0] === first;
    const out = [];
    for (const v of variants) if (fits(v)) out.push({ label: v + ':', insert: before + v + ':', kind: 'variant' });
    for (const c of classes) if (fits(c)) out.push({ label: c, insert: before + c, kind: 'class' });
    return out;
}

/** The colours of a theme (--color-red-500: oklch(...)), by name (red-500). */
export function themeColors(css) {
    const colors = new Map();
    for (const m of String(css ?? '').matchAll(/--color-([\w-]+)\s*:\s*([^;]+);/g)) colors.set(m[1], m[2].trim());
    return colors;
}

/** The colour of a utility (bg-red-500, text-white, border-x-sky-300/50), as #rrggbb, or null when it has none. */
export function colorOf(cls, colors) {
    const name = cls.replace(/^[-!]/, '').replace(/\/\d+$/, '');
    for (let i = name.indexOf('-'); i >= 0; i = name.indexOf('-', i + 1)) {
        const value = colors.get(name.slice(i + 1));
        if (value) return toHex(value);
    }
    return null;
}

const hex2 = (x) => Math.round(Math.min(1, Math.max(0, x)) * 255).toString(16).padStart(2, '0');

/** A CSS colour as #rrggbb: #rgb and #rrggbb as they are, oklch() converted (as Tailwind 4 writes its palette); anything else is null. */
export function toHex(value) {
    const v = value.trim();
    let m = /^#([0-9a-f]{3})$/i.exec(v);
    if (m) return '#' + [...m[1]].map((c) => c + c).join('').toLowerCase();
    m = /^#([0-9a-f]{6})/i.exec(v);
    if (m) return '#' + m[1].toLowerCase();
    m = /^oklch\(\s*([\d.]+)(%?)\s+([\d.]+)\s+([\d.]+)/i.exec(v);
    if (!m) return null;
    const L = Number(m[1]) / (m[2] ? 100 : 1), C = Number(m[3]), h = Number(m[4]) * Math.PI / 180;
    // OKLCH -> OKLab -> linear sRGB -> sRGB
    const a = C * Math.cos(h), b = C * Math.sin(h);
    const l_ = (L + 0.3963377774 * a + 0.2158037573 * b) ** 3;
    const m_ = (L - 0.1055613458 * a - 0.0638541728 * b) ** 3;
    const s_ = (L - 0.0894841775 * a - 1.2914855480 * b) ** 3;
    const lin = [
        4.0767416621 * l_ - 3.3077115913 * m_ + 0.2309699292 * s_,
        -1.2684380046 * l_ + 2.6097574011 * m_ - 0.3413193965 * s_,
        -0.0041960863 * l_ - 0.7034186147 * m_ + 1.7076147010 * s_,
    ];
    const gamma = (x) => (x <= 0.0031308 ? 12.92 * x : 1.055 * x ** (1 / 2.4) - 0.055);
    return '#' + lin.map((x) => hex2(gamma(x))).join('');
}

/** Whether Tailwind's classes are offered: in a project that uses it (/api/tailwind), or in every project when Settings > Editor says so (all). */
export function tailwindOffered(use, where) {
    return !!use?.used || where === 'all';
}

/** The languages whose markup has class attributes (and JavaScript / TypeScript for JSX and the class helpers). */
export const TAILWIND_LANGUAGES = ['html', 'django-html', 'ejs', 'handlebars', 'twig', 'razor', 'php', 'javascript', 'typescript'];

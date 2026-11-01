// What the exported page of a notebook (notebook-model.js `notebookToHtml`) shows besides plain text: Python code with its colours, and the LaTeX
// of Markdown cells as MathML. Both give HTML with no script in it, so the page stays a page that runs nothing. Pure functions, no imports.

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

// ------------------------------------------------------------ Python code

const KEYWORDS = new Set(['False', 'None', 'True', 'and', 'as', 'assert', 'async', 'await', 'break', 'class', 'continue', 'def', 'del', 'elif', 'else',
    'except', 'finally', 'for', 'from', 'global', 'if', 'import', 'in', 'is', 'lambda', 'nonlocal', 'not', 'or', 'pass', 'raise', 'return', 'try', 'while',
    'with', 'yield', 'match', 'case']);
const BUILTINS = new Set(['print', 'len', 'range', 'int', 'str', 'float', 'list', 'dict', 'set', 'tuple', 'bool', 'bytes', 'type', 'isinstance', 'issubclass',
    'open', 'enumerate', 'zip', 'map', 'filter', 'sum', 'min', 'max', 'abs', 'round', 'sorted', 'reversed', 'any', 'all', 'super', 'object', 'input', 'repr',
    'format', 'iter', 'next', 'getattr', 'setattr', 'hasattr', 'property', 'staticmethod', 'classmethod', 'self', 'cls', 'Exception', 'ValueError',
    'TypeError', 'KeyError', 'IndexError', 'RuntimeError', 'StopIteration', 'NotImplementedError']);

// a comment | a string (with its prefix; an unfinished one runs to the end of its line, or of the text for triple quotes) | a decorator | a number | a name
const PY_TOKEN = /(#[^\n]*)|((?:[rRbBuUfF]{1,2})?(?:"""[\s\S]*?(?:"""|$)|'''[\s\S]*?(?:'''|$)|"(?:\\.|[^"\\\n])*(?:"|(?=\n)|$)|'(?:\\.|[^'\\\n])*(?:'|(?=\n)|$)))|(@[A-Za-z_][\w.]*)|(\b0[xXoObB][0-9a-fA-F_]+\b|\b\d[\d_]*(?:\.\d*)?(?:[eE][+-]?\d+)?[jJ]?\b|\.\d[\d_]*(?:[eE][+-]?\d+)?[jJ]?\b)|([A-Za-z_]\w*)/g;

/** Python source as HTML with `<span class="tok-…">` around comments, strings, numbers, decorators, keywords, built-ins and the names after def and class. */
export function highlightPython(src) {
    const text = String(src);
    let out = '';
    let last = 0;
    let after = '';   // the keyword just before, for the name that follows def or class
    PY_TOKEN.lastIndex = 0;
    for (let m = PY_TOKEN.exec(text); m; m = PY_TOKEN.exec(text)) {
        if (m[0] === '') { PY_TOKEN.lastIndex++; continue; }
        out += esc(text.slice(last, m.index));
        last = m.index + m[0].length;
        let cls = '';
        if (m[1] !== undefined) cls = 'tok-c';
        else if (m[2] !== undefined) cls = 'tok-s';
        else if (m[3] !== undefined) cls = 'tok-d';
        else if (m[4] !== undefined) cls = 'tok-n';
        else {
            const word = m[5];
            if (after === 'def' || after === 'class') cls = after === 'def' ? 'tok-f' : 'tok-t';
            else if (KEYWORDS.has(word)) cls = 'tok-k';
            else if (BUILTINS.has(word)) cls = 'tok-b';
            after = word === 'def' || word === 'class' ? word : '';
        }
        if (m[5] === undefined) after = '';
        out += cls ? `<span class="${cls}">${esc(m[0])}</span>` : esc(m[0]);
    }
    return out + esc(text.slice(last));
}

export const HIGHLIGHT_CSS = '.tok-k{color:#0033b3;font-weight:600}.tok-s{color:#067d17}.tok-c{color:#8c8c8c;font-style:italic}.tok-n{color:#1750eb}.tok-d{color:#9e880d}'
    + '.tok-b{color:#871094}.tok-f{color:#00627a}.tok-t{color:#00627a;font-weight:600}';

// ------------------------------------------------------------ LaTeX as MathML

const GREEK = {
    alpha: 'α', beta: 'β', gamma: 'γ', delta: 'δ', epsilon: 'ϵ', varepsilon: 'ε', zeta: 'ζ', eta: 'η', theta: 'θ', vartheta: 'ϑ', iota: 'ι', kappa: 'κ',
    lambda: 'λ', mu: 'μ', nu: 'ν', xi: 'ξ', pi: 'π', varpi: 'ϖ', rho: 'ρ', varrho: 'ϱ', sigma: 'σ', varsigma: 'ς', tau: 'τ', upsilon: 'υ', phi: 'ϕ',
    varphi: 'φ', chi: 'χ', psi: 'ψ', omega: 'ω', Gamma: 'Γ', Delta: 'Δ', Theta: 'Θ', Lambda: 'Λ', Xi: 'Ξ', Pi: 'Π', Sigma: 'Σ', Upsilon: 'Υ', Phi: 'Φ',
    Psi: 'Ψ', Omega: 'Ω',
};
const SYMBOLS = {
    pm: '±', mp: '∓', times: '×', div: '÷', cdot: '⋅', ast: '∗', star: '⋆', circ: '∘', bullet: '•', oplus: '⊕', otimes: '⊗', leq: '≤', le: '≤', geq: '≥',
    ge: '≥', neq: '≠', ne: '≠', approx: '≈', equiv: '≡', sim: '∼', simeq: '≃', cong: '≅', propto: '∝', ll: '≪', gg: '≫', in: '∈', notin: '∉', ni: '∋',
    subset: '⊂', supset: '⊃', subseteq: '⊆', supseteq: '⊇', cup: '∪', cap: '∩', setminus: '∖', emptyset: '∅', varnothing: '∅', forall: '∀', exists: '∃',
    neg: '¬', lnot: '¬', land: '∧', wedge: '∧', lor: '∨', vee: '∨', to: '→', rightarrow: '→', leftarrow: '←', leftrightarrow: '↔', Rightarrow: '⇒',
    Leftarrow: '⇐', Leftrightarrow: '⇔', iff: '⇔', implies: '⇒', mapsto: '↦', uparrow: '↑', downarrow: '↓', infty: '∞', partial: '∂', nabla: '∇', ldots: '…',
    cdots: '⋯', dots: '…', vdots: '⋮', ddots: '⋱', prime: '′', angle: '∠', perp: '⊥', parallel: '∥', mid: '∣', langle: '⟨', rangle: '⟩', lfloor: '⌊',
    rfloor: '⌋', lceil: '⌈', rceil: '⌉', hbar: 'ℏ', ell: 'ℓ', Re: 'ℜ', Im: 'ℑ', aleph: 'ℵ', degree: '°', dagger: '†', colon: ':',
};
const BIG = { sum: '∑', prod: '∏', coprod: '∐', bigcup: '⋃', bigcap: '⋂', int: '∫', iint: '∬', iiint: '∭', oint: '∮' };
const LIMITS = new Set(['sum', 'prod', 'coprod', 'bigcup', 'bigcap']);   // limits above and below in display mode, the rest at the side
const FUNCTIONS = new Set(['sin', 'cos', 'tan', 'cot', 'sec', 'csc', 'arcsin', 'arccos', 'arctan', 'sinh', 'cosh', 'tanh', 'log', 'ln', 'lg', 'exp', 'lim', 'max',
    'min', 'sup', 'inf', 'det', 'dim', 'gcd', 'deg', 'arg', 'ker', 'Pr', 'mod']);
const LIMIT_FUNCTIONS = new Set(['lim', 'max', 'min', 'sup', 'inf']);
const FONTS = { mathbf: 'bold', mathrm: 'normal', mathit: 'italic', mathbb: 'double-struck', mathcal: 'script', mathfrak: 'fraktur', mathsf: 'sans-serif', mathtt: 'monospace', boldsymbol: 'bold-italic' };
const ACCENTS = { hat: '^', widehat: '^', bar: '¯', overline: '¯', vec: '→', tilde: '~', widetilde: '~', dot: '˙', ddot: '¨', check: 'ˇ', acute: '´', grave: '`' };
const SPACES = { ',': '0.17em', ':': '0.22em', ';': '0.28em', '!': '-0.17em', ' ': '0.28em', quad: '1em', qquad: '2em' };

class Unsupported extends Error { }

function tokensOf(src) {
    const tokens = [];
    const re = /\\([A-Za-z]+)|\\([^A-Za-z])|(\d+(?:\.\d+)?)|(\s+)|([\s\S])/g;
    for (let m = re.exec(src); m; m = re.exec(src)) {
        const at = { s: m.index, e: m.index + m[0].length };   // where it stands in the text, for 	ext{...}, which keeps its spaces
        if (m[1] !== undefined) tokens.push({ t: 'cmd', v: m[1], ...at });
        else if (m[2] !== undefined) tokens.push({ t: 'esc', v: m[2], ...at });
        else if (m[3] !== undefined) tokens.push({ t: 'num', v: m[3], ...at });
        else if (m[4] !== undefined) continue;
        else tokens.push({ t: 'ch', v: m[5], ...at });
    }
    return tokens;
}

const tag = (name, inner, attrs = '') => `<${name}${attrs}>${inner}</${name}>`;
const mrow = (items) => (items.length === 1 ? items[0] : tag('mrow', items.join('')));
const mo = (c, attrs = '') => tag('mo', esc(c), attrs);

function parseLatex(src, display) {
    const toks = tokensOf(src);
    let pos = 0;
    const peek = () => toks[pos];
    const isCh = (t, c) => t && t.t === 'ch' && t.v === c;

    function group() {   // after "{" was seen
        const items = sequence('}');
        if (!isCh(peek(), '}')) throw new Unsupported('a group is not closed');
        pos++;
        return mrow(items);
    }
    function argument() {   // one argument: a group or a single token
        const t = peek();
        if (!t) throw new Unsupported('an argument is missing');
        if (isCh(t, '{')) { pos++; return group(); }
        return atom();
    }
    function rawText() {   // the text of a "{...}" with no maths in it
        if (!isCh(peek(), '{')) throw new Unsupported('text needs braces');
        const from = toks[pos].e;
        pos++;
        let depth = 1;
        while (pos < toks.length) {
            const t = toks[pos++];
            if (isCh(t, '{')) depth++;
            else if (isCh(t, '}') && --depth === 0) return src.slice(from, t.s).replace(/\\([{}%$#&_ ])/g, '$1');
        }
        throw new Unsupported('text is not closed');
    }
    function delimiter() {
        const t = toks[pos++];
        if (!t) throw new Unsupported('a delimiter is missing');
        if (t.t === 'ch' && t.v === '.') return '';
        if (t.t === 'ch') return t.v;
        if (t.t === 'esc') return t.v === '{' ? '{' : t.v === '}' ? '}' : t.v === '|' ? '‖' : '';
        if (t.t === 'cmd' && SYMBOLS[t.v]) return SYMBOLS[t.v];
        if (t.t === 'cmd' && (t.v === 'vert' || t.v === 'lvert' || t.v === 'rvert')) return '|';
        throw new Unsupported('a delimiter');
    }

    // returns { node, base } where base names a big operator or a limit function when scripts may go above and below it
    function atom() {
        const t = toks[pos++];
        if (!t) throw new Unsupported('the end of the formula');
        if (t.t === 'num') return tag('mn', esc(t.v));
        if (t.t === 'ch') {
            const c = t.v;
            if (c === '{') return group();
            if (/[A-Za-z]/.test(c)) return tag('mi', c);
            if (c === '-') return mo('−');
            if (c === '*') return mo('∗');
            if (c === '\'') return mo('′');
            if (c === '~') return tag('mspace', '', ' width="0.28em"');
            if ('+=<>/,;:!?|()[].'.includes(c)) return mo(c);
            if (c === '}' || c === '^' || c === '_' || c === '&' || c === '#' || c === '$' || c === '%') throw new Unsupported(`unexpected ${c}`);
            return tag('mtext', esc(c));
        }
        if (t.t === 'esc') {
            const c = t.v;
            if (c in SPACES) return tag('mspace', '', ` width="${SPACES[c]}"`);
            if ('{}%$#&_'.includes(c)) return mo(c);
            if (c === '|') return mo('‖');
            if (c === '\\') throw new Unsupported('a line break');
            throw new Unsupported(`\\${c}`);
        }
        const name = t.v;
        if (GREEK[name]) return tag('mi', GREEK[name]);
        if (SYMBOLS[name]) return mo(SYMBOLS[name]);
        if (BIG[name]) return mo(BIG[name], ' largeop="true"' + (LIMITS.has(name) ? ' movablelimits="true"' : ''));
        if (FUNCTIONS.has(name)) return tag('mi', name, ' mathvariant="normal"');
        if (name in SPACES) return tag('mspace', '', ` width="${SPACES[name]}"`);
        if (name === 'frac' || name === 'dfrac' || name === 'tfrac') { const a = argument(); const b = argument(); return tag('mfrac', a + b); }
        if (name === 'binom') { const a = argument(); const b = argument(); return mo('(') + tag('mfrac', a + b, ' linethickness="0"') + mo(')'); }
        if (name === 'sqrt') {
            if (isCh(peek(), '[')) {
                pos++;
                const idx = [];
                while (pos < toks.length && !isCh(peek(), ']')) idx.push(atom());
                if (!isCh(peek(), ']')) throw new Unsupported('the root index is not closed');
                pos++;
                return tag('mroot', argument() + mrow(idx.length ? idx : [tag('mn', '2')]));
            }
            return tag('msqrt', argument());
        }
        if (name === 'text' || name === 'mbox' || name === 'textrm' || name === 'textbf' || name === 'operatorname') return tag('mtext', esc(rawText()));
        if (FONTS[name]) { const inner = rawGroupChars(); return tag('mi', esc(inner), ` mathvariant="${FONTS[name]}"`); }
        if (ACCENTS[name]) return tag('mover', argument() + mo(ACCENTS[name]), ' accent="true"');
        if (name === 'left') { const d = delimiter(); return d ? mo(d, ' fence="true"') : ''; }
        if (name === 'right') { const d = delimiter(); return d ? mo(d, ' fence="true"') : ''; }
        if (name === 'big' || name === 'Big' || name === 'bigg' || name === 'Bigg' || name === 'bigl' || name === 'bigr') { const d = delimiter(); return d ? mo(d) : ''; }
        if (name === 'displaystyle' || name === 'textstyle' || name === 'limits' || name === 'nolimits') return '';
        throw new Unsupported(`\\${name}`);
    }
    function rawGroupChars() {   // \mathbf{x} and friends: the letters of the group
        const t = peek();
        if (isCh(t, '{')) return rawText();
        pos++;
        return t && (t.t === 'ch' || t.t === 'num') ? t.v : '';
    }

    function sequence(stop) {
        const items = [];
        for (;;) {
            const t = peek();
            if (!t || (stop && isCh(t, stop))) return items;
            const startTok = t;
            let base = atom();
            let sub = null;
            let sup = null;
            for (;;) {
                const n = peek();
                if (isCh(n, '^') && sup === null) { pos++; sup = argument(); }
                else if (isCh(n, '_') && sub === null) { pos++; sub = argument(); }
                else break;
            }
            if (sub !== null || sup !== null) {
                const name = startTok.t === 'cmd' ? startTok.v : '';
                const limits = display && (LIMITS.has(name) || LIMIT_FUNCTIONS.has(name));
                if (sub !== null && sup !== null) base = tag(limits ? 'munderover' : 'msubsup', base + sub + sup);
                else if (sub !== null) base = tag(limits ? 'munder' : 'msub', base + sub);
                else base = tag(limits ? 'mover' : 'msup', base + sup);
            }
            if (base) items.push(base);
        }
    }

    const items = sequence(null);
    if (pos < toks.length) throw new Unsupported('the formula does not end');
    return items.length ? mrow(items) : '';
}

/** A LaTeX formula as MathML (`<math>`), or null when it uses something this reader does not know (the page then shows the LaTeX as it was written). */
export function latexToMathml(tex, display = false) {
    try {
        const body = parseLatex(String(tex), display);
        if (!body) return null;
        return `<math${display ? ' display="block"' : ''} class="tex-math">${tag('mrow', body)}</math>`;
    } catch (e) {
        if (e instanceof Unsupported) return null;
        throw e;
    }
}

// ------------------------------------------------------------ Markdown with formulas

const MATH_MARK = /(\d+)/g;
// a code block | a code span | a display formula | an inline formula: only the formulas are taken out
const MATH_SCAN = /(```[\s\S]*?```|~~~[\s\S]*?~~~)|(`[^`\n]*`)|\$\$([\s\S]+?)\$\$|(?<![\\$])\$(?!\s)([^$\n]*?[^\s\\$])\$(?!\d)/g;

/** Takes the formulas ($…$ and $$…$$, outside code) out of Markdown text, leaving a mark for each, so that the Markdown reader cannot read their `_` and `*` as emphasis. */
export function extractMath(src) {
    const maths = [];
    const text = String(src).replace(MATH_SCAN, (all, fence, span, display, inline) => {
        if (fence !== undefined || span !== undefined) return all;
        maths.push(display !== undefined ? { tex: display.trim(), display: true } : { tex: inline, display: false });
        return `${maths.length - 1}`;
    });
    return { text, maths };
}

/** The HTML that came out of the Markdown reader with each mark replaced by its formula (MathML, or the LaTeX as written when it cannot be read). */
export function restoreMath(html, maths) {
    if (!maths.length) return html;
    return html.replace(MATH_MARK, (all, n) => {
        const m = maths[Number(n)];
        if (!m) return '';
        const ml = latexToMathml(m.tex, m.display);
        if (ml) return ml;
        return `<code class="tex${m.display ? ' display' : ''}">${esc(m.display ? `$$${m.tex}$$` : `$${m.tex}$`)}</code>`;
    });
}

export const MATH_CSS = 'math.tex-math{font-size:1.1em}math[display="block"]{margin:.6rem 0}code.tex{color:#7a3e9d}';

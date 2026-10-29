// Colours in program output: the SGR sequences (ESC [ ... m) of the 16 standard colours, the 256-colour palette and 24-bit colour, with bold, dim,
// italic, underline and inverse; other control sequences (cursor moves, erasing, window titles) are dropped. Output arrives in pieces, so a
// sequence cut in two is kept until the rest comes, and the style carries over from one piece to the next.
// No DOM and no imports, so the checks in web/test/ansi.test.mjs can run it under Node.

const CSI = /\u001b\[([0-9;:?]*)([@-~])/y;
const OSC = /\u001b\][^\u0007\u001b]*(?:\u0007|\u001b\\)/y;

function plain() { return { fg: null, bg: null, bold: false, dim: false, italic: false, underline: false, inverse: false }; }

/** The colour of entry n of xterm's 256-colour palette, as CSS: 0-15 are the standard ones (by number), the rest rgb(). */
export function paletteColor(n) {
    if (n < 16) return n;
    if (n < 232) {
        const i = n - 16, steps = [0, 95, 135, 175, 215, 255];
        return `rgb(${steps[Math.floor(i / 36)]}, ${steps[Math.floor(i / 6) % 6]}, ${steps[i % 6]})`;
    }
    const g = 8 + (n - 232) * 10;
    return `rgb(${g}, ${g}, ${g})`;
}

/** Applies the numbers of one SGR sequence to a style. */
function apply(style, params) {
    const p = params.length === 0 ? [0] : params;
    for (let i = 0; i < p.length; i++) {
        const n = p[i];
        if (n === 0) Object.assign(style, plain());
        else if (n === 1) style.bold = true;
        else if (n === 2) style.dim = true;
        else if (n === 3) style.italic = true;
        else if (n === 4) style.underline = true;
        else if (n === 7) style.inverse = true;
        else if (n === 22) { style.bold = false; style.dim = false; }
        else if (n === 23) style.italic = false;
        else if (n === 24) style.underline = false;
        else if (n === 27) style.inverse = false;
        else if (n >= 30 && n <= 37) style.fg = n - 30;
        else if (n >= 90 && n <= 97) style.fg = n - 90 + 8;
        else if (n === 39) style.fg = null;
        else if (n >= 40 && n <= 47) style.bg = n - 40;
        else if (n >= 100 && n <= 107) style.bg = n - 100 + 8;
        else if (n === 49) style.bg = null;
        else if ((n === 38 || n === 48) && p[i + 1] === 5 && p[i + 2] !== undefined) {
            style[n === 38 ? 'fg' : 'bg'] = paletteColor(p[i + 2]);
            i += 2;
        }
        else if ((n === 38 || n === 48) && p[i + 1] === 2 && p[i + 4] !== undefined) {
            style[n === 38 ? 'fg' : 'bg'] = `rgb(${p[i + 2]}, ${p[i + 3]}, ${p[i + 4]})`;
            i += 4;
        }
    }
}

/** A reader for one stream of output. `feed(text)` gives the pieces of text with the style each has: `[{ text, style }]`. */
export function ansiReader() {
    let style = plain();
    let pending = '';
    return {
        feed(chunk) {
            const text = pending + chunk;
            pending = '';
            const out = [];
            let run = '';
            const flush = () => { if (run) { out.push({ text: run, style: { ...style } }); run = ''; } };
            let i = 0;
            while (i < text.length) {
                const esc = text.indexOf('\u001b', i);
                if (esc < 0) { run += text.slice(i); break; }
                run += text.slice(i, esc);
                CSI.lastIndex = esc;
                OSC.lastIndex = esc;
                const csi = CSI.exec(text);
                if (csi) {
                    if (csi[2] === 'm') { flush(); apply(style, csi[1].split(/[;:]/).filter((s) => s !== '').map(Number)); }
                    i = CSI.lastIndex;
                    continue;
                }
                const osc = OSC.exec(text);
                if (osc) { i = OSC.lastIndex; continue; }
                // a sequence that is not finished yet: kept for the next piece (unless it can never finish)
                const rest = text.slice(esc);
                if (rest.length < 64 && /^\u001b(\[[0-9;:?]*|\][^\u0007]*)?$/.test(rest)) { pending = rest; break; }
                i = esc + 1;   // a lone ESC
            }
            flush();
            return out;
        },
        reset() { style = plain(); pending = ''; },
    };
}

/** The text without any escape sequence (for copying, searching, or where colours are not wanted). */
export function stripAnsi(text) {
    return text.replace(/\u001b\[[0-9;:?]*[@-~]/g, '').replace(/\u001b\][^\u0007\u001b]*(?:\u0007|\u001b\\)/g, '');
}

/** The CSS classes and inline style of a piece: the 16 standard colours as classes (the theme picks them), the others inline. */
export function cssOf(style) {
    let fg = style.fg, bg = style.bg;
    if (style.inverse) [fg, bg] = [bg ?? 'default-bg', fg ?? 'default-fg'];
    const classes = [];
    const inline = [];
    const put = (value, kind) => {
        if (value === null || value === undefined) return;
        if (typeof value === 'number') classes.push(`ansi-${kind}${value}`);
        else if (value === 'default-bg' || value === 'default-fg') classes.push(`ansi-${kind}-${value}`);
        else inline.push(`${kind === 'fg' ? 'color' : 'background-color'}: ${value}`);
    };
    put(fg, 'fg');
    put(bg, 'bg');
    if (style.bold) classes.push('ansi-bold');
    if (style.dim) classes.push('ansi-dim');
    if (style.italic) classes.push('ansi-italic');
    if (style.underline) classes.push('ansi-underline');
    return { className: classes.join(' '), style: inline.join('; ') };
}

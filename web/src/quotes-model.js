// Typing a quote over a selected string literal: the string's quotes are changed to the one typed ("pen" and ' give 'pen'),
// instead of the selection being wrapped in it ('"pen"'). Kept apart from the page so its Node tests can check it.

/**
 * The selected text with its quotes changed to `quote`, or null when the selection is not one whole string literal
 * written with the other quote (then the editor wraps it as usual). Escapes follow: `"it's"` becomes `'it\'s'` and
 * `'say \'hi\''` becomes `"say 'hi'"`. A raw string or an f-string that holds the new quote is left alone, since
 * escaping would change it.
 */
export function requote(text, quote) {
    if (quote !== "'" && quote !== '"')
        return null;
    const other = quote === "'" ? '"' : "'";
    const prefixed = /^([rRbBuUfF]{0,2})([\s\S]*)$/.exec(text);
    const prefix = prefixed[1], literal = prefixed[2];
    const raw = /r/i.test(prefix), formatted = /f/i.test(prefix);
    // a triple-quoted string: its three quotes change when the new ones are not inside it
    const triple = other.repeat(3);
    if (literal.length >= 6 && literal.startsWith(triple) && literal.endsWith(triple)) {
        const body = literal.slice(3, -3);
        if (body.includes(triple) || body.includes(quote.repeat(3)) || body.endsWith(quote) || body.endsWith('\\'))
            return null;
        return prefix + quote.repeat(3) + body + quote.repeat(3);
    }
    if (literal.length < 2 || literal[0] !== other || literal[literal.length - 1] !== other || literal.includes('\n'))
        return null;
    const body = literal.slice(1, -1);
    // one string only: an unescaped quote inside means the selection holds more than one ("a" + "b")
    for (let i = 0; i < body.length; i++) {
        if (body[i] === '\\') {
            i++;
            if (i === body.length)
                return null;   // the closing quote was escaped: not the end of the string
        }
        else if (body[i] === other)
            return null;
    }
    if ((raw || formatted) && body.includes(quote))
        return null;
    if (raw)
        return prefix + quote + body + quote;
    let out = '';
    for (let i = 0; i < body.length; i++) {
        const c = body[i];
        if (c === '\\') {
            const next = body[i + 1];
            out += next === other ? other : c + next;   // \" is no longer needed inside '...'
            i++;
        }
        else
            out += c === quote ? '\\' + quote : c;
    }
    return prefix + quote + out + quote;
}

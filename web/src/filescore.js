// How well a file path matches what was typed in Search everywhere: lower is better, -1 is no match.
export function fileScore(path, query) {
    const p = path.toLowerCase();
    const name = p.slice(p.lastIndexOf('/') + 1);
    const s = query.toLowerCase();
    if (!s)
        return 10;
    if (name === s)
        return 0;
    if (name.startsWith(s))
        return 1;
    const inName = name.indexOf(s);
    if (inName >= 0)
        return 2 + inName / 100;
    const inPath = p.indexOf(s);
    if (inPath >= 0)
        return 3 + inPath / 1000;
    let k = 0;
    for (const c of name)
        if (k < s.length && c === s[k])
            k++;
    return k === s.length ? 5 : -1;
}

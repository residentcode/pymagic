// The dependencies of a package.json that its node_modules does not have, as PyCharm marks them ("Package is not installed"): where each name is in
// the text, and which are missing from what the host read in node_modules. No DOM, no Monaco: npm-check.js shows them, web/test/npm.test.mjs checks it.

/** The sections npm installs: a dependency of them is expected in node_modules (optional and peer ones may be left out). */
export const SECTIONS = ['dependencies', 'devDependencies'];

/** Whether a path is a package.json of the project's own (not one inside node_modules). */
export function isPackageJson(rel) {
    return /(^|\/)package\.json$/i.test(rel) && !/(^|\/)node_modules\//i.test(rel);
}

/** The folder of a package.json ('' for the project's own), where its node_modules is. */
export function folderOf(rel) {
    const at = rel.lastIndexOf('/');
    return at < 0 ? '' : rel.slice(0, at);
}

/**
 * The dependencies written in a package.json's text: [{ name, spec, section, line, start, end }] (1-based line, columns of the name's quotes).
 * A text that is not an object is no dependencies.
 */
export function dependencyEntries(text) {
    const out = [];
    let i = 0, line = 1, col = 1;
    const stack = [];          // for each open object or array: { type, key } (key: the key it is the value of)
    let lastKey = null;        // the key of the value about to come
    let expectKey = false;
    const advance = (ch) => { if (ch === '\n') { line++; col = 1; } else col++; i++; };
    while (i < text.length) {
        const ch = text[i];
        if (ch === '"') {
            const startLine = line, startCol = col;
            let s = '';
            advance(ch);
            while (i < text.length && text[i] !== '"') {
                if (text[i] === '\\' && i + 1 < text.length) { s += text[i + 1]; advance(text[i]); advance(text[i]); continue; }
                if (text[i] === '\n') break;
                s += text[i];
                advance(text[i]);
            }
            if (i < text.length && text[i] === '"') advance('"');
            const top = stack[stack.length - 1];
            if (top?.type === 'object' && expectKey) {
                lastKey = { name: s, line: startLine, start: startCol, end: col };
                expectKey = false;
            } else if (top?.type === 'object' && lastKey) {
                // a string value: a dependency when its object is one of the sections at the top level
                if (stack.length === 2 && SECTIONS.includes(top.key))
                    out.push({ name: lastKey.name, spec: s, section: top.key, line: lastKey.line, start: lastKey.start, end: lastKey.end });
                lastKey = null;
            }
            continue;
        }
        if (ch === '{' || ch === '[') {
            stack.push({ type: ch === '{' ? 'object' : 'array', key: lastKey?.name ?? null });
            lastKey = null;
            expectKey = ch === '{';
        } else if (ch === '}' || ch === ']') {
            stack.pop();
            lastKey = null;
            expectKey = false;
        } else if (ch === ',') {
            expectKey = stack[stack.length - 1]?.type === 'object';
            lastKey = null;
        } else if (ch === '/' && text[i + 1] === '/') {
            while (i < text.length && text[i] !== '\n') advance(text[i]);
            continue;
        } else if (ch !== ':' && !/\s/.test(ch) && stack[stack.length - 1]?.type === 'object' && !expectKey) {
            // a number, true, false or null as a value
            lastKey = null;
        }
        advance(ch);
    }
    return out;
}

/**
 * The markers: each dependency node_modules does not have. `installed` is the host's answer: null when there is no node_modules (nothing is
 * installed yet), else { name: version }.
 */
export function problems(entries, installed) {
    return entries.filter((e) => !(installed && Object.prototype.hasOwnProperty.call(installed, e.name))).map((e) => ({
        line: e.line, start: e.start, end: e.end, severity: 'warning', code: 'npm-not-installed',
        message: installed
            ? `Package '${e.name}' is not installed: npm install puts it in node_modules`
            : `Package '${e.name}' is not installed: there is no node_modules yet (npm install)`,
    }));
}

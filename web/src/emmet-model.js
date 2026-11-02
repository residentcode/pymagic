// Where Emmet's JSX expansion is offered: Monaco has one language for .js and .jsx (and for .ts and .tsx), so it is told from the file: always in
// .jsx and .tsx, and in .js, .mjs, .cjs and .ts when the file is React's (it imports React, or has JSX in it). No DOM here: web/test/emmet.test.mjs.

/** Whether a JavaScript or TypeScript file is written in JSX (only the first 50,000 characters are looked at). */
export function isJsxFile(path, text = '') {
    const lower = path.toLowerCase();
    if (/\.(jsx|tsx)$/.test(lower)) return true;
    if (!/\.(js|mjs|cjs|ts|mts|cts)$/.test(lower)) return false;
    const head = text.slice(0, 50000);
    return /\bfrom\s+['"](react|react-native|preact|solid-js)(\/[\w-]+)?['"]|\brequire\(\s*['"]react['"]\s*\)/.test(head)
        || /<\/[A-Za-z][\w.-]*\s*>|<[A-Za-z][\w.]*(\s+[\w-]+(=("[^"]*"|\{[^}]*\}))?)*\s*\/>|return\s*\(\s*</.test(head);
}

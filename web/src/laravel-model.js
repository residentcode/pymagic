// Laravel without the page: which Artisan commands match what is typed in Run Artisan Command. web/test/laravel.test.mjs checks it.

/**
 * The commands matching what is typed: a name that starts with it first, then one that has it, then a description that has it, each group by
 * name. With nothing typed, the commands run last come first, in that order. `commands`: [{ name, description }].
 */
export function artisanMatches(commands, typed, recent = []) {
    const q = typed.trim().toLowerCase();
    const rank = (c) => (recent.includes(c.name) && !q ? -1 : !q ? 0 : c.name.startsWith(q) ? 0 : c.name.includes(q) ? 1 : (c.description ?? '').toLowerCase().includes(q) ? 2 : 9);
    return commands.map((c) => [rank(c), c]).filter(([r]) => r < 9)
        .sort((a, b) => a[0] - b[0] || (a[0] === -1 ? recent.indexOf(a[1].name) - recent.indexOf(b[1].name) : a[1].name.localeCompare(b[1].name))).map(([, c]) => c);
}

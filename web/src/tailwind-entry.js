// Tailwind CSS's own design system (the npm package tailwindcss, MIT), bundled on its own (dist/tailwind.js) and loaded by the page only in a
// project that uses Tailwind: the list of its classes and variants, and the CSS a class makes, from its default theme and the project's own
// CSS (its @theme). The theme and the utilities come with it: nothing is fetched.
import { __unstable__loadDesignSystem } from 'tailwindcss';
import theme from 'tailwindcss/theme.css';
import utilities from 'tailwindcss/utilities.css';

/** The design system of a project: its CSS (with @import "tailwindcss") or, without one, Tailwind's defaults. */
async function load(projectCss) {
    // only what decides the classes: the project's @theme, @utility and @custom-variant; its other @imports are not followed, and its
    // @plugin and @config (JavaScript, which is not run here: flowbite/plugin and the like) are left out rather than failing the whole list
    const own = String(projectCss ?? '').replace(/\/\*[\s\S]*?\*\//g, '')
        .replace(/@import\s+(["'])(?!tailwindcss)[^"']*\1[^;]*;/g, '').replace(/@import\s+(["'])tailwindcss\1[^;]*;/g, '')
        .replace(/@(plugin|config)\s+(["'])[^"']*\2[^;{]*(;|\{[^}]*\})/g, '');
    const css = `@import "tailwindcss/theme.css" layer(theme);\n@import "tailwindcss/utilities.css" layer(utilities);\n${own}`;
    const ds = await __unstable__loadDesignSystem(css, {
        loadStylesheet: async (id, base) => ({ base, path: id, content: id.includes('theme') ? theme : id.includes('utilities') ? utilities : '' }),
        loadModule: async () => { throw new Error('plugins and JavaScript configs are not loaded'); },
    });
    return {
        classes: ds.getClassList().map(([name]) => name),
        variants: ds.getVariants().filter((v) => !v.isArbitrary && !/[*]/.test(v.name)).flatMap((v) => (v.values.length ? v.values.map((x) => `${v.name}${v.hasDash ? '-' : ''}${x}`) : [v.name])),
        css: (name) => ds.candidatesToCss([name])[0] ?? null,
        theme: theme + '\n' + own,
    };
}

self.pymagicTailwind = { load };

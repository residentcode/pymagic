// Full line completion without the editor: in which files it runs and what the engine is told of their language. No DOM here:
// web/test/fullline.test.mjs checks it.

/** The languages whose lines are completed: code, templates and configuration (not prose: plain text, Markdown, reStructuredText). */
export const FULL_LINE_LANGUAGES = new Set([
    'python', 'javascript', 'typescript', 'html', 'django-html', 'ejs', 'handlebars', 'twig', 'razor', 'php', 'blade', 'xml', 'css', 'scss', 'less',
    'json', 'yaml', 'toml', 'ini', 'sql', 'shell', 'powershell', 'bat', 'dockerfile', 'java', 'kotlin', 'c', 'cpp', 'csharp', 'go', 'rust', 'ruby',
]);

/** Whether a file's lines are completed. */
export function completesLanguage(language) {
    return FULL_LINE_LANGUAGES.has(language);
}

/** The engine reads # comments in Python and the shells, // in the C-like languages; a JSX file is JavaScript to it. */
export function engineLanguage(language) {
    return language === 'powershell' ? 'shell' : language;
}

/** How long typing must pause before a line is asked for, in milliseconds. */
export const FULL_LINE_DELAY = 120;

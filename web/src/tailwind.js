// Tailwind CSS (a plugin of Settings > Plugins, as PyCharm's Tailwind CSS plugin is): in a project that uses Tailwind (the engine looks, /api/tailwind),
// the classes are completed where they are typed (class="", className="", Vue's :class, clsx() and cn()), with their variants (md:, hover:, dark:),
// a swatch for each colour class and the CSS each class makes; hovering a class shows its CSS. Tailwind's own design system lists them
// (dist/tailwind.js, loaded only then), from its default theme and the project's stylesheet that imports it (its @theme).
import * as monaco from 'monaco-editor';
import { classAt, suggestions, themeColors, colorOf, TAILWIND_LANGUAGES } from './tailwind-model.js';
import { isJsxFile } from './emmet-model.js';

/** Loads dist/tailwind.js once. */
function loadScript() {
    if (self.pymagicTailwind) return Promise.resolve(self.pymagicTailwind);
    return new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src = 'tailwind.js';
        s.onload = () => resolve(self.pymagicTailwind);
        s.onerror = () => reject(new Error('tailwind.js was not loaded'));
        document.head.append(s);
    });
}

export async function setupTailwind() {
    const use = await fetch('/api/tailwind').then((r) => r.json()).catch(() => null);
    if (!use?.used) return null;
    const tw = await (await loadScript()).load(use.css);
    const colors = themeColors(tw.theme);
    const markup = (model) => {
        const language = model.getLanguageId();
        return (language !== 'javascript' && language !== 'typescript') || isJsxFile(model.uri.path, model.getValue());
    };
    const before = (model, position) => model.getValueInRange({ startLineNumber: Math.max(1, position.lineNumber - 20), startColumn: 1, endLineNumber: position.lineNumber, endColumn: position.column });
    const completion = {
        triggerCharacters: ['"', "'", '`', ' ', ':', '-'],
        provideCompletionItems(model, position) {
            if (!markup(model)) return { suggestions: [] };
            const word = classAt(before(model, position));
            if (word === null) return { suggestions: [] };
            const range = new monaco.Range(position.lineNumber, position.column - word.length, position.lineNumber, position.column);
            const items = suggestions(word, tw.classes, tw.variants).map((s, i) => {
                const color = s.kind === 'class' ? colorOf(s.label, colors) : null;
                return {
                    label: s.label,
                    kind: s.kind === 'variant' ? monaco.languages.CompletionItemKind.Keyword : color ? monaco.languages.CompletionItemKind.Color : monaco.languages.CompletionItemKind.Constant,
                    insertText: s.insert,
                    filterText: s.insert,
                    // Tailwind's own order (the class list is sorted as its utilities are)
                    sortText: String(i).padStart(6, '0'),
                    range,
                    documentation: color ?? undefined,
                    command: s.kind === 'variant' ? { id: 'editor.action.triggerSuggest', title: '' } : undefined,
                };
            });
            return { suggestions: items, incomplete: false };
        },
        resolveCompletionItem(item) {
            if (item.kind === monaco.languages.CompletionItemKind.Keyword) return item;
            const css = tw.css(item.label);
            if (css) item.detail = css.split('\n').filter((l) => l.includes(':') && !l.trim().startsWith('@')).map((l) => l.trim()).join(' ');
            if (css && item.kind !== monaco.languages.CompletionItemKind.Color) item.documentation = { value: '```css\n' + css + '\n```' };
            return item;
        },
    };
    const hover = {
        provideHover(model, position) {
            if (!markup(model)) return null;
            const line = model.getLineContent(position.lineNumber);
            // the class under the mouse: the run of class characters around it, inside a class attribute
            let start = position.column - 1, end = start;
            while (start > 0 && /[^\s"'`{}]/.test(line[start - 1])) start--;
            while (end < line.length && /[^\s"'`{}]/.test(line[end])) end++;
            const name = line.slice(start, end);
            if (!name || classAt(before(model, { lineNumber: position.lineNumber, column: start + 1 })) === null) return null;
            const css = tw.css(name);
            if (!css) return null;
            return { range: new monaco.Range(position.lineNumber, start + 1, position.lineNumber, end + 1), contents: [{ value: '```css\n' + css + '\n```' }] };
        },
    };
    const off = TAILWIND_LANGUAGES.flatMap((language) => [
        monaco.languages.registerCompletionItemProvider(language, completion),
        monaco.languages.registerHoverProvider(language, hover),
    ]);
    return { classes: tw.classes.length, dispose: () => off.forEach((d) => d.dispose()) };
}

// The Mermaid library (the npm package mermaid, MIT), bundled on its own (dist/mermaid.js) and loaded by the Markdown preview only for a diagram
// that mermaid-lite.js does not draw (class, state, entity relationship, gantt, pie, journey, mindmap, timeline, git graph and the rest).
// Nothing is fetched: the diagram kinds come with it.
import mermaid from 'mermaid';

let count = 0;
let theme = null;

/** The diagram as SVG text, drawn in the theme of the window (dark or light); throws with Mermaid's own message for a text it cannot read. */
async function render(text, dark) {
    const wanted = dark ? 'dark' : 'default';
    if (theme !== wanted) {
        // scripts and links in a diagram are not run: the preview is of the project's own files, but they may come from anywhere
        mermaid.initialize({ startOnLoad: false, theme: wanted, securityLevel: 'strict' });
        theme = wanted;
    }
    const { svg } = await mermaid.render(`pm-mermaid-${++count}`, text);
    return svg;
}

self.pymagicMermaid = { render };

// Checks of web/src/notebook-model.js: `node web/test/notebook.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// web/package.json does not say "type": "module"; Node notices the import lines of src/*.js and reads them as modules (with a warning). The model
// imports notebook-render.js, so it is loaded from its file and not from a copy of its text.
const m = await import(new URL('../src/notebook-model.js', import.meta.url).href);

// a notebook as Jupyter writes it: sorted keys, one space of indent, a final newline
const SAMPLE = `{
 "cells": [
  {
   "cell_type": "markdown",
   "id": "a1b2c3d4",
   "metadata": {},
   "source": [
    "# Title\\n",
    "text é \\"quoted\\""
   ]
  },
  {
   "cell_type": "code",
   "execution_count": 2,
   "id": "e5f6a7b8",
   "metadata": {
    "tags": [
     "keep"
    ]
   },
   "outputs": [
    {
     "name": "stdout",
     "output_type": "stream",
     "text": [
      "hi\\n"
     ]
    },
    {
     "data": {
      "image/png": "iVBORw0KGgo=",
      "text/plain": [
       "<Figure>"
      ]
     },
     "metadata": {},
     "output_type": "display_data"
    },
    {
     "data": {
      "text/plain": [
       "42"
      ]
     },
     "execution_count": 2,
     "metadata": {},
     "output_type": "execute_result"
    }
   ],
   "source": [
    "print('hi')\\n",
    "42"
   ]
  },
  {
   "cell_type": "code",
   "execution_count": null,
   "id": "c9d0e1f2",
   "metadata": {},
   "outputs": [],
   "source": []
  }
 ],
 "metadata": {
  "kernelspec": {
   "display_name": "Python 3",
   "language": "python",
   "name": "python3"
  },
  "language_info": {
   "name": "python",
   "version": "3.13.1"
  }
 },
 "nbformat": 4,
 "nbformat_minor": 5
}
`;

test('a notebook written by Jupyter is written back byte for byte', () => {
    const { nb, error } = m.parseNotebook(SAMPLE);
    assert.equal(error, undefined);
    assert.equal(m.serializeNotebook(nb), SAMPLE);
});

test('things that are not notebooks give an error', () => {
    assert.match(m.parseNotebook('{ nope').error, /not a notebook/);
    assert.match(m.parseNotebook('{"a": 1}').error, /no list of cells/);
    assert.match(m.parseNotebook('{"cells": [], "nbformat": 3}').error, /too old/);
    assert.equal(m.parseNotebook('{"cells": [], "nbformat": 4}').error, undefined);
});

test('source: lists of lines and plain strings read the same way', () => {
    assert.equal(m.sourceOf({ source: ['a\n', 'b'] }), 'a\nb');
    assert.equal(m.sourceOf({ source: 'a\nb' }), 'a\nb');
    assert.equal(m.sourceOf({}), '');
});

test('splitLines keeps the newline on every line but the last', () => {
    assert.deepEqual(m.splitLines('a\nb\nc'), ['a\n', 'b\n', 'c']);
    assert.deepEqual(m.splitLines('a\n'), ['a\n']);
    assert.deepEqual(m.splitLines('a\n\nb'), ['a\n', '\n', 'b']);
    assert.deepEqual(m.splitLines(''), []);
});

test('setSource is read back as written', () => {
    const cell = { source: [] };
    for (const text of ['x = 1', 'x = 1\n', 'a\n\nb\n\n', '\n']) { m.setSource(cell, text); assert.equal(m.sourceOf(cell), text); }
});

test('new cells get an id only in notebooks that have ids, and code cells get outputs', () => {
    const { nb } = m.parseNotebook(SAMPLE);
    const code = m.insertCell(nb, 1, 'code', 'x = 1');
    assert.equal(nb.cells[1], code);
    assert.match(code.id, /^[0-9a-f]{8}$/);
    assert.deepEqual(code.outputs, []);
    assert.equal(code.execution_count, null);
    const md = m.insertCell(nb, 99, 'markdown', '# t');
    assert.equal(nb.cells[nb.cells.length - 1], md);
    assert.equal(md.outputs, undefined);
    const { nb: old } = m.parseNotebook('{"cells": [], "nbformat": 4, "nbformat_minor": 2}');
    assert.equal(m.insertCell(old, 0).id, undefined);
});

test('delete and move', () => {
    const { nb } = m.parseNotebook(SAMPLE);
    const ids = () => nb.cells.map((c) => c.id);
    assert.equal(m.moveCell(nb, 0, 1), 1);
    assert.deepEqual(ids(), ['e5f6a7b8', 'a1b2c3d4', 'c9d0e1f2']);
    assert.equal(m.moveCell(nb, 0, -1), 0);          // cannot go above the first
    assert.equal(m.moveCell(nb, 2, 1), 2);           // nor below the last
    assert.equal(m.deleteCell(nb, 1).id, 'a1b2c3d4');
    assert.equal(nb.cells.length, 2);
    assert.equal(m.deleteCell(nb, 7), null);
});

test('changing a cell\'s type adds or drops what only code cells have', () => {
    const { nb } = m.parseNotebook(SAMPLE);
    m.changeType(nb, nb.cells[1], 'markdown');
    assert.equal(nb.cells[1].outputs, undefined);
    assert.equal(nb.cells[1].execution_count, undefined);
    m.changeType(nb, nb.cells[1], 'code');
    assert.deepEqual(nb.cells[1].outputs, []);
    assert.equal(nb.cells[1].execution_count, null);
});

test('clearing outputs and counting runs', () => {
    const { nb } = m.parseNotebook(SAMPLE);
    assert.equal(m.lastExecutionCount(nb), 2);
    m.clearOutputs(nb);
    assert.equal(m.lastExecutionCount(nb), 0);
    assert.deepEqual(nb.cells[1].outputs, []);
});

test('kernel messages become nbformat outputs, and streams of one name join', () => {
    const cell = { cell_type: 'code', outputs: [], source: [] };
    m.applyKernelMessage(cell, { type: 'stream', name: 'stdout', text: 'a\nb' });
    m.applyKernelMessage(cell, { type: 'stream', name: 'stdout', text: 'c\n' });
    m.applyKernelMessage(cell, { type: 'stream', name: 'stderr', text: 'warn\n' });
    m.applyKernelMessage(cell, { type: 'result', data: { 'text/plain': '3' }, execution_count: 4 });
    m.applyKernelMessage(cell, { type: 'display', data: { 'text/plain': 'x' } });
    m.applyKernelMessage(cell, { type: 'error', ename: 'ValueError', evalue: 'bad', traceback: ['tb'] });
    assert.deepEqual(cell.outputs.map((o) => o.output_type), ['stream', 'stream', 'execute_result', 'display_data', 'error']);
    assert.equal(m.sourceOf({ source: cell.outputs[0].text }), 'a\nbc\n');
    assert.equal(cell.outputs[2].execution_count, 4);
    assert.equal(cell.outputs[4].ename, 'ValueError');
});

test('describeOutput picks the richest form it can show', () => {
    assert.deepEqual(m.describeOutput({ output_type: 'stream', name: 'stderr', text: ['a\n', 'b'] }), { kind: 'text', stream: 'stderr', text: 'a\nb' });
    const both = m.describeOutput({ output_type: 'display_data', data: { 'text/plain': 'x', 'text/html': '<b>x</b>', 'image/png': 'AB\nCD' } });
    assert.deepEqual(both, { kind: 'image', mime: 'image/png', data: 'ABCD' });
    assert.deepEqual(m.describeOutput({ output_type: 'execute_result', data: { 'text/plain': 'x', 'text/html': ['<b>', 'x</b>'] } }), { kind: 'html', html: '<b>x</b>' });
    assert.equal(m.describeOutput({ output_type: 'execute_result', data: { 'application/json': { a: 1 } } }).kind, 'json');
    assert.deepEqual(m.describeOutput({ output_type: 'execute_result', data: { 'text/plain': ['4', '2'] } }), { kind: 'text', stream: 'result', text: '42' });
    const err = m.describeOutput({ output_type: 'error', ename: 'E', evalue: 'v', traceback: ['l1', 'l2'] });
    assert.equal(err.kind, 'error');
    assert.equal(err.text, 'l1\nl2');
    assert.equal(m.describeOutput({ output_type: 'error', ename: 'E', evalue: 'v' }).text, 'E: v');
});

test('ansiSegments turns colour codes into classes and drops the codes', () => {
    const segs = m.ansiSegments('plain \u001b[0;31mred\u001b[0m \u001b[1;32mgreen bold\u001b[0m end');
    assert.deepEqual(segs.map((s) => s.text), ['plain ', 'red', ' ', 'green bold', ' end']);
    assert.deepEqual(segs.map((s) => s.cls), ['', 'ansi-red', '', 'ansi-bold ansi-green', '']);
    assert.deepEqual(m.ansiSegments('no codes'), [{ text: 'no codes', cls: '' }]);
    assert.equal(m.ansiSegments('\u001b[91mbright\u001b[39m after').map((s) => s.cls).join('|'), 'ansi-red|');
});

test('collapseCarriageReturns keeps the last rewrite of a line', () => {
    assert.equal(m.collapseCarriageReturns('10%\r50%\r100%\ndone\n'), '100%\ndone\n');
    assert.equal(m.collapseCarriageReturns('a\r\nb\r\n'), 'a\nb\n');
    assert.equal(m.collapseCarriageReturns('x\r'), 'x');
    assert.equal(m.collapseCarriageReturns('plain'), 'plain');
});

const nbOf = (...cells) => ({ cells: cells.map(([cell_type, text]) => ({ cell_type, source: text, metadata: {} })) });

test('virtualDocument joins the code cells and leaves out the text cells', () => {
    const doc = m.virtualDocument(nbOf(['code', 'a = 1\nb = 2'], ['markdown', '# not code'], ['code', ''], ['code', 'print(a)']));
    assert.equal(doc.text, 'a = 1\nb = 2\n\nprint(a)');
    assert.deepEqual(doc.cells, [{ index: 0, start: 0, lines: 2, count: null }, { index: 2, start: 2, lines: 1, count: null }, { index: 3, start: 3, lines: 1, count: null }]);
});

test('virtualDocument sends each code cell\'s run count (null when it never ran), for the run order of the names check', () => {
    const nb = nbOf(['code', 'print(x)'], ['markdown', 'text'], ['code', 'x = 1'], ['code', 'y = 2']);
    nb.cells[0].execution_count = 2;
    nb.cells[2].execution_count = 1;
    assert.deepEqual(m.virtualDocument(nb).cells.map((c) => c.count), [2, 1, null]);
});

test('magics and shell lines become pass, keeping their indent', () => {
    assert.equal(m.maskMagics('%matplotlib inline\nx = 1\n!pip install y\n  %time f()'), 'pass\nx = 1\npass\n  pass');
    assert.equal(m.virtualDocument(nbOf(['code', '!ls\nprint(1)'])).text, 'pass\nprint(1)');
});

test('lines map between a cell and the virtual file, both ways', () => {
    const doc = m.virtualDocument(nbOf(['code', 'a = 1\nb = 2'], ['markdown', 'text'], ['code', 'c = 3\nd = 4']));
    assert.deepEqual(m.cellOfLine(doc, 0), { index: 0, line: 0 });
    assert.deepEqual(m.cellOfLine(doc, 1), { index: 0, line: 1 });
    assert.deepEqual(m.cellOfLine(doc, 3), { index: 2, line: 1 });
    assert.equal(m.cellOfLine(doc, 4), null);
    assert.equal(m.virtualLine(doc, 2, 1), 3);
    assert.equal(m.virtualLine(doc, 1, 0), null);
});

test('line endings in a cell do not change the line count', () => {
    const doc = m.virtualDocument(nbOf(['code', 'a\r\nb\r\nc'], ['code', 'd']));
    assert.equal(doc.cells[1].start, 3);
    assert.equal(doc.text, 'a\nb\nc\nd');
});

// ------------------------------------------------------------ merge, split, tags, sections, headings

test('merging joins a cell with the one below, keeps the first one\'s tags and drops the outputs of code', () => {
    const nb = nbOf(['code', 'a = 1'], ['code', 'b = 2'], ['markdown', 'end']);
    nb.cells[0].metadata.tags = ['keep'];
    nb.cells[0].outputs = [{ output_type: 'stream', name: 'stdout', text: ['x'] }];
    nb.cells[0].execution_count = 3;
    const merged = m.mergeCells(nb, 0);
    assert.equal(nb.cells.length, 2);
    assert.equal(m.sourceOf(merged), 'a = 1\n\nb = 2');
    assert.deepEqual(merged.outputs, []);
    assert.equal(merged.execution_count, null);
    assert.deepEqual(merged.metadata.tags, ['keep']);
    assert.equal(m.mergeCells(nb, 1), null, 'nothing below the last cell');
});

test('merging with an empty cell does not add a blank line', () => {
    const nb = nbOf(['markdown', 'text'], ['markdown', '']);
    assert.equal(m.sourceOf(m.mergeCells(nb, 0)), 'text');
});

test('splitting cuts the text at an offset into two cells of the same kind', () => {
    const nb = nbOf(['code', 'a = 1\nb = 2\nc = 3']);
    nb.cells[0].metadata.tags = ['t'];
    nb.cells[0].outputs = [{ output_type: 'stream', name: 'stdout', text: ['x'] }];
    const made = m.splitCell(nb, 0, 'a = 1\n'.length);
    assert.equal(nb.cells.length, 2);
    assert.equal(m.sourceOf(nb.cells[0]), 'a = 1');
    assert.equal(m.sourceOf(made), 'b = 2\nc = 3');
    assert.equal(made, nb.cells[1]);
    assert.equal(made.cell_type, 'code');
    assert.deepEqual(nb.cells[0].outputs, []);
    assert.deepEqual(made.metadata, {});
    assert.deepEqual(nb.cells[0].metadata.tags, ['t']);
});

test('splitting in the middle of a line, at the end and at the start', () => {
    let nb = nbOf(['markdown', 'hello world']);
    m.splitCell(nb, 0, 5);
    assert.deepEqual(nb.cells.map(m.sourceOf), ['hello', ' world']);
    nb = nbOf(['code', 'x']);
    m.splitCell(nb, 0, 1);
    assert.deepEqual(nb.cells.map(m.sourceOf), ['x', '']);
    nb = nbOf(['code', 'x']);
    m.splitCell(nb, 0, 0);
    assert.deepEqual(nb.cells.map(m.sourceOf), ['', 'x']);
    assert.equal(m.splitCell(nb, 9, 0), null);
});

test('the offset of an editor position in the text', () => {
    assert.equal(m.offsetOfPosition('ab\ncd\nef', 1, 1), 0);
    assert.equal(m.offsetOfPosition('ab\ncd\nef', 2, 2), 4);
    assert.equal(m.offsetOfPosition('ab\ncd\nef', 3, 3), 8);
});

test('tags are read from the metadata, parsed from text and removed when empty', () => {
    const cell = { cell_type: 'code', metadata: { tags: ['keep', 3], other: 1 } };
    assert.deepEqual(m.tagsOf(cell), ['keep']);
    assert.deepEqual(m.parseTags(' a, b  a,,c '), ['a', 'b', 'c']);
    m.setTags(cell, ['x', 'y']);
    assert.deepEqual(cell.metadata, { tags: ['x', 'y'], other: 1 });
    m.setTags(cell, []);
    assert.deepEqual(cell.metadata, { other: 1 });
    assert.deepEqual(m.tagsOf({ cell_type: 'code' }), []);
});

test('headings of a text ignore code fences and take the closing hashes off', () => {
    const hs = m.headingsOfText('# One ##\ntext\n```\n# not\n```\n## Two\n#nospace\n');
    assert.deepEqual(hs, [{ level: 1, name: 'One', line: 0 }, { level: 2, name: 'Two', line: 5 }]);
});

test('a section runs to the next heading of the same or a higher level', () => {
    const nb = nbOf(['markdown', '# A'], ['code', '1'], ['markdown', '## A1'], ['code', '2'], ['markdown', '# B'], ['code', '3']);
    assert.deepEqual(m.sectionOf(nb, 0), { start: 0, end: 4 });
    assert.deepEqual(m.sectionOf(nb, 2), { start: 2, end: 4 });
    assert.deepEqual(m.sectionOf(nb, 4), { start: 4, end: 6 });
    assert.equal(m.sectionOf(nb, 1), null, 'a code cell opens no section');
    assert.equal(m.sectionOf(nbOf(['markdown', 'plain text']), 0), null);
    assert.equal(m.cellHeadingLevel({ cell_type: 'markdown', source: ['text\n', '# late'] }), 0);
});

test('the headings of a notebook are nested symbols that point at cells', () => {
    const nb = nbOf(['markdown', '# A'], ['code', '1'], ['markdown', '## A1\ntext\n## A2'], ['markdown', '# B'], ['code', '2']);
    const s = m.notebookSymbols(nb);
    assert.deepEqual(s.map((x) => x.name), ['A', 'B']);
    assert.deepEqual(s[0].children.map((x) => x.name), ['A1', 'A2']);
    assert.equal(s[0].children[1].cell, 2);
    assert.equal(s[0].children[1].selectionRange.start.character, 2, 'the heading\'s line inside the cell');
    assert.equal(s[0].range.end.line, 2);
    assert.equal(s[1].range.end.line, 4);
    assert.equal(s[0].kind, 'md-heading');
});

// ------------------------------------------------------------ objects of the Variables window, debugging a cell, the console, servers, HTML

const mdSource = readFileSync(new URL('../src/markdown-lite.js', import.meta.url), 'utf8');
const { blocksOf } = await import('data:text/javascript;base64,' + Buffer.from(mdSource).toString('base64'));

test('variable rows: open ones list the members the kernel sent, nested', () => {
    const vars = [{ name: 'd', type: 'dict', size: '2', value: '{...}', expandable: true }, { name: 'n', type: 'int', size: '', value: '3', expandable: false }];
    const open = new Set();
    const members = new Map();
    assert.deepEqual(m.variableRows(vars, open, members).map((r) => [r.name, r.depth, r.isOpen]), [['d', 0, false], ['n', 0, false]]);
    // opening a row that is not loaded yet shows it as loading
    const dKey = m.pathKey([['v', 'd']]);
    open.add(dKey);
    members.set(dKey, { loading: true });
    assert.equal(m.variableRows(vars, open, members)[0].loading, true);
    members.set(dKey, { list: [{ name: "'a'", key: ['i', 0], type: 'list', size: '2', value: '[1, 2]', expandable: true }, { name: "'b'", key: ['i', 1], type: 'int', value: '7', expandable: false }] });
    open.add(m.pathKey([['v', 'd'], ['i', 0]]));
    members.set(m.pathKey([['v', 'd'], ['i', 0]]), { list: [{ name: '0', key: ['i', 0], type: 'int', value: '1', expandable: false }] });
    const rows = m.variableRows(vars, open, members);
    assert.deepEqual(rows.map((r) => [r.name, r.depth]), [['d', 0], ["'a'", 1], ['0', 2], ["'b'", 1], ['n', 0]]);
    assert.deepEqual(rows[2].path, [['v', 'd'], ['i', 0], ['i', 0]]);
    // a row that cannot be opened is never open, even when its key is in the set
    open.add(m.pathKey([['v', 'n']]));
    assert.equal(m.variableRows(vars, open, members).at(-1).isOpen, false);
    members.set(dKey, { error: 'KeyError: gone' });
    assert.equal(m.variableRows(vars, open, members)[0].error, 'KeyError: gone');
    assert.deepEqual(m.pathsToRefresh(new Set([m.pathKey([['v', 'd'], ['i', 0]]), dKey])).map((p) => p.length), [1, 2]);
});

test('a cell debugged is a file of the project with the notebook and the cell in its name', () => {
    const file = m.cellDebugFile('docs/analysis.ipynb', 2);
    assert.equal(file, '.pymagic/notebooks/docs__analysis_ipynb.cell3.py');
    assert.deepEqual(m.parseCellDebugFile(file), { slug: 'docs__analysis_ipynb', index: 2 });
    assert.deepEqual(m.parseCellDebugFile('.pymagic\\notebooks\\a_ipynb.cell1.py'), { slug: 'a_ipynb', index: 0 });
    assert.equal(m.parseCellDebugFile('src/app.py'), null);
    assert.equal(m.notebookSlug('docs/analysis.ipynb'), 'docs__analysis_ipynb');
});

test('console history keeps distinct lines and walks up and down', () => {
    const h = [];
    m.pushHistory(h, 'x = 1');
    m.pushHistory(h, 'x = 1');
    m.pushHistory(h, '   ');
    m.pushHistory(h, 'x + 1');
    assert.deepEqual(h, ['x = 1', 'x + 1']);
    let at = m.stepHistory(h, h.length, -1, 'draft');
    assert.deepEqual(at, { index: 1, text: 'x + 1' });
    at = m.stepHistory(h, at.index, -1, 'draft');
    assert.equal(at.text, 'x = 1');
    at = m.stepHistory(h, at.index, -1, 'draft');
    assert.equal(at.index, 0);
    at = m.stepHistory(h, 1, 1, 'draft');
    assert.deepEqual(at, { index: 2, text: 'draft' });
    const long = [];
    for (let i = 0; i < 5; i++) m.pushHistory(long, 'l' + i, 3);
    assert.deepEqual(long, ['l2', 'l3', 'l4']);
});

test('the console runs a line at Enter unless the statement goes on', () => {
    assert.equal(m.needsMoreInput('x = 1'), false);
    assert.equal(m.needsMoreInput('x = [1,'), true);
    assert.equal(m.needsMoreInput('s = """abc'), true);
    assert.equal(m.needsMoreInput('s = "a(" + "b"'), false);
    assert.equal(m.needsMoreInput('if x:'), true);
    assert.equal(m.needsMoreInput('if x:\n    y = 1'), true);
    assert.equal(m.needsMoreInput('if x:\n    y = 1\n'), false);
    assert.equal(m.needsMoreInput('a = 1 + \\'), true);
    assert.equal(m.needsMoreInput('# comment ('), false);
});

test('a server is typed as Jupyter prints it, and kept without the token in the address', () => {
    const s = m.normalizeServer({ url: 'localhost:8888/lab?token=abc123' });
    assert.equal(s.url, 'http://localhost:8888');
    assert.equal(s.token, 'abc123');
    assert.equal(s.name, 'localhost:8888');
    const t = m.normalizeServer({ url: 'https://hub.example.org/user/me/tree', token: 'T', name: 'Hub', kernel: 'python3' });
    assert.deepEqual([t.url, t.token, t.name, t.kernel], ['https://hub.example.org/user/me', 'T', 'Hub', 'python3']);
    assert.ok(m.normalizeServer({ url: '' }).error);
    assert.ok(m.normalizeServer({ url: 'ftp://x' }).error);
    const list = m.addServer(m.addServer([], s), { ...s, name: 'again' });
    assert.equal(list.length, 1);
    assert.equal(list[0].name, 'again');
    assert.equal(m.parseServers('not json').length, 0);
    assert.equal(m.parseServers(JSON.stringify([s, { url: '' }, 5])).length, 1);
    assert.equal(m.BUILT_IN_SERVER.id, 'builtin');
});

test('a notebook becomes one HTML page with nothing live in it', () => {
    const nb = m.parseNotebook(JSON.stringify({
        nbformat: 4, nbformat_minor: 5, metadata: {},
        cells: [
            { cell_type: 'markdown', metadata: {}, source: ['# Title\n', 'some *words* and `code` <b>not bold</b>\n', '\n', '- one\n', '- two'] },
            { cell_type: 'code', metadata: {}, execution_count: 3, source: ['print("<hi>")'], outputs: [
                { output_type: 'stream', name: 'stdout', text: ['<hi>\n'] },
                { output_type: 'execute_result', execution_count: 3, metadata: {}, data: { 'text/html': '<script>alert(1)</script><b>x</b>', 'text/plain': 'x' } },
                { output_type: 'display_data', metadata: {}, data: { 'image/png': 'iVBORw0KGgo=', 'text/plain': '<Figure>' } },
                { output_type: 'error', ename: 'ValueError', evalue: 'bad', traceback: ['\u001b[31mValueError\u001b[0m: bad'] },
            ] },
        ],
    })).nb;
    const html = m.notebookToHtml(nb, { title: 'My <nb>', blocksOf });
    assert.ok(html.startsWith('<!doctype html>'));
    assert.ok(html.includes('<title>My &lt;nb&gt;</title>'));
    assert.ok(html.includes('<h1>Title</h1>'));
    assert.ok(html.includes('<em>words</em>') && html.includes('<code>code</code>'));
    assert.ok(html.includes('&lt;b&gt;not bold&lt;/b&gt;'), 'markup in a text cell is text');
    assert.ok(html.includes('<li>one</li>') && html.includes('<li>two</li>'));
    assert.ok(html.includes('In [3]:') && html.includes('<span class="tok-b">print</span>(<span class="tok-s">&quot;&lt;hi&gt;&quot;</span>)'), 'code is coloured and its markup is text');
    assert.ok(html.includes('<img class="out" src="data:image/png;base64,iVBORw0KGgo="'));
    assert.ok(html.includes('<span class="ansi-red">ValueError</span>'));
    // the script of an HTML output is only ever inside the attribute of a frame with every right taken away
    assert.ok(!html.includes('<script'));
    assert.ok(html.includes('sandbox="" srcdoc="&lt;script&gt;alert(1)&lt;/script&gt;'));
});

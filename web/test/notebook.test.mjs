// Checks of web/src/notebook-model.js: `node web/test/notebook.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// web/package.json does not say "type": "module", so Node would read src/*.js as CommonJS; load the file as a module instead
const source = readFileSync(new URL('../src/notebook-model.js', import.meta.url), 'utf8');
const m = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

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
    assert.deepEqual(doc.cells, [{ index: 0, start: 0, lines: 2 }, { index: 2, start: 2, lines: 1 }, { index: 3, start: 3, lines: 1 }]);
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

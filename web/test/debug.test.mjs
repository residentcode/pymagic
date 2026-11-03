// Checks of web/src/debug-model.js: `node web/test/debug.test.mjs` (tests/PyMagic.Tests/WebScriptTests.cs runs it).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/debug-model.js', import.meta.url), 'utf8');
const { codeOf, namesOf, inlineLines, inlineText, callsOn, expressionAt, stackText, functionBreakpoint, childExpression, readWatches, moveItem,
    declarationLine, historyStep, framesOfStop, threadHeadings, serverProgramNote } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

test('codeOf: comments and the text of strings are left out', () => {
    assert.equal(codeOf('x = "a # b"  # note'), 'x = "     "  ');
    assert.equal(codeOf("y = 'it\\'s' + z"), "y = '     ' + z");
});

test('namesOf: variables in order, without keywords, attributes, keyword arguments and string text', () => {
    assert.deepEqual(namesOf('total = a + b'), ['total', 'a', 'b']);
    assert.deepEqual(namesOf('if self.count > limit and not done:'), ['self', 'limit', 'done']);
    assert.deepEqual(namesOf('r = f(x, key=value)  # key is not a name'), ['r', 'f', 'x', 'value']);
    assert.deepEqual(namesOf('msg = "total is " + str(total)'), ['msg', 'total']);
    assert.deepEqual(namesOf('for i in range(3):'), ['i']);
    assert.deepEqual(namesOf('a == b'), ['a', 'b']);
});

test('inlineLines: the lines from the start of the frame to the stopped one that have names', () => {
    const text = ['def add(a, b):', '    total = a + b', '', '    return total'];
    const lines = inlineLines((l) => text[l - 1] ?? null, 1, 4);
    assert.deepEqual(lines.map((l) => l.line), [1, 2, 4]);
    assert.deepEqual(lines[1].names, ['total', 'a', 'b']);
});

test('inlineText: only the names that have a value, cut at the limit', () => {
    assert.equal(inlineText(['total', 'a', 'b'], { a: '1', b: '2' }), 'a = 1, b = 2');
    assert.equal(inlineText(['x'], {}), '');
    assert.equal(inlineText(['x'], { x: 'y'.repeat(200) }, 20).length, 20);
});

test('callsOn: the calls of a line by name, in the order they are written', () => {
    assert.deepEqual(callsOn('r = outer(inner(2))'), ['outer', 'inner']);
    assert.deepEqual(callsOn('self.items.append(make(x))'), ['append', 'make']);
    assert.deepEqual(callsOn('if check(a) and (b or c):'), ['check']);
    assert.deepEqual(callsOn('print("f(x)")'), []);
    assert.deepEqual(callsOn('def run(self):'), []);
});

test('expressionAt: the dotted expression up to the word under the column', () => {
    const line = 'n = self.items.count(x)';
    assert.equal(expressionAt(line, line.indexOf('items') + 2), 'self.items');
    assert.equal(expressionAt(line, line.indexOf('count') + 1), 'self.items.count');
    assert.equal(expressionAt(line, 1), 'n');
    assert.equal(expressionAt(line, 3), null);         // on the "="
    assert.equal(expressionAt('return x', 2), null);   // a keyword
});

test('stackText: one frame per line, innermost first', () => {
    assert.equal(stackText([{ name: 'add', file: 'C:/p/a.py', line: 2 }, { name: '<module>', file: 'C:/p/a.py', line: 7 }]),
        'add, C:/p/a.py:2\n<module>, C:/p/a.py:7');
});

test('functionBreakpoint: a def line names its function, and its code starts at the first decorator', () => {
    const text = ['class A:', '    @property', '    # a note', '    @cached', '    def size(self):', '        return 1', 'x = 1'];
    const at = (l) => text[l - 1] ?? null;
    assert.deepEqual(functionBreakpoint(at, 5), { function: 'size', codeLine: 2 });
    assert.equal(functionBreakpoint(at, 6), null);
    assert.deepEqual(functionBreakpoint((l) => ['async def go():'][l - 1] ?? null, 1), { function: 'go', codeLine: 1 });
});

test('childExpression: items of dicts and lists by index, attributes by name, none for sets and return values', () => {
    assert.equal(childExpression('data', 'dict', "'k'"), "data['k']");
    assert.equal(childExpression('items', 'list', '2'), 'items[2]');
    assert.equal(childExpression('self', 'Point', 'x'), 'self.x');
    assert.equal(childExpression('tags', 'set', '0'), null);
    assert.equal(childExpression('scale()', 'Point', 'x'), null);
    assert.equal(childExpression('obj', 'Thing', 'not a name'), null);
});

test('readWatches: old lists of strings and new lists of objects', () => {
    assert.deepEqual(readWatches(['a', 'b.c']), [{ expression: 'a', paused: false }, { expression: 'b.c', paused: false }]);
    assert.deepEqual(readWatches([{ expression: 'x', paused: true }, { expression: '' }]), [{ expression: 'x', paused: true }]);
    assert.deepEqual(readWatches(null), []);
});

test('declarationLine: where a name of the frame is first bound', () => {
    const text = ['def scale(p, *, factor=2):', '    total: int = 0', '    for i, row in enumerate(rows):', '        x, big = 1, 2',
        '    with open(f) as fh:', '    import os', '    total += 1', '    print(total == 3)'];
    const at = (l) => text[l - 1] ?? null;
    const where = (name) => declarationLine(at, 1, 8, name);
    assert.deepEqual(['p', 'factor', 'total', 'i', 'row', 'big', 'fh', 'os'].map(where), [1, 1, 2, 3, 3, 4, 5, 6]);
    assert.equal(where('rows'), null);   // only read here
    assert.equal(declarationLine((l) => ['    if total == 3:'][l - 1] ?? null, 1, 1, 'total'), null);   // a comparison binds nothing
});

test('historyStep: Up from the empty line goes to the newest entry, Down past the newest empties the line', () => {
    assert.deepEqual(historyStep(['a', 'b'], 2, -1), { index: 1, text: 'b' });
    assert.deepEqual(historyStep(['a', 'b'], 0, -1), { index: 0, text: 'a' });
    assert.deepEqual(historyStep(['a', 'b'], 1, 1), { index: 2, text: '' });
    assert.deepEqual(historyStep([], 0, -1), { index: 0, text: '' });
});

test('moveItem: one place up or down, stopping at the ends', () => {
    assert.deepEqual(moveItem(['a', 'b', 'c'], 2, -1), ['a', 'c', 'b']);
    assert.deepEqual(moveItem(['a', 'b', 'c'], 0, -1), ['a', 'b', 'c']);
    assert.deepEqual(moveItem(['a', 'b', 'c'], 0, 1), ['b', 'a', 'c']);
});

test("framesOfStop: the stopped thread's frames, then the other threads', each frame at the index of its id", () => {
    const frames = framesOfStop({
        frames: [{ id: 0, name: 'f', line: 3 }, { id: 1, name: '<module>', line: 9 }],
        threads: [{ name: 'spinner', frames: [{ id: 2, name: 'worker', line: 4 }] }, { name: 'w2', frames: [{ id: 3, name: 'g', line: 1 }, { id: 4, name: 'run', line: 2 }] }],
    });
    assert.deepEqual(frames.map((f) => f.id), [0, 1, 2, 3, 4]);
    assert.deepEqual(frames.map((f) => f.thread), [null, null, 'spinner', 'w2', 'w2']);
    assert.deepEqual(framesOfStop({ frames: [{ id: 0, name: 'f', line: 1 }] }).map((f) => f.thread), [null]);
});

test('threadHeadings: one heading before each thread, none when only the stopped thread is shown', () => {
    const frames = framesOfStop({ frames: [{ id: 0 }, { id: 1 }], threads: [{ name: 'spinner', frames: [{ id: 2 }, { id: 3 }] }] });
    assert.deepEqual([...threadHeadings(frames, 'MainThread')], [[0, 'MainThread (stopped)'], [2, 'spinner']]);
    assert.equal(threadHeadings(framesOfStop({ frames: [{ id: 0 }] }), 'MainThread').size, 0);
});

test('serverProgramNote: several programs on the debug server, and the last one leaving', () => {
    assert.equal(serverProgramNote({ type: 'connected', from: '10.0.0.2:5000', program: 1, programs: 1 }).text, 'Program 1 connected from 10.0.0.2:5000.\n');
    assert.match(serverProgramNote({ type: 'connected', from: 'b', program: 2, programs: 2 }).text, /^Program 2 connected from b \(2 programs are connected/);
    assert.deepEqual(serverProgramNote({ type: 'disconnected', program: 1, programs: 1 }), { text: 'Program 1 disconnected; 1 program is still connected.\n', still: true });
    assert.equal(serverProgramNote({ type: 'disconnected', program: 2, programs: 0 }).still, false);
    // an engine that does not number them
    assert.equal(serverProgramNote({ type: 'connected', from: 'x' }).text, 'A program connected from x.\n');
    assert.equal(serverProgramNote({ type: 'disconnected' }).still, false);
});

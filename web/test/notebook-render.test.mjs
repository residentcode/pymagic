// Checks of web/src/notebook-render.js: Python code with its colours and LaTeX as MathML, the two things the exported page of a notebook shows besides text
// (`node --test web/test/notebook-render.test.mjs`).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { extractMath, highlightPython, latexToMathml, restoreMath } from '../src/notebook-render.js';
import { notebookToHtml, parseNotebook } from '../src/notebook-model.js';
import { blocksOf } from '../src/markdown-lite.js';

test('keywords, built-ins, strings, comments, numbers and decorators get their own colour, and the text stays text', () => {
    const html = highlightPython('@app.route("/x")\ndef f(a, b=0x1F):\n    return print("<hi>", 3.5e2)  # <done>\n');
    assert.ok(html.includes('<span class="tok-d">@app.route</span>'));
    assert.ok(html.includes('<span class="tok-k">def</span> <span class="tok-f">f</span>'));
    assert.ok(html.includes('<span class="tok-n">0x1F</span>'));
    assert.ok(html.includes('<span class="tok-k">return</span> <span class="tok-b">print</span>'));
    assert.ok(html.includes('<span class="tok-s">&quot;&lt;hi&gt;&quot;</span>'));
    assert.ok(html.includes('<span class="tok-n">3.5e2</span>'));
    assert.ok(html.includes('<span class="tok-c"># &lt;done&gt;</span>'));
    assert.ok(!html.includes('<hi>') && !html.includes('<done>'), 'markup in code is text');
});

test('a class name, f-strings, triple quotes and an unfinished string are read as one string each', () => {
    const html = highlightPython('class Cart:\n    x = f"{a} items"\n    """doc\nmore"""\n    y = \'oops\n');
    assert.ok(html.includes('<span class="tok-k">class</span> <span class="tok-t">Cart</span>'));
    assert.ok(html.includes('<span class="tok-s">f&quot;{a} items&quot;</span>'));
    assert.ok(html.includes('<span class="tok-s">&quot;&quot;&quot;doc\nmore&quot;&quot;&quot;</span>'));
    assert.ok(html.includes('<span class="tok-s">\'oops</span>'.replace("'", "'")));
    // names that only contain a keyword are not keywords
    assert.ok(!highlightPython('iffy = format_x(isit)').includes('tok-k'));
    assert.equal(highlightPython(''), '');
});

test('a formula becomes MathML: fractions, roots, scripts, sums with limits, Greek letters, text', () => {
    const frac = latexToMathml('\\frac{a+1}{2}');
    assert.ok(frac.startsWith('<math ') && frac.includes('<mfrac>'));
    assert.ok(frac.includes('<mi>a</mi><mo>+</mo><mn>1</mn>'));
    assert.ok(latexToMathml('x^2 + y_i').includes('<msup><mi>x</mi><mn>2</mn></msup>'));
    assert.ok(latexToMathml('x_i^2').includes('<msubsup><mi>x</mi><mi>i</mi><mn>2</mn></msubsup>'));
    assert.ok(latexToMathml('\\sqrt{x}').includes('<msqrt><mi>x</mi></msqrt>'));
    assert.ok(latexToMathml('\\sqrt[3]{x}').includes('<mroot>'));
    assert.ok(latexToMathml('\\alpha \\leq \\infty').includes('<mi>α</mi><mo>≤</mo><mo>∞</mo>'));
    assert.ok(latexToMathml('\\text{if } x').includes('<mtext>if </mtext>'));
    assert.ok(latexToMathml('a < b').includes('<mo>&lt;</mo>'), 'a less-than sign is escaped');
    // limits stand above and below a sum in a display formula and beside it in a line of text
    const sum = '\\sum_{i=1}^{n} i';
    assert.ok(latexToMathml(sum, true).includes('<munderover>'));
    assert.ok(latexToMathml(sum, true).startsWith('<math display="block"'));
    assert.ok(latexToMathml(sum, false).includes('<msubsup>'));
    assert.ok(latexToMathml('\\int_0^1 f(x)\\,dx').includes('<msubsup><mo largeop="true">∫</mo>'));
    assert.ok(latexToMathml('\\left( \\frac{1}{2} \\right)').includes('<mo fence="true">(</mo>'));
});

test('a formula this reader does not know is not guessed at', () => {
    assert.equal(latexToMathml('\\begin{matrix} a & b \\end{matrix}'), null);
    assert.equal(latexToMathml('\\frac{a}{'), null);
    assert.equal(latexToMathml('a^'), null);
    assert.equal(latexToMathml('}'), null);
    assert.equal(latexToMathml(''), null);
});

test('formulas are taken out of Markdown before it is read, and put back after, but not out of code', () => {
    const src = 'Euler: $e^{i\\pi} + 1 = 0$ and a price of $5 or $6.\n\n$$\\sum_{k=1}^n k_a * k_b$$\n\nA `code $x_1$ span` and\n\n```\n$y_1$\n```\n';
    const { text, maths } = extractMath(src);
    assert.equal(maths.length, 2);
    assert.deepEqual(maths.map((m) => m.display), [false, true]);
    assert.ok(text.includes('code $x_1$ span') && text.includes('$y_1$'), 'code keeps its dollars');
    assert.ok(text.includes('$5 or $6'), 'prices are not formulas');
    const html = restoreMath('<p>' + text + '</p>', maths);
    assert.ok(html.includes('<math class="tex-math">') && html.includes('<math display="block"'));
    assert.ok(!html.includes('\uE000'));
    // one that cannot be read stays as written
    const bad = restoreMath('<p>\uE0000\uE001</p>', [{ tex: '\\begin{x}', display: false }]);
    assert.equal(bad, '<p><code class="tex">$\\begin{x}$</code></p>');
});

test('the exported page has coloured code and set formulas, and still no script', () => {
    const { nb } = parseNotebook(JSON.stringify({
        nbformat: 4, nbformat_minor: 5, metadata: { kernelspec: { language: 'python', name: 'python3' } },
        cells: [
            { cell_type: 'markdown', metadata: {}, source: ['# Maths\n', '\n', 'Here $a_1 * b_1$ is *emphasised* and $$\\frac{x}{y}$$\n'] },
            { cell_type: 'code', metadata: {}, execution_count: 1, source: ['for i in range(3):\n', '    print(i)'], outputs: [
                { output_type: 'display_data', metadata: {}, data: { 'text/markdown': 'out $z^2$', 'text/plain': 'z' } } ] },
        ],
    }));
    const html = notebookToHtml(nb, { title: 'n', blocksOf });
    assert.ok(html.includes('<span class="tok-k">for</span>') && html.includes('<span class="tok-b">range</span>'));
    assert.ok(html.includes('<msub><mi>a</mi><mn>1</mn></msub>'), 'the underscore was not read as emphasis');
    assert.ok(html.includes('<em>emphasised</em>'));
    assert.ok(html.includes('<mfrac>') && html.includes('<msup><mi>z</mi><mn>2</mn></msup>'));
    assert.ok(html.includes('.tok-k{') && html.includes('math.tex-math'));
    assert.ok(!html.includes('<script'));
    // another language is not coloured as Python
    const other = notebookToHtml({ ...nb, metadata: { kernelspec: { language: 'r' } } }, { title: 'n', blocksOf });
    assert.ok(!other.includes('<span class="tok-k">for</span>'));
});

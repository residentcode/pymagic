// HTML's and YAML's syntax checks (web/src/htmlcheck-model.js).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { htmlProblems, templateProblems, blankTemplate, hasBranches, yamlProblems, isYamlTemplate } from '../src/htmlcheck-model.js';

const messages = (text, opts) => htmlProblems(text, opts).map((p) => `${p.start}: ${p.message}`);
const at = (text, problems) => problems.map((p) => text.slice(p.start, p.end));

test('a well-formed page has no problems: void elements, optional end tags, scripts, comments, attributes with > in quotes', () => {
    const page = '<!DOCTYPE html>\n<html lang="en">\n<head>\n<meta charset="utf-8">\n<title>A < B</title>\n<link rel="stylesheet" href="a.css">\n'
        + '<script>if (a < b && c > d) { document.write("</div>"); }</script>\n<style>p > a { color: red }</style>\n</head>\n<body>\n<!-- <div> not open -->\n'
        + '<ul><li>one<li>two</ul>\n<p>first<p>second\n<table><tr><td>a<td>b<tr><td>c</table>\n<img src="x.png" alt="a > b"><br/>\n<svg><path d="M0 0"/></svg>\n'
        + '<input value=\'it is "x"\' disabled>\n<my-widget data-x="1"></my-widget>\n<p>text <div>block</div>\n</body>\n</html>\n';
    assert.deepEqual(messages(page), []);
});

test('an element never closed, a closing tag that matches nothing, a tag never ended, a comment never ended', () => {
    const t1 = '<div>\n  <p>hi</div>\n<span>\n';
    assert.deepEqual(messages(t1), ['20: Element <span> is not closed: add </span>']);
    const t2 = '<section>\n<div>\n</section>\n';
    assert.deepEqual(messages(t2), ['10: Element <div> is not closed: add </div> before </section>']);
    const t3 = '<p>hi</div>\n';
    assert.deepEqual(messages(t3), ['5: Closing tag </div> matches nothing']);
    const t4 = '<div>\n<span class="a"\n<p>x</p>\n</div>\n';
    assert.deepEqual(messages(t4), ['6: Tag <span> is not ended: add >']);
    assert.deepEqual(at(t4, htmlProblems(t4)), ['<span']);
    assert.deepEqual(messages('<a href="x>y</a>\n'), ['0: Tag <a> is not ended: a quote of an attribute is not closed']);
    assert.deepEqual(messages('<p>a</p>\n<!-- note\n<p>b</p>\n'), ['9: Comment is not closed: add -->']);
    assert.deepEqual(messages('<script>\nlet a = 1;\n'), ['0: Element <script> is not closed: add </script>']);
    assert.deepEqual(messages('<DIV>x</div>'), []);   // names in any case
});

test('a template\'s tags are blanked, and with branches only broken syntax is told', () => {
    assert.equal(blankTemplate('a{{ b }}c{% if x %}\nd{# e #}'), 'a       c          \nd       ');
    assert.deepEqual(messages(blankTemplate('<a href="{% url \'x\' %}" {% if y %}class="on"{% endif %}>{{ name }}</a>')), []);
    const branched = '{% if wide %}<div class="wide">{% else %}<div>{% endif %}\n<p>x</p>\n</div>\n';
    assert.ok(hasBranches(branched));
    assert.deepEqual(templateProblems(branched), []);
    assert.deepEqual(templateProblems('{% if a %}<div>{% else %}<span{% endif %}\n<p>x</p>').map((p) => p.message), ['Tag <span> is not ended: add >']);
    assert.deepEqual(templateProblems('{% block content %}\n<div>\n{% endblock %}\n').map((p) => p.message), ['Element <div> is not closed: add </div>']);
    assert.ok(!hasBranches('{% block x %}{% endblock %}'));
    assert.ok(hasBranches('{%- elif y %}'));
});

test('YAML\'s errors with their place; warnings and empty files are fine', () => {
    const bad = 'a: 1\n  b: 2\n';
    const p = yamlProblems(bad);
    assert.equal(p.length >= 1, true);
    assert.match(p.map((x) => x.message).join(' '), /Nested mappings are not allowed/);
    assert.match(yamlProblems('a:\n\tb: 1\n')[0].message, /Tabs are not allowed/);
    assert.match(yamlProblems('a: [1, 2\nb: 3\n').map((x) => x.message).join(' '), /flow|\]/i);
    assert.match(yamlProblems('a: 1\na: 2\n')[0].message, /unique|duplicate/i);
    assert.deepEqual(yamlProblems('a: 1\nlist:\n  - x\n  - y\n---\nother: "two"\n'), []);
    assert.deepEqual(yamlProblems('Resources:\n  B:\n    Value: !Ref Thing\n'), []);
    assert.deepEqual(yamlProblems(''), []);
    assert.deepEqual(yamlProblems('on:\n  push:\nrun: echo ${{ github.sha }}\n'), []);
    assert.ok(isYamlTemplate('chart/templates/deploy.yaml', 'a: {{ .Values.x }}'));
    assert.ok(!isYamlTemplate('ci.yaml', 'a: {{ x }}'));
});

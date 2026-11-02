import { test } from 'node:test';
import assert from 'node:assert/strict';
import { tagPairAt, scanTags } from '../src/tag-pairs-model.js';

/** The two names found from the offset of the first `|` (which is taken out). */
const pair = (marked) => {
    const at = marked.indexOf('|');
    const text = marked.replace('|', '');
    const found = tagPairAt(text, at);
    return found && found.map(([s, e]) => `${text.slice(s, e)}@${s}`);
};

test('the opening and the closing name of an element, from either', () => {
    assert.deepEqual(pair('<p|>Welcome</p>'), ['p@1', 'p@12']);
    assert.deepEqual(pair('<p>Welcome</|p>'), ['p@1', 'p@12']);
    assert.deepEqual(pair('<div class="a"><span>x</span></d|iv>'), ['div@1', 'div@31']);
    assert.deepEqual(pair('<ul>\n  <li|>a</li>\n  <li>b</li>\n</ul>'), ['li@8', 'li@14']);
});

test('while a name is being changed, the closing tag still belongs to its element', () => {
    // <h>Welcome to {{ title }}</p>: the picture the user sent
    assert.deepEqual(pair('<h|>Welcome to {{ title }}</p>'), ['h@1', 'p@27']);
});

test('void elements, self-closing tags and text outside a tag have no pair', () => {
    assert.equal(pair('<br|><p>x</p>'), null);
    assert.equal(pair('<img src="a.png"|>'), null);
    assert.equal(pair('<Icon| name="x" /><p></p>'), null);
    assert.equal(pair('<p>Wel|come</p>'), null);
});

test('comments, template tags, script and style are not tags', () => {
    assert.deepEqual(pair('<div|>{% if a < b %}<!-- </div> -->{{ "<p>" }}<%= "</div>" %></div>'), ['div@1', 'div@62']);
    assert.deepEqual(pair('<script>if (a < b && c > d) { x = "</b>"; }</script><b|>x</b>'), ['b@53', 'b@58']);
    assert.equal(scanTags('<style>a > b { }</style>').length, 2);
});

test('JSX: components with dots, attributes with {expressions} that hold >', () => {
    assert.deepEqual(pair('<Foo.Bar| onClick={() => a > b}>x</Foo.Bar>'), ['Foo.Bar@1', 'Foo.Bar@34']);
    assert.deepEqual(pair('<div|>{items.map((i) => <Item key={i} />)}</div>'), ['div@1', 'div@43']);
});

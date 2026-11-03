import { test } from 'node:test';
import assert from 'node:assert/strict';
import { classAt, splitVariants, suggestions, themeColors, colorOf, toHex, tailwindOffered } from '../src/tailwind-model.js';
import { merged } from '../src/settings.js';
import { PLUGINS } from '../src/plugin-model.js';

test('the class being typed, in class attributes and class helpers only', () => {
    assert.equal(classAt('<div class="md:flex items-center ml-'), 'ml-');
    assert.equal(classAt('    <div class="ml-au'), 'ml-au');     // the picture the user sent
    assert.equal(classAt('<div class="'), '');
    assert.equal(classAt('<div class="flex '), '');
    assert.equal(classAt("<div class='p-4 bg-"), 'bg-');
    assert.equal(classAt('<Card className="hover:bg-'), 'hover:bg-');
    assert.equal(classAt('<Card className={`gap-'), 'gap-');
    assert.equal(classAt('<div :class="text-'), 'text-');
    assert.equal(classAt('const c = cn("px-2", "py-'), 'py-');
    assert.equal(classAt('const c = clsx(active && "font-'), 'font-');
    assert.equal(classAt('<div class="{% if a %}ring-'), 'ring-');
    assert.equal(classAt('<div class="p-4">text ml-'), null);   // the attribute is closed
    assert.equal(classAt('<div id="ml-'), null);
    assert.equal(classAt('const x = "ml-'), null);
});

test('variants are split off the utility; a colon in brackets is the utility\'s', () => {
    assert.deepEqual(splitVariants('md:hover:bg-red-500'), { variants: 'md:hover:', leaf: 'bg-red-500' });
    assert.deepEqual(splitVariants('ml-auto'), { variants: '', leaf: 'ml-auto' });
    assert.deepEqual(splitVariants('lg:[mask-type:luminance]'), { variants: 'lg:', leaf: '[mask-type:luminance]' });
});

test('the utilities and variants offered keep the variants typed', () => {
    const got = suggestions('md:ml-au', ['ml-auto', 'ml-2', 'mx-auto', 'p-4', '-ml-2'], ['md', 'hover']);
    assert.deepEqual(got.map((s) => s.insert), ['md:md:', 'md:ml-auto', 'md:ml-2', 'md:mx-auto', 'md:-ml-2']);
    assert.equal(suggestions('', ['p-4'], ['md']).length, 2);
});

test('colour classes have their colour, oklch converted', () => {
    const colors = themeColors('@theme { --color-red-500: oklch(63.7% 0.237 25.331); --color-white: #fff; --color-brand: #3366ff; }');
    assert.equal(colorOf('bg-white', colors), '#ffffff');
    assert.equal(colorOf('text-brand', colors), '#3366ff');
    assert.equal(colorOf('border-x-red-500/50', colors), colorOf('bg-red-500', colors));
    assert.match(colorOf('bg-red-500', colors), /^#f[0-9a-f]{5}$/);   // Tailwind's red-500 is about #fb2c36
    assert.equal(colorOf('ml-auto', colors), null);
    assert.equal(toHex('oklch(0% 0 0)'), '#000000');
    assert.equal(toHex('oklch(100% 0 0)'), '#ffffff');
    assert.equal(toHex('currentColor'), null);
});

test('Tailwind CSS is a plugin', () => {
    assert.ok(PLUGINS.some((p) => p.id === 'tailwind' && p.name === 'Tailwind CSS'));
});

test('its classes are offered in a project that uses Tailwind, or in every project when the setting says so', () => {
    assert.equal(merged({}).tailwindIn, 'project');
    assert.ok(tailwindOffered({ used: true }, 'project'));
    assert.ok(!tailwindOffered({ used: false }, 'project'));
    // the engine not answering is a project without it
    assert.ok(!tailwindOffered(null, 'project'));
    assert.ok(tailwindOffered({ used: false }, 'all'));
});

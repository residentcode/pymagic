import test from 'node:test';
import assert from 'node:assert/strict';
import { buildInfo, shortTime, buildLabel } from '../src/build-info.js';

test('run from source files the build is "dev"', () => {
    assert.equal(buildInfo.commit, 'dev');
    assert.equal(buildLabel(), 'build dev');
});

test('the time of a build, short', () => {
    const iso = new Date(2026, 9, 29, 14, 5).toISOString();
    assert.equal(shortTime(iso), '29 Oct 14:05');
    assert.equal(shortTime(new Date(2026, 0, 3, 9, 7).toISOString()), '3 Jan 09:07');
    assert.equal(shortTime(null), '');
    assert.equal(shortTime('not a date'), '');
});

test('the line of the status bar', () => {
    const iso = new Date(2026, 9, 29, 14, 5).toISOString();
    assert.equal(buildLabel({ commit: '8fc7660', builtAt: iso }), 'build 8fc7660 · 29 Oct 14:05');
    assert.equal(buildLabel({ commit: '8fc7660+', builtAt: iso }), 'build 8fc7660+ · 29 Oct 14:05');
    assert.equal(buildLabel({ commit: 'unknown', builtAt: null }), 'build unknown');
});

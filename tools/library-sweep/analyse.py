"""Joins the probe's hovers with the annotations: each reference whose source annotation names a type but whose hover says Unknown (or gives
no def at all), grouped by the shape of the annotation, with examples."""
import collections
import glob
import os
import re
import sys

sweep = sys.argv[1]
expect = {}
for line in open(os.path.join(sweep, 'expect.tsv'), encoding='utf-8'):
    n, root, name, ann = line.rstrip('\n').split('\t', 3)
    expect['V_' + n] = (name, ann)
hovers = {}
for f in glob.glob(os.path.join(sweep, 'out_*.txt')):
    section = ''
    for line in open(f, encoding='utf-8'):
        if line.startswith('== '):
            section = line[3:5]
        prefix = sys.argv[2] if len(sys.argv) > 2 and not sys.argv[2].startswith('--') else None
        if prefix and section != prefix:
            continue
        if line.startswith('V_') and '\t' in line:
            k, h = line.rstrip('\n').split('\t', 1)
            hovers[k] = h


def returns_of(h):
    if h.startswith(('def ', 'async def ')):
        depth = 0
        for i, ch in enumerate(h):
            if ch in '([':
                depth += 1
            elif ch in ')]':
                depth -= 1
                if depth == 0 and h[i + 1:i + 5] == ' -> ':
                    return h[i + 5:]
        return '(none)'
    if ': ' in h:
        return h.split(': ', 1)[1]
    return h


bad = collections.defaultdict(list)
total = unknown = 0
for k, h in hovers.items():
    if k not in expect:
        continue
    name, ann = expect[k]
    total += 1
    ret = returns_of(h)
    if ann == '?' or re.search(r'\bAny\b', ann) or ann in ('None', 'NoReturn', 'Never', 'typing.NoReturn') or ret == ann:
        continue
    partial = '--partial' in sys.argv and re.search(r'Unknown', ret) and not re.search(r'Any', ann)
    if ret in ('Unknown', '(none)') or h == '(no hover)' or h.startswith('V_') and ': Unknown' in h or partial:
        unknown += 1
        shape = re.sub(r'[A-Za-z_][A-Za-z_0-9.]*', 'N', ann)
        shape = 'Self' if 'Self' in ann else 'string' if ann.startswith(("'", '"')) else shape
        bad[shape].append((name, ann, h[:140]))
print('checked', total, 'unknown where the source says a type:', unknown)
for shape, items in sorted(bad.items(), key=lambda kv: -len(kv[1]))[:25]:
    print('\n## %s  (%d)' % (shape, len(items)))
    for name, ann, h in items[:6]:
        print('   %s -> %s\n      %s' % (name, ann, h))

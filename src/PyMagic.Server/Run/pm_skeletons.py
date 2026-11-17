"""PyMagic: stubs ("skeletons") of the compiled modules of an interpreter's packages, which have no Python source to read.

    python -I pm_skeletons.py OUT SITE_PACKAGES [SITE_PACKAGES ...]

Each extension module (.pyd / .so) without a .pyi or .py of its own is imported in a child process and described as a .pyi under OUT, at
its module path (OUT/qh3/_hazmat.pyi): its classes with their methods and properties, its functions, its constants. Signatures come from
the module itself (__text_signature__) or from the first line of the docstring (pybind11, Cython: `f(x: int) -> str`). OUT/manifest.json
remembers the size and time of each module's file, so a later run only does what changed; skeletons of modules that went are removed.
The last line printed is a JSON summary: {"made": n, "kept": n, "removed": n, "failed": n}.
"""
import ast
import builtins
import importlib
import importlib.machinery
import inspect
import json
import keyword
import os
import re
import subprocess
import sys
import types

VERSION = 1
SKIP_DIRS = {'__pycache__', 'tests', 'test', 'testing'}
TIMEOUT = 30
BUILTIN_NAMES = set(dir(builtins))
SIMPLE_TYPES = {'int', 'float', 'str', 'bytes', 'bool', 'complex', 'bytearray', 'object', 'None', 'list', 'dict', 'tuple', 'set', 'frozenset',
                'type', 'Any', 'Optional', 'Union', 'Callable', 'Iterable', 'Iterator', 'Sequence', 'Mapping', 'memoryview', 'slice', 'range'}


# ------------------------------------------------------------ finding the modules

def suffix_of(name):
    for suffix in sorted(importlib.machinery.EXTENSION_SUFFIXES, key=len, reverse=True):
        if name.endswith(suffix):
            return suffix
    return None


def compiled_modules(site):
    """(module name, file) of each extension module in a site-packages folder that has no source or stub beside it."""
    for dirpath, dirs, files in os.walk(site):
        rel = os.path.relpath(dirpath, site)
        parts = [] if rel == '.' else rel.split(os.sep)
        # only importable packages: a folder with an __init__ (or the top), named as a module can be
        if parts and (not all(p.isidentifier() for p in parts) or not any(os.path.exists(os.path.join(dirpath, '__init__' + e)) for e in ('.py', '.pyi', '.pyd', '.so'))):
            dirs[:] = []
            continue
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS and d.isidentifier()]
        for f in files:
            suffix = suffix_of(f)
            if suffix is None:
                continue
            stem = f[:-len(suffix)]
            if not stem.isidentifier():
                continue
            if os.path.exists(os.path.join(dirpath, stem + '.pyi')) or os.path.exists(os.path.join(dirpath, stem + '.py')):
                continue
            name = '.'.join(parts + ([] if stem == '__init__' else [stem]))
            if not name or (parts and os.path.isdir(os.path.join(site, parts[0] + '-stubs'))) or os.path.isdir(os.path.join(site, name.split('.')[0] + '-stubs')):
                continue
            yield name, os.path.join(dirpath, f), stem == '__init__'


# ------------------------------------------------------------ describing one module (in a child process)

def safe(name):
    return name + '_' if keyword.iskeyword(name) else name


def annotation(text, known):
    """A type from a docstring kept only when every name in it is one the stub can spell."""
    if not text:
        return None
    text = text.strip().replace('numpy.ndarray', 'Any').replace('NoneType', 'None')
    try:
        tree = ast.parse(text, mode='eval')
    except SyntaxError:
        return None
    for node in ast.walk(tree):
        if isinstance(node, ast.Name) and node.id not in SIMPLE_TYPES and node.id not in known:
            return None
        if isinstance(node, (ast.Attribute, ast.Call, ast.Lambda, ast.Constant)) and not (isinstance(node, ast.Constant) and node.value is None):
            return None
    return text


def doc_signature(obj, name, known):
    """The parameters and return of `name(a: int, b=1) -> str` on the first line of the docstring, else None."""
    doc = getattr(obj, '__doc__', None)
    if not isinstance(doc, str):
        return None
    first = doc.strip().splitlines()[0] if doc.strip() else ''
    m = re.match(r'^\s*' + re.escape(name) + r'\((.*)\)\s*(?:->\s*(.+?))?\s*:?\s*$', first)
    if not m:
        return None
    params = []
    depth, current = 0, ''
    for ch in m.group(1):
        if ch in '([{':
            depth += 1
        elif ch in ')]}':
            depth -= 1
        if ch == ',' and depth == 0:
            params.append(current.strip())
            current = ''
        else:
            current += ch
    if current.strip():
        params.append(current.strip())
    out = []
    for p in params:
        if p in ('/', '*') or p.startswith('*'):
            star = '**' if p.startswith('**') else '*' if p.startswith('*') and p != '*' else ''
            if p in ('/', '*'):
                out.append(p)
                continue
            p = p.lstrip('*')
        else:
            star = ''
        default = None
        if '=' in p:
            p, default = p.split('=', 1)
        pname, _, ptype = p.partition(':')
        pname = pname.strip()
        if not pname.isidentifier():
            return None
        typed = annotation(ptype, known)
        out.append(star + safe(pname) + (': ' + typed if typed else '') + (' = ...' if default is not None else ''))
    return '(' + ', '.join(out) + ')', annotation(m.group(2), known)


def signature(obj, name, known, method):
    try:
        sig = inspect.signature(obj)
        parts = []
        seen_kw = False
        last_kind = None
        for p in sig.parameters.values():
            if p.kind == p.POSITIONAL_ONLY:
                pass
            elif last_kind == p.POSITIONAL_ONLY:
                parts.append('/')
            if p.kind == p.KEYWORD_ONLY and not seen_kw:
                parts.append('*')
                seen_kw = True
            star = '*' if p.kind == p.VAR_POSITIONAL else '**' if p.kind == p.VAR_KEYWORD else ''
            if p.kind == p.VAR_POSITIONAL:
                seen_kw = True
            parts.append(star + safe(p.name) + (' = ...' if p.default is not p.empty else ''))
            last_kind = p.kind
        if last_kind == inspect.Parameter.POSITIONAL_ONLY:
            parts.append('/')
        text, returns = '(' + ', '.join(parts) + ')', None
        if (found := doc_signature(obj, name, known)) is not None:
            returns = found[1]
    except (ValueError, TypeError):
        found = doc_signature(obj, name, known)
        text, returns = found if found is not None else ('(*args, **kwargs)', None)
    if method and not text.startswith(('(self', '(cls', '(*args')):
        text = '(self' + (', ' if text != '()' else '') + text[1:]
    return text, returns


def value_type(value, known):
    t = type(value)
    if t.__module__ == 'builtins' and t.__name__ in BUILTIN_NAMES:
        return 'None' if value is None else t.__name__
    return known.get(id(t), 'Any')


def describe(module_name):
    module = importlib.import_module(module_name)
    lines = ['# Generated by PyMagic from the compiled module %s; what Python reports about it, not its source.' % module_name,
             'from typing import Any', '']
    imports = []
    known = {}            # id(class) -> how this stub spells it
    exported = set(getattr(module, '__all__', ()))
    # public names, and the private classes the module defines itself (lxml's _Element is what its functions give)
    names = [n for n in sorted(vars(module)) if not n.startswith('__') and (not n.startswith('_') or n in exported
             or isinstance(getattr(module, n, None), type) and getattr(getattr(module, n), '__module__', None) == module_name)]
    own_classes = []
    for n in names:
        v = getattr(module, n, None)
        if isinstance(v, type):
            if getattr(v, '__module__', None) == module_name or getattr(v, '__module__', '').split('.')[0] == module_name.split('.')[0] and not hasattr(sys.modules.get(v.__module__), v.__name__):
                known[id(v)] = n
                own_classes.append((n, v))
            elif v.__module__ == 'builtins':
                known[id(v)] = v.__name__
            elif '.' not in v.__qualname__ and not v.__module__.startswith('_') or v.__module__.split('.')[0] == module_name.split('.')[0]:
                imports.append('from %s import %s as %s' % (v.__module__, v.__qualname__, n))
                known[id(v)] = n
    known_names = set(known.values())
    lines[2:2] = imports

    def base_list(cls):
        bases = []
        for b in cls.__bases__:
            if b is object:
                continue
            spelled = known.get(id(b)) or (b.__name__ if b.__module__ == 'builtins' else None)
            if spelled:
                bases.append(spelled)
        return '(' + ', '.join(bases) + ')' if bases else ''

    def write_class(name, cls, indent):
        pad = '    ' * indent
        lines.append('')
        lines.append('%sclass %s%s:' % (pad, safe(name), base_list(cls)))
        body = 0
        for member in sorted(vars(cls)):
            if member.startswith('__') and member not in ('__init__', '__call__', '__iter__', '__next__', '__enter__', '__exit__', '__len__', '__getitem__',
                                                         '__setitem__', '__contains__', '__aenter__', '__aexit__', '__await__', '__aiter__', '__anext__'):
                continue
            if member.startswith('_') and not member.startswith('__'):
                continue
            raw = inspect.getattr_static(cls, member)
            kind = type(raw).__name__
            try:
                value = getattr(cls, member)
            except Exception:
                value = raw
            if isinstance(raw, (staticmethod,)) or kind == 'staticmethod':
                params, ret = signature(value, member, known_names, method=False)
                lines.append('%s    @staticmethod' % pad)
                lines.append('%s    def %s%s%s: ...' % (pad, safe(member), params, ' -> ' + ret if ret else ''))
            elif isinstance(raw, classmethod) or kind == 'classmethod_descriptor':
                params, ret = signature(value, member, known_names, method=False)
                params = '(cls' + (', ' if params != '()' else '') + params[1:]
                lines.append('%s    @classmethod' % pad)
                lines.append('%s    def %s%s%s: ...' % (pad, safe(member), params, ' -> ' + ret if ret else ''))
            elif callable(raw) and kind in ('method_descriptor', 'wrapper_descriptor', 'builtin_function_or_method', 'function', 'cython_function_or_method', 'instancemethod'):
                params, ret = signature(raw, member, known_names, method=True)
                if member == '__init__':
                    ret = 'None'
                lines.append('%s    def %s%s%s: ...' % (pad, safe(member), params, ' -> ' + ret if ret else ''))
            elif kind in ('getset_descriptor', 'member_descriptor', 'property'):
                lines.append('%s    @property' % pad)
                lines.append('%s    def %s(self) -> Any: ...' % (pad, safe(member)))
            elif isinstance(raw, type):
                write_class(member, raw, indent + 1)
            else:
                lines.append('%s    %s: %s' % (pad, safe(member), value_type(raw, known)))
            body += 1
        if body == 0:
            lines.append('%s    ...' % pad)

    for n, cls in own_classes:
        write_class(n, cls, 0)
    lines.append('')
    for n in names:
        v = getattr(module, n, None)
        if isinstance(v, (type, types.ModuleType)):
            continue
        if callable(v):
            params, ret = signature(v, n, known_names, method=False)
            lines.append('def %s%s%s: ...' % (safe(n), params, ' -> ' + ret if ret else ''))
        else:
            lines.append('%s: %s' % (safe(n), value_type(v, known)))
    text = '\n'.join(lines) + '\n'
    compile(text, module_name + '.pyi', 'exec')   # a stub that does not parse is not written
    return text


# ------------------------------------------------------------ the run

def main():
    if len(sys.argv) >= 3 and sys.argv[1] == '--one':
        sys.stdout.write(describe(sys.argv[2]))
        return
    out = sys.argv[1]
    sites = [s for s in sys.argv[2:] if os.path.isdir(s)]
    os.makedirs(out, exist_ok=True)
    manifest_path = os.path.join(out, 'manifest.json')
    try:
        manifest = json.load(open(manifest_path, encoding='utf-8'))
        if manifest.get('version') != VERSION:
            manifest = {}
    except (OSError, ValueError):
        manifest = {}
    old = manifest.get('modules', {})
    modules = {}
    made = kept = failed = 0
    for site in sites:
        for name, path, is_package in compiled_modules(site):
            if name in modules:
                continue
            st = os.stat(path)
            stamp = [st.st_size, int(st.st_mtime)]
            target = os.path.join(out, *name.split('.')) + ('/__init__.pyi' if is_package else '.pyi')
            if old.get(name, {}).get('stamp') == stamp and (old[name].get('failed') or os.path.exists(target)):
                modules[name] = old[name]
                kept += 1
                continue
            try:
                done = subprocess.run([sys.executable, '-I', __file__, '--one', name], capture_output=True, text=True, timeout=TIMEOUT, cwd=site, encoding='utf-8')
                if done.returncode != 0 or not done.stdout.strip():
                    raise RuntimeError((done.stderr or 'no output').strip().splitlines()[-1][:200] if (done.stderr or '').strip() else 'no output')
                os.makedirs(os.path.dirname(target), exist_ok=True)
                with open(target, 'w', encoding='utf-8') as f:
                    f.write(done.stdout)
                modules[name] = {'stamp': stamp, 'file': os.path.relpath(target, out)}
                made += 1
            except Exception as e:  # a module that cannot be imported here (needs a GPU, a display, another package): remembered, not tried again until it changes
                modules[name] = {'stamp': stamp, 'failed': str(e)[:200]}
                failed += 1
    removed = 0
    for name, entry in old.items():
        if name not in modules and entry.get('file'):
            try:
                os.remove(os.path.join(out, entry['file']))
                removed += 1
            except OSError:
                pass
    with open(manifest_path, 'w', encoding='utf-8') as f:
        json.dump({'version': VERSION, 'modules': modules}, f, indent=1)
    print(json.dumps({'made': made, 'kept': kept, 'removed': removed, 'failed': failed}))


if __name__ == '__main__':
    main()

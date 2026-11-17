"""Writes one probe file per package: a `V_n = module.func` / `V_n = module.Class.method` line for each public function or method whose
source has a return annotation, plus `V_n = module.Class` instance-attribute reads of annotated class attributes. The annotations go to
expect.tsv so that a hover that says Unknown can be told from one whose source says so."""
import ast
import os
import sys

SKIP_PARTS = {'tests', 'test', 'testing', '_vendor', 'vendor', 'examples', 'docs', 'benchmarks', 'conftest'}


def modules_of(root, only=None):
    for top in sorted(os.listdir(root)):
        if only is not None and top not in (only, only + '.py'):
            continue
        if top.endswith(('.dist-info', '.data')) or top.startswith(('_', '.')) or top in SKIP_PARTS:
            continue
        path = os.path.join(root, top)
        if os.path.isfile(path) and top.endswith('.py'):
            yield top[:-3], path
        elif os.path.isdir(path) and os.path.exists(os.path.join(path, '__init__.py')):
            for dirpath, dirs, files in os.walk(path):
                rel = os.path.relpath(dirpath, root).replace(os.sep, '.')
                dirs[:] = [d for d in dirs if not d.startswith(('_', '.')) and d not in SKIP_PARTS and os.path.exists(os.path.join(dirpath, d, '__init__.py'))]
                for f in sorted(files):
                    if not f.endswith('.py') or (f.startswith('_') and f != '__init__.py') or f[:-3] in SKIP_PARTS:
                        continue
                    yield (rel if f == '__init__.py' else rel + '.' + f[:-3]), os.path.join(dirpath, f)


def text(node):
    try:
        return ast.unparse(node)
    except Exception:
        return '?'



if __name__ == '__main__':
    roots = open(sys.argv[1]).read().strip().split(';')
    out_dir = sys.argv[2]
    per_package = int(sys.argv[3]) if len(sys.argv) > 3 else 60
    os.makedirs(out_dir, exist_ok=True)
    expect = open(os.path.join(out_dir, 'expect.tsv'), 'w', encoding='utf-8')

    count = 0
    for root in roots:
        if not os.path.isdir(root):
            continue
        lines = []
        for module, path in modules_of(root):
            if len(lines) >= per_package:
                break
            try:
                tree = ast.parse(open(path, encoding='utf-8').read())
            except Exception:
                continue
            alias = 'm%d' % len(lines)
            body = []
            for node in tree.body:
                if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and not node.name.startswith('_') and node.returns is not None:
                    body.append((node.name, text(node.returns)))
                elif isinstance(node, ast.ClassDef) and not node.name.startswith('_'):
                    for item in node.body:
                        if isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef)) and not item.name.startswith('_') and item.returns is not None:
                            decorators = {text(d) for d in item.decorator_list}
                            if decorators & {'overload', 'typing.overload', 't.overload'}:
                                continue
                            body.append((node.name + '.' + item.name, text(item.returns)))
            if not body:
                continue
            lines.append('import %s as %s' % (module, alias))
            for name, returns in body[:12]:
                count += 1
                lines.append('V_%d = %s.%s' % (count, alias, name))
                expect.write('%d\t%s\t%s.%s\t%s\n' % (count, os.path.basename(root), module, name, returns))
        if lines:
            safe = ''.join(ch if ch.isalnum() else '_' for ch in os.path.basename(os.path.dirname(os.path.dirname(root))) or 'pkg')
            with open(os.path.join(out_dir, 'p_%s.py' % os.path.basename(root).lower().replace('-', '_')), 'w', encoding='utf-8') as f:
                f.write('\n'.join(lines) + '\n')
    print(count)

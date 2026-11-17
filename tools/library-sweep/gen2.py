"""Usage probes: for each public class with annotated members, a function taking an instance (`x: mod.Class`) that uses it the ways code does
- a property read, a call of a method that takes no arguments, `await` of such an async method, `with x as v` / `async with x as v` when the
class has __enter__ / __aenter__, `for v in x` / `async for v in x` when it has __iter__ / __aiter__. expect.tsv has what the source says each
gives, so a hover of Unknown can be told from one the source leaves untyped."""
import ast
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
from gen import modules_of, text  # noqa: E402

site = sys.argv[1]
# one group per top-level package of the site-packages folder
roots = [(site, top[:-3] if top.endswith('.py') else top) for top in sorted(os.listdir(site))
         if not top.startswith(('_', '.')) and not top.endswith(('.dist-info', '.pth', '.data')) and (top.endswith('.py') or os.path.isdir(os.path.join(site, top)))]
out_dir = sys.argv[2]
per_package = int(sys.argv[3]) if len(sys.argv) > 3 else 40
os.makedirs(out_dir, exist_ok=True)
expect = open(os.path.join(out_dir, 'expect.tsv'), 'w', encoding='utf-8')
count = 0


def required(fn):
    a = fn.args
    positional = a.posonlyargs + a.args
    defaults = len(a.defaults)
    needed = len(positional) - defaults - 1          # self
    kw_needed = sum(1 for d in a.kw_defaults if d is None)
    return needed + kw_needed


for root, only in roots:
    lines = []
    classes = 0
    for module, path in modules_of(root, only):
        if classes >= per_package:
            break
        try:
            tree = ast.parse(open(path, encoding='utf-8').read())
        except Exception:
            continue
        alias = 'm%d' % classes
        imported = False
        for node in tree.body:
            if not isinstance(node, ast.ClassDef) or node.name.startswith('_') or classes >= per_package:
                continue
            members = {item.name: item for item in node.body if isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef))}
            uses = []
            for name, fn in members.items():
                decorators = {text(d) for d in fn.decorator_list}
                if decorators & {'overload', 'typing.overload', 't.overload', 'staticmethod', 'classmethod'}:
                    continue
                if fn.returns is None or name.startswith('_'):
                    continue
                ret = text(fn.returns)
                if 'property' in decorators or 'cached_property' in decorators or 'functools.cached_property' in decorators:
                    uses.append(('x.%s' % name, name + ' (property)', ret))
                elif decorators - {'override', 'typing.override', 'abstractmethod', 'abc.abstractmethod'}:
                    continue   # another decorator (contextmanager, cache, ...) decides what the call gives
                elif isinstance(fn, ast.AsyncFunctionDef) and any(isinstance(n, (ast.Yield, ast.YieldFrom)) for n in ast.walk(fn)):
                    continue   # an async generator is iterated, not awaited
                elif required(fn) == 0:
                    if isinstance(fn, ast.AsyncFunctionDef):
                        uses.append(('await x.%s()' % name, name + '() awaited', ret))
                    else:
                        uses.append(('x.%s()' % name, name + '()', ret))
            for dunder, form in (('__enter__', 'with'), ('__aenter__', 'async with'), ('__iter__', 'for'), ('__aiter__', 'async for')):
                fn = members.get(dunder)
                if fn is None:
                    continue
                ret = text(fn.returns) if fn.returns is not None else '?'
                uses.append((form, dunder, ret))
            if not uses:
                continue
            if not imported:
                lines.append('import %s as %s' % (module, alias))
                imported = True
            classes += 1
            lines.append('')
            lines.append('')
            lines.append('async def use_%d(x: %s.%s):' % (count, alias, node.name))
            for expr, what, ret in uses[:10]:
                count += 1
                v = 'V_%d' % count
                if expr == 'with':
                    lines.append('    with x as %s:\n        pass' % v)
                elif expr == 'async with':
                    lines.append('    async with x as %s:\n        pass' % v)
                elif expr == 'for':
                    lines.append('    for %s in x:\n        pass' % v)
                elif expr == 'async for':
                    lines.append('    async for %s in x:\n        pass' % v)
                else:
                    lines.append('    %s = %s' % (v, expr))
                expect.write('%d\t%s\t%s.%s.%s\t%s\n' % (count, os.path.basename(root), module, node.name, what, ret))
    if lines:
        with open(os.path.join(out_dir, 'u_%s.py' % only), 'w', encoding='utf-8') as f:
            f.write('\n'.join(lines) + '\n')
print(count)

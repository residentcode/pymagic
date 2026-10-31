using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

public sealed partial class TypeEvaluator
{
    readonly Dictionary<ModuleInfo, HashSet<string>?> _exports = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A name defined at the top level of a module, including names it re-exports through <c>from x import *</c>.
    /// Stubs lean on this heavily (collections.abc is one star import).
    /// </summary>
    public Symbol? FindModuleSymbol(ModuleInfo m, string name) => FindModuleSymbol(m, name, new HashSet<ModuleInfo>(ReferenceEqualityComparer.Instance));

    Symbol? FindModuleSymbol(ModuleInfo m, string name, HashSet<ModuleInfo> visiting)
    {
        var scope = m.Semantics.ModuleScope;
        if (scope.Symbols.TryGetValue(name, out var s) && (s.Redirect ?? s).Declarations.Count > 0) return s.Redirect ?? s;
        if (scope.StarImports.Count == 0 || !visiting.Add(m)) return null;

        foreach (var star in scope.StarImports)
        {
            var abs = Workspace.AbsoluteName(m, star.Level, star.Module);
            var target = abs == null ? null : _ws.GetModule(abs);
            if (target == null || !IsExported(target, name)) continue;
            var found = FindModuleSymbol(target, name, visiting);
            if (found != null) return found;
        }
        return null;
    }

    bool IsExported(ModuleInfo target, string name)
    {
        var all = ExportedNames(target);
        return all != null ? all.Contains(name) : !name.StartsWith('_');
    }

    /// <summary>The names a module's <c>__all__</c> lists, or null if it has none.</summary>
    public IReadOnlySet<string>? AllNamesOf(ModuleInfo m) => ExportedNames(m);

    /// <summary>The names listed in a module's <c>__all__</c>, or null if it has none.</summary>
    HashSet<string>? ExportedNames(ModuleInfo m)
    {
        if (_exports.TryGetValue(m, out var cached)) return cached;
        _exports[m] = null;   // cycle guard
        HashSet<string>? names = null;
        var env = m.Semantics.Environment;

        void AddAll(IEnumerable<string> items) => (names ??= new HashSet<string>()).UnionWith(items);
        IEnumerable<string> Literal(Expr? e) => e switch
        {
            ListExpr l => l.Items.Select(StringValue).Where(x => x != null).Select(x => x!),
            TupleExpr t => t.Items.Select(StringValue).Where(x => x != null).Select(x => x!),
            _ => Array.Empty<string>(),
        };
        // __all__ += other_module.__all__
        void AddModuleAll(Expr? e)
        {
            if (e is AttributeExpr { Attr: "__all__", Value: NameExpr mod })
            {
                var sym = m.Semantics.ModuleScope.Symbols.GetValueOrDefault(mod.Name);
                if (sym != null && TypeOfSymbol(sym) is ModuleType mt && ExportedNames(mt.Module) is { } theirs) AddAll(theirs);
            }
        }

        void Scan(IEnumerable<Stmt> body)
        {
            foreach (var st in body)
            {
                switch (st)
                {
                    case AssignStmt { Targets: [NameExpr { Name: "__all__" }] } a:
                        AddAll(Literal(a.Value)); AddModuleAll(a.Value);
                        break;
                    case AnnAssignStmt { Target: NameExpr { Name: "__all__" }, Value: not null } a:
                        AddAll(Literal(a.Value));
                        break;
                    case AugAssignStmt { Target: NameExpr { Name: "__all__" } } a:
                        AddAll(Literal(a.Value)); AddModuleAll(a.Value);
                        break;
                    case ExprStmt { Value: CallExpr { Func: AttributeExpr { Value: NameExpr { Name: "__all__" }, Attr: var method }, Args: var args } }:
                        if (method == "extend" && args.Count == 1) { AddAll(Literal(args[0].Value)); AddModuleAll(args[0].Value); }
                        else if (method == "append" && args.Count == 1 && StringValue(args[0].Value) is string one) AddAll(new[] { one });
                        break;
                    case ImportFromStmt imp when imp.Names.Any(n => n.Name == "__all__"):
                    {
                        // `from x import __all__ as __all__` re-exports x's list
                        var abs = Workspace.AbsoluteName(m, imp.Level, imp.Module);
                        var other = abs == null ? null : _ws.GetModule(abs);
                        if (other != null && ExportedNames(other) is { } theirs) AddAll(theirs);
                        break;
                    }
                    case IfStmt i:
                    {
                        var live = StaticConditions.Evaluate(i.Test, env);
                        if (live != false) Scan(i.Body);
                        if (live != true) Scan(i.Orelse);
                        break;
                    }
                    case TryStmt t:
                        Scan(t.Body);
                        break;
                }
            }
        }
        Scan(m.Ast.Body);
        return _exports[m] = names;
    }
}

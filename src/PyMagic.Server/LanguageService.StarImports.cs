using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>
/// Names and star imports: a name that no `from x import *` of the file brings (each module found, its `__all__` respected), which the
/// binder leaves alone because it cannot see other modules; and a name `__all__` lists that the module does not define.
/// </summary>
public sealed partial class LanguageService
{
    IEnumerable<Diag> StarImportDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub) yield break;
        foreach (var f in StarImportFindings(m, ev).Concat(AllListFindings(m, ev)).Concat(StubExportFindings(m, ev)))
            if (!Suppressed(m.Source, f)) yield return AsDiag(f);
    }

    /// <summary>Every module a star import of this one reaches was found and none answers any name (no module __getattr__).</summary>
    bool StarImportsKnown(ModuleInfo m, TypeEvaluator ev, HashSet<ModuleInfo> seen)
    {
        if (!seen.Add(m)) return true;
        foreach (var star in m.Semantics.ModuleScope.StarImports)
        {
            var abs = Workspace.AbsoluteName(m, star.Level, star.Module);
            var target = abs == null ? null : ev.Workspace.GetModule(abs);
            if (target == null || target.Semantics.ModuleScope.Symbols.ContainsKey("__getattr__") || !StarImportsKnown(target, ev, seen)) return false;
        }
        return true;
    }

    IEnumerable<Finding> StarImportFindings(ModuleInfo m, TypeEvaluator ev)
    {
        var scope = m.Semantics.ModuleScope;
        if (!scope.HasStarImport || scope.Symbols.ContainsKey("__getattr__") || !StarImportsKnown(m, ev, new HashSet<ModuleInfo>(ReferenceEqualityComparer.Instance))) yield break;
        foreach (var n in AstChildren.Descendants(m.Ast).OfType<NameExpr>())
        {
            var r = m.Semantics.ReferenceOf(n);
            if (r == null || r.Symbol != null || r.IsBuiltin || (r.Access & Access.Read) == 0) continue;
            if (ev.FindModuleSymbol(m, n.Name) != null) continue;
            if (n.Name is "__file__" or "__name__" or "__doc__" or "__spec__" or "__path__" or "__package__" or "__builtins__" or "__loader__") continue;
            yield return new Finding(n.Start, n.Name.Length, $"Unresolved reference '{n.Name}'", "unresolved-reference", FindingLevel.Warning);
        }
    }

    /// <summary>
    /// `from pkg import Hidden` where pkg's stub only imports Hidden for itself (PEP 484: a stub re-exports `import a as a`,
    /// `from x import a as a`, star imports and what `__all__` lists, nothing else). The type is kept; the import is only said to rely on it.
    /// </summary>
    IEnumerable<Finding> StubExportFindings(ModuleInfo m, TypeEvaluator ev)
    {
        foreach (var imp in AstChildren.Descendants(m.Ast).OfType<ImportFromStmt>())
        {
            if (imp.IsStar || Workspace.AbsoluteName(m, imp.Level, imp.Module) is not { Length: > 0 } abs) continue;
            if (ev.Workspace.GetModule(abs) is not { IsStub: true } target) continue;
            var all = ev.AllNamesOf(target);
            foreach (var a in imp.Names)
            {
                if (all?.Contains(a.Name) == true || ev.Workspace.GetModule($"{abs}.{a.Name}") != null) continue;
                if (!target.Semantics.ModuleScope.Symbols.TryGetValue(a.Name, out var sym) || sym.Declarations.Count == 0) continue;
                bool private_ = sym.Declarations.All(d => d.Kind == DeclKind.Import && d.Node is ImportAlias alias && alias.AsName != (alias.Name.Contains('.') ? null : alias.Name));
                if (!private_) continue;
                int at = m.Source.IndexOf(a.Name, a.Start, StringComparison.Ordinal);
                yield return new Finding(at < 0 ? a.Start : at, a.Name.Length,
                    $"'{a.Name}' is not exported from '{abs}': its stub imports it for itself only (a re-export is written `import x as x` or listed in __all__)", "stub-not-exported", FindingLevel.Weak);
            }
        }
    }

    /// <summary>`__all__ = ["gone"]` where the module has no `gone`: `from module import *` stops with AttributeError.</summary>
    IEnumerable<Finding> AllListFindings(ModuleInfo m, TypeEvaluator ev)
    {
        var scope = m.Semantics.ModuleScope;
        if (!scope.Symbols.ContainsKey("__all__") || scope.Symbols.ContainsKey("__getattr__")) yield break;
        bool starsKnown = !scope.HasStarImport || StarImportsKnown(m, ev, new HashSet<ModuleInfo>(ReferenceEqualityComparer.Instance));
        if (!starsKnown) yield break;
        foreach (var stmt in m.Ast.Body)
        {
            Expr? list = stmt switch
            {
                AssignStmt { Targets: [NameExpr { Name: "__all__" }], Value: var v } => v,
                AugAssignStmt { Target: NameExpr { Name: "__all__" }, Value: var v } => v,
                AnnAssignStmt { Target: NameExpr { Name: "__all__" }, Value: { } v } => v,
                _ => null,
            };
            var items = list switch { ListExpr l => l.Items, TupleExpr t => t.Items, _ => null };
            if (items == null) continue;
            foreach (var item in items)
                if (item is ConstExpr c && Pytest.FixtureModel.StringValue(c) is { } name && ev.FindModuleSymbol(m, name) == null
                    && !(m.IsPackage && ev.Workspace.GetModule($"{m.Name}.{name}") != null))
                    yield return new Finding(c.Start, c.End - c.Start, $"'{name}' is in __all__ but the module does not define it: `from {m.Name} import *` stops with AttributeError", "all-undefined", FindingLevel.Warning);
        }
    }
}

using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// PyCharm's Django inspections that the other checks do not cover: ORM methods a relation does not have (`remove()` and `clear()` on the
/// reverse manager of a ForeignKey that is not null=True, a manager reached through an instance), and django-constance keys.
/// </summary>
public sealed partial class LanguageService
{
    IEnumerable<Diag> DjangoOrmDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub || !m.Source.Contains(".remove(", StringComparison.Ordinal) && !m.Source.Contains(".clear(", StringComparison.Ordinal) && !m.Source.Contains(".objects", StringComparison.Ordinal)) yield break;
        foreach (var access in AstChildren.Descendants(m.Ast).OfType<AttributeExpr>())
        {
            int attrStart = access.End - access.Attr.Length;
            // author.book_set.remove(b): Django gives the reverse manager of a ForeignKey remove() and clear() only when the key can be NULL
            if (access.Attr is "remove" or "clear" && access.Value is AttributeExpr related
                && ev.TypeOf(m, related.Value) is ClassType { IsInstance: true } owner && ev.IsDjangoModel(owner.Info)
                && ev.DjangoReverseSource(owner.Info, related.Attr) is { Kind: "foreign", Nullable: false } source)
            {
                var finding = new Finding(attrStart, access.Attr.Length,
                    $"'{related.Attr}' has no {access.Attr}(): {source.Owner.Name}.{source.Name} is not null=True, so a {source.Owner.Name} cannot be taken out of the relation (delete it, or give the ForeignKey null=True)",
                    "django-orm", FindingLevel.Warning);
                if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
                continue;
            }
            // book.objects: "Manager isn't accessible via Book instances"
            if (ev.TypeOf(m, access.Value) is ClassType { IsInstance: true } instance && ev.IsDjangoModel(instance.Info) && ManagerNamesOf(ev, instance.Info).Contains(access.Attr))
            {
                var finding = new Finding(attrStart, access.Attr.Length,
                    $"Manager isn't accessible via {instance.Info.Name} instances: use {instance.Info.Name}.{access.Attr}", "django-orm", FindingLevel.Warning);
                if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
            }
        }
    }

    /// <summary>The managers of a model: `objects` when it declares none, and each class attribute set to a manager (`Manager()`, `QuerySet.as_manager()`).</summary>
    static HashSet<string> ManagerNamesOf(TypeEvaluator ev, ClassInfo model)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in ev.MroOf(model))
        {
            if (entry is not ClassType e || e.Info.Module.IsStub || e.Info.Module.Name.StartsWith("django.", StringComparison.Ordinal)) continue;
            foreach (var sym in e.Info.Scope.Symbols.Values)
                foreach (var d in sym.Declarations)
                    if (d.Node is NameExpr target && e.Info.Module.ParentOf(target) is AssignStmt { Value: CallExpr call }
                        && (CalleeName(call) is { } callee && (callee.EndsWith("Manager", StringComparison.Ordinal) || callee == "as_manager")))
                        names.Add(sym.Name);
        }
        if (names.Count == 0) names.Add("objects");
        return names;
    }

    // ------------------------------------------------------------ django-constance

    sealed record ConstanceSettings(ModuleInfo Module, Dictionary<string, ConstExpr> Keys, List<(string Key, ConstExpr At)> Fieldsets, bool HasFieldsets);

    /// <summary>`CONSTANCE_CONFIG` and `CONSTANCE_CONFIG_FIELDSETS` of the project's settings, when it has them.</summary>
    ConstanceSettings? ConstanceOf(Snapshot snap)
    {
        foreach (var module in snap.Project)
        {
            if (module.IsStub || !module.Source.Contains("CONSTANCE_CONFIG", StringComparison.Ordinal)) continue;
            DictExpr? config = null;
            Expr? fieldsets = null;
            foreach (var stmt in module.Ast.Body.OfType<AssignStmt>())
            {
                if (stmt.Targets is not [NameExpr target]) continue;
                if (target.Name == "CONSTANCE_CONFIG" && stmt.Value is DictExpr d) config = d;
                else if (target.Name == "CONSTANCE_CONFIG_FIELDSETS") fieldsets = stmt.Value;
            }
            if (config == null) continue;
            var keys = new Dictionary<string, ConstExpr>(StringComparer.Ordinal);
            foreach (var k in config.Keys)
                if (k is ConstExpr c && FixtureModel.StringValue(c) is { } name) keys.TryAdd(name, c);
            var listed = new List<(string, ConstExpr)>();
            // {"Group": ("A", "B")}, a list of pairs, or {"Group": {"fields": ("A",), "collapse": True}}
            IEnumerable<Expr> groups = fieldsets switch
            {
                DictExpr fd => fd.Values,
                ListExpr or TupleExpr => (fieldsets is ListExpr l ? l.Items : ((TupleExpr)fieldsets).Items).Select(p => p is TupleExpr { Items.Count: 2 } t ? t.Items[1] : p),
                _ => Array.Empty<Expr>(),
            };
            foreach (var g in groups)
            {
                var fields = g is DictExpr gd ? gd.Keys.Select((k, i) => (k, i)).Where(x => FixtureModel.StringValue(x.k) == "fields").Select(x => gd.Values[x.i]).FirstOrDefault() : g;
                var items = fields switch { TupleExpr t => t.Items, ListExpr l => l.Items, _ => Array.Empty<Expr>() };
                foreach (var item in items)
                    if (item is ConstExpr c && FixtureModel.StringValue(c) is { } name) listed.Add((name, c));
            }
            return new ConstanceSettings(module, keys, listed, fieldsets != null);
        }
        return null;
    }

    IEnumerable<Diag> ConstanceDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub || !m.Source.Contains("constance", StringComparison.Ordinal) && !m.Source.Contains("CONSTANCE_CONFIG", StringComparison.Ordinal)) yield break;
        var settings = ConstanceOf(Current());
        if (settings == null) yield break;

        // config.KEY where `config` comes from `from constance import config`
        var aliases = AstChildren.Descendants(m.Ast).OfType<ImportFromStmt>().Where(i => i.Module == "constance")
            .SelectMany(i => i.Names).Where(n => n.Name == "config").Select(n => n.AsName ?? n.Name).ToHashSet(StringComparer.Ordinal);
        if (aliases.Count > 0)
            foreach (var access in AstChildren.Descendants(m.Ast).OfType<AttributeExpr>())
            {
                if (access.Value is not NameExpr { Name: var local } || !aliases.Contains(local) || settings.Keys.ContainsKey(access.Attr)) continue;
                var finding = new Finding(access.End - access.Attr.Length, access.Attr.Length,
                    $"'{access.Attr}' is not a key of CONSTANCE_CONFIG", "django-constance", FindingLevel.Warning);
                if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
            }

        // the settings file itself: every key in one fieldset, and no fieldset key that the config does not have
        if (settings.Module.Name != m.Name || !settings.HasFieldsets) yield break;
        var inSets = settings.Fieldsets.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, at) in settings.Keys)
            if (!inSets.Contains(key))
            {
                var finding = new Finding(at.Start, at.End - at.Start, $"'{key}' is in no fieldset of CONSTANCE_CONFIG_FIELDSETS: the admin does not show it", "django-constance", FindingLevel.Warning);
                if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
            }
        foreach (var (key, at) in settings.Fieldsets)
            if (!settings.Keys.ContainsKey(key))
            {
                var finding = new Finding(at.Start, at.End - at.Start, $"'{key}' is not a key of CONSTANCE_CONFIG", "django-constance", FindingLevel.Warning);
                if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
            }
    }
}

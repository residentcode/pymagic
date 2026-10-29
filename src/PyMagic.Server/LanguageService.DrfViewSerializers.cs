using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>
/// A serializer as the view that uses it will run it (item 149): a plain `Serializer` reads its fields from the view's model and needs its own `create()`
/// and `update()`, and `Meta.depth` makes relations read-only. Each case was run with Django 6.1 and REST framework 3.18.3 first.
/// </summary>
public sealed partial class LanguageService
{
    /// <summary>The view's base classes of the REST framework, by name (`CreateModelMixin`, `ModelViewSet`, ...).</summary>
    static HashSet<string> DrfBasesOf(TypeEvaluator ev, ClassInfo view) =>
        ev.MroOf(view).OfType<ClassType>().Where(t => t.Info.Module.Name.StartsWith("rest_framework", StringComparison.Ordinal)).Select(t => t.Info.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether the project's part of a class (its own body and its project bases) defines a method.</summary>
    static bool DefinesMethod(TypeEvaluator ev, ClassInfo c, string name) =>
        ev.MroOf(c).OfType<ClassType>().Any(t => !t.Info.Module.IsStub && !t.Info.Module.Name.StartsWith("rest_framework", StringComparison.Ordinal)
            && t.Info.Node.Body.OfType<FunctionDef>().Any(f => f.Name == name));

    static bool IsModelSerializer(TypeEvaluator ev, ClassInfo c) =>
        ev.MroOf(c).OfType<ClassType>().Any(t => t.Info.Name is "ModelSerializer" or "HyperlinkedModelSerializer" && t.Info.Module.Name.StartsWith("rest_framework", StringComparison.Ordinal));

    /// <summary>`depth = 1` in the Meta of a model serializer; 0 when there is none.</summary>
    static int MetaDepth(TypeEvaluator ev, ClassInfo serializer)
    {
        if (ev.DjangoMetaOf(serializer) is not { } meta) return 0;
        var stmt = meta.Meta.Body.OfType<AssignStmt>().FirstOrDefault(a => a.Targets.Count == 1 && a.Targets[0] is NameExpr { Name: "depth" });
        return stmt?.Value is ConstExpr { Kind: ConstKind.Int } n && int.TryParse(n.Text, out int d) ? d : 0;
    }

    IEnumerable<Diag> ViewSerializerDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub || m.Source.IndexOf("serializer_class", StringComparison.Ordinal) < 0 || m.Source.IndexOf("rest_framework", StringComparison.Ordinal) < 0) yield break;
        var findings = new List<Finding>();
        foreach (var cd in AstChildren.Descendants(m.Ast).OfType<ClassDef>().ToList())
        {
            if (ClassInfoOf(m, cd, ev) is not { } view || ev.DjangoRoleOf(view) != DjangoClassRole.View) continue;
            var (query, serializer, at) = ev.DjangoViewParts(view);
            if (serializer == null || at == null || !IsSerializerClass(ev, serializer)) continue;
            var bases = DrfBasesOf(ev, view);
            bool creates = bases.Contains("CreateModelMixin") && !DefinesMethod(ev, view, "perform_create");
            bool updates = bases.Contains("UpdateModelMixin") && !DefinesMethod(ev, view, "perform_update");
            void Warn(string message) => findings.Add(new Finding(at.Start, at.End - at.Start, message, "drf-view-serializer", FindingLevel.Warning));

            if (!IsModelSerializer(ev, serializer) && ev.DjangoMetaModel(serializer) == null)
            {
                // a plain Serializer: save() calls create() or update(), which it must write itself
                if (creates && !DefinesMethod(ev, serializer, "create"))
                    Warn($"{serializer.Name} is a plain Serializer without create(): a POST to {view.Name} fails with NotImplementedError \"`create()` must be implemented.\" Use a ModelSerializer, or write create()");
                if (updates && !DefinesMethod(ev, serializer, "update"))
                    Warn($"{serializer.Name} is a plain Serializer without update(): a PUT or PATCH to {view.Name} fails with NotImplementedError \"`update()` must be implemented.\" Use a ModelSerializer, or write update()");
                // it reads each field from the objects of the queryset, in a view that lists or retrieves them (one that only creates answers with what create() returns)
                if (query != null && (bases.Contains("ListModelMixin") || bases.Contains("RetrieveModelMixin")))
                    foreach (var f in ev.MroOf(serializer).OfType<ClassType>().Where(t => !t.Info.Module.IsStub && !t.Info.Module.Name.StartsWith("rest_framework", StringComparison.Ordinal))
                        .SelectMany(t => DeclaredFieldsOf(t.Info.Module, t.Info.Node, ev)))
                    {
                        if (f.WriteOnly || NotReadFromTheObject.Contains(f.FieldClass) || f.Source == "*") continue;
                        string first = (f.Source ?? f.Name).Split('.')[0];
                        if (ModelAttribute(ev, query, first) == null)
                            Warn($"{serializer.Name} reads '{first}' from each {query.Name} (its field '{f.Name}'), and {query.Name} has no such attribute: DRF raises AttributeError when {view.Name} answers");
                    }
                continue;
            }

            // depth: the relations become nested and read-only, so a POST cannot set them
            int depth = MetaDepth(ev, serializer);
            if (depth > 0 && creates && !DefinesMethod(ev, serializer, "create") && ev.DjangoMetaModel(serializer) is { } model && ev.DjangoMetaOf(serializer) is { } meta)
            {
                var listed = MetaList(meta.Meta, "fields");
                var excluded = MetaList(meta.Meta, "exclude") ?? new List<string>();
                var declared = DeclaredFieldsOf(meta.Owner.Module, meta.Owner.Node, ev).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
                var required = ev.DjangoFieldsOf(model).Where(f => f.Kind is "foreign" or "one-to-one" && !f.Nullable && !declared.Contains(f.Name)
                    && !f.Call.Args.Any(a => a.Name == "default") && (listed == null ? !excluded.Contains(f.Name) : listed.Contains(f.Name))).Select(f => f.Name).ToList();
                if (required.Count > 0)
                    Warn($"With depth = {depth} in the Meta of {serializer.Name}, {string.Join(", ", required.Select(r => $"'{r}'"))} {(required.Count == 1 ? "is" : "are")} nested and read-only: a POST to {view.Name} cannot set {(required.Count == 1 ? "it" : "them")}, and {model.Name} requires {(required.Count == 1 ? "it" : "them")}, so creating fails with IntegrityError (NOT NULL constraint failed). Declare the field writable, or use a serializer without depth for writing");
            }
        }
        foreach (var f in findings) if (!Suppressed(m.Source, f)) yield return AsDiag(f);
    }
}

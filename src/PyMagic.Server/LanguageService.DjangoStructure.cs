using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>An entry of the Django Structure: a model, an admin class (with the model it is for) or a view (a function taking the request, or a class-based view).</summary>
/// <param name="Model">For an admin class: the model it is registered for; for a model: whether the admin has it (Registered).</param>
public sealed record DjangoStructureItem(string Kind, string Name, string Uri, int Line, string? Model = null, bool Registered = false);

/// <summary>The Django Structure window: the project's models, admin classes and views, which the Django tab groups by app or by kind.</summary>
public sealed partial class LanguageService
{
    public IReadOnlyList<DjangoStructureItem> DjangoStructure()
    {
        var snap = Full();
        var ev = snap.Workspace.Evaluator;
        var items = new List<DjangoStructureItem>();
        var registered = new HashSet<string>(StringComparer.Ordinal);
        var adminFor = new Dictionary<string, string>(StringComparer.Ordinal);   // admin class -> model

        bool Derives(ModuleInfo m, ClassDef cd, string baseName, string modulePrefix) =>
            ClassInfoOf(m, cd, ev) is { } info && KnownMro(info, ev) is { } mro
            && mro.Skip(1).Any(c => c.Info.Name == baseName && c.Info.Module.Name.StartsWith(modulePrefix, StringComparison.Ordinal));
        static string? NameOf(Expr e) => e switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };

        var project = snap.Project.Where(m => InProject(snap, m) && !m.Name.StartsWith("django.", StringComparison.Ordinal) && m.Name != "django").ToList();
        // who is registered in the admin: @admin.register(Model, ...) and admin.site.register(Model[, Admin]) / register([A, B])
        foreach (var m in project)
            foreach (var n in AstChildren.Descendants(m.Ast))
            {
                if (n is ClassDef cd)
                    foreach (var d in cd.Decorators)
                        if (d is CallExpr { Func: var f } call && NameOf(f) == "register")
                            foreach (var a in call.Args.Where(a => a.Name == null))
                                if (NameOf(a.Value) is { } model) { registered.Add(model); adminFor.TryAdd(cd.Name, model); }
                if (n is CallExpr { Func: AttributeExpr { Attr: "register", Value: var site } } reg && NameOf(site) == "site" && reg.Args.Count > 0)
                {
                    var models = reg.Args[0].Value is ListExpr or TupleExpr ? AstChildren.Of(reg.Args[0].Value).OfType<Expr>().Select(NameOf).OfType<string>().ToList()
                        : NameOf(reg.Args[0].Value) is { } one ? new List<string> { one } : new List<string>();
                    foreach (var model in models) registered.Add(model);
                    if (models.Count == 1 && reg.Args.Count > 1 && NameOf(reg.Args[1].Value) is { } admin) adminFor.TryAdd(admin, models[0]);
                }
            }

        foreach (var m in project)
        {
            string? uri = UriOfModule(m);
            if (uri == null) continue;
            var lines = Lines(m);
            int LineOf(int offset) => lines.RangeOf(offset, offset).Start.Line;
            bool viewsModule = m.Name.Split('.') is var parts && (parts[^1] == "views" || parts.Length > 1 && parts[^2] == "views");
            foreach (var s in m.Ast.Body)
            {
                if (s is ClassDef cd)
                {
                    if (Derives(m, cd, "Model", "django.db.models"))
                    {
                        bool isAbstract = cd.Body.OfType<ClassDef>().Any(meta => meta.Name == "Meta" && meta.Body.OfType<AssignStmt>().Any(a => a.Targets is [NameExpr { Name: "abstract" }] && a.Value is ConstExpr { Kind: ConstKind.True }));
                        if (!isAbstract) items.Add(new DjangoStructureItem("model", cd.Name, uri, LineOf(cd.NameStart), Registered: registered.Contains(cd.Name)));
                    }
                    else if (Derives(m, cd, "ModelAdmin", "django.contrib.admin"))
                        items.Add(new DjangoStructureItem("admin", cd.Name, uri, LineOf(cd.NameStart), adminFor.GetValueOrDefault(cd.Name)));
                    else if (Derives(m, cd, "View", "django.views") || viewsModule && cd.Bases.Any(b => NameOf(b.Value) is { } bn && bn.EndsWith("View")))
                        items.Add(new DjangoStructureItem("view", cd.Name, uri, LineOf(cd.NameStart)));
                }
                else if (s is FunctionDef f && viewsModule && !f.Name.StartsWith('_') && f.Params.FirstOrDefault() is { Name: "request" })
                    items.Add(new DjangoStructureItem("view", f.Name, uri, LineOf(f.NameStart)));
            }
        }
        return items.OrderBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal).ToList();
    }
}

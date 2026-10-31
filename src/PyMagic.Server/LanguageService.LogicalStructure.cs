using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>An entry of a file's logical structure: what a class or function is for Django (model, serializer, view, admin, form, signal) and what it holds.</summary>
/// <param name="HasAdmin">For a model: the admin has it. HasSerializer: a ModelSerializer is made for it.</param>
public sealed record LogicalNode(string Kind, string Name, string? Detail, int Line, IReadOnlyList<LogicalNode> Children, bool HasAdmin = false, bool HasSerializer = false);

/// <summary>
/// The logical structure of a file (PyCharm's Django structure view): its models with their fields and relations, serializers with their fields and the
/// ones nested in them, views with their endpoints, admin classes with their model, forms with their fields, and the functions hooked to signals.
/// </summary>
public sealed partial class LanguageService
{
    public IReadOnlyList<LogicalNode> LogicalStructure(string uri)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) return Array.Empty<LogicalNode>();
        var ev = snap.Workspace.Evaluator;
        var lines = Lines(m);
        int LineOf(int offset) => lines.RangeOf(offset, offset).Start.Line;
        string Text(Node n) => m.Source[n.Start..n.End];
        static string? NameOf(Expr e) => e switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, CallExpr c => NameOf(c.Func), _ => null };

        // which models the admin has and which have a ModelSerializer, anywhere in the project
        var structure = DjangoStructure();
        var withAdmin = structure.Where(i => i.Kind == "model" && i.Registered).Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        var withSerializer = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pm in snap.Project.Where(p => InProject(snap, p)))
            foreach (var cd in AstChildren.Descendants(pm.Ast).OfType<ClassDef>())
                if (cd.Body.OfType<ClassDef>().FirstOrDefault(c => c.Name == "Meta") is { } meta && cd.Bases.Any(b => NameOf(b.Value)?.EndsWith("Serializer") == true))
                    foreach (var a in meta.Body.OfType<AssignStmt>())
                        if (a.Targets is [NameExpr { Name: "model" }] && NameOf(a.Value) is { } model) withSerializer.Add(model);
        List<RouteEntry>? routes = null;
        List<RouteEntry> Routes() { try { return routes ??= DjangoRoutes().ToList(); } catch (Exception e) when (e is InvalidOperationException or ServiceException) { return routes = new(); } }

        string? KindOf(ClassDef cd)
        {
            // from what it derives from, when the libraries are there to tell; else from the names of its bases
            if (ClassInfoOf(m, cd, ev) is { } info && KnownMro(info, ev) is { } mro && mro.Count > 1)
            {
                bool Has(string name, string prefix) => mro.Skip(1).Any(c => c.Info.Name == name && c.Info.Module.Name.StartsWith(prefix, StringComparison.Ordinal));
                if (Has("Model", "django.db.models")) return "model";
                if (Has("ModelAdmin", "django.contrib.admin")) return "admin";
                if (Has("BaseSerializer", "rest_framework") || Has("Serializer", "rest_framework")) return "serializer";
                if (Has("BaseForm", "django.forms") || Has("Form", "django.forms")) return "form";
                if (Has("View", "django.views") || Has("APIView", "rest_framework") || Has("ViewSetMixin", "rest_framework")) return "view";
            }
            foreach (var b in cd.Bases.Where(b => b.Name == null))
            {
                string text = Text(b.Value);
                if (text.EndsWith("ModelAdmin")) return "admin";
                if (text.EndsWith("Serializer")) return "serializer";
                if (text.EndsWith("Form")) return "form";
                if (text.EndsWith("View") || text.EndsWith("ViewSet") || text.EndsWith("APIView")) return "view";
                if (text is "models.Model" or "Model") return "model";
            }
            return null;
        }

        List<LogicalNode> FieldsOf(ClassDef cd, string kind)
        {
            var fields = new List<LogicalNode>();
            foreach (var a in cd.Body.OfType<AssignStmt>())
            {
                if (a.Targets is not [NameExpr target] || a.Value is not CallExpr call) continue;
                string field = NameOf(call.Func) ?? "";
                if (kind == "serializer" && field.EndsWith("Serializer"))
                {
                    bool many = call.Args.Any(x => x.Name == "many" && x.Value is ConstExpr { Kind: ConstKind.True });
                    fields.Add(new LogicalNode("nested", target.Name, field + (many ? " (many)" : ""), LineOf(target.Start), Array.Empty<LogicalNode>()));
                    continue;
                }
                if (!field.EndsWith("Field") && field is not ("ForeignKey" or "ManyToManyField" or "OneToOneField")) continue;
                string detail = field;
                if (field is "ForeignKey" or "ManyToManyField" or "OneToOneField" && call.Args.FirstOrDefault(x => x.Name is null or "to") is { } to)
                    detail += " → " + (to.Value is ConstExpr { Kind: ConstKind.Str } s ? System.Text.RegularExpressions.Regex.Replace(s.Text, @"^[rRbBuU]*(['""]+)(.*?)\1$", "$2") : Text(to.Value));
                fields.Add(new LogicalNode(field is "ForeignKey" or "ManyToManyField" or "OneToOneField" ? "relation" : "field", target.Name, detail, LineOf(target.Start), Array.Empty<LogicalNode>()));
            }
            return fields;
        }

        string? MetaModel(ClassDef cd) =>
            cd.Body.OfType<ClassDef>().FirstOrDefault(c => c.Name == "Meta")?.Body.OfType<AssignStmt>()
                .FirstOrDefault(a => a.Targets is [NameExpr { Name: "model" }]) is { } assign ? NameOf(assign.Value) : null;

        List<LogicalNode> EndpointsOf(string name, int line) =>
            Routes().Where(r => r.Uri == uri && (r.Line == line || r.Name == name || r.Name.EndsWith("." + name)))
                .Select(r => new LogicalNode("endpoint", $"{r.Methods} /{r.Path.TrimStart('/')}", r.Name, line, Array.Empty<LogicalNode>())).DistinctBy(n => n.Name).ToList();

        bool viewsModule = m.Name.Split('.') is var parts && (parts[^1] == "views" || parts.Length > 1 && parts[^2] == "views");
        var adminOf = structure.Where(i => i.Kind == "admin" && i.Model != null).ToDictionary(i => i.Name, i => i.Model!, StringComparer.Ordinal);
        var result = new List<LogicalNode>();
        foreach (var s in m.Ast.Body)
        {
            switch (s)
            {
                case ClassDef cd when KindOf(cd) is { } kind:
                {
                    int line = LineOf(cd.NameStart);
                    var node = kind switch
                    {
                        "model" => new LogicalNode("model", cd.Name, null, line, FieldsOf(cd, kind), withAdmin.Contains(cd.Name), withSerializer.Contains(cd.Name)),
                        "serializer" => new LogicalNode("serializer", cd.Name, MetaModel(cd) is { } sm ? "of " + sm : null, line, FieldsOf(cd, kind)),
                        "form" => new LogicalNode("form", cd.Name, MetaModel(cd) is { } fm ? "of " + fm : null, line, FieldsOf(cd, kind)),
                        "admin" => new LogicalNode("admin", cd.Name, adminOf.TryGetValue(cd.Name, out var am) ? "of " + am : null, line, Array.Empty<LogicalNode>()),
                        _ => new LogicalNode("view", cd.Name, null, line, EndpointsOf(cd.Name, line)),
                    };
                    result.Add(node);
                    break;
                }
                case FunctionDef f:
                {
                    // @receiver(pre_save, sender=Book): a signal's handler
                    var receiver = f.Decorators.OfType<CallExpr>().FirstOrDefault(d => NameOf(d.Func) == "receiver");
                    if (receiver != null)
                    {
                        string signal = receiver.Args.FirstOrDefault(a => a.Name == null) is { } sig ? Text(sig.Value) : "?";
                        string? sender = receiver.Args.FirstOrDefault(a => a.Name == "sender") is { } snd ? Text(snd.Value) : null;
                        result.Add(new LogicalNode("signal", f.Name, signal + (sender != null ? " of " + sender : ""), LineOf(f.NameStart), Array.Empty<LogicalNode>()));
                    }
                    else if (viewsModule && f.Params.FirstOrDefault() is { Name: "request" })
                        result.Add(new LogicalNode("view", f.Name, null, LineOf(f.NameStart), EndpointsOf(f.Name, LineOf(f.NameStart))));
                    break;
                }
            }
        }
        return result;
    }
}

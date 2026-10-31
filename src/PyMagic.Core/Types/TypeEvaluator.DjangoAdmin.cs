using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>The model of an admin class, and what it gives the code of the admin: `get_queryset`, the functions its `list_display` calls.</summary>
public sealed partial class TypeEvaluator
{
    Dictionary<ClassInfo, ClassInfo>? _djangoAdminRegistry;
    Dictionary<Node, ClassInfo>? _djangoAdminCallables;

    /// <summary>`@admin.register(Book)` and `admin.site.register(Book, BookAdmin)` in the given modules: the model each admin class is registered for.</summary>
    public static Dictionary<ClassInfo, ClassInfo> DjangoAdminRegistry(IEnumerable<ModuleInfo> modules, TypeEvaluator ev)
    {
        var map = new Dictionary<ClassInfo, ClassInfo>();
        foreach (var m in modules.ToList())
        {
            if (m.IsStub || m.Source.IndexOf("register", StringComparison.Ordinal) < 0) continue;
            ClassInfo? ModelOf(Expr e) => ev.TypeOf(m, e) is ClassType { IsInstance: false } c && ev.IsDjangoModel(c.Info) ? c.Info : null;
            foreach (var cd in AstChildren.Descendants(m.Ast).OfType<ClassDef>().ToList())
                foreach (var d in cd.Decorators)
                    if (d is CallExpr { Func: var f } call && (f is NameExpr { Name: "register" } || f is AttributeExpr { Attr: "register" })
                        && call.Args.Where(a => a.Name == null).Select(a => ModelOf(a.Value)).FirstOrDefault(x => x != null) is { } model)
                        map[ev.ClassOf(m, cd)] = model;
            foreach (var call in AstChildren.Descendants(m.Ast).OfType<CallExpr>().ToList())
            {
                if (call.Func is not AttributeExpr { Attr: "register" }) continue;
                var args = call.Args.Where(a => a.Name == null).ToList();
                var adminArg = args.Count > 1 ? args[1].Value : call.Args.FirstOrDefault(a => a.Name == "admin_class")?.Value;
                if (args.Count == 0 || adminArg == null || ev.TypeOf(m, adminArg) is not ClassType { IsInstance: false } adminClass) continue;
                var models = args[0].Value is ListExpr l ? l.Items : args[0].Value is TupleExpr t ? t.Items : new[] { args[0].Value };
                if (models.Select(ModelOf).FirstOrDefault(x => x != null) is { } model2) map[adminClass.Info] = model2;
            }
        }
        return map;
    }

    /// <summary>The model an admin is for: the one it is registered for, or an inline's `model = Book`.</summary>
    public ClassInfo? DjangoAdminModel(ClassInfo admin)
    {
        var role = DjangoRoleOf(admin);
        if (role == DjangoClassRole.Inline) return DjangoMetaModel(admin);
        if (role != DjangoClassRole.Admin) return null;
        if (_djangoAdminRegistry == null)
        {
            _djangoAdminRegistry = new();   // a lookup made while scanning sees nothing yet
            _djangoAdminRegistry = DjangoAdminRegistry(_ws.UserModules, this);
        }
        return _djangoAdminRegistry.GetValueOrDefault(admin);
    }

    /// <summary>
    /// A function of the user's code that a registered admin lists in `list_display` or `readonly_fields` (`list_display = ("title", short_title)`):
    /// the model whose instances it is called with.
    /// </summary>
    public ClassInfo? DjangoAdminCallableModel(Node function)
    {
        if (_djangoAdminCallables == null)
        {
            _djangoAdminCallables = new();
            var found = new Dictionary<Node, ClassInfo>();
            foreach (var module in _ws.UserModules.ToList())
            {
                if (module.IsStub || module.Source.IndexOf("list_display", StringComparison.Ordinal) < 0 && module.Source.IndexOf("readonly_fields", StringComparison.Ordinal) < 0) continue;
                foreach (var cd in AstChildren.Descendants(module.Ast).OfType<ClassDef>().ToList())
                {
                    var admin = ClassOf(module, cd);
                    if (DjangoAdminModel(admin) is not { } model) continue;
                    foreach (var entry in DjangoAdminCallableEntries(cd))
                        if (TypeOf(module, entry) is FunctionType { Info.Owner: null } f) found[f.Info.Node] = model;
                }
            }
            _djangoAdminCallables = found;
        }
        return _djangoAdminCallables.GetValueOrDefault(function);
    }

    /// <summary>The entries of an admin's `list_display` and `readonly_fields` that are not strings: functions it calls with each object.</summary>
    public static IEnumerable<Expr> DjangoAdminCallableEntries(ClassDef admin)
    {
        foreach (var stmt in admin.Body)
            if (stmt is AssignStmt { Targets: [NameExpr { Name: "list_display" or "readonly_fields" }], Value: var value } && value is ListExpr or TupleExpr)
                foreach (var item in value is ListExpr l ? l.Items : ((TupleExpr)value).Items)
                    if (item is NameExpr or AttributeExpr or LambdaExpr) yield return item;
    }

    /// <summary>`super().get_queryset(request)` and `self.get_queryset(request)` in an admin: a queryset of its model.</summary>
    PyType? DjangoAdminQuerySet(ModuleInfo m, CallExpr call)
    {
        if (call.Func is not AttributeExpr { Attr: "get_queryset", Value: var receiver }) return null;
        if (!(receiver is NameExpr { Name: "self" } || receiver is CallExpr { Func: NameExpr { Name: "super" } })) return null;
        Node? node = call;
        while (node != null && node is not ClassDef) node = m.ParentOf(node);
        if (node is not ClassDef cd || m.IsStub) return null;
        return DjangoAdminModel(ClassOf(m, cd)) is { } model ? InstanceOfClass(DjangoQuerySetOf(model)) : null;
    }
}

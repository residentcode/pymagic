using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>Django's own models (`User` and the other contrib ones), the user model of a project, and the rows a `values()` / `values_list()` gives.</summary>
public sealed partial class TypeEvaluator
{
    readonly Dictionary<(ClassInfo Model, string Rows), ClassInfo> _djangoRowSets = new();
    readonly HashSet<ClassInfo> _djangoRowSetClasses = new(ReferenceEqualityComparer.Instance);

    // ------------------------------------------------------------ the models Django brings

    static readonly Dictionary<string, (string Module, string Class)> DjangoContribModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["auth.User"] = ("django.contrib.auth.models", "User"),
        ["auth.Group"] = ("django.contrib.auth.models", "Group"),
        ["auth.Permission"] = ("django.contrib.auth.models", "Permission"),
        ["contenttypes.ContentType"] = ("django.contrib.contenttypes.models", "ContentType"),
        ["sessions.Session"] = ("django.contrib.sessions.models", "Session"),
        ["sites.Site"] = ("django.contrib.sites.models", "Site"),
        ["admin.LogEntry"] = ("django.contrib.admin.models", "LogEntry"),
    };

    /// <summary>One of Django's own models, from the way a relation names it (`"auth.User"`); null for any other name.</summary>
    ClassInfo? DjangoContribModel(string label)
    {
        if (!DjangoContribModels.TryGetValue(label, out var where) || _ws.GetModule(where.Module) is not { IsStub: false } module) return null;
        return FindModuleSymbol(module, where.Class) is { } sym && TypeOfSymbol(sym) is ClassType { IsInstance: false } ct && IsDjangoModel(ct.Info) ? ct.Info : null;
    }

    /// <summary>
    /// The model `get_user_model()` and `settings.AUTH_USER_MODEL` mean: the one the project's settings name (`AUTH_USER_MODEL = "accounts.Member"`, found
    /// among the project's models by its class name), else Django's own `auth.User`.
    /// </summary>
    public ClassInfo? DjangoUserModel()
    {
        if (DjangoSettingsModule() is { } settings)
            foreach (var stmt in settings.Ast.Body.OfType<AssignStmt>())
                if (stmt.Targets.Count == 1 && stmt.Targets[0] is NameExpr { Name: "AUTH_USER_MODEL" } && StringValue(stmt.Value) is { } label)
                {
                    string name = label.Contains('.') ? label[(label.LastIndexOf('.') + 1)..] : label;
                    return DjangoProjectModels().FirstOrDefault(c => c.Name == name) ?? DjangoContribModel(label);
                }
        return DjangoContribModel("auth.User");
    }

    // ------------------------------------------------------------ calls Django gives a meaning to

    /// <summary>
    /// The result of a call that depends on what is passed, or null when this is not such a call: `get_user_model()` is the user model (the class),
    /// `Book.objects.values(...)` a queryset of dicts and `values_list(...)` one of tuples (of the one value with `flat=True`).
    /// </summary>
    PyType? DjangoSpecialCall(ModuleInfo m, CallExpr call, PyType callee)
    {
        if (callee is FunctionType { Info: { Name: "get_user_model" } f } && f.Module.Name == "django.contrib.auth" && !f.Module.IsStub)
            return DjangoUserModel() is { } user ? new ClassType(user, false) : null;

        if (call.Func is not AttributeExpr { Attr: "values" or "values_list" } access) return null;
        var receiver = TypeOf(m, access.Value);
        if (DjangoModelOfQuery(receiver) is not { } model) return null;
        // rows already: `values("a").values("b")` is left to the plain method
        foreach (var member in receiver.Members)
            if (member is ClassType c && _djangoRowSetClasses.Contains(c.Info)) return null;

        if (access.Attr == "values") return InstanceOfClass(DjangoValuesRowsOf(model, call) is { } typed ? DjangoRowsQuerySetOf(model, InstanceOfClass(typed)) : DjangoRowsQuerySetOf(model, Inst("dict", Inst("str"), PyType.Unknown)));

        var fields = call.Args.Where(a => a.Name == null && a.Stars == 0).ToList();
        bool flat = call.Args.Any(a => a.Name == "flat" && a.Value is ConstExpr { Kind: ConstKind.True });
        bool named = call.Args.Any(a => a.Name == "named" && a.Value is ConstExpr { Kind: ConstKind.True });
        if (named || call.Args.Any(a => a.Stars > 0) || fields.Count == 0) return InstanceOfClass(DjangoRowsQuerySetOf(model, PyType.Unknown));
        var types = fields.Select(a => StringValue(a.Value) is { } path ? DjangoValuesType(model, path) : PyType.Unknown).ToList();
        PyType row = flat && types.Count == 1 ? types[0] : TupleOf(types);
        return InstanceOfClass(DjangoRowsQuerySetOf(model, row));
    }

    readonly Dictionary<(ClassInfo Model, string Keys), ClassInfo> _djangoValueRows = new();

    /// <summary>
    /// The row of `values(...)` as a TypedDict, so that each key has its type and a key that was not asked for is reported (Django raises KeyError):
    /// `values("title", "author")` has the keys as written (a relation gives its target's primary key, `author__name` the name), `n=F(...)` adds `n`,
    /// and `values()` without arguments has every field that is not many-to-many, a relation as `author_id`. Null when the keys cannot be told (`*names`, `**exprs`).
    /// </summary>
    ClassInfo? DjangoValuesRowsOf(ClassInfo model, CallExpr call)
    {
        if (call.Args.Any(a => a.Stars > 0)) return null;
        var keys = new List<(string Name, PyType Type)>();
        var positional = call.Args.Where(a => a.Name == null).ToList();
        if (positional.Count == 0 && call.Args.Count == 0)
        {
            if (!DjangoFieldsOf(model).Any(f => f.PrimaryKey)) keys.Add(("id", Inst("int")));
            foreach (var f in DjangoFieldsOf(model))
            {
                if (f.Kind == "many-to-many") continue;
                keys.Add(f.Kind is "foreign" or "one-to-one" ? (f.Name + "_id", f.Target != null ? DjangoPrimaryKeyType(f.Target) : PyType.Unknown) : (f.Name, DjangoValueType(f)));
            }
        }
        else
        {
            foreach (var a in positional)
            {
                if (StringValue(a.Value) is not { } path) return null;
                keys.Add((path, DjangoValuesType(model, path)));
            }
            // values(n=F("author__name")): the keyword is the key, the expression's value is not told
            foreach (var a in call.Args.Where(a => a.Name != null)) keys.Add((a.Name!, PyType.Unknown));
        }
        if (keys.Count == 0) return null;
        string signature = string.Join(", ", keys.Select(k => k.Name + ": " + k.Type.Display()));
        if (_djangoValueRows.TryGetValue((model, signature), out var known)) return known;
        var row = MakeSyntheticClass(model.Module, $"{model.Name}Row({string.Join(", ", keys.Select(k => k.Name))})", call, BuiltinClass("dict"), Array.Empty<(string, PyType)>());
        ClassDataOf(row).IsTypedDict = true;
        _syntheticTypedDictFields[row] = keys.Select(k => (new FieldInfo(k.Name, k.Type, false), true)).ToList();
        return _djangoValueRows[(model, signature)] = row;
    }

    /// <summary>A queryset whose rows are <paramref name="row"/>: filtering keeps it, reading it gives the rows.</summary>
    ClassInfo DjangoRowsQuerySetOf(ClassInfo model, PyType row)
    {
        var key = (model, row.Display());
        if (_djangoRowSets.TryGetValue(key, out var existing)) return existing;
        var qs = NewSyntheticClass(model.Module, "QuerySet", null);
        _djangoRowSets[key] = qs;
        _djangoRowSetClasses.Add(qs);
        _djangoSyntheticModel[qs] = model;
        AddQuerySetMembers(qs, model, InstanceOfClass(qs), row);
        return qs;
    }

    /// <summary>The type of the value a path (`title`, `author__name`, `pk`) gives in a row: a relation gives its target's primary key, an unknown name (an expression) Unknown.</summary>
    PyType DjangoValuesType(ClassInfo model, string path)
    {
        var parts = path.Split("__");
        var current = model;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (DjangoRelatedModel(current, parts[i]) is not { } next) return PyType.Unknown;
            current = next;
        }
        string last = parts[^1];
        if (last == "pk") return DjangoPrimaryKeyType(current);
        if (DjangoFieldNamed(current, last) is { } field)
            return field.Kind is "foreign" or "one-to-one" or "many-to-many" ? (field.Target != null ? DjangoPrimaryKeyType(field.Target) : PyType.Unknown) : DjangoValueType(field);
        if (last == "id" && !DjangoFieldsOf(current).Any(f => f.PrimaryKey)) return Inst("int");
        if (last.EndsWith("_id", StringComparison.Ordinal) && DjangoFieldNamed(current, last[..^3]) is { Target: { } target }) return DjangoPrimaryKeyType(target);
        return PyType.Unknown;
    }
}

using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>A field of a Django model, as declared in its class body.</summary>
/// <param name="Kind">The kind of value: str, int, float, decimal, bool, date, datetime, time, timedelta, uuid, bytes, any, or the relations foreign, one-to-one, many-to-many.</param>
/// <param name="Target">The model a relation points at, when it can be found.</param>
public sealed record DjangoField(string Name, ClassInfo Owner, CallExpr Call, ClassInfo FieldClass, string Kind, bool Nullable, ClassInfo? Target, bool PrimaryKey, bool HasChoices);

/// <summary>
/// Django models, understood the way Django's own machinery makes them behave: a field is a descriptor (`book.title` is a str, `book.author` an Author),
/// the model gets `objects`, `DoesNotExist`, `id`, `pk`, `author_id` and the reverse names (`author.book_set`), and managers and querysets hand out
/// the model's own type. Applies to Django's real source; django-stubs describe all of this themselves.
/// </summary>
public sealed partial class TypeEvaluator
{
    readonly Dictionary<ClassInfo, bool> _djangoModel = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ClassInfo, List<DjangoField>> _djangoFields = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<(ClassInfo Model, ClassInfo? Base, bool Related), ClassInfo> _djangoManagers = new();
    readonly Dictionary<ClassInfo, ClassInfo> _djangoQuerySets = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ClassInfo, ClassInfo> _djangoSyntheticModel = new(ReferenceEqualityComparer.Instance);   // Manager[M] and QuerySet[M] -> M
    readonly Dictionary<ClassInfo, (ClassInfo Does, ClassInfo Multiple)> _djangoExceptions = new(ReferenceEqualityComparer.Instance);
    List<ClassInfo>? _djangoProjectModels;
    Dictionary<ClassInfo, List<(string Name, DjangoField Source)>>? _djangoReverse;
    string? _djangoSettingsModule;
    bool _djangoSettingsSearched;

    static readonly Dictionary<string, string> DjangoFieldKinds = new(StringComparer.Ordinal)
    {
        ["CharField"] = "str", ["TextField"] = "str", ["SlugField"] = "str", ["EmailField"] = "str", ["URLField"] = "str",
        ["GenericIPAddressField"] = "str", ["IPAddressField"] = "str", ["FilePathField"] = "str",
        ["IntegerField"] = "int", ["BigIntegerField"] = "int", ["SmallIntegerField"] = "int", ["PositiveIntegerField"] = "int",
        ["PositiveSmallIntegerField"] = "int", ["PositiveBigIntegerField"] = "int", ["AutoField"] = "int", ["BigAutoField"] = "int", ["SmallAutoField"] = "int",
        ["FloatField"] = "float", ["DecimalField"] = "decimal", ["BooleanField"] = "bool", ["NullBooleanField"] = "bool?",
        ["DateTimeField"] = "datetime", ["DateField"] = "date", ["TimeField"] = "time", ["DurationField"] = "timedelta",
        ["UUIDField"] = "uuid", ["BinaryField"] = "bytes", ["JSONField"] = "any", ["FileField"] = "any", ["ImageField"] = "any",
        ["ForeignKey"] = "foreign", ["OneToOneField"] = "one-to-one", ["ManyToManyField"] = "many-to-many",
    };

    // ------------------------------------------------------------ recognising models and fields

    /// <summary>A class that derives from Django's Model (real source; a stub describes its own attributes).</summary>
    public bool IsDjangoModel(ClassInfo c)
    {
        if (_djangoModel.TryGetValue(c, out var known)) return known;
        _djangoModel[c] = false;   // a cycle in the hierarchy answers no
        bool result = false;
        if (!c.Module.IsStub)
            foreach (var entry in Mro(c))
                if (entry is ClassType e && e.Info.Name == "Model" && e.Info.Module.Name.StartsWith("django.db.models", StringComparison.Ordinal) && !e.Info.Module.IsStub) { result = true; break; }
        return _djangoModel[c] = result;
    }

    static bool IsDjangoInternal(ClassInfo c) => c.Module.Name.StartsWith("django.", StringComparison.Ordinal) || c.Module.Name == "django";

    string? DjangoFieldKind(ClassInfo fieldClass)
    {
        if (fieldClass.Module.IsStub) return null;
        foreach (var entry in Mro(fieldClass))
            if (entry is ClassType e && IsDjangoInternal(e.Info) && DjangoFieldKinds.TryGetValue(e.Info.Name, out var kind)) return kind;
        return null;
    }

    /// <summary>Every model of the user's code: what reverse relations and name-only references (`"Author"`) are looked up in.</summary>
    List<ClassInfo> DjangoProjectModels()
    {
        if (_djangoProjectModels != null) return _djangoProjectModels;
        var list = new List<ClassInfo>();
        _djangoProjectModels = list;   // a lookup made while scanning sees what has been found so far
        foreach (var module in _ws.UserModules.ToList())
        {
            if (module.IsStub) continue;
            foreach (var sym in module.Semantics.ModuleScope.Symbols.Values)
                foreach (var d in sym.Declarations)
                    if (d.Kind == DeclKind.Class && d.Node is ClassDef cd)
                    {
                        var info = ClassOf(module, cd);
                        if (!IsDjangoInternal(info) && IsDjangoModel(info)) list.Add(info);
                    }
        }
        return list;
    }

    /// <summary>The model a relation field points at: `Author`, `"Author"`, `"app.Author"`, `"self"`, `to=Author`.</summary>
    ClassInfo? DjangoModelRef(ClassInfo owner, Expr? e)
    {
        if (e == null) return null;
        if (StringValue(e) is { } s)
        {
            if (s == "self") return owner;
            string name = s.Contains('.') ? s[(s.LastIndexOf('.') + 1)..] : s;
            if (owner.Module.Semantics.ModuleScope.Symbols.TryGetValue(name, out var sym) && TypeOfSymbol(sym) is ClassType { IsInstance: false } local && IsDjangoModel(local.Info)) return local.Info;
            return DjangoProjectModels().FirstOrDefault(c => c.Name == name);
        }
        return TypeOf(owner.Module, e) is ClassType { IsInstance: false } ct && IsDjangoModel(ct.Info) ? ct.Info : null;
    }

    DjangoField? DjangoFieldOf(ClassInfo owner, Symbol sym)
    {
        for (int i = sym.Declarations.Count - 1; i >= 0; i--)
        {
            var d = sym.Declarations[i];
            if (d.Kind != DeclKind.Variable || d.Node is not NameExpr target) continue;
            CallExpr? call = owner.Module.ParentOf(target) switch
            {
                AssignStmt a when a.Targets.Count == 1 && ReferenceEquals(a.Targets[0], target) => a.Value as CallExpr,
                AnnAssignStmt aa when ReferenceEquals(aa.Target, target) => aa.Value as CallExpr,
                _ => null,
            };
            if (call == null || TypeOf(owner.Module, call.Func) is not ClassType { IsInstance: false } fc) continue;
            string? kind = DjangoFieldKind(fc.Info);
            if (kind == null) continue;
            bool relation = kind is "foreign" or "one-to-one" or "many-to-many";
            var toArg = KeywordArg(call, "to") ?? call.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0)?.Value;
            return new DjangoField(sym.Name, owner, call, fc.Info, kind,
                KeywordArg(call, "null") is ConstExpr { Kind: ConstKind.True },
                relation ? DjangoModelRef(owner, toArg) : null,
                KeywordArg(call, "primary_key") is ConstExpr { Kind: ConstKind.True },
                KeywordArg(call, "choices") != null);
        }
        return null;
    }

    /// <summary>The fields of a model: its own and those of its abstract bases (not Django's internals).</summary>
    public IReadOnlyList<DjangoField> DjangoFieldsOf(ClassInfo model)
    {
        if (_djangoFields.TryGetValue(model, out var cached)) return cached;
        var list = new List<DjangoField>();
        _djangoFields[model] = list;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Mro(model))
        {
            if (entry is not ClassType e || IsDjangoInternal(e.Info) || !IsDjangoModel(e.Info)) continue;
            foreach (var sym in e.Info.Scope.Symbols.Values)
            {
                if (sym.Redirect != null || !names.Add(sym.Name)) continue;
                if (DjangoFieldOf(e.Info, sym) is { } field) list.Add(field);
            }
        }
        return list;
    }

    // ------------------------------------------------------------ value types

    PyType? DjangoInstanceOf(string module, string name) => InstanceOf(module, name);

    PyType DjangoValueType(DjangoField f)
    {
        PyType value = f.Kind switch
        {
            "str" => Inst("str"),
            "int" => Inst("int"),
            "float" => Inst("float"),
            "bool" => Inst("bool"),
            "bool?" => PyType.Union(new[] { Inst("bool"), NoneInstance }),
            "bytes" => Inst("bytes"),
            "decimal" => DjangoInstanceOf("decimal", "Decimal") ?? PyType.Unknown,
            "date" => DjangoInstanceOf("datetime", "date") ?? PyType.Unknown,
            "datetime" => DjangoInstanceOf("datetime", "datetime") ?? PyType.Unknown,
            "time" => DjangoInstanceOf("datetime", "time") ?? PyType.Unknown,
            "timedelta" => DjangoInstanceOf("datetime", "timedelta") ?? PyType.Unknown,
            "uuid" => DjangoInstanceOf("uuid", "UUID") ?? PyType.Unknown,
            "foreign" or "one-to-one" => f.Target != null ? InstanceOfClass(f.Target) : PyType.Unknown,
            "many-to-many" => f.Target != null ? InstanceOfClass(DjangoManagerOf(f.Target, null, related: true)) : PyType.Unknown,
            _ => PyType.Unknown,
        };
        if (f.Nullable && f.Kind != "many-to-many" && value is not UnknownType) value = PyType.Union(new[] { value, NoneInstance });
        return value;
    }

    ClassType InstanceOfClass(ClassInfo c) => new(c, ClassTypeParamsAsArgs(c), true);

    /// <summary>The type of the primary key: the field that says primary_key=True, else the automatic integer `id`.</summary>
    PyType DjangoPrimaryKeyType(ClassInfo model)
    {
        var pk = DjangoFieldsOf(model).FirstOrDefault(f => f.PrimaryKey);
        if (pk == null) return Inst("int");
        return pk.Kind is "foreign" or "one-to-one" ? (pk.Target != null ? DjangoPrimaryKeyType(pk.Target) : PyType.Unknown) : DjangoValueType(pk);
    }

    // ------------------------------------------------------------ managers and querysets

    static readonly IReadOnlyList<ParamInfo> AnyArguments = new[]
    {
        new ParamInfo("args", PyType.Unknown, ParamCategory.VarPositional, true),
        new ParamInfo("kwargs", PyType.Unknown, ParamCategory.VarKeyword, true),
    };

    SignatureType Method(PyType returns) => new(AnyArguments, returns);

    void AddSyntheticMember(ClassInfo c, string name, PyType type)
    {
        var sym = c.Scope.GetOrAdd(name);
        if (sym.Declarations.Count > 0) return;
        var decl = new Declaration(name, DeclKind.Variable, new NameExpr(name));
        sym.Declarations.Add(decl);
        _declOverride[decl] = type;
    }

    ClassInfo NewSyntheticClass(ModuleInfo m, string name, ClassType? baseClass)
    {
        var anchor = new NameExpr(name);
        return MakeSyntheticClass(m, name, anchor, baseClass, Array.Empty<(string, PyType)>());
    }

    /// <summary>`QuerySet[Model]`: filtering and ordering give the same, getting gives a model, counting an int.</summary>
    ClassInfo DjangoQuerySetOf(ClassInfo model)
    {
        if (_djangoQuerySets.TryGetValue(model, out var existing)) return existing;
        var qs = NewSyntheticClass(model.Module, "QuerySet", null);
        _djangoQuerySets[model] = qs;
        _djangoSyntheticModel[qs] = model;
        AddQuerySetMembers(qs, model, InstanceOfClass(qs));
        return qs;
    }

    void AddQuerySetMembers(ClassInfo target, ClassInfo model, PyType self)
    {
        var modelInstance = InstanceOfClass(model);
        var qsInstance = InstanceOfClass(DjangoQuerySetOf(model));
        var optionalModel = PyType.Union(new[] { (PyType)modelInstance, NoneInstance });
        foreach (var name in new[]
        {
            "filter", "exclude", "order_by", "reverse", "distinct", "select_related", "prefetch_related", "annotate", "defer", "only", "using",
            "select_for_update", "none", "alias", "extra", "union", "intersection", "difference",
        }) AddSyntheticMember(target, name, Method(qsInstance));
        AddSyntheticMember(target, "all", Method(qsInstance));
        foreach (var name in new[] { "get", "create", "earliest", "latest" }) AddSyntheticMember(target, name, Method(modelInstance));
        foreach (var name in new[] { "first", "last" }) AddSyntheticMember(target, name, Method(optionalModel));
        foreach (var name in new[] { "get_or_create", "update_or_create" })
            AddSyntheticMember(target, name, Method(TupleOf(new PyType[] { modelInstance, Inst("bool") })));
        AddSyntheticMember(target, "count", Method(Inst("int")));
        AddSyntheticMember(target, "update", Method(Inst("int")));
        AddSyntheticMember(target, "exists", Method(Inst("bool")));
        AddSyntheticMember(target, "bulk_create", Method(Inst("list", modelInstance)));
        AddSyntheticMember(target, "__iter__", Method(TypingInst("Iterator", modelInstance)));
        AddSyntheticMember(target, "__len__", Method(Inst("int")));
        AddSyntheticMember(target, "__bool__", Method(Inst("bool")));
        AddSyntheticMember(target, "model", new ClassType(model, false));
    }

    /// <summary>`Manager[Model]`: the queryset methods too, since a manager passes them on. A custom manager class stays in the chain, so its own methods are found.</summary>
    ClassInfo DjangoManagerOf(ClassInfo model, ClassInfo? baseManager, bool related = false)
    {
        var key = (model, baseManager, related);
        if (_djangoManagers.TryGetValue(key, out var existing)) return existing;
        ClassType? baseType = baseManager == null ? null : new ClassType(baseManager, false);
        var manager = NewSyntheticClass(model.Module, baseManager?.Name ?? (related ? "RelatedManager" : "Manager"), baseType);
        _djangoManagers[key] = manager;
        _djangoSyntheticModel[manager] = model;
        AddQuerySetMembers(manager, model, InstanceOfClass(manager));
        AddSyntheticMember(manager, "get_queryset", Method(InstanceOfClass(DjangoQuerySetOf(model))));
        if (related)
            foreach (var name in new[] { "add", "remove", "clear", "set" }) AddSyntheticMember(manager, name, Method(NoneInstance));
        return manager;
    }

    /// <summary>`Book.DoesNotExist` and `Book.MultipleObjectsReturned`, subclasses of Django's ObjectDoesNotExist.</summary>
    (ClassInfo Does, ClassInfo Multiple) DjangoExceptionsOf(ClassInfo model)
    {
        if (_djangoExceptions.TryGetValue(model, out var existing)) return existing;
        ClassType? doesBase = null, multipleBase = null;
        if (_ws.GetModule("django.core.exceptions") is { } exceptions)
        {
            if (exceptions.Semantics.ModuleScope.Symbols.TryGetValue("ObjectDoesNotExist", out var d) && TypeOfSymbol(d) is ClassType { IsInstance: false } dc) doesBase = dc;
            if (exceptions.Semantics.ModuleScope.Symbols.TryGetValue("MultipleObjectsReturned", out var mo) && TypeOfSymbol(mo) is ClassType { IsInstance: false } mc) multipleBase = mc;
        }
        var exceptionBase = BuiltinClass("Exception");
        var result = (NewSyntheticClass(model.Module, "DoesNotExist", doesBase ?? exceptionBase), NewSyntheticClass(model.Module, "MultipleObjectsReturned", multipleBase ?? exceptionBase));
        _djangoExceptions[model] = result;
        return result;
    }

    // ------------------------------------------------------------ reverse relations

    Dictionary<ClassInfo, List<(string Name, DjangoField Source)>> DjangoReverseRelations()
    {
        if (_djangoReverse != null) return _djangoReverse;
        var index = new Dictionary<ClassInfo, List<(string, DjangoField)>>(ReferenceEqualityComparer.Instance);
        _djangoReverse = index;
        foreach (var model in DjangoProjectModels().ToList())
            foreach (var f in DjangoFieldsOf(model))
            {
                if (f.Target == null || f.Kind is not ("foreign" or "one-to-one" or "many-to-many")) continue;
                string? name = DjangoReverseName(f, model);
                if (name == null) continue;
                if (!index.TryGetValue(f.Target, out var list)) index[f.Target] = list = new();
                list.Add((name, f));
            }
        return index;
    }

    static string? DjangoReverseName(DjangoField f, ClassInfo declaringModel)
    {
        string lower = declaringModel.Name.ToLowerInvariant();
        var related = f.Call.Args.FirstOrDefault(a => a.Name == "related_name")?.Value;
        if (related != null)
        {
            if (StringValue(related) is not { } text || text == "+" || text.EndsWith('+')) return null;
            return text.Replace("%(class)s", lower).Replace("%(app_label)s", "");
        }
        return f.Kind == "one-to-one" ? lower : lower + "_set";
    }

    /// <summary>The reverse accessors the models of the project add to a model.</summary>
    IEnumerable<(string Name, DjangoField Source)> DjangoReverseOf(ClassInfo model)
    {
        var all = DjangoReverseRelations();
        foreach (var entry in Mro(model))
            if (entry is ClassType e && all.TryGetValue(e.Info, out var list))
                foreach (var item in list) yield return item;
    }

    // ------------------------------------------------------------ what the model gets that its class body does not say

    /// <summary>The attributes Django adds to a model class and its instances; null when the name is not one of them.</summary>
    PyType? DjangoImplicitMember(ClassType model, string name, bool instance)
    {
        var info = model.Info;
        switch (name)
        {
            case "objects" when !instance:
            case "_default_manager" when !instance:
            case "_base_manager" when !instance:
                return InstanceOfClass(DjangoManagerOf(info, null));
            case "DoesNotExist" when !instance:
                return new ClassType(DjangoExceptionsOf(info).Does, false);
            case "MultipleObjectsReturned" when !instance:
                return new ClassType(DjangoExceptionsOf(info).Multiple, false);
            case "DoesNotExist":
                return new ClassType(DjangoExceptionsOf(info).Does, false);
            case "MultipleObjectsReturned":
                return new ClassType(DjangoExceptionsOf(info).Multiple, false);
            case "pk" when instance:
                return DjangoPrimaryKeyType(info);
            case "id" when instance && !DjangoFieldsOf(info).Any(f => f.PrimaryKey):
                return Inst("int");
            case "_meta":
                return PyType.Unknown;
        }

        foreach (var f in DjangoFieldsOf(info))
        {
            if (instance && f.Kind is "foreign" or "one-to-one" && name == f.Name + "_id")
                return f.Target != null ? DjangoPrimaryKeyType(f.Target) : PyType.Unknown;
            if (instance && f.HasChoices && name == $"get_{f.Name}_display") return Method(Inst("str"));
        }
        foreach (var (reverse, source) in DjangoReverseOf(info))
            if (reverse == name)
                return source.Kind == "one-to-one" ? InstanceOfClass(source.Owner) : InstanceOfClass(DjangoManagerOf(source.Owner, null, related: true));
        return null;
    }

    /// <summary>Names offered after the dot on a model that the class body does not declare.</summary>
    IEnumerable<string> DjangoImplicitNames(ClassType model)
    {
        var info = model.Info;
        if (!model.IsInstance) { yield return "objects"; yield return "_default_manager"; }
        yield return "DoesNotExist";
        yield return "MultipleObjectsReturned";
        if (model.IsInstance)
        {
            yield return "pk";
            if (!DjangoFieldsOf(info).Any(f => f.PrimaryKey)) yield return "id";
            foreach (var f in DjangoFieldsOf(info))
            {
                if (f.Kind is "foreign" or "one-to-one") yield return f.Name + "_id";
                if (f.HasChoices) yield return $"get_{f.Name}_display";
            }
        }
        foreach (var (reverse, _) in DjangoReverseOf(info)) yield return reverse;
    }

    /// <summary>A field read through an instance gives its value, a manager read through the class gives a manager of this model.</summary>
    PyType? DjangoBoundMember(MemberHit hit, ClassType receiver, bool receiverIsInstance)
    {
        if (hit.ClassSymbol == null || !IsDjangoModel(receiver.Info) || IsDjangoInternal(hit.Owner.Info)) return null;
        if (receiverIsInstance && DjangoFieldOf(hit.Owner.Info, hit.ClassSymbol) is { } field) return DjangoValueType(field);
        if (!receiverIsInstance && TypeOfSymbol(hit.ClassSymbol) is ClassType { IsInstance: true } manager && IsDjangoManagerClass(manager.Info))
        {
            // `objects = BookManager()`: the manager of this model, with the custom methods still in reach
            return InstanceOfClass(DjangoManagerOf(receiver.Info, IsDjangoInternal(manager.Info) ? null : manager.Info));
        }
        return null;
    }

    bool IsDjangoManagerClass(ClassInfo c)
    {
        foreach (var entry in Mro(c))
            if (entry is ClassType e && e.Info.Name is "BaseManager" or "Manager" && IsDjangoInternal(e.Info)) return true;
        return false;
    }

    /// <summary>The model a manager or queryset type stands for (null for anything else): lets tools check and complete the keywords of filter(...).</summary>
    public ClassInfo? DjangoModelOfQuery(PyType receiver)
    {
        foreach (var member in receiver.Members)
            if (member is ClassType c)
            {
                foreach (var entry in Mro(c.Info))
                    if (entry is ClassType e && _djangoSyntheticModel.TryGetValue(e.Info, out var model)) return model;
            }
        return null;
    }

    /// <summary>The names a query on this model may use: fields, relations (and what they reach), reverse relations, `pk`, `id`.</summary>
    public IReadOnlyList<string> DjangoQueryNames(ClassInfo model)
    {
        var names = new List<string> { "pk" };
        foreach (var f in DjangoFieldsOf(model)) names.Add(f.Name);
        if (!DjangoFieldsOf(model).Any(f => f.PrimaryKey)) names.Add("id");
        foreach (var (reverse, source) in DjangoReverseOf(model)) names.Add(source.Kind == "many-to-many" || source.Kind == "foreign" ? (source.Call.Args.Any(a => a.Name == "related_query_name") ? reverse : reverse.EndsWith("_set") ? reverse[..^4] : reverse) : reverse);
        return names;
    }

    /// <summary>The model a relation named by a query reaches (`author` in `author__name`), or null.</summary>
    public ClassInfo? DjangoRelatedModel(ClassInfo model, string name)
    {
        var f = DjangoFieldsOf(model).FirstOrDefault(x => x.Name == name);
        if (f?.Target != null) return f.Target;
        foreach (var (reverse, source) in DjangoReverseOf(model))
            if (reverse == name || reverse == name + "_set") return source.Owner;
        return null;
    }

    public DjangoField? DjangoFieldNamed(ClassInfo model, string name) => DjangoFieldsOf(model).FirstOrDefault(f => f.Name == name);

    // ------------------------------------------------------------ settings

    /// <summary>`settings.X` on django.conf.settings: the module DJANGO_SETTINGS_MODULE names (found in manage.py / wsgi.py / asgi.py), else a settings module of the project.</summary>
    ModuleInfo? DjangoSettingsModule()
    {
        if (!_djangoSettingsSearched)
        {
            _djangoSettingsSearched = true;
            var pattern = new Regex(@"DJANGO_SETTINGS_MODULE['""]\s*,\s*['""]([\w.]+)['""]");
            foreach (var module in _ws.UserModules.ToList())
            {
                if (module.IsStub || module.Source.IndexOf("DJANGO_SETTINGS_MODULE", StringComparison.Ordinal) < 0) continue;
                var match = pattern.Match(module.Source);
                if (match.Success && _ws.GetModule(match.Groups[1].Value) != null) { _djangoSettingsModule = match.Groups[1].Value; break; }
            }
            _djangoSettingsModule ??= _ws.UserModules.FirstOrDefault(m => !m.IsStub && (m.Name == "settings" || m.Name.EndsWith(".settings", StringComparison.Ordinal)))?.Name;
        }
        return _djangoSettingsModule == null ? null : _ws.GetModule(_djangoSettingsModule);
    }

    bool IsDjangoSettingsObject(ClassInfo c) => c.Name is "LazySettings" or "Settings" or "UserSettingsHolder" && c.Module.Name == "django.conf" && !c.Module.IsStub;

    /// <summary>The symbol a setting name resolves to: the project's settings module first, then Django's defaults.</summary>
    Symbol? DjangoSettingSymbol(string name)
    {
        if (DjangoSettingsModule() is { } module && FindModuleSymbol(module, name) is { } own) return own;
        return _ws.GetModule("django.conf.global_settings") is { } defaults ? FindModuleSymbol(defaults, name) : null;
    }

    public IEnumerable<string> DjangoSettingNames()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in new[] { DjangoSettingsModule(), _ws.GetModule("django.conf.global_settings") })
            if (module != null)
                foreach (var name in module.Semantics.ModuleScope.Symbols.Keys)
                    if (name.Length > 0 && name.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_') && seen.Add(name)) yield return name;
    }
}

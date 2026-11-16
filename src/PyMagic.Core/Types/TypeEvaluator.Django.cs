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
        // Django 5: a value the database computes (its output_field says what it is); Django 5.2: a primary key of several fields
        ["GeneratedField"] = "generated", ["CompositePrimaryKey"] = "composite",
    };

    // ------------------------------------------------------------ recognising models and fields

    /// <summary>
    /// A class of the project (or a library's source) that derives from Django's Model, whether Django's source or django-stubs describe it:
    /// the stubs leave a model's fields, managers and relations to their mypy plugin, so they are read from the class body either way.
    /// </summary>
    public bool IsDjangoModel(ClassInfo c)
    {
        if (_djangoModel.TryGetValue(c, out var known)) return known;
        _djangoModel[c] = false;   // a cycle in the hierarchy answers no
        bool result = false;
        if (!c.Module.IsStub)
            foreach (var entry in Mro(c))
                if (entry is ClassType e && e.Info.Name == "Model" && e.Info.Module.Name.StartsWith("django.db.models", StringComparison.Ordinal)) { result = true; break; }
        return _djangoModel[c] = result;
    }

    /// <summary>A model django-stubs describe (`django.contrib.auth.models.User` of the stubs): its fields are typed there; relations to it are not.</summary>
    bool IsDjangoStubModelClass(ClassInfo c) =>
        c.Module.IsStub && IsDjangoInternal(c) && Mro(c).Any(e => e is ClassType { Info: { Name: "Model" } m } && m.Module.Name.StartsWith("django.db.models", StringComparison.Ordinal));

    static bool IsDjangoInternal(ClassInfo c) => c.Module.Name.StartsWith("django.", StringComparison.Ordinal) || c.Module.Name == "django";

    /// <summary>What a field class of Django (its source or django-stubs) holds: "str", "foreign", ...; null for another class.</summary>
    string? DjangoFieldKind(ClassInfo fieldClass)
    {
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
        // the models files nothing imported yet: their relations add reverse accessors to models of other apps
        foreach (var name in _ws.Options.ModelModules) _ws.GetModule(name);
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
            return DjangoProjectModels().FirstOrDefault(c => c.Name == name) ?? DjangoContribModel(s);
        }
        if (e is AttributeExpr { Attr: "AUTH_USER_MODEL" }) return DjangoUserModel();
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
            if (entry is not ClassType e || !IsDjangoModel(e.Info) || e.Info.Name == "Model" && IsDjangoInternal(e.Info)) continue;
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
            "generated" => GeneratedFieldType(f),
            "composite" => CompositeKeyType(f),
            _ => PyType.Unknown,
        };
        if (f.Nullable && f.Kind != "many-to-many" && value is not UnknownType) value = PyType.Union(new[] { value, NoneInstance });
        return value;
    }

    ClassType InstanceOfClass(ClassInfo c) => new(c, ClassTypeParamsAsArgs(c), true);

    /// <summary>GeneratedField(expression=..., output_field=models.DecimalField(...)): the value of its output field.</summary>
    PyType GeneratedFieldType(DjangoField f)
    {
        if (f.Call.Args.FirstOrDefault(a => a.Name == "output_field")?.Value is not CallExpr { Func: var of }) return PyType.Unknown;
        string? name = of switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };
        return name != null && DjangoFieldKinds.TryGetValue(name, out var kind) && kind is not ("generated" or "composite" or "foreign" or "one-to-one" or "many-to-many")
            ? DjangoValueType(f with { Kind = kind, Nullable = false })
            : PyType.Unknown;
    }

    /// <summary>CompositePrimaryKey("order_id", "product_id"): pk is a tuple of those fields' values (a relation's `_id` its target's key).</summary>
    PyType CompositeKeyType(DjangoField f)
    {
        var items = new List<PyType>();
        foreach (var arg in f.Call.Args)
        {
            if (arg.Name != null || StringValue(arg.Value) is not { } name) return PyType.Unknown;
            var fields = DjangoFieldsOf(f.Owner);
            if (fields.FirstOrDefault(x => x.Name == name && x.Kind != "composite") is { } plain) items.Add(DjangoValueType(plain));
            else if (name.EndsWith("_id") && fields.FirstOrDefault(x => x.Name == name[..^3] && x.Kind is "foreign" or "one-to-one") is { Target: { } target }) items.Add(DjangoPrimaryKeyType(target));
            else items.Add(PyType.Unknown);
        }
        return TupleOf(items);
    }

    /// <summary>The type of the primary key: the field that says primary_key=True, else the automatic integer `id`.</summary>
    PyType DjangoPrimaryKeyType(ClassInfo model)
    {
        var pk = DjangoFieldsOf(model).FirstOrDefault(f => f.PrimaryKey || f.Kind == "composite");
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

    /// <summary>
    /// `@task` / `@task(priority=10)` of django.tasks (Django 6.0): the backend's task class wraps the function, which Django's source picks at
    /// run time. The function becomes a Task whose enqueue() gives a TaskResult, whose return_value is what the function returns; call() runs
    /// it in place, and using() gives the same Task with other options.
    /// </summary>
    PyType? DjangoTask(ModuleInfo m, FunctionDef def, Expr dec, PyType fn)
    {
        var target = dec is CallExpr c ? c.Func : dec;
        if (!TypeOf(m, target).Members.Any(x => x is FunctionType { Info: { Name: "task", Module: { Name: "django.tasks.base", IsStub: false } } })) return null;
        if (_ws.GetModule("django.tasks.base") is not { IsStub: false } tasks
            || FindModuleSymbol(tasks, "Task") is not { } taskSym || TypeOfSymbol(taskSym) is not ClassType { IsInstance: false } taskClass
            || FindModuleSymbol(tasks, "TaskResult") is not { } resultSym || TypeOfSymbol(resultSym) is not ClassType { IsInstance: false } resultClass) return null;
        // what the function gives; an async def's, once awaited
        PyType value = fn is FunctionType f ? ReturnTypeOf(f) : PyType.Unknown;
        if (def.IsAsync && value is ClassType { Info.Name: "Coroutine", Args.Count: 3 } co) value = co.Args[2];
        var result = MakeSyntheticClass(m, "TaskResult", dec, resultClass, Array.Empty<(string, PyType)>());
        AddSyntheticMember(result, "return_value", value);
        var resultInstance = InstanceOfClass(result);
        var task = MakeSyntheticClass(m, "Task", def, taskClass, Array.Empty<(string, PyType)>());
        var taskInstance = InstanceOfClass(task);
        // enqueue() and call() take what the function takes
        var takes = fn is FunctionType ff ? ParametersOf(ff.Info).ToArray() : AnyArguments.ToArray();
        SignatureType Async(SignatureType sync) => sync with { Return = TypingInst("Coroutine", PyType.Any, PyType.Any, sync.Return) };
        var enqueue = Def(resultInstance, takes);
        var getResult = Def(resultInstance, Arg("result_id", Inst("str")));
        var call = Def(value, takes);
        AddSyntheticMember(task, "enqueue", enqueue);
        AddSyntheticMember(task, "aenqueue", Async(enqueue));
        AddSyntheticMember(task, "get_result", getResult);
        AddSyntheticMember(task, "aget_result", Async(getResult));
        AddSyntheticMember(task, "call", call);
        AddSyntheticMember(task, "acall", Async(call));
        AddSyntheticMember(task, "using", Def(taskInstance, KwArg("priority", Inst("int")), KwArg("queue_name", Inst("str")), KwArg("run_after", PyType.Any), KwArg("backend", Inst("str"))));
        AddSyntheticMember(task, "func", fn);
        return taskInstance;
    }

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
        // a model whose manager is made from a QuerySet of the project (`objects = BookQuerySet.as_manager()`) has that class's methods on every queryset
        var custom = DjangoCustomQuerySetOf(model);
        var qs = NewSyntheticClass(model.Module, "QuerySet", custom == null ? null : new ClassType(custom, false));
        qs.DisplayArgs = model.Name;
        _djangoQuerySets[model] = qs;
        _djangoSyntheticModel[qs] = model;
        AddQuerySetMembers(qs, model, InstanceOfClass(qs));
        return qs;
    }

    static ParamInfo Arg(string name, PyType type, bool hasDefault = false) => new(name, type, ParamCategory.Normal, hasDefault);
    static ParamInfo KwArg(string name, PyType type) => new(name, type, ParamCategory.KeywordOnly, true);
    static ParamInfo StarArgs(string name, PyType type) => new(name, type, ParamCategory.VarPositional, false);
    static ParamInfo StarKwargs(string name = "kwargs") => new(name, PyType.Any, ParamCategory.VarKeyword, false);

    /// <summary>A method with the parameters Django's own def has, shown by name in hovers and signature help.</summary>
    static SignatureType Def(PyType returns, params ParamInfo[] ps) => new(ps, returns, Named: true);

    /// <summary>The methods of QuerySet (and of a manager, which passes them on), each with the parameters of Django's def and what it gives.</summary>
    void AddQuerySetMembers(ClassInfo target, ClassInfo model, PyType self, PyType? rows = null)
    {
        var modelInstance = InstanceOfClass(model);
        // a queryset of values() / values_list() keeps its rows when it is filtered, and gives them back when it is read
        var qsInstance = rows == null ? InstanceOfClass(DjangoQuerySetOf(model)) : self;
        var element = rows ?? modelInstance;
        var optionalModel = PyType.Union(new[] { element, NoneInstance });
        var str = Inst("str");
        var boolean = Inst("bool");
        var integer = Inst("int");
        PyType Optional(PyType t) => PyType.Union(new[] { t, NoneInstance });
        var fieldNames = TypingInst("Sequence", str);
        var defaults = Optional(TypingInst("Mapping", str, PyType.Any));
        var models = TypingInst("Iterable", modelInstance);
        SignatureType Async(SignatureType sync) => sync with { Return = TypingInst("Coroutine", PyType.Any, PyType.Any, sync.Return) };
        void Both(string name, SignatureType sync)
        {
            AddSyntheticMember(target, name, sync);
            AddSyntheticMember(target, "a" + name, Async(sync));
        }

        // filtering, ordering and the rest give the same kind of queryset
        foreach (var name in new[] { "filter", "exclude", "annotate", "alias" }) AddSyntheticMember(target, name, Def(qsInstance, StarArgs("args", PyType.Any), StarKwargs()));
        foreach (var name in new[] { "all", "none", "reverse" }) AddSyntheticMember(target, name, Def(qsInstance));
        AddSyntheticMember(target, "order_by", Def(qsInstance, StarArgs("field_names", PyType.Any)));
        AddSyntheticMember(target, "distinct", Def(qsInstance, StarArgs("field_names", PyType.Any)));
        AddSyntheticMember(target, "select_related", Def(qsInstance, StarArgs("fields", PyType.Any)));
        AddSyntheticMember(target, "prefetch_related", Def(qsInstance, StarArgs("lookups", PyType.Any)));
        foreach (var name in new[] { "defer", "only" }) AddSyntheticMember(target, name, Def(qsInstance, StarArgs("fields", PyType.Any)));
        AddSyntheticMember(target, "using", Def(qsInstance, Arg("alias", Optional(str))));
        AddSyntheticMember(target, "select_for_update", Def(qsInstance, Arg("nowait", boolean, true), Arg("skip_locked", boolean, true), Arg("of", fieldNames, true), Arg("no_key", boolean, true)));
        AddSyntheticMember(target, "extra", Def(qsInstance, Arg("select", PyType.Any, true), Arg("where", PyType.Any, true), Arg("params", PyType.Any, true),
            Arg("tables", PyType.Any, true), Arg("order_by", PyType.Any, true), Arg("select_params", PyType.Any, true)));
        AddSyntheticMember(target, "union", Def(qsInstance, StarArgs("other_qs", PyType.Any), KwArg("all", boolean)));
        foreach (var name in new[] { "intersection", "difference" }) AddSyntheticMember(target, name, Def(qsInstance, StarArgs("other_qs", PyType.Any)));
        AddSyntheticMember(target, "complex_filter", Def(qsInstance, Arg("filter_obj", PyType.Any)));
        // Django 6.1: fetch_mode(FETCH_PEERS) gives the same queryset, fetching related rows its way
        AddSyntheticMember(target, "fetch_mode", Def(qsInstance, Arg("mode", PyType.Any)));

        // the rows themselves; each has its async form, which gives the same once awaited (`await Book.objects.afirst()` is a Book | None)
        Both("get", Def(element, StarArgs("args", PyType.Any), StarKwargs()));
        foreach (var name in new[] { "earliest", "latest" }) Both(name, Def(element, StarArgs("fields", PyType.Any)));
        foreach (var name in new[] { "first", "last" }) Both(name, Def(optionalModel));
        Both("create", Def(modelInstance, StarKwargs()));
        var createdOrNot = TupleOf(new PyType[] { modelInstance, boolean });
        Both("get_or_create", Def(createdOrNot, Arg("defaults", defaults, true), StarKwargs()));
        Both("update_or_create", Def(createdOrNot, Arg("defaults", defaults, true), Arg("create_defaults", defaults, true), StarKwargs()));
        Both("count", Def(integer));
        Both("exists", Def(boolean));
        Both("contains", Def(boolean, Arg("obj", modelInstance)));
        Both("update", Def(integer, StarKwargs()));
        Both("bulk_create", Def(Inst("list", modelInstance), Arg("objs", models), Arg("batch_size", Optional(integer), true), Arg("ignore_conflicts", boolean, true),
            Arg("update_conflicts", boolean, true), Arg("update_fields", Optional(fieldNames), true), Arg("unique_fields", Optional(fieldNames), true)));
        Both("bulk_update", Def(integer, Arg("objs", models), Arg("fields", fieldNames), Arg("batch_size", Optional(integer), true)));
        // what Django documents the rest to give: aggregate() a dict of the names asked for, in_bulk() the rows by key, delete() the counts
        Both("aggregate", Def(Inst("dict", str, PyType.Any), StarArgs("args", PyType.Any), StarKwargs()));
        Both("in_bulk", Def(Inst("dict", PyType.Any, modelInstance), Arg("id_list", Optional(TypingInst("Iterable", PyType.Any)), true), KwArg("field_name", str)));
        Both("delete", Def(TupleOf(new PyType[] { integer, Inst("dict", str, integer) })));
        Both("explain", Def(str, KwArg("format", Optional(str)), StarKwargs("options")));
        AddSyntheticMember(target, "iterator", Def(TypingInst("Iterator", element), Arg("chunk_size", Optional(integer), true)));
        AddSyntheticMember(target, "aiterator", Def(TypingInst("AsyncIterator", element), Arg("chunk_size", integer, true)));
        AddSyntheticMember(target, "__iter__", Def(TypingInst("Iterator", element)));
        AddSyntheticMember(target, "__aiter__", Def(TypingInst("AsyncIterator", element)));
        AddSyntheticMember(target, "__len__", Def(integer));
        AddSyntheticMember(target, "__bool__", Def(boolean));
        AddSyntheticMember(target, "model", new ClassType(model, false));
        // dates() / datetimes(): a queryset of the dates themselves
        var order = Arg("order", str, true);
        AddSyntheticMember(target, "dates", Def(DjangoInstanceOf("datetime", "date") is { } date ? InstanceOfClass(DjangoRowsQuerySetOf(model, date)) : PyType.Unknown,
            Arg("field_name", str), Arg("kind", str), order));
        AddSyntheticMember(target, "datetimes", Def(DjangoInstanceOf("datetime", "datetime") is { } dateTime ? InstanceOfClass(DjangoRowsQuerySetOf(model, dateTime)) : PyType.Unknown,
            Arg("field_name", str), Arg("kind", str), order, Arg("tzinfo", PyType.Any, true)));
        AddSyntheticMember(target, "raw", Def(PyType.Unknown, Arg("raw_query", str), Arg("params", PyType.Any, true), Arg("translations", PyType.Any, true), Arg("using", Optional(str), true)));
        foreach (var name in new[] { "resolve_expression", "db_manager", "check" }) AddSyntheticMember(target, name, Method(PyType.Unknown));
        // what values() and values_list() give depends on the call (DjangoSpecialCall); where it cannot be told it stays unknown
        AddSyntheticMember(target, "values", Def(PyType.Unknown, StarArgs("fields", PyType.Any), StarKwargs("expressions")));
        AddSyntheticMember(target, "values_list", Def(PyType.Unknown, StarArgs("fields", PyType.Any), KwArg("flat", boolean), KwArg("named", boolean)));
        foreach (var name in new[] { "query", "db" }) AddSyntheticMember(target, name, PyType.Unknown);
        // ordered; Django 6.0's totally_ordered (an order that names a unique field, so every row has its place)
        foreach (var name in new[] { "ordered", "totally_ordered" }) AddSyntheticMember(target, name, boolean);
    }

    /// <summary>`Manager[Model]`: the queryset methods too, since a manager passes them on. A custom manager class stays in the chain, so its own methods are found.</summary>
    ClassInfo DjangoManagerOf(ClassInfo model, ClassInfo? baseManager, bool related = false)
    {
        var key = (model, baseManager, related);
        if (_djangoManagers.TryGetValue(key, out var existing)) return existing;
        ClassType? baseType = baseManager == null ? null : new ClassType(baseManager, false);
        // a manager made from a QuerySet class is called as Django calls it: ManagerFromTagQuerySet
        string managerName = baseManager == null ? (related ? "RelatedManager" : "Manager") : IsDjangoQuerySetClass(baseManager) ? "ManagerFrom" + baseManager.Name : baseManager.Name;
        var manager = NewSyntheticClass(model.Module, managerName, baseType);
        manager.DisplayArgs = model.Name;
        _djangoManagers[key] = manager;
        _djangoSyntheticModel[manager] = model;
        AddQuerySetMembers(manager, model, InstanceOfClass(manager));
        AddSyntheticMember(manager, "get_queryset", Def(InstanceOfClass(DjangoQuerySetOf(model))));
        if (related)
        {
            // add(*objs, bulk=True), remove(*objs, bulk=True), clear(), set(objs, *, bulk=True, clear=False) and their async forms (Django 4.2)
            var boolean = Inst("bool");
            var none = NoneInstance;
            var objs = StarArgs("objs", PyType.Any);
            foreach (var (name, sig) in new[]
            {
                ("add", Def(none, objs, KwArg("bulk", boolean), KwArg("through_defaults", PyType.Any))),
                ("remove", Def(none, objs, KwArg("bulk", boolean))),
                ("clear", Def(none, KwArg("bulk", boolean))),
                ("set", Def(none, Arg("objs", TypingInst("Iterable", PyType.Any)), KwArg("bulk", boolean), KwArg("clear", boolean), KwArg("through_defaults", PyType.Any))),
            })
            {
                AddSyntheticMember(manager, name, sig);
                AddSyntheticMember(manager, "a" + name, sig with { Return = TypingInst("Coroutine", PyType.Any, PyType.Any, none) });
            }
        }
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

    /// <summary>The relation field behind a reverse accessor of a model (`author.book_set` gives `Book.author`); null when the name is not one.</summary>
    public DjangoField? DjangoReverseSource(ClassInfo model, string name) =>
        DjangoReverseOf(model).Where(r => r.Name == name).Select(r => r.Source).FirstOrDefault();

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
            // the Options the metaclass sets (`Book._meta.get_field("title")`)
            case "_meta":
                return DjangoInstanceOf("django.db.models.options", "Options") ?? PyType.Unknown;
            case "pk" when instance:
                return DjangoPrimaryKeyType(info);
            case "id" when instance && !DjangoFieldsOf(info).Any(f => f.PrimaryKey || f.Kind == "composite"):
                return Inst("int");
        }

        foreach (var f in DjangoFieldsOf(info))
        {
            if (instance && f.Kind is "foreign" or "one-to-one" && name == f.Name + "_id")
                return f.Target != null ? DjangoPrimaryKeyType(f.Target) : PyType.Unknown;
            if (instance && f.HasChoices && name == $"get_{f.Name}_display") return Def(Inst("str"));
        }
        return DjangoReverseMember(info, name);
    }

    /// <summary>`author.book_set` / a related_name: the related manager of the model that points here; a one-to-one, that model's row.</summary>
    PyType? DjangoReverseMember(ClassInfo info, string name)
    {
        foreach (var (reverse, source) in DjangoReverseOf(info))
            if (reverse == name)
                return source.Kind == "one-to-one" ? InstanceOfClass(source.Owner) : InstanceOfClass(DjangoManagerOf(source.Owner, null, related: true));
        return null;
    }

    /// <summary>A model of the user's code built on django-stubs' Model: stubs leave `objects` to their mypy plugin.</summary>
    bool IsDjangoStubModel(ClassInfo c)
    {
        if (c.Module.IsStub) return false;
        foreach (var entry in Mro(c))
            if (entry is ClassType e && e.Info.Name == "Model" && e.Info.Module.IsStub && e.Info.Module.Name.StartsWith("django.db.models", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// `Address.objects` on a model built on django-stubs: the stubs declare no `objects` (their mypy plugin adds it), only
    /// `_default_manager: ClassVar[Manager[Self]]`, which is what the default `objects` is.
    /// </summary>
    PyType? DjangoStubManager(ClassType model)
    {
        if (!IsDjangoStubModel(model.Info)) return null;
        var hit = FindMember(model, "_default_manager", false, null, out _);
        return hit == null ? null : BoundMember(hit, model, false);
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
            if (!DjangoFieldsOf(info).Any(f => f.PrimaryKey || f.Kind == "composite")) yield return "id";
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
        if (receiverIsInstance && DjangoViewPlaceholder(hit, receiver)) return PyType.Unknown;
        if (hit.ClassSymbol == null || !IsDjangoModel(receiver.Info) || hit.Owner.Info.Name == "Model" && IsDjangoInternal(hit.Owner.Info)) return null;
        if (receiverIsInstance && DjangoFieldOf(hit.Owner.Info, hit.ClassSymbol) is { } field) return DjangoValueType(field);
        // `popular = TagQuerySet.as_manager()`: a manager of this model with the QuerySet's own methods
        if (!receiverIsInstance && TypeOfSymbol(hit.ClassSymbol) is ClassType { IsInstance: true } made && DjangoQuerySetOfManager(made.Info) is { } querySet)
            return InstanceOfClass(DjangoManagerOf(receiver.Info, querySet));
        if (!receiverIsInstance && TypeOfSymbol(hit.ClassSymbol) is ClassType { IsInstance: true } manager && IsDjangoManagerClass(manager.Info))
        {
            // `objects = BookManager()`: the manager of this model, with the custom methods still in reach
            // Django's plain Manager is replaced by the synthetic one; a manager of its own that Django ships (`UserManager`) stays in the chain for its methods
            return InstanceOfClass(DjangoManagerOf(receiver.Info, IsDjangoInternal(manager.Info) && manager.Info.Name is "Manager" or "BaseManager" ? null : manager.Info));
        }
        return null;
    }

    /// <summary>
    /// `action = None` in a view of the project, unannotated and set nowhere else: a placeholder for `as_view(action='my-test')`, which
    /// sets it on each instance (as_view takes only names the class has). Read through self it is whatever the URLconf gives, not None.
    /// </summary>
    bool DjangoViewPlaceholder(MemberHit hit, ClassType receiver)
    {
        if (hit.ClassSymbol is not { } sym || hit.InstanceAttrs.Count > 0 || IsDjangoInternal(hit.Owner.Info) || sym.Declarations.Count == 0) return false;
        var m = hit.Owner.Info.Module;
        if (!sym.Declarations.All(d => d.Kind == DeclKind.Variable && m.ParentOf(d.Node) is AssignStmt { Value: ConstExpr { Kind: ConstKind.None } })) return false;
        return Mro(receiver.Info).Any(e => e is ClassType ec && ec.Info.Name == "View" && ec.Info.Module.Name is "django.views.generic.base" or "django.views.generic" or "django.views");
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
        foreach (var f in DjangoFieldsOf(model))
        {
            if (f.Name != "pk") names.Add(f.Name);
            // filter(author_id=1): a relation's column name is a query name too
            if (f.Kind is "foreign" or "one-to-one") names.Add(f.Name + "_id");
        }
        if (!DjangoFieldsOf(model).Any(f => f.PrimaryKey || f.Kind == "composite")) names.Add("id");
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

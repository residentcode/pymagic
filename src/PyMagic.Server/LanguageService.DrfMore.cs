using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// More of REST framework: what a serializer's methods are given (`instance`, `validated_data`), a writable nested serializer without the
/// `create()` / `update()` DRF asks for, and for the Endpoints view the extras of a DefaultRouter and the URLs of installed apps.
/// </summary>
public sealed partial class LanguageService
{
    // ------------------------------------------------------------ what a serializer's methods are given

    /// <summary>What DRF's value of a declared field class is (CharField a str, ...), by the class's name.</summary>
    static readonly Dictionary<string, string> DrfFieldValues = new(StringComparer.Ordinal)
    {
        ["CharField"] = "str", ["EmailField"] = "str", ["SlugField"] = "str", ["URLField"] = "str", ["RegexField"] = "str", ["ChoiceField"] = "str", ["IPAddressField"] = "str", ["FilePathField"] = "str",
        ["IntegerField"] = "int", ["FloatField"] = "float", ["BooleanField"] = "bool", ["ListField"] = "list", ["MultipleChoiceField"] = "set", ["DictField"] = "dict", ["HStoreField"] = "dict",
    };

    /// <summary>
    /// In a serializer: the `instance` of `to_representation(self, instance)` and `update(self, instance, validated_data)` is a model instance; the
    /// `validated_data` of `create` / `update` and the `attrs` of `validate(self, attrs)` hold the fields the serializer writes.
    /// </summary>
    PyType? SerializerParameterType(Workspace ws, ModuleInfo m, Parameter p)
    {
        if (m.IsStub || p.Name is not ("instance" or "validated_data" or "attrs") || m.ParentOf(p) is not FunctionDef f || m.ParentOf(f) is not ClassDef cd) return null;
        var ev = ws.Evaluator;
        if (ClassInfoOf(m, cd, ev) is not { } info || !IsSerializerClass(ev, info) || IsDjangoInternalModule(info.Module)) return null;
        if (p.Name == "instance" && f.Name is "to_representation" or "update")
            return ev.DjangoMetaModel(info) is { } model ? new ClassType(model, Array.Empty<PyType>(), true) : null;
        if (p.Name == "validated_data" && f.Name is "create" or "update" || p.Name == "attrs" && f.Name == "validate")
        {
            var keys = WrittenFields(ev, info);
            return keys == null ? null : ev.DjangoDataDict(info, "validated_data of " + info.Name, keys);
        }
        return null;
    }

    static bool IsDjangoInternalModule(ModuleInfo m) => m.Name.StartsWith("rest_framework", StringComparison.Ordinal) || m.Name.StartsWith("django.", StringComparison.Ordinal);

    /// <summary>The fields a serializer writes (those of a request body), with the value DRF gives each in validated_data; null when they cannot be told.</summary>
    List<(string Name, PyType Type)>? WrittenFields(TypeEvaluator ev, ClassInfo serializer)
    {
        var written = SerializerFieldParams(ev, serializer, body: true);
        if (written.Count == 0) return null;
        var declared = new Dictionary<string, DeclaredField>(StringComparer.Ordinal);
        foreach (var t in ev.MroOf(serializer).OfType<ClassType>().Reverse())
            if (!t.Info.Module.IsStub && !IsDjangoInternalModule(t.Info.Module))
                foreach (var d in DeclaredFieldsOf(t.Info.Module, t.Info.Node, ev)) declared[d.Name] = d;
        var model = ev.DjangoMetaModel(serializer);
        var keys = new List<(string, PyType)>();
        foreach (var w in written)
        {
            PyType type = PyType.Unknown;
            if (declared.TryGetValue(w.Name, out var d))
            {
                if (d.Nested != null) type = d.Many ? ev.BuiltinInstance("list") ?? PyType.Unknown : ev.BuiltinInstance("dict") ?? PyType.Unknown;
                else if (DrfFieldValues.TryGetValue(d.FieldClass, out var kind)) type = ev.BuiltinInstance(kind) ?? PyType.Unknown;
            }
            else if (model != null && ev.DjangoFieldNamed(model, w.Name) is { } field) type = field.Kind == "many-to-many" ? ev.BuiltinInstance("list") ?? PyType.Unknown : ev.DjangoFieldValue(field);
            keys.Add((w.Name, type));
        }
        return keys;
    }

    /// <summary>
    /// A nested serializer that is written to (not `read_only=True`) on a ModelSerializer without its own `create()` / `update()`: DRF's raise
    /// AssertionError ("The `.create()` method does not support writable nested fields by default").
    /// </summary>
    IEnumerable<Finding> WritableNestedFindings(ModuleInfo m, ClassDef cd, ClassInfo info, TypeEvaluator ev, List<DeclaredField> declared)
    {
        // only a serializer something writes with: a serializer that is only read may nest as it likes
        if (!OnlyProjectBases(ev, info) || !declared.Any(f => f.Nested != null && !f.ReadOnly) || !WrittenSerializers().Contains(info.Module.Name + "." + info.Name)) yield break;
        var own = ev.DjangoOwnNames(info).ToHashSet(StringComparer.Ordinal);
        var missing = new[] { "create", "update" }.Where(x => !own.Contains(x)).ToList();
        if (missing.Count == 0) yield break;
        foreach (var f in declared.Where(f => f.Nested != null && !f.ReadOnly))
            yield return new Finding(f.NameStart, f.Name.Length,
                $"'{f.Name}' is a writable nested serializer and {info.Name} has no {string.Join(" or ", missing.Select(x => x + "()"))}: ModelSerializer's own raise AssertionError (\"The `.{missing[0]}()` method does not support writable nested fields by default\") when it saves; write {(missing.Count > 1 ? "them" : "it")}, or make the field read_only=True",
                "drf-serializer-field", FindingLevel.Warning);
    }

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<Snapshot, HashSet<string>> _writtenSerializers = new();

    /// <summary>
    /// The serializers the project writes with: those of a view that creates or updates (a ModelViewSet, a create or update mixin or generic view, a
    /// `post` / `put` / `patch` of its own), and those made with `data=` (`BookSerializer(data=request.data)`), whose save() may create.
    /// </summary>
    /// <remarks>Read from the whole project (the view that writes may be in a file that is not open), as module-qualified class names.</remarks>
    HashSet<string> WrittenSerializers() => _writtenSerializers.GetValue(Full(), snap =>
    {
        var written = new HashSet<string>(StringComparer.Ordinal);
        var e = snap.Workspace.Evaluator;
        void Add(ClassInfo c) => written.Add(c.Module.Name + "." + c.Name);
        foreach (var module in snap.Project)
        {
            if (module.IsStub || module.Source.IndexOf("Serializer", StringComparison.Ordinal) < 0) continue;
            foreach (var call in AstChildren.Descendants(module.Ast).OfType<CallExpr>())
                if (call.Args.Any(a => a.Name == "data") && e.TypeOf(module, call.Func) is ClassType { IsInstance: false } made && IsSerializerClass(e, made.Info)) Add(made.Info);
            foreach (var cd in AstChildren.Descendants(module.Ast).OfType<ClassDef>())
            {
                if (ClassInfoOf(module, cd, e) is not { } view || !IsRestFramework(e, view)) continue;
                var names = BaseNamesOf(e, view);
                bool writes = names.Any(n => n is "ModelViewSet" or "CreateModelMixin" or "UpdateModelMixin" || GenericMethods.TryGetValue(n, out var verbs) && verbs.Any(v => v is "POST" or "PUT" or "PATCH"))
                    || HandlerMethodsOf(e, view).Any(v => v is "POST" or "PUT" or "PATCH")
                    || e.MroOf(view).OfType<ClassType>().Any(t => !t.Info.Module.IsStub && t.Info.Node.Body.OfType<FunctionDef>().Any(f => f.Name is "create" or "update" or "partial_update"));
                if (!writes) continue;
                foreach (var (cls, _, _) in SerializersOf(e, view).All) Add(cls);
                if (ClassAttribute(e, view, "serializer_class") is { } attr && e.TypeOf(attr.Module, attr.Value) is ClassType { IsInstance: false } sc) Add(sc.Info);
            }
        }
        return written;
    });

    // ------------------------------------------------------------ routers: the DefaultRouter's extras

    /// <summary>How a router was made: `DefaultRouter(trailing_slash=False)`, with the API root and the format suffixes a DefaultRouter adds.</summary>
    sealed record RouterKind(bool Default, bool TrailingSlash, bool RootView, bool FormatSuffixes, ModuleInfo Module, int At);

    static RouterKind? RouterKindOf(ModuleInfo module, string name)
    {
        var made = module.Ast.Body.OfType<AssignStmt>().Select(a => a.Targets.Count == 1 && a.Targets[0] is NameExpr t && t.Name == name && a.Value is CallExpr c ? c : null).FirstOrDefault(c => c != null);
        if (made == null || CalleeName(made) is not { } callee) return null;
        bool Arg(string key, bool fallback) => made.Args.FirstOrDefault(a => a.Name == key)?.Value switch
        {
            ConstExpr { Kind: ConstKind.True } => true,
            ConstExpr { Kind: ConstKind.False } => false,
            _ => fallback,
        };
        bool isDefault = callee.Contains("DefaultRouter", StringComparison.Ordinal);
        return new RouterKind(isDefault, Arg("trailing_slash", true), isDefault && Arg("include_root_view", true), isDefault && Arg("include_format_suffixes", true), module, made.Start);
    }

    // ------------------------------------------------------------ URLs of installed apps

    Workspace? _routesWorkspace;

    /// <summary>`include("allauth.urls")`: the urls module of an installed app, when it can be read (not a stub) and has url patterns.</summary>
    ModuleInfo? LibraryUrls(string name) =>
        _routesWorkspace?.GetModule(name) is { IsStub: false } library && library.Source.Contains("urlpatterns", StringComparison.Ordinal) ? library : null;

    /// <summary>
    /// A view written as a name the module assigns or imports (`obtain_auth_token = ObtainAuthToken.as_view()`, `from rest_framework.authtoken.views
    /// import obtain_auth_token`, `views.login`): the class whose as_view() it is, with its module.
    /// </summary>
    (ClassInfo Class, ModuleInfo Module)? AsViewClassOf(TypeEvaluator ev, ModuleInfo m, Expr view, int depth = 0)
    {
        if (depth > 4) return null;
        string? name = null;
        ModuleInfo? where = m;
        switch (view)
        {
            case NameExpr n: name = n.Name; break;
            case AttributeExpr { Value: var owner, Attr: var attr } when ev.TypeOf(m, owner) is ModuleType { Module: var target }:
                name = attr; where = target; break;
        }
        if (name == null || where == null) return null;
        foreach (var st in where.Ast.Body)
        {
            if (st is AssignStmt { Targets: [NameExpr t], Value: CallExpr { Func: AttributeExpr { Attr: "as_view", Value: var classRef } } } && t.Name == name
                && ev.TypeOf(where, classRef) is ClassType { IsInstance: false } cls)
                return (cls.Info, where);
            if (st is ImportFromStmt imp && imp.Names.FirstOrDefault(a => (a.AsName ?? a.Name) == name) is { } alias && imp.Module != null)
            {
                string from = imp.Level == 0 ? imp.Module : string.Join('.', where.Name.Split('.').SkipLast(imp.Level).Append(imp.Module));
                if (_routesWorkspace?.GetModule(from) is { } source) return AsViewClassOf(ev, source, new NameExpr(alias.Name), depth + 1);
            }
        }
        return null;
    }
}

using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// REST framework serializers beyond the names in Meta: the fields a serializer declares (nested serializers among them) checked against the model and the Meta,
/// and the serializers a view picks in `get_serializer_class`, for the endpoints and the view checks.
/// </summary>
public sealed partial class LanguageService
{
    // ------------------------------------------------------------ the fields a serializer declares

    /// <summary>`author = AuthorSerializer(read_only=True)` or `title = serializers.CharField(source="name")` in the body of a serializer.</summary>
    sealed record DeclaredField(string Name, int NameStart, CallExpr Call, string FieldClass, ClassInfo? Nested, bool Many, string? Source, int SourceStart, bool ReadOnly, bool WriteOnly, bool Required);

    static readonly HashSet<string> NotReadFromTheObject = new(StringComparer.Ordinal) { "SerializerMethodField", "HiddenField", "CurrentUserDefault" };
    static readonly HashSet<string> AlwaysReadOnly = new(StringComparer.Ordinal) { "ReadOnlyField", "SerializerMethodField", "StringRelatedField", "HyperlinkedIdentityField" };

    /// <summary>True when a class is a field or serializer of the REST framework, or (the library not being installed) is named like one.</summary>
    static bool IsDrfFieldClass(TypeEvaluator ev, ClassInfo c) =>
        ev.MroOf(c).OfType<ClassType>().Any(t => t.Info.Name is "Field" or "BaseSerializer" && t.Info.Module.Name.StartsWith("rest_framework", StringComparison.Ordinal))
        || ev.DjangoRoleOf(c) == DjangoClassRole.Serializer;

    static bool IsSerializerClass(TypeEvaluator ev, ClassInfo c) =>
        ev.DjangoRoleOf(c) == DjangoClassRole.Serializer || ev.MroOf(c).OfType<ClassType>().Any(t => t.Info.Name == "BaseSerializer" && t.Info.Module.Name.StartsWith("rest_framework", StringComparison.Ordinal))
        || !c.Module.IsStub && c.Name.EndsWith("Serializer", StringComparison.Ordinal) && !c.Module.Name.StartsWith("rest_framework", StringComparison.Ordinal);

    static bool Flag(CallExpr call, string name) => call.Args.FirstOrDefault(a => a.Name == name)?.Value is ConstExpr { Kind: ConstKind.True };

    /// <summary>The fields one serializer class declares in its own body (not those of its bases).</summary>
    static List<DeclaredField> DeclaredFieldsOf(ModuleInfo m, ClassDef cd, TypeEvaluator ev)
    {
        var fields = new List<DeclaredField>();
        foreach (var stmt in cd.Body.OfType<AssignStmt>())
        {
            if (stmt.Targets.Count != 1 || stmt.Targets[0] is not NameExpr target || stmt.Value is not CallExpr call) continue;
            string callee = SourceText(m, call.Func);
            string fieldClass = LastSegment(callee);
            ClassInfo? cls = ev.TypeOf(m, call.Func) is ClassType { IsInstance: false } ct ? ct.Info : null;
            bool known = cls != null && IsDrfFieldClass(ev, cls);
            bool named = cls == null && (callee.StartsWith("serializers.", StringComparison.Ordinal) || fieldClass.EndsWith("Field", StringComparison.Ordinal) || fieldClass.EndsWith("Serializer", StringComparison.Ordinal));
            if (!known && !named && !(cls != null && IsSerializerClass(ev, cls))) continue;
            ClassInfo? nested = cls != null && IsSerializerClass(ev, cls) ? cls : null;
            var sourceArg = call.Args.FirstOrDefault(a => a.Name == "source")?.Value;
            string? source = FixtureModel.StringValue(sourceArg);
            int sourceStart = sourceArg is ConstExpr sourceText && FixtureModel.StringSpan(sourceText) is { } span ? span.Start : target.Start;
            bool readOnly = Flag(call, "read_only") || AlwaysReadOnly.Contains(fieldClass);
            bool writeOnly = Flag(call, "write_only");
            bool required = !readOnly && call.Args.FirstOrDefault(a => a.Name == "required")?.Value is not ConstExpr { Kind: ConstKind.False } && !call.Args.Any(a => a.Name == "default");
            fields.Add(new DeclaredField(target.Name, target.Start, call, fieldClass, nested, Flag(call, "many"), source, sourceStart, readOnly, writeOnly, required));
        }
        return fields;
    }

    /// <summary>The strings of `fields` / `exclude` / `read_only_fields` of a Meta, or null when it is not a list of strings (or `"__all__"`).</summary>
    static List<string>? MetaList(ClassDef meta, string name)
    {
        var stmt = meta.Body.OfType<AssignStmt>().FirstOrDefault(a => a.Targets.Count == 1 && a.Targets[0] is NameExpr n && n.Name == name);
        var items = stmt?.Value switch { ListExpr l => l.Items, TupleExpr t => t.Items, _ => null };
        if (items == null) return null;
        var names = items.Select(i => FixtureModel.StringValue(i)).ToList();
        return names.Any(n => n == null) ? null : names!;
    }

    /// <summary>What an attribute of a model instance is: the model it reaches and whether it holds many (a manager), or neither; null when the model has no such attribute.</summary>
    static (ClassInfo? Target, bool Many, PyType Type)? ModelAttribute(TypeEvaluator ev, ClassInfo model, string name)
    {
        var r = ev.AttributeType(new ClassType(model, Array.Empty<PyType>(), true), name);
        if (r.Missing.Count > 0 && r.Type is UnknownType) return null;
        if (ev.DjangoModelOfQuery(r.Type) is { } many) return (many, true, r.Type);
        if (r.Type is ClassType { IsInstance: true } one && ev.IsDjangoModel(one.Info)) return (one.Info, false, r.Type);
        var field = ev.DjangoFieldNamed(model, name);
        if (field?.Target != null) return (field.Target, field.Kind == "many-to-many", r.Type);
        return (null, false, r.Type);
    }

    // ------------------------------------------------------------ checks

    IEnumerable<Diag> SerializerDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub || m.Source.IndexOf("Serializer", StringComparison.Ordinal) < 0 || m.Source.IndexOf("rest_framework", StringComparison.Ordinal) < 0) yield break;
        var findings = new List<Finding>();
        foreach (var cd in AstChildren.Descendants(m.Ast).OfType<ClassDef>().ToList())
        {
            if (ClassInfoOf(m, cd, ev) is not { } info || ev.DjangoRoleOf(info) != DjangoClassRole.Serializer) continue;
            var declared = DeclaredFieldsOf(m, cd, ev);
            if (declared.Count == 0) continue;
            var model = ev.DjangoMetaModel(info);
            var meta = ev.DjangoMetaOf(info);
            void Warn(int start, int length, string message) => findings.Add(new Finding(start, length, message, "drf-serializer-field", FindingLevel.Warning));

            // DRF asserts that a field declared here is listed in fields, and not in exclude
            if (meta is { } mt)
            {
                var listed = MetaList(mt.Meta, "fields");
                var excluded = MetaList(mt.Meta, "exclude");
                foreach (var f in declared)
                {
                    if (listed != null && !listed.Contains(f.Name))
                        Warn(f.NameStart, f.Name.Length, $"'{f.Name}' is declared on {info.Name} but not listed in Meta.fields: DRF stops with \"The field '{f.Name}' was declared on serializer {info.Name}, but has not been included in the 'fields' option\"");
                    if (excluded != null && excluded.Contains(f.Name))
                        Warn(f.NameStart, f.Name.Length, $"'{f.Name}' is declared on {info.Name} and listed in Meta.exclude: DRF stops with \"Cannot both declare the field '{f.Name}' and include it in the 'exclude' option\"");
                }
            }

            foreach (var f in declared)
            {
                // a method field needs its method on the serializer
                if (f.FieldClass == "SerializerMethodField")
                {
                    string method = FixtureModel.StringValue(f.Call.Args.FirstOrDefault(a => a.Name == "method_name")?.Value ?? f.Call.Args.FirstOrDefault(a => a.Name == null)?.Value) ?? "get_" + f.Name;
                    if (!ev.DjangoOwnNames(info).Contains(method) && OnlyProjectBases(ev, info))
                        Warn(f.NameStart, f.Name.Length, $"'{f.Name}' is a SerializerMethodField and {info.Name} has no method '{method}': DRF raises AttributeError when it serializes");
                    continue;
                }
                if (model == null || f.WriteOnly || NotReadFromTheObject.Contains(f.FieldClass) || f.Source == "*") continue;

                // what DRF reads from the object: the source path, else the name
                string path = f.Source ?? f.Name;
                var segments = path.Split('.');
                ClassInfo? current = model;
                (ClassInfo? Target, bool Many, PyType Type)? last = null;
                int offset = f.Source != null ? f.SourceStart : f.NameStart;
                bool stopped = false;
                for (int i = 0; i < segments.Length && current != null; i++)
                {
                    var attr = ModelAttribute(ev, current, segments[i]);
                    if (attr == null)
                    {
                        string hint = f.Nested != null && ev.DjangoMetaModel(f.Nested) is { } wanted && RelationTo(ev, current, wanted) is { } accessor ? $"; to read the {wanted.Name}s of {("AEIOU".Contains(current.Name[0]) ? "an" : "a")} {current.Name}, write source='{accessor}'" : "";
                        Warn(offset, f.Source != null ? f.Source.Length : f.Name.Length, $"{current.Name} has no attribute '{segments[i]}': DRF raises AttributeError when it reads '{f.Name}'{hint}");
                        stopped = true;
                        break;
                    }
                    last = attr;
                    current = i < segments.Length - 1 ? (attr.Value.Target ?? (attr.Value.Type is ClassType { IsInstance: true } c && ev.IsDjangoModel(c.Info) ? c.Info : null)) : null;
                    if (i < segments.Length - 1 && current == null) { stopped = true; break; }
                }
                if (stopped || last is not { } end || end.Target == null) continue;

                // a nested serializer, or a related field: one or many, and for which model
                bool related = f.Nested != null || f.FieldClass.EndsWith("RelatedField", StringComparison.Ordinal);
                if (!related) continue;
                if (end.Many && !f.Many)
                    Warn(f.NameStart, f.Name.Length, $"'{f.Name}' holds many {end.Target.Name}s: give it many=True, without it DRF serializes the manager instead of each {end.Target.Name}");
                else if (!end.Many && f.Many)
                    Warn(f.NameStart, f.Name.Length, $"'{f.Name}' is one {end.Target.Name}: many=True makes DRF iterate over it and fail with \"'{end.Target.Name}' object is not iterable\"");
                if (f.Nested != null && ev.DjangoMetaModel(f.Nested) is { } serves && serves != end.Target && !IsSubclass(ev, end.Target, serves))
                    Warn(f.Call.Func.Start, f.Call.Func.End - f.Call.Func.Start, $"{f.Nested.Name} is for {serves.Name}, but '{f.Name}' gives {end.Target.Name}s: DRF raises AttributeError for the fields {end.Target.Name} does not have");
            }
        }
        foreach (var f in findings) if (!Suppressed(m.Source, f)) yield return AsDiag(f);
    }

    static bool IsSubclass(TypeEvaluator ev, ClassInfo c, ClassInfo of) => ev.MroOf(c).OfType<ClassType>().Any(t => t.Info == of);

    /// <summary>True when every base of a serializer that could hold methods is the project's or the REST framework's (a mixin from elsewhere may bring the method).</summary>
    static bool OnlyProjectBases(TypeEvaluator ev, ClassInfo c) =>
        ev.MroOf(c).OfType<ClassType>().All(t => !t.Info.Module.IsStub || t.Info.Name == "object");

    /// <summary>The attribute of a model that reaches another model (a field or a reverse accessor), for a hint.</summary>
    static string? RelationTo(TypeEvaluator ev, ClassInfo model, ClassInfo target)
    {
        foreach (var f in ev.DjangoFieldsOf(model)) if (f.Target == target) return f.Name;
        foreach (var name in ev.MemberNames(new ClassType(model, Array.Empty<PyType>(), true)).Select(x => x.Name))
            if (name.EndsWith("_set", StringComparison.Ordinal) || ev.DjangoRelatedModel(model, name) == target)
                if (ModelAttribute(ev, model, name) is { Target: var t } && t == target) return name;
        return null;
    }

    // ------------------------------------------------------------ the serializers a view picks

    /// <summary>
    /// What `get_serializer_class` returns: for an action (`if self.action == "list"`), for a request method (`if self.request.method == "POST"`), by default,
    /// and the returns under conditions the editor cannot read (possible for every request). `serializer_class` when the view has no such method.
    /// </summary>
    sealed class SerializerChoice
    {
        public Dictionary<string, List<(ClassInfo Class, ModuleInfo Module, Expr At)>> ByAction { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<(ClassInfo Class, ModuleInfo Module, Expr At)>> ByMethod { get; } = new(StringComparer.Ordinal);
        public List<(ClassInfo Class, ModuleInfo Module, Expr At)> Default { get; } = new();
        public List<(ClassInfo Class, ModuleInfo Module, Expr At)> Other { get; } = new();

        public IEnumerable<(ClassInfo Class, ModuleInfo Module, Expr At)> All => ByAction.Values.SelectMany(x => x).Concat(ByMethod.Values.SelectMany(x => x)).Concat(Default).Concat(Other);

        /// <summary>The serializers an action answered with this method may use, as "A" or "A | B".</summary>
        public string? For(string? action, string method)
        {
            var picked = action != null && ByAction.TryGetValue(action, out var a) ? a : ByMethod.TryGetValue(method, out var b) ? b : Default;
            // in the order the code returns them
            var names = picked.Concat(Other).OrderBy(x => x.At.Start).Select(x => x.Class.Name).Distinct().ToList();
            return names.Count == 0 ? null : string.Join(" | ", names);
        }
    }

    SerializerChoice SerializersOf(TypeEvaluator ev, ClassInfo view)
    {
        var choice = new SerializerChoice();
        foreach (var t in ev.MroOf(view).OfType<ClassType>())
        {
            if (t.Info.Module.IsStub || t.Info.Module.Name.StartsWith("rest_framework", StringComparison.Ordinal)) continue;
            if (t.Info.Node.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "get_serializer_class") is not { } getter) continue;
            Walk(t.Info.Module, getter.Body, null, null);
            if (choice.All.Any()) return choice;
            break;
        }
        if (ClassAttribute(ev, view, "serializer_class") is { } attr && ev.TypeOf(attr.Module, attr.Value) is ClassType { IsInstance: false } sc)
            choice.Default.Add((sc.Info, attr.Module, attr.Value));
        return choice;

        void Walk(ModuleInfo m, IReadOnlyList<Stmt> body, List<string>? actions, List<string>? methods)
        {
            foreach (var stmt in body)
            {
                switch (stmt)
                {
                    case IfStmt ifs:
                    {
                        var (a, mth, readable) = Condition(ifs.Test);
                        if (readable) Walk(m, ifs.Body, a.Count > 0 ? a : actions, mth.Count > 0 ? mth : methods);
                        else WalkUnknown(m, ifs.Body);
                        Walk(m, ifs.Orelse, actions, methods);
                        break;
                    }
                    case ReturnStmt { Value: { } value }:
                        foreach (var (cls, at) in Returned(m, value))
                        {
                            var item = (cls, m, at);
                            if (actions != null) foreach (var a in actions) Bucket(choice.ByAction, a).Add(item);
                            else if (methods != null) foreach (var x in methods) Bucket(choice.ByMethod, x).Add(item);
                            else choice.Default.Add(item);
                        }
                        // self.serializer_classes.get(self.action, Default): the keys of that dict are actions
                        if (value is CallExpr { Func: AttributeExpr { Attr: "get", Value: AttributeExpr { Attr: var dictName } } } get && get.Args.FirstOrDefault()?.Value is AttributeExpr { Attr: "action" }
                            && ClassAttribute(ev, view, dictName) is { Value: DictExpr dict } holder)
                            for (int i = 0; i < dict.Keys.Count; i++)
                                if (FixtureModel.StringValue(dict.Keys[i]) is { } key && ev.TypeOf(holder.Module, dict.Values[i]) is ClassType { IsInstance: false } kc)
                                    Bucket(choice.ByAction, key).Add((kc.Info, holder.Module, dict.Values[i]));
                        break;
                    case ReturnStmt:
                        break;
                    default:
                        // with, try, for, while: their returns may happen for any request
                        WalkUnknown(m, AstChildren.Descendants(stmt).OfType<ReturnStmt>().Select(r => (Stmt)r).ToList());
                        break;
                }
            }
        }

        void WalkUnknown(ModuleInfo m, IReadOnlyList<Stmt> body)
        {
            foreach (var r in body.SelectMany(s => s is ReturnStmt rs ? new[] { rs } : AstChildren.Descendants(s).OfType<ReturnStmt>()))
                if (r.Value != null) foreach (var (cls, at) in Returned(m, r.Value)) choice.Other.Add((cls, m, at));
        }

        IEnumerable<(ClassInfo, Expr)> Returned(ModuleInfo m, Expr value)
        {
            // super().get_serializer_class() and self.serializer_class: the attribute
            if (value is CallExpr { Func: AttributeExpr { Attr: "get_serializer_class" } } || value is AttributeExpr { Attr: "serializer_class" })
            {
                if (ClassAttribute(ev, view, "serializer_class") is { } attr && ev.TypeOf(attr.Module, attr.Value) is ClassType { IsInstance: false } sc) yield return (sc.Info, value);
                yield break;
            }
            if (value is CallExpr { Func: AttributeExpr { Attr: "get" } } get)
            {
                var fallback = get.Args.Skip(1).FirstOrDefault()?.Value;
                if (fallback != null) foreach (var x in Returned(m, fallback)) yield return x;
                yield break;
            }
            if (ev.TypeOf(m, value) is ClassType { IsInstance: false } c && IsSerializerClass(ev, c.Info)) yield return (c.Info, value);
        }

        static List<(ClassInfo, ModuleInfo, Expr)> Bucket(Dictionary<string, List<(ClassInfo, ModuleInfo, Expr)>> map, string key) =>
            map.TryGetValue(key, out var list) ? list : map[key] = new();
    }

    /// <summary>`self.action == "list"`, `self.action in ("list", "retrieve")`, `self.request.method == "POST"`, joined with `or`; anything else is not readable.</summary>
    static (List<string> Actions, List<string> Methods, bool Readable) Condition(Expr test)
    {
        var actions = new List<string>();
        var methods = new List<string>();
        bool Read(Expr e)
        {
            if (e is BinaryExpr { Op: "or" } or) return Read(or.Left) && Read(or.Right);
            if (e is not CompareExpr { Ops.Count: 1 } cmp) return false;
            var (subject, other) = IsSubject(cmp.Left) ? (cmp.Left, cmp.Comparators[0]) : IsSubject(cmp.Comparators[0]) && cmp.Ops[0] == "==" ? (cmp.Comparators[0], cmp.Left) : (null, null);
            if (subject == null || other == null) return false;
            var into = subject is AttributeExpr { Attr: "action" } ? actions : methods;
            var values = cmp.Ops[0] switch
            {
                "==" => new[] { FixtureModel.StringValue(other) },
                "in" => (other switch { ListExpr l => l.Items, TupleExpr t => t.Items, SetExpr s => s.Items, _ => Array.Empty<Expr>() }).Select(FixtureModel.StringValue).ToArray(),
                _ => Array.Empty<string?>(),
            };
            if (values.Length == 0 || values.Any(v => v == null)) return false;
            into.AddRange(values.Select(v => into == methods ? v!.ToUpperInvariant() : v!));
            return true;
        }
        static bool IsSubject(Expr e) => e is AttributeExpr { Attr: "action", Value: NameExpr { Name: "self" } } || e is AttributeExpr { Attr: "method", Value: AttributeExpr { Attr: "request", Value: NameExpr { Name: "self" } } };
        bool ok = Read(test);
        return (actions, methods, ok && (actions.Count > 0) != (methods.Count > 0));
    }

    /// <summary>The serializers of `get_serializer_class` that are for another model than the view's queryset.</summary>
    IEnumerable<Diag> SerializerClassDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub || m.Source.IndexOf("get_serializer_class", StringComparison.Ordinal) < 0 || m.Source.IndexOf("rest_framework", StringComparison.Ordinal) < 0) yield break;
        foreach (var cd in AstChildren.Descendants(m.Ast).OfType<ClassDef>().ToList())
        {
            if (!cd.Body.OfType<FunctionDef>().Any(f => f.Name == "get_serializer_class") || ClassInfoOf(m, cd, ev) is not { } info || ev.DjangoRoleOf(info) != DjangoClassRole.View) continue;
            if (ev.DjangoViewParts(info).QueryModel is not { } query) continue;
            foreach (var (cls, module, at) in SerializersOf(ev, info).All.Distinct())
            {
                if (!ReferenceEquals(module, m) || ev.DjangoMetaModel(cls) is not { } serves || serves == query) continue;
                var finding = new Finding(at.Start, at.End - at.Start, $"{cls.Name} is for {serves.Name}, but the queryset of {info.Name} is {query.Name}", "django-view-serializer", FindingLevel.Warning);
                if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
            }
        }
    }

    // ------------------------------------------------------------ the fields of a serializer for the Endpoints tab

    /// <summary>
    /// The fields a serializer reads or writes, in the order of Meta.fields: declared fields with their class (a nested serializer with its own fields one level
    /// deeper), and the model's fields for the names it lists (or all of them for "__all__"). For a request body the read-only ones are left out.
    /// </summary>
    List<RouteParam> SerializerFieldParams(TypeEvaluator ev, ClassInfo serializer, bool body, int depth = 0)
    {
        var result = new List<RouteParam>();
        if (depth > 3) return result;
        var declared = new Dictionary<string, DeclaredField>(StringComparer.Ordinal);
        foreach (var t in ev.MroOf(serializer).OfType<ClassType>().Reverse())
            if (!t.Info.Module.IsStub && !t.Info.Module.Name.StartsWith("rest_framework", StringComparison.Ordinal))
                foreach (var f in DeclaredFieldsOf(t.Info.Module, t.Info.Node, ev)) declared[f.Name] = f;
        var model = ev.DjangoMetaModel(serializer);
        var meta = ev.DjangoMetaOf(serializer);
        var readOnlyFields = meta is { } mt ? MetaList(mt.Meta, "read_only_fields") ?? new List<string>() : new List<string>();
        List<string> names;
        if (meta is { } m2 && MetaList(m2.Meta, "fields") is { } listed) names = listed;
        else
        {
            names = model == null ? new List<string>() : ev.DjangoFieldsOf(model).Select(f => f.Name).ToList();
            if (model != null && !ev.DjangoFieldsOf(model).Any(f => f.PrimaryKey)) names.Insert(0, "id");
            if (meta is { } m3 && MetaList(m3.Meta, "exclude") is { } excluded) names.RemoveAll(excluded.Contains);
            names.AddRange(declared.Keys.Where(k => !names.Contains(k)));
        }
        foreach (var name in names)
        {
            if (declared.TryGetValue(name, out var d))
            {
                if (body && d.ReadOnly || !body && d.WriteOnly) continue;
                List<RouteParam>? children = d.Nested != null ? SerializerFieldParams(ev, d.Nested, body, depth + 1) : null;
                string type = d.Nested != null ? (d.Many ? $"list[{d.Nested.Name}]" : d.Nested.Name) : d.FieldClass;
                result.Add(new RouteParam(name, "field", type, body && d.Required, d.ReadOnly ? "read-only" : d.WriteOnly ? "write-only" : d.Source != null ? "source " + d.Source : null, children is { Count: > 0 } ? children : null));
                continue;
            }
            var field = model == null ? null : ev.DjangoFieldNamed(model, name);
            bool readOnly = readOnlyFields.Contains(name) || name is "id" or "pk" || field != null && (field.PrimaryKey || field.FieldClass.Name.Contains("AutoField", StringComparison.Ordinal)
                || field.Call.Args.Any(a => a.Name is "auto_now" or "auto_now_add" && a.Value is ConstExpr { Kind: ConstKind.True } || a.Name == "editable" && a.Value is ConstExpr { Kind: ConstKind.False }));
            if (field == null && model != null && name is not ("id" or "pk")) readOnly = true;   // a property or method of the model
            // Meta.depth: a relation is the related object, nested and read-only, so a request body cannot set it (DRF drops it)
            if (field?.Target != null && body && MetaDepth(ev, serializer) > 0) continue;
            if (body && readOnly) continue;
            string? fieldType = field == null ? null : field.Target != null ? (field.Kind == "many-to-many" ? $"list[pk of {field.Target.Name}]" : $"pk of {field.Target.Name}") : field.FieldClass.Name;
            bool required = body && field != null && !field.Nullable && !field.Call.Args.Any(a => a.Name is "default" || a.Name == "blank" && a.Value is ConstExpr { Kind: ConstKind.True });
            result.Add(new RouteParam(name, "field", fieldType, required, readOnly ? "read-only" : null));
        }
        return result;
    }
}

using System.Runtime.CompilerServices;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>Field names written as strings in the places Django reads them: model Meta, model forms, REST framework serializers and the admin.</summary>
public sealed partial class LanguageService
{
    /// <summary>How a string is read: a field name, or a path across relations (with lookups when the field is searched).</summary>
    sealed record MetaRule(string Mode, bool Lenient);

    sealed record MetaContext(ClassInfo Model, ClassInfo Owner, DjangoClassRole Role, string Attr, MetaRule Rule);

    static readonly HashSet<string> FormKeyAttributes = new(StringComparer.Ordinal) { "labels", "widgets", "help_texts", "error_messages", "field_classes", "extra_kwargs" };

    static MetaRule? RuleFor(DjangoClassRole role, bool inMeta, string attr)
    {
        switch (role)
        {
            case DjangoClassRole.Model when inMeta:
                return attr switch
                {
                    "ordering" or "get_latest_by" => new MetaRule("path", false),
                    "unique_together" or "index_together" or "order_with_respect_to" => new MetaRule("field", false),
                    _ => null,
                };
            case DjangoClassRole.Form when inMeta:
                return attr is "fields" or "exclude" ? new MetaRule("field", true) : FormKeyAttributes.Contains(attr) ? new MetaRule("field", true) : null;
            case DjangoClassRole.Serializer when inMeta:
                return attr is "fields" or "exclude" or "read_only_fields" ? new MetaRule("field", true) : FormKeyAttributes.Contains(attr) ? new MetaRule("field", true) : null;
            case DjangoClassRole.View when !inMeta:
                return attr switch
                {
                    "filterset_fields" or "ordering" => new MetaRule("path", false),
                    "search_fields" => new MetaRule("lookup", false),
                    "ordering_fields" => new MetaRule("field", true),
                    "lookup_field" => new MetaRule("field", false),
                    _ => null,
                };
            case DjangoClassRole.Admin or DjangoClassRole.Inline when !inMeta:
                return attr switch
                {
                    "list_display" or "list_display_links" or "readonly_fields" or "fields" or "exclude" or "fieldsets" or "add_fieldsets" or "sortable_by" => new MetaRule("field", true),
                    "raw_id_fields" or "autocomplete_fields" or "filter_horizontal" or "filter_vertical" or "list_editable" or "date_hierarchy" or "prepopulated_fields" => new MetaRule("field", false),
                    "list_filter" or "ordering" or "list_select_related" => new MetaRule("path", false),
                    "search_fields" => new MetaRule("lookup", false),
                    _ => null,
                };
        }
        return null;
    }

    // ------------------------------------------------------------ which model an admin class is for

    readonly ConditionalWeakTable<Snapshot, Dictionary<ClassInfo, ClassInfo>> _adminModels = new();

    Dictionary<ClassInfo, ClassInfo> AdminModelsOf(Snapshot snap) => _adminModels.GetValue(snap, s => AdminRegistry(s.Project, s.Workspace.Evaluator));

    /// <summary>`@admin.register(Book)` and `admin.site.register(Book, BookAdmin)`: the model each admin class is registered for.</summary>
    static Dictionary<ClassInfo, ClassInfo> AdminRegistry(IEnumerable<ModuleInfo> modules, TypeEvaluator ev)
    {
        var map = new Dictionary<ClassInfo, ClassInfo>();
        foreach (var m in modules.ToList())
        {
            if (m.IsStub || m.Source.IndexOf("register", StringComparison.Ordinal) < 0) continue;
            ClassInfo? ModelOf(Expr e) => ev.TypeOf(m, e) is ClassType { IsInstance: false } c && ev.IsDjangoModel(c.Info) ? c.Info : null;
            foreach (var cd in AstChildren.Descendants(m.Ast).OfType<ClassDef>().ToList())
                foreach (var d in cd.Decorators)
                    if (d is CallExpr { Func: var f } call && (f is NameExpr { Name: "register" } || f is AttributeExpr { Attr: "register" })
                        && call.Args.Where(a => a.Name == null).Select(a => ModelOf(a.Value)).FirstOrDefault(x => x != null) is { } model
                        && ClassInfoOf(m, cd, ev) is { } admin)
                        map[admin] = model;
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

    readonly ConditionalWeakTable<Workspace, Dictionary<ClassInfo, ClassInfo>> _adminModelsByWorkspace = new();

    /// <summary>`def save_model(self, request, obj, form, change)` of a registered admin: obj is a model instance.</summary>
    PyType? AdminParameterType(Workspace ws, ModuleInfo m, Parameter p)
    {
        if (m.IsStub || p.Name is not ("obj" or "instance") || m.ParentOf(p) is not FunctionDef f || m.ParentOf(f) is not ClassDef cd) return null;
        var ev = ws.Evaluator;
        if (ClassInfoOf(m, cd, ev) is not { } info || ev.DjangoRoleOf(info) is not (DjangoClassRole.Admin or DjangoClassRole.Inline)) return null;
        var model = ev.DjangoMetaModel(info);
        if (model == null)
        {
            var registry = _adminModelsByWorkspace.GetValue(ws, w => AdminRegistry(w.UserModules.ToList(), w.Evaluator));
            if (registry.TryGetValue(info, out var registered)) model = registered;
        }
        return model == null ? null : new ClassType(model, Array.Empty<PyType>(), true);
    }

    // ------------------------------------------------------------ the context of a string

    /// <summary>What a string literal in a list, tuple or dict assigned in a model Meta, form, serializer or admin class means: the model, the class and how the name is read.</summary>
    MetaContext? MetaContextOf(ModuleInfo m, TypeEvaluator ev, Dictionary<ClassInfo, ClassInfo> admins, ConstExpr literal)
    {
        Node node = literal;
        bool key = false, value = false;
        bool underFieldsKey = false;
        int dictDepth = 0;
        AssignStmt? assign = null;
        for (int guard = 0; guard < 12; guard++)
        {
            var parent = m.ParentOf(node);
            switch (parent)
            {
                case ListExpr or TupleExpr or SetExpr:
                    node = parent;
                    continue;
                case DictExpr d:
                    dictDepth++;
                    int at = d.Keys.ToList().FindIndex(k => ReferenceEquals(k, node));
                    if (at >= 0) key = true;
                    else
                    {
                        value = true;
                        int vi = d.Values.ToList().FindIndex(v => ReferenceEquals(v, node));
                        if (vi >= 0 && d.Keys[vi] is ConstExpr kc && FixtureModel.StringValue(kc) == "fields") underFieldsKey = true;
                    }
                    node = parent;
                    continue;
                case AssignStmt a when a.Targets.Count == 1 && a.Targets[0] is NameExpr:
                    assign = a;
                    break;
                default:
                    return null;
            }
            break;
        }
        if (assign == null || m.ParentOf(assign) is not ClassDef cd) return null;
        string attr = ((NameExpr)assign.Targets[0]).Name;

        bool inMeta = cd.Name == "Meta";
        var ownerDef = inMeta ? m.ParentOf(cd) as ClassDef : cd;
        if (ownerDef == null || ClassInfoOf(m, ownerDef, ev) is not { } owner) return null;
        var role = ev.DjangoRoleOf(owner);
        if (RuleFor(role, inMeta, attr) is not { } rule) return null;

        // what a dict contributes: its keys name fields in some attributes, its values in `fieldsets` and `prepopulated_fields`
        if (dictDepth > 0)
        {
            bool ok = attr switch
            {
                "prepopulated_fields" => true,
                "fieldsets" or "add_fieldsets" => value && underFieldsKey,
                _ when FormKeyAttributes.Contains(attr) || attr == "filterset_fields" => key && dictDepth == 1,
                _ => false,
            };
            if (!ok) return null;
        }
        else if (attr is "fieldsets" or "add_fieldsets") return null;

        ClassInfo? model = role switch
        {
            DjangoClassRole.Model => owner,
            DjangoClassRole.Admin => admins.GetValueOrDefault(owner),
            _ => ev.DjangoMetaModel(owner),
        };
        return model == null ? null : new MetaContext(model, owner, role, attr, rule);
    }

    /// <summary>The names valid at such a place.</summary>
    static HashSet<string> MetaNames(TypeEvaluator ev, MetaContext c)
    {
        var names = new HashSet<string>(ev.DjangoFieldsOf(c.Model).Select(f => f.Name), StringComparer.Ordinal) { "pk" };
        if (!ev.DjangoFieldsOf(c.Model).Any(f => f.PrimaryKey)) names.Add("id");
        if (c.Rule.Lenient)
        {
            foreach (var member in ev.MemberNames(new ClassType(c.Model, Array.Empty<PyType>(), true)))
                if (!member.Name.StartsWith('_') || member.Name == "__str__") names.Add(member.Name);
            foreach (var own in ev.DjangoOwnNames(c.Owner)) if (!own.StartsWith('_') || own == "__str__") names.Add(own);
            names.Add("action_checkbox");
        }
        return names;
    }

    /// <summary>The part of a string that names the field: a leading `-`, `?`, `^`, `=` or `@` is not part of it.</summary>
    static string MetaPath(string value) => value.TrimStart('-', '?', '^', '=', '@');

    // ------------------------------------------------------------ checks

    IEnumerable<Diag> MetaDiagnostics(ModuleInfo m, TypeEvaluator ev, Snapshot snap)
    {
        if (m.IsStub || m.Source.IndexOf("Meta", StringComparison.Ordinal) < 0 && m.Source.IndexOf("list_display", StringComparison.Ordinal) < 0 && m.Source.IndexOf("search_fields", StringComparison.Ordinal) < 0
            && m.Source.IndexOf("fields", StringComparison.Ordinal) < 0 && m.Source.IndexOf("queryset", StringComparison.Ordinal) < 0) yield break;
        if (m.Source.IndexOf("django", StringComparison.OrdinalIgnoreCase) < 0 && m.Source.IndexOf("rest_framework", StringComparison.Ordinal) < 0) yield break;
        var admins = AdminModelsOf(snap);
        foreach (var literal in AstChildren.Descendants(m.Ast).OfType<ConstExpr>())
        {
            if (literal.Kind != ConstKind.Str || FixtureModel.StringValue(literal) is not { } value || FixtureModel.StringSpan(literal) is not { } span) continue;
            if (MetaContextOf(m, ev, admins, literal) is not { } c) continue;
            string path = MetaPath(value);
            if (path.Length == 0 || path is "__all__" or "__str__" or "*") continue;
            string? message = null;
            if (c.Rule.Mode == "field")
            {
                var names = MetaNames(ev, c);
                if (!names.Contains(path))
                    message = $"'{path}' is not a field of {c.Model.Name}" + (c.Rule.Lenient ? $" or a method or property of {c.Owner.Name} or the model" : "")
                        + $". Choices are: {string.Join(", ", ev.DjangoFieldsOf(c.Model).Select(f => f.Name).Take(12))}";
            }
            else
            {
                if (path == "?" || path == "pk") continue;
                if (ev.DjangoFieldNamed(c.Model, path.Split("__")[0]) == null && c.Attr is "ordering" && c.Role == DjangoClassRole.Admin && MetaNames(ev, c with { Rule = new MetaRule("field", true) }).Contains(path)) continue;
                if (CheckLookup(ev, c.Model, path, c.Rule.Mode == "lookup") is { } bad)
                    message = $"Cannot resolve '{bad.Bad}' into a field of {c.Model.Name}. Choices are: {string.Join(", ", bad.Choices.Take(12))}";
            }
            if (message == null) continue;
            var finding = new Finding(span.Start, span.End - span.Start, message, "django-meta-field", FindingLevel.Warning);
            if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
        }
    }

    /// <summary>A view whose `serializer_class` is for another model than its `queryset`.</summary>
    IEnumerable<Diag> ViewDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub || m.Source.IndexOf("serializer_class", StringComparison.Ordinal) < 0 || m.Source.IndexOf("rest_framework", StringComparison.Ordinal) < 0) yield break;
        foreach (var cd in AstChildren.Descendants(m.Ast).OfType<ClassDef>().ToList())
        {
            if (ClassInfoOf(m, cd, ev) is not { } info || ev.DjangoRoleOf(info) != DjangoClassRole.View) continue;
            var (query, serializer, expr) = ev.DjangoViewParts(info);
            if (query == null || serializer == null || expr == null || ev.DjangoMetaModel(serializer) is not { } serves || serves == query) continue;
            var finding = new Finding(expr.Start, expr.End - expr.Start, $"{serializer.Name} is for {serves.Name}, but the queryset of {info.Name} is {query.Name}", "django-view-serializer", FindingLevel.Warning);
            if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
        }
    }

    // ------------------------------------------------------------ go to and hover

    (List<Location> Locations, string Hover, int Start, int End)? MetaFieldTarget(Snapshot snap, ModuleInfo m, int offset)
    {
        var ev = snap.Workspace.Evaluator;
        var literal = AstChildren.Descendants(m.Ast).OfType<ConstExpr>().FirstOrDefault(c => c.Kind == ConstKind.Str && c.Start <= offset && offset <= c.End);
        if (literal == null || FixtureModel.StringValue(literal) is not { } value || FixtureModel.StringSpan(literal) is not { } span) return null;
        if (offset < span.Start || offset > span.End) return null;
        if (MetaContextOf(m, ev, AdminModelsOf(snap), literal) is not { } c) return null;
        string path = MetaPath(value);
        int lead = value.Length - path.Length;
        if (path.Length == 0 || path == "__all__") return null;

        var locations = new List<Location>();
        string hover = "";
        var current = c.Model;
        var parts = path.Split("__");
        // the part of the path under the cursor: everything up to the end of its segment
        int rel = offset - span.Start - lead;
        int upTo = 0, consumed = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            consumed += parts[i].Length + (i < parts.Length - 1 ? 2 : 0);
            upTo = i;
            if (rel <= consumed) break;
        }
        for (int i = 0; i <= upTo; i++)
        {
            var field = ev.DjangoFieldNamed(current, parts[i]);
            if (field != null)
            {
                var decl = field.Owner.Scope.Symbols.GetValueOrDefault(parts[i])?.Declarations.FirstOrDefault();
                if (i == upTo)
                {
                    if (decl != null && FixtureLocation(field.Owner.Module, decl.Node.Start, decl.Node.End) is { } loc) locations.Add(loc);
                    hover = $"```python\n(field) {current.Name}.{parts[i]}\n```\n\n{field.Kind}" + (field.Target != null ? $" to `{field.Target.Name}`" : "") + (field.Nullable ? ", may be null" : "");
                }
                if (field.Target == null) { if (i < upTo) return null; continue; }
                current = field.Target;
                continue;
            }
            if (i < upTo) { var next = ev.DjangoRelatedModel(current, parts[i]); if (next == null) return null; current = next; continue; }
            // a method or property of the model, or of the class that lists it
            foreach (var cls in new[] { current, c.Owner })
                locations.AddRange(MemberLocations(ev, new ClassType(cls, Array.Empty<PyType>(), true), parts[i]));
            if (locations.Count > 0) hover = $"```python\n(attribute) {parts[i]}\n```";
        }
        if (locations.Count == 0 && hover.Length == 0) return null;
        return (locations.Take(1).ToList(), hover, span.Start, span.End);
    }

    // ------------------------------------------------------------ completion

    IReadOnlyList<CompletionItem>? MetaStringCompletion(Doc doc, int start, int offset)
    {
        string text = doc.Text;
        int lineStart = LineStart(text, offset);
        string before = text[lineStart..offset];
        var quote = System.Text.RegularExpressions.Regex.Match(before, @"[""'](?<typed>[-?^=@\w]*)$");
        if (!quote.Success) return null;
        if (text.IndexOf("django", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("rest_framework", StringComparison.Ordinal) < 0) return null;
        var (ws, patched) = PatchedModule(doc, start, offset);
        var ev = ws.Evaluator;
        var literal = AstChildren.Descendants(patched.Ast).OfType<ConstExpr>().FirstOrDefault(c => c.Kind == ConstKind.Str && c.Text.Contains(CursorMark));
        if (literal == null) return null;
        var admins = AdminRegistry(ws.UserModules.Append(patched).Distinct().ToList(), ev);
        if (MetaContextOf(patched, ev, admins, literal) is not { } c) return null;

        string typed = quote.Groups["typed"].Value;
        string path = MetaPath(typed);
        var items = new List<CompletionItem>();
        if (c.Rule.Mode == "field")
        {
            foreach (var name in MetaNames(ev, c).Where(n => n.StartsWith(path, StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
            {
                var field = ev.DjangoFieldNamed(c.Model, name);
                items.Add(new CompletionItem(name, field != null ? CompletionKind.Field : CompletionKind.Property, field?.Kind ?? "attribute", (field != null ? "0" : "1") + name));
            }
            return items;
        }
        // a path across relations: `author__na`
        var parts = path.Split("__");
        var current = c.Model;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var next = ev.DjangoRelatedModel(current, parts[i]);
            if (next == null) return items;
            current = next;
        }
        string stem = parts.Length > 1 ? string.Join("__", parts[..^1]) + "__" : "";
        foreach (var name in ev.DjangoQueryNames(current).Distinct().Where(n => n.StartsWith(parts[^1], StringComparison.Ordinal)))
        {
            var field = ev.DjangoFieldNamed(current, name);
            items.Add(new CompletionItem(stem + name, CompletionKind.Field, field?.Kind ?? "relation", "0" + name));
        }
        return items;
    }
}

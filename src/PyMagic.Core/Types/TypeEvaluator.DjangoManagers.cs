using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>Custom managers, of the project and of installed apps: what `self.get_queryset()` gives in one, and managers made from a QuerySet class.</summary>
public sealed partial class TypeEvaluator
{
    // a manager made by `TagQuerySet.as_manager()` or `Manager.from_queryset(TagQuerySet)`: the QuerySet class whose methods it has
    readonly Dictionary<ClassInfo, ClassInfo> _djangoQuerySetManagers = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ClassInfo, ClassInfo?> _djangoManagerModels = new(ReferenceEqualityComparer.Instance);

    bool IsDjangoQuerySetClass(ClassInfo c)
    {
        foreach (var entry in Mro(c))
            if (entry is ClassType e && e.Info.Name == "QuerySet" && IsDjangoInternal(e.Info)) return true;
        return false;
    }

    /// <summary>
    /// `TagQuerySet.as_manager()` is a manager with the QuerySet's methods; `Manager.from_queryset(TagQuerySet)` is such a manager class. Inside a manager
    /// class of the user's code or of an installed app, `self.get_queryset()` (or `super().get_queryset()`) is a queryset of the model it is the manager of.
    /// </summary>
    PyType? DjangoManagerCall(ModuleInfo m, CallExpr call)
    {
        if (call.Func is not AttributeExpr access) return null;
        switch (access.Attr)
        {
            case "as_manager" when TypeOf(m, access.Value) is ClassType { IsInstance: false } qs && !IsDjangoInternal(qs.Info) && IsDjangoQuerySetClass(qs.Info):
                return InstanceOfClass(QuerySetManager(qs.Info));
            case "from_queryset" when call.Args.FirstOrDefault(a => a.Name == null)?.Value is { } arg && TypeOf(m, access.Value) is ClassType { IsInstance: false } mc
                                      && IsDjangoManagerClass(mc.Info) && TypeOf(m, arg) is ClassType { IsInstance: false } qs2 && IsDjangoQuerySetClass(qs2.Info):
                return new ClassType(QuerySetManager(qs2.Info), false);
            case "get_queryset" when access.Value is NameExpr { Name: "self" } || access.Value is CallExpr { Func: NameExpr { Name: "super" } }:
            {
                Node? node = call;
                while (node != null && node is not ClassDef) node = m.ParentOf(node);
                if (node is not ClassDef cd) return null;
                var owner = ClassOf(m, cd);
                if (IsDjangoInternal(owner) || !IsDjangoManagerClass(owner) || DjangoModelOfManagerClass(owner) is not { } model) return null;
                return InstanceOfClass(DjangoQuerySetOf(model));
            }
        }
        return null;
    }

    ClassInfo QuerySetManager(ClassInfo querySet)
    {
        foreach (var (manager, qs) in _djangoQuerySetManagers) if (ReferenceEquals(qs, querySet)) return manager;
        var made = NewSyntheticClass(querySet.Module, "Manager", new ClassType(querySet, false));
        _djangoQuerySetManagers[made] = querySet;
        return made;
    }

    /// <summary>The QuerySet class a manager made from one has its methods from (null for any other class).</summary>
    ClassInfo? DjangoQuerySetOfManager(ClassInfo manager) => _djangoQuerySetManagers.GetValueOrDefault(manager);

    /// <summary>The one model a manager class is the manager of (`objects = TagManager()` in its body), among the project's models and those of the manager's own module.</summary>
    ClassInfo? DjangoModelOfManagerClass(ClassInfo manager)
    {
        if (_djangoManagerModels.TryGetValue(manager, out var known)) return known;
        _djangoManagerModels[manager] = null;
        var candidates = DjangoProjectModels().ToList();
        foreach (var sym in manager.Module.Semantics.ModuleScope.Symbols.Values)
            foreach (var d in sym.Declarations)
                if (d.Kind == DeclKind.Class && d.Node is ClassDef cd && ClassOf(manager.Module, cd) is var info && IsDjangoModel(info) && !candidates.Contains(info)) candidates.Add(info);
        var found = new HashSet<ClassInfo>(ReferenceEqualityComparer.Instance);
        foreach (var model in candidates)
            foreach (var stmt in model.Node.Body.OfType<AssignStmt>())
                if (stmt.Value is CallExpr { Func: var f } && (f is NameExpr n && n.Name == manager.Name || f is AttributeExpr a && a.Attr == manager.Name)
                    && TypeOf(model.Module, stmt.Value) is ClassType { IsInstance: true } t && Mro(t.Info).Any(e => e is ClassType ec && ReferenceEquals(ec.Info, manager)))
                    found.Add(model);
        return _djangoManagerModels[manager] = found.Count == 1 ? found.First() : null;
    }

    readonly Dictionary<ClassInfo, ClassInfo?> _djangoCustomQuerySets = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The QuerySet class of the project a model's managers are made from: `objects = BookQuerySet.as_manager()`, `Manager.from_queryset(BookQuerySet)()`,
    /// or a manager whose get_queryset() returns a `BookQuerySet(self.model, using=self._db)`. Null when the model's querysets are Django's own.
    /// </summary>
    ClassInfo? DjangoCustomQuerySetOf(ClassInfo model)
    {
        if (_djangoCustomQuerySets.TryGetValue(model, out var known)) return known;
        _djangoCustomQuerySets[model] = null;
        bool Ours(ClassInfo c) => !c.Module.IsStub && !IsDjangoInternal(c) && !_djangoSyntheticModel.ContainsKey(c) && IsDjangoQuerySetClass(c);
        foreach (var entry in Mro(model))
        {
            if (entry is not ClassType { Info: var e } || e.Module.IsStub || IsDjangoInternal(e)) continue;
            foreach (var stmt in e.Node.Body.OfType<AssignStmt>())
            {
                if (stmt.Targets.Count != 1 || stmt.Targets[0] is not NameExpr || stmt.Value is not CallExpr) continue;
                if (TypeOf(e.Module, stmt.Value) is not ClassType { IsInstance: true } made) continue;
                if (DjangoQuerySetOfManager(made.Info) is { } fromQuerySet && Ours(fromQuerySet)) return _djangoCustomQuerySets[model] = fromQuerySet;
                if (!IsDjangoManagerClass(made.Info)) continue;
                foreach (var me in Mro(made.Info))
                {
                    if (me is not ClassType { Info: var mc } || mc.Module.IsStub || IsDjangoInternal(mc)) continue;
                    if (mc.Node.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "get_queryset") is not { } getter) continue;
                    foreach (var ret in AstChildren.Descendants(getter).OfType<ReturnStmt>())
                        if (ret.Value != null && TypeOf(mc.Module, ret.Value) is ClassType { IsInstance: true } returned && Ours(returned.Info))
                            return _djangoCustomQuerySets[model] = returned.Info;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// A method of the project's QuerySet class read through a queryset or manager of a model: what it gives, with a queryset (`return self.filter(...)`)
    /// being one of that model, so that `Book.objects.published().first()` is a Book. Null when the member is not such a method.
    /// </summary>
    PyType? DjangoCustomQuerySetMethod(MemberHit hit, ClassType receiver, PyType bound)
    {
        if (!_djangoSyntheticModel.TryGetValue(receiver.Info, out var model) || _djangoRowSetClasses.Contains(receiver.Info)) return null;
        var owner = hit.Owner.Info;
        if (owner.Module.IsStub || IsDjangoInternal(owner) || _djangoSyntheticModel.ContainsKey(owner) || !IsDjangoQuerySetClass(owner)) return null;
        if (bound is not FunctionType { Info.IsStatic: false } f) return null;
        var returns = ReturnTypeOf(f);
        bool Plain(PyType t) => t is ClassType { IsInstance: true } c && !_djangoSyntheticModel.ContainsKey(c.Info) && IsDjangoQuerySetClass(c.Info);
        if (!returns.Members.Any(Plain)) return null;
        var queryset = InstanceOfClass(DjangoQuerySetOf(model));
        var mapped = PyType.Union(returns.Members.Select(t => Plain(t) ? queryset : t).ToList());
        var ps = ParametersOf(f.Info).ToList();
        if (f.BoundFirst && ps.Count > 0 && ps[0].Category is ParamCategory.PositionalOnly or ParamCategory.Normal) ps.RemoveAt(0);
        return new SignatureType(ps, mapped, Origin: f, Named: true);
    }

    /// <summary>
    /// `self.filter(...)` inside a manager class of the project: Django copies the QuerySet methods onto Manager at run time, so the class does not
    /// declare them. When the manager belongs to one model they are that model's (`self.filter()` is a QuerySet[Article]); null otherwise.
    /// </summary>
    PyType? DjangoManagerSelfMember(ClassType c, string name)
    {
        var info = c.Info;
        if (info.Module.IsStub || IsDjangoInternal(info) || _djangoSyntheticModel.ContainsKey(info) || !IsDjangoManagerClass(info)) return null;
        if (DjangoModelOfManagerClass(info) is not { } model) return null;
        var manager = DjangoManagerOf(model, info);
        return manager.Scope.Symbols.TryGetValue(name, out var sym) && sym.Declarations.Count > 0 ? TypeOfSymbol(sym) : null;
    }
}

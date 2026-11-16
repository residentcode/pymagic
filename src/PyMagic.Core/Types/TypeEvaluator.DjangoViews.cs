using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>
/// Django's generic class-based views (DetailView, ListView, UpdateView, ...): `self.request` is the HttpRequest View.setup() stores, `self.object` the
/// model the view is for (`model = Book`, `queryset = Book.objects...`), `self.object_list` a queryset of it, and get_object() / get_queryset() give the
/// same, also through super(). Django's source only stores parameters there, so the types come from the view's class body.
/// </summary>
public sealed partial class TypeEvaluator
{
    readonly Dictionary<ClassInfo, ClassInfo?> _djangoViewModels = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether a class of the user's code derives from Django's View, and which of the object mixins it has.</summary>
    (bool View, bool Single, bool Multiple) DjangoGenericViewKind(ClassInfo c)
    {
        if (c.Module.IsStub || IsDjangoInternal(c)) return default;
        bool view = false, single = false, multiple = false;
        foreach (var entry in Mro(c))
        {
            if (entry is not ClassType { Info: var e } || !IsDjangoInternal(e) || e.Module.IsStub) continue;
            if (e.Name == "View" && e.Module.Name == "django.views.generic.base") view = true;
            else if (e.Name == "SingleObjectMixin" && e.Module.Name == "django.views.generic.detail") single = true;
            else if (e.Name == "MultipleObjectMixin" && e.Module.Name == "django.views.generic.list") multiple = true;
        }
        return (view, single, multiple);
    }

    /// <summary>The model of a generic view: `model = Book` along its bases of the user's code, else the model of its `queryset` or of what get_queryset returns.</summary>
    ClassInfo? DjangoGenericViewModel(ClassInfo c)
    {
        if (_djangoViewModels.TryGetValue(c, out var known)) return known;
        _djangoViewModels[c] = null;   // get_queryset's return may ask again
        ClassInfo? model = null;
        // `model = Book` or `queryset = Book.objects...` along the bases of the user's code, before what get_queryset returns (which may ask again)
        foreach (var entry in Mro(c))
        {
            if (entry is not ClassType { Info: var e } || e.Module.IsStub || IsDjangoInternal(e)) continue;
            if (ModelAssignedIn(e.Node.Body, e.Module) is { } assigned) { model = assigned; break; }
            if (e.Node.Body.OfType<AssignStmt>().FirstOrDefault(s => s.Targets is [NameExpr { Name: "queryset" }]) is { } qs && DjangoModelOfQuery(TypeOf(e.Module, qs.Value)) is { } queried) { model = queried; break; }
        }
        model ??= DjangoViewParts(c).QueryModel;
        return _djangoViewModels[c] = model;
    }

    /// <summary>`self.request`, `self.object` and `self.object_list` of a generic view; null for another class or name.</summary>
    PyType? DjangoGenericViewMember(ClassType c, string name)
    {
        if (name is not ("request" or "object" or "object_list")) return null;
        var kind = DjangoGenericViewKind(c.Info);
        if (!kind.View) return null;
        if (name == "request") return DjangoInstanceOf("django.http.request", "HttpRequest");
        if (name == "object" && kind.Single && DjangoGenericViewModel(c.Info) is { } model) return InstanceOfClass(model);
        if (name == "object_list" && kind.Multiple && DjangoGenericViewModel(c.Info) is { } listed) return InstanceOfClass(DjangoQuerySetOf(listed));
        return null;
    }

    /// <summary>`self.get_object()` / `super().get_queryset()` inside a generic view: its model, or a queryset of it.</summary>
    PyType? DjangoGenericViewCall(ModuleInfo m, CallExpr call)
    {
        if (call.Func is not AttributeExpr { Attr: "get_object" or "get_queryset" } access
            || access.Value is not (NameExpr { Name: "self" } or CallExpr { Func: NameExpr { Name: "super" } })) return null;
        Node? node = call;
        while (node != null && node is not ClassDef) node = m.ParentOf(node);
        if (node is not ClassDef cd) return null;
        var owner = ClassOf(m, cd);
        var kind = DjangoGenericViewKind(owner);
        if (!kind.View || !(kind.Single || kind.Multiple)) return null;
        // self.get_queryset() of a view that defines its own: what that gives
        if (access.Value is NameExpr && owner.Scope.Symbols.ContainsKey(access.Attr)) return null;
        if (access.Attr == "get_object" && !kind.Single) return null;
        if (DjangoGenericViewModel(owner) is not { } model) return null;
        return access.Attr == "get_object" ? InstanceOfClass(model) : InstanceOfClass(DjangoQuerySetOf(model));
    }
}

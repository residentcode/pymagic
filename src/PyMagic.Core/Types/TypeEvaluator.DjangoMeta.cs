using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>What a class is to Django: a model, a model form, a REST framework model serializer, a model admin or an inline of one.</summary>
public enum DjangoClassRole { None, Model, Form, Serializer, Admin, Inline }

public sealed partial class TypeEvaluator
{
    readonly Dictionary<ClassInfo, DjangoClassRole> _djangoRoles = new();
    readonly Dictionary<ClassInfo, ClassInfo?> _djangoMetaModels = new();

    /// <summary>The role of a class of the user's code, found from the library classes among its bases.</summary>
    public DjangoClassRole DjangoRoleOf(ClassInfo c)
    {
        if (_djangoRoles.TryGetValue(c, out var known)) return known;
        _djangoRoles[c] = DjangoClassRole.None;   // a cycle in the hierarchy answers none
        var role = DjangoClassRole.None;
        if (!c.Module.IsStub && !IsDjangoInternal(c) && !c.Module.Name.StartsWith("rest_framework", StringComparison.Ordinal))
        {
            if (IsDjangoModel(c)) role = DjangoClassRole.Model;
            else
                foreach (var entry in Mro(c))
                {
                    if (entry is not ClassType e || e.Info.Module.IsStub) continue;
                    string module = e.Info.Module.Name;
                    if (e.Info.Name is "ModelForm" or "BaseModelForm" && module.StartsWith("django.forms", StringComparison.Ordinal)) { role = DjangoClassRole.Form; break; }
                    if (e.Info.Name == "ModelSerializer" && module.StartsWith("rest_framework", StringComparison.Ordinal)) { role = DjangoClassRole.Serializer; break; }
                    if (e.Info.Name is "InlineModelAdmin" or "TabularInline" or "StackedInline" && module.StartsWith("django.contrib.admin", StringComparison.Ordinal)) { role = DjangoClassRole.Inline; break; }
                    if (e.Info.Name is "ModelAdmin" or "BaseModelAdmin" && module.StartsWith("django.contrib.admin", StringComparison.Ordinal)) { role = DjangoClassRole.Admin; break; }
                }
        }
        return _djangoRoles[c] = role;
    }

    /// <summary>The `Meta` class of a class of the user's code (its own or the nearest inherited one) and the class that declares it.</summary>
    public (ClassDef Meta, ClassInfo Owner)? DjangoMetaOf(ClassInfo c)
    {
        foreach (var entry in Mro(c))
        {
            if (entry is not ClassType e || e.Info.Module.IsStub || IsDjangoInternal(e.Info)) continue;
            var decl = e.Info.Scope.Symbols.GetValueOrDefault("Meta")?.Declarations.FirstOrDefault();
            if (decl?.Node is ClassDef meta) return (meta, e.Info);
        }
        return null;
    }

    /// <summary>The model a form or serializer is for (`class Meta: model = Book`) or an inline admin (`model = Book`).</summary>
    public ClassInfo? DjangoMetaModel(ClassInfo c)
    {
        if (_djangoMetaModels.TryGetValue(c, out var known)) return known;
        _djangoMetaModels[c] = null;
        ClassInfo? result = null;
        var role = DjangoRoleOf(c);
        if (role is DjangoClassRole.Form or DjangoClassRole.Serializer && DjangoMetaOf(c) is { } meta)
            result = ModelAssignedIn(meta.Meta.Body, meta.Owner.Module);
        else if (role == DjangoClassRole.Inline)
            foreach (var entry in Mro(c))
                if (entry is ClassType e && !e.Info.Module.IsStub && !IsDjangoInternal(e.Info) && ModelAssignedIn(e.Info.Node.Body, e.Info.Module) is { } m) { result = m; break; }
        return _djangoMetaModels[c] = result;
    }

    ClassInfo? ModelAssignedIn(IReadOnlyList<Stmt> body, ModuleInfo module)
    {
        foreach (var stmt in body.OfType<AssignStmt>())
            if (stmt.Targets.Count == 1 && stmt.Targets[0] is NameExpr { Name: "model" } && TypeOf(module, stmt.Value) is ClassType { IsInstance: false } mc && IsDjangoModel(mc.Info))
                return mc.Info;
        return null;
    }

    /// <summary>Names a form, serializer or admin class declares itself (declared fields, methods, properties), along its bases of the user's code.</summary>
    public IEnumerable<string> DjangoOwnNames(ClassInfo c)
    {
        foreach (var entry in Mro(c))
        {
            if (entry is not ClassType e || e.Info.Module.IsStub || IsDjangoInternal(e.Info) || e.Info.Module.Name.StartsWith("rest_framework", StringComparison.Ordinal)) continue;
            foreach (var name in e.Info.Scope.Symbols.Keys.ToList()) yield return name;
        }
    }

    /// <summary>`form.instance` and `form.save()` of a model form or model serializer are the model the Meta names.</summary>
    PyType? DjangoMetaMember(ClassType c, string name, bool instance)
    {
        if (!instance || name is not ("instance" or "save") || c.Info.Module.IsStub) return null;
        if (DjangoRoleOf(c.Info) is not (DjangoClassRole.Form or DjangoClassRole.Serializer) || DjangoMetaModel(c.Info) is not { } model) return null;
        var inst = InstanceOfClass(model);
        return name == "instance" ? inst : Method(inst);
    }
}

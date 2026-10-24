using PyMagic.Core.Semantics;

namespace PyMagic.Core.Types;

/// <summary>A name that can follow a dot on a value, with where it was found.</summary>
public sealed record MemberName(string Name, string Owner, bool IsInstanceAttribute);

public sealed partial class TypeEvaluator
{
    /// <summary>
    /// Every attribute name visible on a value of this type (for completion): class and instance attributes along the
    /// MRO, module-level names of a module. Names are unique, nearest owner wins; a union offers the names of all members.
    /// </summary>
    public IReadOnlyList<MemberName> MemberNames(PyType receiver)
    {
        var seen = new HashSet<string>();
        var result = new List<MemberName>();
        void Add(string name, string owner, bool inst)
        {
            if (seen.Add(name)) result.Add(new MemberName(name, owner, inst));
        }

        foreach (var member in receiver.Members)
        {
            switch (member)
            {
                case ClassType c:
                    foreach (var anc in Mro(c.Info))
                    {
                        if (anc is not ClassType ac) continue;
                        if (c.IsInstance)
                            foreach (var name in InstanceAttributes(ac.Info).Keys) Add(name, ac.Info.Name, true);
                        foreach (var name in ac.Info.Scope.Symbols.Keys) Add(name, ac.Info.Name, false);
                    }
                    if (IsDjangoModel(c.Info)) foreach (var name in DjangoImplicitNames(c)) Add(name, c.Info.Name, c.IsInstance);
                    else if (c.IsInstance && IsDjangoSettingsObject(c.Info)) foreach (var name in DjangoSettingNames()) Add(name, "settings", false);
                    if (c.Info.Name != "type" || c.Info.Module.Name != "builtins")
                    {
                        if (!c.IsInstance && BuiltinClass("type") is { } meta)
                            foreach (var anc in Mro(meta.Info))
                                if (anc is ClassType mc)
                                    foreach (var name in mc.Info.Scope.Symbols.Keys) Add(name, mc.Info.Name, false);
                    }
                    break;

                case ModuleType mt:
                    foreach (var name in mt.Module.Semantics.ModuleScope.Symbols.Keys) Add(name, mt.Module.Name, false);
                    foreach (var star in mt.Module.Semantics.ModuleScope.StarImports)
                    {
                        var target = Workspace.GetModule(Workspace.AbsoluteName(mt.Module, star.Level, star.Module) ?? "");
                        if (target == null) continue;
                        foreach (var name in target.Semantics.ModuleScope.Symbols.Keys)
                            if (!name.StartsWith('_')) Add(name, target.Name, false);
                    }
                    break;

                case FunctionType or OverloadedType or SignatureType:
                    if (BuiltinClass("function") is { } fn)
                        foreach (var anc in Mro(fn.Info))
                            if (anc is ClassType fc)
                                foreach (var name in fc.Info.Scope.Symbols.Keys) Add(name, fc.Info.Name, false);
                    break;
            }
        }
        return result;
    }
}

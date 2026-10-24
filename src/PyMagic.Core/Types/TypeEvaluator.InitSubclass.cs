using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

public sealed partial class TypeEvaluator
{
    /// <summary>`class Child(Base, flag=True)`: the keywords go to the nearest `__init_subclass__` of the bases, which must accept them.</summary>
    void InspectClassKeywords(ModuleInfo m, ClassDef cd, List<Diagnostic> diags)
    {
        var keywords = cd.Bases.Where(b => b.Name != null).ToList();
        if (keywords.Count == 0 || keywords.Any(b => b.Name == "metaclass")) return;
        var sym = m.Semantics.ScopeOf(cd)?.Parent?.Symbols.GetValueOrDefault(cd.Name);
        if (sym == null || TypeOfSymbol(sym) is not ClassType { IsInstance: false } ct || !ReferenceEquals(ct.Info.Node, cd)) return;
        if (TransformOfAncestors(ct.Info) != null) return;         // pydantic, attrs and friends read the keywords themselves
        var mro = Mro(ct.Info);
        if (mro.Any(e => e is not ClassType)) return;
        var bases = mro.Skip(1).Cast<ClassType>().ToList();
        if (bases.Any(e => e.Info.Node.Bases.Any(b => b.Name == "metaclass"))) return;   // a metaclass takes the keywords itself

        var owner = bases.FirstOrDefault(e => e.Info.QualifiedName != "builtins.object" && e.Info.Scope.Symbols.ContainsKey("__init_subclass__"));
        if (owner == null)
        {
            // only classes of the project and the standard `object` are known not to read keywords any other way
            if (bases.Any(e => e.Info.Module.IsStub && e.Info.QualifiedName != "builtins.object")) return;
            foreach (var k in keywords)
                diags.Add(new Diagnostic(k.Start, Math.Max(k.End - k.Start, 1), $"Unexpected argument '{k.Name}'"));
            return;
        }
        if (TypeOfSymbol(owner.Info.Scope.Symbols["__init_subclass__"]) is not FunctionType f) return;
        var ps = ParametersOf(f.Info).ToList();
        if (ps.Count > 0 && ps[0].Category is ParamCategory.PositionalOnly or ParamCategory.Normal) ps.RemoveAt(0);   // cls
        var args = keywords.Select(k => new ArgInfo(k, k.Name, 0, () => PyType.Unknown)).ToList();
        var (_, problems) = MapArguments(ps, args, null);
        foreach (var p in problems.Where(p => p.Kind == CallProblemKind.Arity))
        {
            var at = p.At ?? keywords[0];
            diags.Add(new Diagnostic(at.Start, Math.Max(at.End - at.Start, 1), p.Message));
        }
    }
}

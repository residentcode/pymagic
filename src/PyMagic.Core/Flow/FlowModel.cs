using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Flow;

/// <summary>Per-scope control-flow graphs and reaching definitions, built lazily on top of a <see cref="SemanticModel"/>.</summary>
public sealed class FlowModel
{
    readonly SemanticModel _model;
    readonly Dictionary<Node, (Symbol, Declaration)> _decls = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<Scope, ScopeFlow> _flows = new(ReferenceEqualityComparer.Instance);

    public FlowModel(SemanticModel model)
    {
        _model = model;
        Index(model.ModuleScope);
    }

    void Index(Scope s)
    {
        foreach (var sym in s.Symbols.Values)
            foreach (var d in sym.Declarations) _decls[d.Node] = (sym, d);
        foreach (var c in s.Children) Index(c);
    }

    /// <summary>The flow of a scope, or null for scopes that have no executable body of their own (type parameters).</summary>
    public ScopeFlow? For(Scope scope)
    {
        if (scope.Kind == ScopeKind.TypeParams) return null;
        if (!_flows.TryGetValue(scope, out var f)) _flows[scope] = f = new ScopeFlow(_model, scope, _decls);
        return f;
    }

    /// <summary>
    /// Bindings that may reach this use. Null when the name is not a local of the scope it is used in
    /// (builtins, outer-scope names, unresolved names): those need the other scope's flow, which is not static.
    /// </summary>
    public ReachingResult? ReachingDefinitions(NameExpr use)
    {
        var scope = _model.ReferenceOf(use)?.UseScope;
        return scope == null ? null : For(scope)?.ReachingFor(use);
    }

    /// <summary>Warnings for locals read before assignment in functions, and for unreachable statements.</summary>
    public IReadOnlyList<Diagnostic> Analyse()
    {
        var diags = new List<Diagnostic>();
        void Walk(Scope s)
        {
            var flow = For(s);
            if (flow != null)
            {
                foreach (var st in flow.UnreachableStatements())
                    diags.Add(new Diagnostic(st.Start, Math.Max(st.End - st.Start, 1), "Unreachable code"));
                if (s.Kind is ScopeKind.Function or ScopeKind.Lambda)
                    foreach (var (use, definitely) in flow.UnboundUses())
                        diags.Add(new Diagnostic(use.Start, use.End - use.Start, definitely
                            ? $"Local variable '{use.Name}' referenced before assignment"
                            : $"Local variable '{use.Name}' might be referenced before assignment"));
            }
            foreach (var c in s.Children) Walk(c);
        }
        Walk(_model.ModuleScope);
        return diags;
    }
}

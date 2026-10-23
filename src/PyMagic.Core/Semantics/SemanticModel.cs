using PyMagic.Core.Syntax;

namespace PyMagic.Core.Semantics;

/// <summary>Scopes, symbols and name resolution for one parsed module.</summary>
public sealed class SemanticModel
{
    readonly Dictionary<Node, Scope> _scopes;
    readonly Dictionary<NameExpr, Reference> _refs;

    public Scope ModuleScope { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public IReadOnlyCollection<Reference> References => _refs.Values;
    /// <summary>The interpreter version/platform that decided the static conditions while binding.</summary>
    public PythonEnvironment Environment { get; }

    internal SemanticModel(Scope module, Dictionary<Node, Scope> scopes, Dictionary<NameExpr, Reference> refs, List<Diagnostic> diags, PythonEnvironment env)
    {
        ModuleScope = module; _scopes = scopes; _refs = refs; Diagnostics = diags; Environment = env;
    }

    public static SemanticModel Build(Module module, PythonEnvironment? env = null) => new Binder(module, env ?? PythonEnvironment.Default).Run();

    /// <summary>The scope a Module/FunctionDef/ClassDef/LambdaExpr/CompExpr node creates.</summary>
    public Scope? ScopeOf(Node node) => _scopes.GetValueOrDefault(node);

    public Reference? ReferenceOf(NameExpr name) => _refs.GetValueOrDefault(name);

    /// <summary>The symbol a name expression refers to (null for builtins and unresolved names).</summary>
    public Symbol? Resolve(NameExpr name) => ReferenceOf(name)?.Symbol;

    /// <summary>Deepest scope whose node contains the offset; the starting point for completion and lookup.</summary>
    public Scope InnermostScopeAt(int offset)
    {
        var scope = ModuleScope;
        while (true)
        {
            Scope? next = null;
            foreach (var c in scope.Children)
                if (c.Node.Start <= offset && offset <= c.Node.End) { next = c; break; }
            if (next == null) return scope;
            scope = next;
        }
    }
}

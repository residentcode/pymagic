using PyMagic.Core.Syntax;

namespace PyMagic.Core.Semantics;

public enum ScopeKind { Module, Class, Function, Lambda, Comprehension, TypeParams }

public enum DeclKind
{
    Variable,       // assignment, for/with target, walrus, del
    Parameter,
    Function,
    Class,
    Import,
    TypeAlias,
    TypeParam,
    ExceptionVar,
    MatchCapture,
}

/// <summary>One place that binds a name. Node is the most specific syntax node available for it.</summary>
public sealed record Declaration(string Name, DeclKind Kind, Node Node);

[Flags]
public enum Access { Read = 1, Write = 2, Delete = 4 }

/// <summary>A name in a scope together with everything that binds or uses it.</summary>
public sealed class Symbol
{
    public string Name { get; }
    public Scope Scope { get; }
    public List<Declaration> Declarations { get; } = new();
    public List<Reference> References { get; } = new();
    /// <summary>For names declared <c>global</c>/<c>nonlocal</c> in Scope: the symbol they really denote.</summary>
    public Symbol? Redirect { get; internal set; }

    internal Symbol(string name, Scope scope) { Name = name; Scope = scope; }
    public override string ToString() => $"{Scope.Kind}:{Name}";
}

/// <summary>A use of a name: the NameExpr, what it resolved to (null when unresolved or builtin), and how it is accessed.</summary>
public sealed record Reference(NameExpr Node, Symbol? Symbol, bool IsBuiltin, Access Access);

public sealed class Scope
{
    public ScopeKind Kind { get; }
    /// <summary>The Module, FunctionDef, ClassDef, LambdaExpr, CompExpr or (for type params) the def/alias node.</summary>
    public Node Node { get; }
    public Scope? Parent { get; }
    public List<Scope> Children { get; } = new();
    public Dictionary<string, Symbol> Symbols { get; } = new(StringComparer.Ordinal);
    /// <summary>A "from m import *" occurred here, so any unresolved name might come from it.</summary>
    public bool HasStarImport { get; internal set; }

    internal HashSet<string> GlobalNames { get; } = new();
    internal HashSet<string> NonlocalNames { get; } = new();

    internal Scope(ScopeKind kind, Node node, Scope? parent)
    {
        Kind = kind; Node = node; Parent = parent;
        parent?.Children.Add(this);
    }

    public Scope ModuleScope { get { var s = this; while (s.Parent != null) s = s.Parent; return s; } }

    internal Symbol GetOrAdd(string name)
    {
        if (!Symbols.TryGetValue(name, out var s)) Symbols[name] = s = new Symbol(name, this);
        return s;
    }

    /// <summary>
    /// Python name lookup: own scope, then enclosing function-like scopes and the module.
    /// Class scopes are visible only to code directly in the class body.
    /// </summary>
    public Symbol? Lookup(string name)
    {
        for (Scope? s = this; s != null; s = s.Parent)
        {
            if (s.Kind == ScopeKind.Class && s != this) continue;
            if (s.Symbols.TryGetValue(name, out var sym)) return sym.Redirect ?? sym;
        }
        return null;
    }

    /// <summary>All names visible from this scope (innermost binding wins), e.g. for completion.</summary>
    public IEnumerable<Symbol> VisibleSymbols()
    {
        var seen = new HashSet<string>();
        for (Scope? s = this; s != null; s = s.Parent)
        {
            if (s.Kind == ScopeKind.Class && s != this) continue;
            foreach (var kv in s.Symbols)
                if (seen.Add(kv.Key)) yield return kv.Value.Redirect ?? kv.Value;
        }
    }

    public override string ToString() => $"{Kind} [{string.Join(", ", Symbols.Keys)}]";
}

using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>A class definition. Everything derived from it (bases, MRO, members) is computed by the evaluator and cached there.</summary>
public sealed class ClassInfo
{
    public string Name { get; }
    public ModuleInfo Module { get; }
    public ClassDef Node { get; }
    /// <summary>The class body scope (members live in its symbols).</summary>
    public Scope Scope { get; }

    internal ClassInfo(string name, ModuleInfo module, ClassDef node, Scope scope)
    {
        Name = name; Module = module; Node = node; Scope = scope;
    }

    string? _qualifiedName;

    /// <summary>`module.Class`, made once: the evaluator compares it on every call and assignment it checks.</summary>
    public string QualifiedName => _qualifiedName ??= $"{Module.Name}.{Name}";
    public override string ToString() => QualifiedName;
}

/// <summary>A def or lambda. The signature is computed by the evaluator.</summary>
public sealed class FunctionInfo
{
    public string Name { get; }
    public ModuleInfo Module { get; }
    /// <summary>FunctionDef or LambdaExpr.</summary>
    public Node Node { get; }
    public Scope Scope { get; }
    /// <summary>The class whose body directly contains this def, if any.</summary>
    public ClassInfo? Owner { get; internal set; }
    public FuncFlags Flags { get; internal set; }

    internal FunctionInfo(string name, ModuleInfo module, Node node, Scope scope)
    {
        Name = name; Module = module; Node = node; Scope = scope;
    }

    public bool IsStatic => (Flags & FuncFlags.Static) != 0;
    public bool IsClassMethod => (Flags & FuncFlags.ClassMethod) != 0;
    public bool IsProperty => (Flags & FuncFlags.Property) != 0;
    public override string ToString() => Owner != null ? $"{Owner.Name}.{Name}" : Name;
}

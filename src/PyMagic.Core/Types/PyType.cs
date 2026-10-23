using System.Text;

namespace PyMagic.Core.Types;

/// <summary>Base of the type model. Types are immutable values; equality is structural where it matters (unions).</summary>
public abstract record PyType
{
    /// <summary>Not known statically. Never produces diagnostics; unions keep it as a member.</summary>
    public static readonly PyType Unknown = new UnknownType(false, false);
    /// <summary>Explicit typing.Any.</summary>
    public static readonly PyType Any = new UnknownType(true, false);
    public static readonly PyType Never = new NeverType();
    /// <summary>
    /// Placeholder for a value that depends on itself (x = x + 1 in a loop). Behaves like Unknown, but a union
    /// drops it as soon as it has any real member, so loop-carried variables converge to their real type.
    /// </summary>
    internal static readonly PyType CycleUnknown = new UnknownType(false, true);

    public bool IsDynamic => this is UnknownType;

    /// <summary>Union of the given types: flattened, deduplicated, no subtype collapsing.</summary>
    public static PyType Union(IEnumerable<PyType> types)
    {
        var list = new List<PyType>();
        foreach (var t in types)
        {
            if (t is NeverType) continue;
            if (t is UnionType u) { foreach (var m in u.Members) AddUnique(list, m); }
            else AddUnique(list, t);
        }
        // placeholders for self-dependent values disappear once the union has any real member
        if (list.Count > 1 && list.Any(x => x is not UnknownType { FromCycle: true }))
            list.RemoveAll(x => x is UnknownType { FromCycle: true });
        return list.Count switch { 0 => Never, 1 => list[0], _ => new UnionType(list) };
    }

    public static PyType Union(params PyType[] types) => Union((IEnumerable<PyType>)types);

    static void AddUnique(List<PyType> list, PyType t)
    {
        foreach (var x in list) if (x.Equals(t)) return;
        list.Add(t);
    }

    /// <summary>The members of a union, or the type itself.</summary>
    public IReadOnlyList<PyType> Members => this is UnionType u ? u.MemberList : new[] { this };

    public abstract string Display(int depth = 0);
    public sealed override string ToString() => Display();
}

public sealed record UnknownType(bool Explicit, bool FromCycle) : PyType
{
    public override string Display(int depth = 0) => Explicit ? "Any" : "Unknown";
}

public sealed record NeverType : PyType
{
    public override string Display(int depth = 0) => "Never";
}

public sealed record UnionType : PyType
{
    public IReadOnlyList<PyType> MemberList { get; }
    internal UnionType(List<PyType> members) { MemberList = members; }

    public bool Equals(UnionType? other) =>
        other != null && MemberList.Count == other.MemberList.Count && MemberList.All(m => other.MemberList.Contains(m));
    public override int GetHashCode() => MemberList.Aggregate(17, (h, m) => h ^ m.GetHashCode());

    public override string Display(int depth = 0) =>
        string.Join(" | ", MemberList.Select(m => m.Display(depth + 1)));
}

/// <summary>A class object (IsInstance = false, printed type[C]) or an instance of a class (IsInstance = true).</summary>
/// <param name="EnumMember">For an instance of an enum class: the member it is known to be (Color.RED), else null.</param>
public sealed record ClassType(ClassInfo Info, IReadOnlyList<PyType> Args, bool IsInstance, IReadOnlyList<PyType>? TupleItems = null, string? EnumMember = null) : PyType
{
    public ClassType(ClassInfo info, bool isInstance) : this(info, Array.Empty<PyType>(), isInstance) { }

    public ClassType AsInstance() => IsInstance ? this : this with { IsInstance = true };
    public ClassType AsClassObject() => IsInstance ? this with { IsInstance = false } : this;

    public bool Equals(ClassType? other) =>
        other != null && ReferenceEquals(Info, other.Info) && IsInstance == other.IsInstance
        && Args.SequenceEqual(other.Args) && EnumMember == other.EnumMember
        && (TupleItems == null ? other.TupleItems == null : other.TupleItems != null && TupleItems.SequenceEqual(other.TupleItems));

    public override int GetHashCode() => HashCode.Combine(Info.Name, IsInstance, Args.Count, TupleItems?.Count);

    public override string Display(int depth = 0)
    {
        if (IsInstance && Info.Name == "NoneType" && Info.Module.Name is "builtins" or "types") return "None";
        if (IsInstance && EnumMember != null) return $"{Info.Name}.{EnumMember}";
        var sb = new StringBuilder(Info.Name);
        if (TupleItems != null)
        {
            sb.Append('[');
            sb.Append(TupleItems.Count == 0 ? "()" : string.Join(", ", TupleItems.Select(a => a.Display(depth + 1))));
            sb.Append(']');
        }
        else if (Info.Name == "tuple" && Info.Module.Name == "builtins" && Args.Count == 1)
            sb.Append('[').Append(Args[0].Display(depth + 1)).Append(", ...]");
        else if (Args.Count > 0)
            sb.Append('[').Append(string.Join(", ", Args.Select(a => a.Display(depth + 1)))).Append(']');
        return IsInstance ? sb.ToString() : $"type[{sb}]";
    }
}

public sealed record TypeVarType(string Name) : PyType
{
    public override string Display(int depth = 0) => Name;
}

public sealed record ModuleType(ModuleInfo Module) : PyType
{
    public bool Equals(ModuleType? o) => o != null && ReferenceEquals(Module, o.Module);
    public override int GetHashCode() => Module.Name.GetHashCode();
    public override string Display(int depth = 0) => $"Module('{Module.Name}')";
}

/// <summary>A def or lambda, optionally bound to a receiver (first parameter already supplied).</summary>
/// <param name="Subst">TypeVar substitution coming from the receiver, e.g. T -> int for a list[int] method.</param>
public sealed record FunctionType(FunctionInfo Info, bool BoundFirst, IReadOnlyDictionary<string, PyType>? Subst = null) : PyType
{
    public bool Equals(FunctionType? o) => o != null && ReferenceEquals(Info, o.Info) && BoundFirst == o.BoundFirst;
    public override int GetHashCode() => Info.Name.GetHashCode() ^ (BoundFirst ? 1 : 0);
    public override string Display(int depth = 0) => $"function {Info.Name}";
}

/// <summary>A callable described only by a signature, from Callable[[A], R] annotations.</summary>
public sealed record SignatureType(IReadOnlyList<ParamInfo>? Params, PyType Return) : PyType
{
    public bool Equals(SignatureType? o) =>
        o != null && Return.Equals(o.Return) && (Params == null ? o.Params == null : o.Params != null && Params.SequenceEqual(o.Params));
    public override int GetHashCode() => Return.GetHashCode();
    public override string Display(int depth = 0) =>
        $"({(Params == null ? "..." : string.Join(", ", Params.Select(p => p.Type.Display(depth + 1))))}) -> {Return.Display(depth + 1)}";
}

/// <summary>Result of super() inside a method: attribute lookup starts after Current in the MRO of Receiver.</summary>
public sealed record SuperType(ClassInfo Current, PyType Receiver) : PyType
{
    public bool Equals(SuperType? o) => o != null && ReferenceEquals(Current, o.Current) && Receiver.Equals(o.Receiver);
    public override int GetHashCode() => Current.Name.GetHashCode();
    public override string Display(int depth = 0) => "super";
}

/// <summary>Names in typing that need special handling when used in annotations (Optional, Union, Callable, ...).</summary>
public sealed record SpecialFormType(string Name) : PyType
{
    public override string Display(int depth = 0) => $"typing.{Name}";
}

public enum ParamCategory { PositionalOnly, Normal, KeywordOnly, VarPositional, VarKeyword }

public sealed record ParamInfo(string Name, PyType Type, ParamCategory Category, bool HasDefault);

[Flags]
public enum FuncFlags
{
    None = 0, Static = 1, ClassMethod = 2, Property = 4, Abstract = 8, Overload = 16,
    Async = 32, Generator = 64, PropertySetter = 128,
}

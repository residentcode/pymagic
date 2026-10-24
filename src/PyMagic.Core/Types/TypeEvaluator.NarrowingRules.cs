using PyMagic.Core.Flow;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>What conditions other than `is None`, truthiness and `isinstance` tell us: enum members, `type(x) is C`, `in`, `callable`, `hasattr`, `issubclass` and TypeGuard/TypeIs functions.</summary>
public sealed partial class TypeEvaluator
{
    /// <summary>True when <paramref name="e"/> (or the target of a walrus) is the reference the assertion narrows.</summary>
    bool IsReferenceSide(ModuleInfo m, Expr e, Instruction assertion)
    {
        e = e is NamedExpr { Target: NameExpr t } ? t : e;
        if (!CfgBuilder.TryChain(e, out var root, out var path) || path != assertion.Path) return false;
        return ReferenceEquals(m.Semantics.ReferenceOf(root)?.Symbol, assertion.Symbol);
    }

    // ------------------------------------------------------------ comparisons

    PyType NarrowCompare(ModuleInfo m, Instruction assertion, CompareExpr c, PyType t, bool positive)
    {
        string op = c.Ops[0];
        var right = c.Comparators[0];

        if (op is "in" or "not in")
            return IsReferenceSide(m, c.Left, assertion) ? NarrowIn(m, right, t, positive == (op == "in")) : t;

        bool holdsWhenTrue = op is "is" or "==";
        bool equal = positive == holdsWhenTrue;     // on this branch, the reference equals the other operand

        // type(x) is C
        if (c.Left is CallExpr { Func: NameExpr { Name: "type" }, Args.Count: 1 } tc && IsReferenceSide(m, tc.Args[0].Value, assertion))
            return equal ? NarrowExactType(m, right, t) : t;

        Expr other;
        if (IsReferenceSide(m, c.Left, assertion)) other = right;
        else if (IsReferenceSide(m, right, assertion)) other = c.Left;
        else return t;

        if (other is ConstExpr { Kind: ConstKind.None }) return equal ? OnlyNone(t) : WithoutNone(t);

        // x == "a", x != 3, x is True
        if (LiteralOf(other) is { } literal) return NarrowLiteral(t, literal, equal);

        // x is Color.RED, x == Color.RED
        if (other is NameExpr or AttributeExpr && TypeOf(m, other) is ClassType { IsInstance: true, EnumMember: not null } member && IsEnumClass(member.Info))
            return equal ? NarrowToEnumMember(t, member, identity: op is "is" or "is not") : WithoutEnumMember(t, member);

        return t;
    }

    PyType NarrowToEnumMember(PyType t, ClassType member, bool identity)
    {
        var result = new List<PyType>();
        foreach (var x in t.Members)
        {
            switch (x)
            {
                case ClassType { IsInstance: true } xc when ReferenceEquals(xc.Info, member.Info):
                    if (xc.EnumMember == null || xc.EnumMember == member.EnumMember) result.Add(member);
                    break;
                case UnknownType:
                case TypeVarType:
                    result.Add(member);
                    break;
                case ClassType { IsInstance: true } xc when IsNone(xc):
                    break;
                default:
                    if (!identity) result.Add(x);   // `==` can be true for other types; `is` cannot
                    break;
            }
        }
        return result.Count == 0 ? PyType.Never : PyType.Union(result);
    }

    /// <summary>The type after `x is not Color.RED`: that member goes; an enum whose members are all known splits into the others.</summary>
    PyType WithoutEnumMember(PyType t, ClassType member)
    {
        var result = new List<PyType>();
        foreach (var x in t.Members)
        {
            if (x is ClassType { IsInstance: true } xc && ReferenceEquals(xc.Info, member.Info))
            {
                if (xc.EnumMember == member.EnumMember) continue;
                if (xc.EnumMember == null)
                {
                    var members = EnumMembersOf(xc.Info);
                    if (members.Count > 1 && members.Any(mm => mm.Name == member.EnumMember))
                    {
                        result.AddRange(members.Where(mm => mm.Name != member.EnumMember).Select(mm => (PyType)new ClassType(xc.Info, xc.Args, true, null, mm.Name)));
                        continue;
                    }
                }
            }
            result.Add(x);
        }
        return result.Count == 0 ? PyType.Never : PyType.Union(result);
    }

    /// <summary>`type(x) is C`: instances of exactly C, so subclasses of C and superclasses that C derives from become C.</summary>
    PyType NarrowExactType(ModuleInfo m, Expr classExpr, PyType t)
    {
        if (TypeOf(m, classExpr) is not ClassType { IsInstance: false } cls) return t;
        var result = new List<PyType>();
        foreach (var x in t.Members)
        {
            switch (x)
            {
                case UnknownType:
                case TypeVarType:
                    result.Add(cls.AsInstance());
                    break;
                case ClassType { IsInstance: true } xc:
                    if (IsSubclass(cls.Info, xc.Info) || IsSubclass(xc.Info, cls.Info) || HasUnknownAncestor(xc.Info) || HasUnknownAncestor(cls.Info))
                        result.Add(cls.AsInstance());
                    break;
            }
        }
        return result.Count == 0 ? PyType.Never : PyType.Union(result);
    }

    // ------------------------------------------------------------ in

    /// <summary>`x in container`: x is one of the container's elements, so None goes unless the elements can be None.</summary>
    PyType NarrowIn(ModuleInfo m, Expr container, PyType t, bool isIn)
    {
        // x in ("a", "b"): a Literal member stays when it is one of the values (and goes when it is, for `not in`)
        var values = LiteralSet(container);
        if (values != null && t.Members.Any(x => x is ClassType { IsInstance: true, LiteralValue: not null } || IsNone(x)))
        {
            var kept = new List<PyType>();
            foreach (var x in t.Members)
            {
                if (x is ClassType { IsInstance: true, LiteralValue: { } lv }) { if (values.Any(v => Equals(v.Value, lv)) == isIn) kept.Add(x); }
                else if (IsNone(x)) { if (!isIn) kept.Add(x); }
                else kept.Add(x);
            }
            return kept.Count == 0 ? PyType.Never : PyType.Union(kept);
        }
        if (!isIn) return t;
        // only None and enum members can be removed, so most conditions need no look at the container
        if (!t.Members.Any(x => IsNone(x) || x is ClassType { IsInstance: true } c && IsEnumClass(c.Info))) return t;
        var elements = IterElement(TypeOf(m, container));
        if (elements.Members.Any(e => e is UnknownType || IsNone(e) || e is TypeVarType)) return t;
        if (elements is NeverType) return t;
        // an enum member in a tuple of members: narrow to those members
        var members = new List<ClassType>();
        foreach (var e in elements.Members) if (e is ClassType { IsInstance: true, EnumMember: not null } ec && IsEnumClass(ec.Info)) members.Add(ec);
        if (members.Count == elements.Members.Count && members.Count > 0)
        {
            var result = new List<PyType>();
            foreach (var x in t.Members)
            {
                if (x is ClassType { IsInstance: true } xc && IsEnumClass(xc.Info))
                    result.AddRange(members.Where(em => ReferenceEquals(em.Info, xc.Info) && (xc.EnumMember == null || xc.EnumMember == em.EnumMember)));
                else if (x is UnknownType) result.Add(x);
            }
            return result.Count == 0 ? t : PyType.Union(result);
        }
        return WithoutNone(t);
    }

    // ------------------------------------------------------------ calls

    PyType NarrowCall(ModuleInfo m, CallExpr call, PyType t, bool positive)
    {
        string? name = call.Func is NameExpr n ? n.Name : null;
        int count = call.Args.Count;

        if (name == "isinstance" && count == 2)
        {
            var classes = IsinstanceClasses(m, call.Args[1].Value);
            if (classes == null) return t;
            return positive ? IsInstancePositive(t, classes) : IsInstanceNegative(t, classes);
        }
        if (name == "issubclass" && count == 2)
        {
            var classes = IsinstanceClasses(m, call.Args[1].Value);
            return classes == null ? t : positive ? IsSubclassPositive(t, classes) : IsSubclassNegative(t, classes);
        }
        if (name == "callable" && count == 1) return positive ? KeepCallable(t) : DropCallable(t);
        if (name == "hasattr" && count == 2 && StringValue(call.Args[1].Value) is { } attr) return positive ? WithAttribute(t, attr) : t;

        // a function declared `-> TypeGuard[T]` or `-> TypeIs[T]`
        var guard = GuardOf(m, call);
        if (guard == null) return t;
        var (narrowedTo, isTypeIs) = guard.Value;
        if (!isTypeIs) return positive ? narrowedTo : t;
        var guardClasses = narrowedTo.Members.OfType<ClassType>().Where(c => c.IsInstance).Select(c => c.AsClassObject()).ToList();
        if (guardClasses.Count == 0) return t;
        return positive ? IsInstancePositive(t, guardClasses) : IsInstanceNegative(t, guardClasses);
    }

    (PyType Type, bool IsTypeIs)? GuardOf(ModuleInfo m, CallExpr call)
    {
        foreach (var target in new[] { TypeOf(m, call.Func) }.SelectMany(x => x.Members))
        {
            if (target is not FunctionType { Info.Node: FunctionDef def } ft || def.Returns == null) continue;
            string? head = AnnotationHead(def.Returns);
            if (head is not ("TypeGuard" or "TypeIs") || def.Returns is not SubscriptExpr sub) continue;
            return (AnnotationType(ft.Info.Module, sub.Index), head == "TypeIs");
        }
        return null;
    }

    PyType IsSubclassPositive(PyType t, List<ClassType> classes)
    {
        var result = new List<PyType>();
        foreach (var member in t.Members)
        {
            switch (member)
            {
                case UnknownType:
                case TypeVarType:
                    result.AddRange(classes.Select(c => (PyType)c.AsClassObject()));
                    break;
                case ClassType { IsInstance: false } mc:
                    foreach (var fc in classes)
                    {
                        if (IsSubclass(mc.Info, fc.Info)) result.Add(mc);
                        else if (IsSubclass(fc.Info, mc.Info) || HasUnknownAncestor(mc.Info) || HasUnknownAncestor(fc.Info)) result.Add(fc.AsClassObject());
                    }
                    break;
            }
        }
        return result.Count == 0 ? PyType.Never : PyType.Union(result);
    }

    PyType IsSubclassNegative(PyType t, List<ClassType> classes) =>
        PyType.Union(t.Members.Where(member =>
            !(member is ClassType { IsInstance: false } mc && classes.Any(fc => IsSubclass(mc.Info, fc.Info)))));

    // ------------------------------------------------------------ callable and hasattr

    /// <summary>Surely callable: functions, class objects, and instances whose class defines __call__.</summary>
    bool IsCallable(PyType x) => x switch
    {
        FunctionType or OverloadedType or SignatureType => true,
        ClassType { IsInstance: false } => true,
        ClassType { IsInstance: true } c => FindMember(c, "__call__", true, null, out _) != null,
        _ => false,
    };

    PyType KeepCallable(PyType t)
    {
        var result = t.Members.Where(x => IsCallable(x) || x is UnknownType or TypeVarType
            || x is ClassType { IsInstance: true, Info.Name: "object" }).ToList();
        return result.Count == 0 ? PyType.Never : PyType.Union(result);
    }

    PyType DropCallable(PyType t) => PyType.Union(t.Members.Where(x => !IsCallable(x)));

    /// <summary>After `hasattr(x, "n")`: members that have the attribute stay, the others become Unknown so that using it is not reported.</summary>
    PyType WithAttribute(PyType t, string attribute) =>
        PyType.Union(t.Members.Select(x => x is ClassType { IsInstance: true } && AttributeType(x, attribute).Missing.Count > 0 ? PyType.Unknown : x));

    // ------------------------------------------------------------ match statements

    /// <summary>What `case <pattern>:` tells us about the subject when it matched, or when it did not.</summary>
    PyType NarrowPattern(ModuleInfo m, Pattern pattern, PyType t, bool matched)
    {
        switch (pattern)
        {
            case MatchAs a:
                return NarrowPattern(m, a.Pattern, t, matched);

            case MatchOr o:
                if (matched) return PyType.Union(o.Alternatives.Select(alt => NarrowPattern(m, alt, t, true)));
                foreach (var alt in o.Alternatives) t = NarrowPattern(m, alt, t, false);
                return t;

            case MatchClass c:
            {
                if (TypeOf(m, c.Cls) is not ClassType { IsInstance: false } cls) return t;
                var classes = new List<ClassType> { cls };
                if (matched) return IsInstancePositive(t, classes);
                // `case Point(x=0)` can fail for a Point: only a pattern without sub-patterns excludes the class
                return c.Positional.Count == 0 && c.KeywordNames.Count == 0 ? IsInstanceNegative(t, classes) : t;
            }

            case MatchValue v:
            {
                if (v.Value is ConstExpr { Kind: ConstKind.None }) return matched ? OnlyNone(t) : WithoutNone(t);
                if (LiteralOf(v.Value) is { } literal) return NarrowLiteral(t, literal, matched);
                if (v.Value is NameExpr or AttributeExpr && TypeOf(m, v.Value) is ClassType { IsInstance: true, EnumMember: not null } member && IsEnumClass(member.Info))
                    return matched ? NarrowToEnumMember(t, member, identity: false) : WithoutEnumMember(t, member);
                return t;
            }
        }
        return t;
    }

    // ------------------------------------------------------------ literal values

    /// <summary>A constant in the source that can be a Literal value: "a", 3, -1, b"x", True, False.</summary>
    static (string Class, object Value)? LiteralOf(Expr e) => e switch
    {
        ConstExpr { Kind: ConstKind.Str, IsFString: false } c when StringValue(c) is { } s => ("str", s),
        ConstExpr { Kind: ConstKind.Int } c when LiteralInt(c.Text, negative: false) is { } i => ("int", i),
        UnaryExpr { Op: "-", Operand: ConstExpr { Kind: ConstKind.Int } c } when LiteralInt(c.Text, negative: true) is { } i => ("int", i),
        ConstExpr { Kind: ConstKind.Bytes } c when LiteralBytesValue(c) is { } b => ("bytes", b),
        ConstExpr { Kind: ConstKind.True } => ("bool", true),
        ConstExpr { Kind: ConstKind.False } => ("bool", false),
        _ => null,
    };

    /// <summary>The values of a tuple, list or set display made only of constants; null for anything else.</summary>
    static List<(string Class, object Value)>? LiteralSet(Expr container)
    {
        var items = container switch { TupleExpr t => t.Items, ListExpr l => l.Items, SetExpr s => s.Items, _ => null };
        if (items == null || items.Count == 0) return null;
        var values = new List<(string, object)>();
        foreach (var it in items)
        {
            if (LiteralOf(it) is not { } v) return null;
            values.Add(v);
        }
        return values;
    }

    static bool IsTruthyLiteral(object value) => value switch
    {
        string s => s.Length > 0,
        long l => l != 0,
        bool b => b,
        LiteralBytes bytes => bytes.Raw.Length > 0,
        _ => true,
    };

    /// <summary>The type on the branch where the reference equals the constant (<paramref name="equal"/>) or differs from it.</summary>
    PyType NarrowLiteral(PyType t, (string Class, object Value) literal, bool equal)
    {
        var result = new List<PyType>();
        foreach (var x in t.Members)
        {
            if (x is not ClassType { IsInstance: true } xc) { result.Add(x); continue; }
            bool sameClass = xc.Info.Module.Name == "builtins" && xc.Info.Name == literal.Class;

            if (xc.LiteralValue != null)
            {
                if (sameClass) { if (Equals(xc.LiteralValue, literal.Value) == equal) result.Add(xc); }
                else if (!equal || !(literal.Class is "str" or "bytes" || xc.Info.Name is "str" or "bytes")) result.Add(xc);   // a str is never equal to an int
            }
            else if (IsNone(xc)) { if (!equal) result.Add(xc); }
            else if (sameClass && literal.Class == "bool")
            {
                // `x is True` leaves a bool that is True
                bool value = (bool)literal.Value;
                result.Add(xc with { LiteralValue = equal ? value : !value });
            }
            else result.Add(xc);
        }
        return result.Count == 0 ? PyType.Never : PyType.Union(result);
    }
}

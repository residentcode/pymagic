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

        // len(x) == 2, len(x) < 3, 3 > len(x)
        if (op is "==" or "!=" or "<" or "<=" or ">" or ">=")
        {
            string? lenOp = null;
            long length = 0;
            if (CfgBuilder.LenArgument(c.Left) is { } a && IsReferenceSide(m, a, assertion) && LiteralOf(right) is ("int", long n1)) (lenOp, length) = (op, n1);
            else if (CfgBuilder.LenArgument(right) is { } b && IsReferenceSide(m, b, assertion) && LiteralOf(c.Left) is ("int", long n2)) (lenOp, length) = (Flipped(op), n2);
            if (lenOp != null) return NarrowLength(t, positive ? lenOp : Negated(lenOp), length);
            if (op is not ("==" or "!=")) return t;
        }

        // x[0] == "a", x[1] is None: the tuples whose item cannot be that go
        if (c.Left is SubscriptExpr ls && IsReferenceSide(m, ls.Value, assertion) && ConstIndex(ls.Index) is int li) return NarrowByItem(m, t, li, right, equal, op);
        if (right is SubscriptExpr rs && IsReferenceSide(m, rs.Value, assertion) && ConstIndex(rs.Index) is int ri) return NarrowByItem(m, t, ri, c.Left, equal, op);
        // x["type"] == "union" on a union of TypedDicts: those whose key cannot be that go
        if (c.Left is SubscriptExpr lk && IsReferenceSide(m, lk.Value, assertion) && StringValue(lk.Index) is string lkey) return NarrowByTypedDictKey(t, lkey, right, equal);
        if (right is SubscriptExpr rk && IsReferenceSide(m, rk.Value, assertion) && StringValue(rk.Index) is string rkey) return NarrowByTypedDictKey(t, rkey, c.Left, equal);

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

    /// <summary>
    /// `x[i] == L`, `x[i] != L`, `x[i] is None`, `x[i] is Color.RED` on a union of tuples (as pyright does): a fixed tuple whose
    /// item i cannot be what the comparison says on this branch goes; the others, and anything else, stay as they are.
    /// </summary>
    PyType NarrowByItem(ModuleInfo m, PyType t, int index, Expr other, bool equal, string op)
    {
        Func<PyType, PyType>? narrowItem = null;
        if (other is ConstExpr { Kind: ConstKind.None }) narrowItem = item => equal ? OnlyNone(item) : WithoutNone(item);
        else if (LiteralOf(other) is { } literal) narrowItem = item => NarrowLiteral(item, literal, equal);
        else if (other is NameExpr or AttributeExpr && TypeOf(m, other) is ClassType { IsInstance: true, EnumMember: not null } member && IsEnumClass(member.Info))
            narrowItem = item => equal ? NarrowToEnumMember(item, member, identity: op is "is" or "is not") : WithoutEnumMember(item, member);
        if (narrowItem == null) return t;
        var kept = new List<PyType>();
        foreach (var x in t.Members)
        {
            if (x is ClassType { IsInstance: true } c && TupleItemsOf(c) is { } items)
            {
                int i = index < 0 ? items.Count + index : index;
                if (i >= 0 && i < items.Count && !items[i].IsDynamic && narrowItem(items[i]) is NeverType) continue;
            }
            kept.Add(x);
        }
        return kept.Count == 0 ? PyType.Never : PyType.Union(kept);
    }

    /// <summary>
    /// `x["type"] == "union"`, `x["type"] != "union"` on a union of TypedDicts that tell themselves apart by a literal key (pydantic's core schemas,
    /// as mypy and pyright narrow them): a TypedDict whose declared key cannot be the literal on this branch goes; the others, and anything else, stay.
    /// </summary>
    PyType NarrowByTypedDictKey(PyType t, string key, Expr other, bool equal)
    {
        if (LiteralOf(other) is not { } literal || t is not UnionType) return t;
        var kept = new List<PyType>();
        foreach (var x in t.Members)
        {
            if (x is ClassType { IsInstance: true } c && TypedDictFieldsOf(c.Info) is { } fields
                && fields.FirstOrDefault(f => f.Field.Name == key) is { Field: { } field, Required: true }
                && field.Type is ClassType { LiteralValue: not null } or UnionType && NarrowLiteral(field.Type, literal, equal) is NeverType) continue;
            kept.Add(x);
        }
        return kept.Count == 0 ? t : PyType.Union(kept);
    }

    /// <summary>A key's value pattern in `case {key: pattern}:`: a literal, or literals joined by `|`, narrow by the key; anything else leaves the union.</summary>
    PyType NarrowByTypedDictKeyPattern(PyType t, string key, Pattern pattern) => pattern switch
    {
        MatchAs { Pattern: { } inner } => NarrowByTypedDictKeyPattern(t, key, inner),
        MatchValue v when LiteralOf(v.Value) != null => NarrowByTypedDictKey(t, key, v.Value, equal: true),
        MatchOr o when o.Alternatives.All(a => a is MatchValue av && LiteralOf(av.Value) != null)
            => PyType.Union(o.Alternatives.Select(a => NarrowByTypedDictKey(t, key, ((MatchValue)a).Value, equal: true))),
        _ => t,
    };

    static string Flipped(string op) => op switch { "<" => ">", "<=" => ">=", ">" => "<", ">=" => "<=", _ => op };
    static string Negated(string op) => op switch { "==" => "!=", "!=" => "==", "<" => ">=", "<=" => ">", ">" => "<=", _ => "<" };

    /// <summary>
    /// `len(x) == n`, `len(x) < n` and the like (<paramref name="op"/> holds on this branch): a fixed tuple stays when its length
    /// fits; `tuple[X, ...]` becomes the fixed tuples of the lengths that fit, when none is longer than 8, or the "at least n"
    /// form `tuple[X, X, *tuple[X, ...]]` for `>=` and `>` (n up to 8); a tuple already in that form likewise.
    /// </summary>
    PyType NarrowLength(PyType t, string op, long n)
    {
        bool Fits(long k) => op switch { "==" => k == n, "!=" => k != n, "<" => k < n, "<=" => k <= n, ">" => k > n, _ => k >= n };
        long? longest = op switch { "==" or "<=" => n, "<" => Math.Max(n, 0) - 1, _ => null };     // null: no longest length
        var result = new List<PyType>();
        foreach (var x in t.Members)
        {
            if (x is ClassType { IsInstance: true, Info.Name: "tuple" } tup && tup.Info.Module.Name == "builtins")
            {
                // tuple[int, *Ts] has no fixed length
                if (tup.TupleItems != null && !tup.TupleItems.Any(i => i is TypeVarTupleType or UnboundedItemsType)) { if (Fits(tup.TupleItems.Count)) result.Add(x); continue; }
                if (AtLeastParts(tup) is { } p)
                {
                    int least = p.Before.Count + p.After.Count;
                    if (longest != null)
                    {
                        if (longest > 8) { result.Add(x); continue; }
                        for (int k = least; k <= longest; k++) if (Fits(k)) result.Add(Lengthened(p, k, false));
                        continue;
                    }
                    // `len(t) >= 2`, `len(t) > 1`, `len(t) != 0`: at least so many items
                    long atLeast = op switch { ">=" => n, ">" => n + 1, _ => n == least ? n + 1 : least };
                    result.Add(atLeast > least && atLeast <= 8 ? Lengthened(p, (int)atLeast, true) : x);
                    continue;
                }
            }
            result.Add(x);
        }
        return result.Count == 0 ? PyType.Never : PyType.Union(result);
    }

    /// <summary>
    /// The parts of a tuple of unbounded length: the items before a `*tuple[X, ...]`, X, and the items after it (none
    /// before or after for `tuple[X, ...]`); null for a fixed tuple and any other type.
    /// </summary>
    static (IReadOnlyList<PyType> Before, PyType Item, IReadOnlyList<PyType> After)? AtLeastParts(PyType x)
    {
        if (x is not ClassType { IsInstance: true, Info.Name: "tuple" } c || c.Info.Module.Name != "builtins") return null;
        if (c.TupleItems == null) return c.Args.Count == 1 ? (Array.Empty<PyType>(), c.Args[0], Array.Empty<PyType>()) : null;
        var items = c.TupleItems;
        if (items.Any(i => i is TypeVarTupleType)) return null;
        int at = items.ToList().FindIndex(i => i is UnboundedItemsType);
        if (at < 0) return null;
        return (items.Take(at).ToList(), ((UnboundedItemsType)items[at]).Item, items.Skip(at + 1).ToList());
    }

    /// <summary>The tuple of <paramref name="length"/> items (or, <paramref name="orMore"/>, of at least that many) that an unbounded tuple can be.</summary>
    PyType Lengthened((IReadOnlyList<PyType> Before, PyType Item, IReadOnlyList<PyType> After) p, int length, bool orMore)
    {
        if (BuiltinClass("tuple") is not { } tuple) return PyType.Unknown;
        var items = p.Before.Concat(Enumerable.Repeat(p.Item, length - p.Before.Count - p.After.Count));
        if (!orMore) return TupleOf(items.Concat(p.After).ToList());
        return TupleOf(tuple, items.Append(new UnboundedItemsType(p.Item)).Concat(p.After).ToList());
    }

    static readonly HashSet<string> NotSequences = new() { "str", "bytes", "bytearray", "int", "float", "bool", "complex", "dict", "set", "frozenset", "NoneType" };
    static readonly HashSet<string> NotMappings = new() { "tuple", "list", "str", "bytes", "bytearray", "int", "float", "bool", "complex", "set", "frozenset", "range", "NoneType" };

    static bool IsBuiltinInstance(PyType x, HashSet<string> names) =>
        x is ClassType { IsInstance: true } c && (IsNone(c) ? names.Contains("NoneType") : c.Info.Module.Name == "builtins" && names.Contains(c.Info.Name));

    /// <summary>`case [a, b]:`, `case (x, *rest):`: only sequences match, a tuple only when its length fits; the items of a fixed tuple are narrowed by their sub-patterns.</summary>
    PyType NarrowSequence(ModuleInfo m, MatchSequence p, PyType t, bool matched)
    {
        bool star = p.Items.Any(i => i is MatchStar);
        int count = p.Items.Count(i => i is not MatchStar);
        var result = new List<PyType>();
        foreach (var x in t.Members)
        {
            var unbounded = AtLeastParts(x);
            bool fits = unbounded is { } u ? star || count >= u.Before.Count + u.After.Count
                : x is ClassType { IsInstance: true, Info.Name: "tuple", TupleItems: { } items } tc && tc.Info.Module.Name == "builtins" ? (star ? items.Count >= count : items.Count == count) : true;
            if (matched)
            {
                if (IsBuiltinInstance(x, NotSequences) || !fits) continue;
                var y = x;
                if (unbounded != null && !star) y = Lengthened(unbounded.Value, count, false);
                if (y is ClassType { TupleItems: { } fixedItems } && fixedItems.All(i => i is not (TypeVarTupleType or UnboundedItemsType)))
                {
                    // a tuple in which some item cannot match is left out
                    var narrowed = fixedItems.ToList();
                    foreach (var (sub, index) in ItemPatterns(p, narrowed.Count)) narrowed[index] = NarrowPattern(m, sub, narrowed[index], true);
                    if (narrowed.Any(i => i is NeverType)) continue;
                    if (!narrowed.SequenceEqual(fixedItems)) y = TupleOf(narrowed);
                }
                result.Add(y);
            }
            // `case (a, b, *rest):` surely matches a tuple of at least two items
            else if (unbounded is { } least && star && count <= least.Before.Count + least.After.Count && p.Items.All(Irrefutable)) continue;
            else if (fits && unbounded == null && x is ClassType { IsInstance: true, Info.Name: "tuple", TupleItems: { } fixedItems })
            {
                // a fitting tuple goes when every item surely matches; when only one can fail, that item is narrowed the other way
                bool variadic = fixedItems.Any(i => i is TypeVarTupleType);
                var failing = ItemPatterns(p, fixedItems.Count)
                    .Where(ip => !Irrefutable(ip.Sub) && (variadic || NarrowPattern(m, ip.Sub, fixedItems[ip.Index], false) is not NeverType)).ToList();
                if (failing.Count == 0) continue;
                if (failing.Count == 1 && !variadic)
                {
                    var (sub, index) = failing[0];
                    var narrowed = fixedItems.ToList();
                    narrowed[index] = NarrowPattern(m, sub, narrowed[index], false);
                    result.Add(narrowed.SequenceEqual(fixedItems) ? x : TupleOf(narrowed));
                    continue;
                }
                result.Add(x);
            }
            else result.Add(x);
        }
        return result.Count == 0 ? PyType.Never : PyType.Union(result);
    }

    /// <summary>The tuple item each sub-pattern stands for: those before the star count from the start, those after it from the end.</summary>
    static IEnumerable<(Pattern Sub, int Index)> ItemPatterns(MatchSequence p, int length)
    {
        int starAt = p.Items.ToList().FindIndex(i => i is MatchStar);
        for (int i = 0; i < p.Items.Count; i++)
            if (i != starAt) yield return (p.Items[i], starAt < 0 || i < starAt ? i : length - (p.Items.Count - i));
    }

    /// <summary>A pattern that matches anything: a capture, `_`, a star, or one of those with `as` or `|`.</summary>
    static bool Irrefutable(Pattern p) => p is MatchCapture or MatchStar || p is MatchAs a && Irrefutable(a.Pattern) || p is MatchOr o && o.Alternatives.Any(Irrefutable);

    /// <summary>`case {"k": v}:`: only mappings match.</summary>
    PyType NarrowMapping(PyType t, bool matched)
    {
        if (!matched) return t;
        var kept = t.Members.Where(x => !IsBuiltinInstance(x, NotMappings)).ToList();
        return kept.Count == 0 ? PyType.Never : PyType.Union(kept);
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

        if (name is "isinstance" or "issubclass" && count == 2)
        {
            bool sub = name == "issubclass";
            var (classes, unknown) = IsinstanceClasses(m, call.Args[1].Value);
            if (!positive) return classes.Count == 0 ? t : sub ? IsSubclassNegative(t, classes) : IsInstanceNegative(t, classes);
            var known = classes.Count == 0 ? null : sub ? IsSubclassPositive(t, classes) : IsInstancePositive(t, classes);
            // a class nothing is known about: the value is one of those, so the members it had before must not stay
            if (unknown) return known == null ? PyType.Unknown : PyType.Union(known, PyType.Unknown);
            return known ?? t;
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
        PyType.Union(t.Members.Select(x => x is ClassType { IsInstance: true } or FunctionType or OverloadedType or SignatureType
            && AttributeType(x, attribute).Missing.Count > 0 ? PyType.Unknown : x));

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

            case MatchSequence sq:
                return NarrowSequence(m, sq, t, matched);

            case MatchMapping mp:
            {
                t = NarrowMapping(t, matched);
                if (!matched) return t;
                // `case {"type": "a" | "b"}:` on a union of TypedDicts, as `x["type"] == "a"` narrows it
                for (int i = 0; i < mp.Keys.Count && i < mp.Patterns.Count; i++)
                    if (StringValue(mp.Keys[i]) is string key) t = NarrowByTypedDictKeyPattern(t, key, mp.Patterns[i]);
                return t;
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

    // ------------------------------------------------------------ names bound by patterns

    /// <summary>
    /// The type of a name a pattern binds (a capture, `as`, `*rest`, `**rest`): the part of the subject it matches, after
    /// the narrowing of the earlier cases and of the pattern itself. A subject nothing is known about binds Unknown, except where the
    /// pattern says what it is: as in pyright, `case Point(x=a) as p` makes `p` a Point and `a` its `x`.
    /// </summary>
    PyType PatternBindingType(ModuleInfo m, Declaration d)
    {
        Node? node = d.Node;
        while (node != null && node is not MatchCase) node = m.ParentOf(node);
        if (node is not MatchCase mc || m.ParentOf(mc) is not MatchStmt ms) return PyType.Unknown;
        var t = TypeOf(m, ms.Subject);
        foreach (var earlier in ms.Cases)
        {
            if (ReferenceEquals(earlier, mc)) break;
            // a case with a guard can fail after its pattern matched
            if (earlier.Guard == null) t = NarrowPattern(m, earlier.Pattern, t, false);
        }
        if (t is NeverType) return t;    // the earlier cases leave nothing for this one
        return BoundType(m, mc.Pattern, t, d.Name) ?? PyType.Unknown;
    }

    /// <summary>What <paramref name="name"/> is bound to when <paramref name="p"/> matches a value of type <paramref name="t"/>; null when p does not bind it.</summary>
    PyType? BoundType(ModuleInfo m, Pattern p, PyType t, string name)
    {
        switch (p)
        {
            case MatchCapture c:
                return c.Name == name ? t : null;

            case MatchAs a:
                return a.Name == name ? NarrowPattern(m, a.Pattern, t, true) : BoundType(m, a.Pattern, t, name);

            case MatchOr o:
            {
                // each alternative is tried on what the ones before it did not match
                var parts = new List<PyType>();
                foreach (var alt in o.Alternatives)
                {
                    if (BoundType(m, alt, t, name) is { } part) parts.Add(part);
                    t = NarrowPattern(m, alt, t, false);
                }
                return parts.Count == 0 ? null : PyType.Union(parts);
            }

            case MatchSequence sq:
            {
                int at = sq.Items.ToList().FindIndex(i => Binds(i, name));
                if (at < 0) return null;
                var narrowed = NarrowSequence(m, sq, t, true);
                if (sq.Items[at] is MatchStar)
                {
                    // one list of what the star takes from each sequence; one that it takes nothing from adds nothing
                    var taken = PyType.Union(narrowed.Members.Select(x => SequenceItemType(sq, x, at)));
                    return Inst("list", taken is NeverType ? PyType.Unknown : taken);
                }
                return PyType.Union(narrowed.Members.Select(x => BoundType(m, sq.Items[at], SequenceItemType(sq, x, at), name) ?? PyType.Unknown));
            }

            case MatchMapping mp:
            {
                int at = mp.Patterns.ToList().FindIndex(i => Binds(i, name));
                if (at < 0 && mp.Rest != name) return null;
                var narrowed = NarrowMapping(t, true);
                return PyType.Union(narrowed.Members.Select(x =>
                {
                    var (key, value) = MappingItemTypes(x, at < 0 ? null : mp.Keys[at]);
                    return at < 0 ? Inst("dict", key, value) : BoundType(m, mp.Patterns[at], value, name) ?? PyType.Unknown;
                }));
            }

            case MatchClass c:
            {
                int pos = c.Positional.ToList().FindIndex(i => Binds(i, name));
                int kw = c.KeywordPatterns.ToList().FindIndex(i => Binds(i, name));
                if (pos < 0 && kw < 0) return null;
                var sub = pos >= 0 ? c.Positional[pos] : c.KeywordPatterns[kw];
                var narrowed = NarrowPattern(m, c, t, true);
                return PyType.Union(narrowed.Members.Select(x =>
                {
                    if (x is not ClassType { IsInstance: true } xc) return PyType.Unknown;
                    // `case int(n):`: the builtins that match their one positional argument against the whole subject
                    if (pos == 0 && xc.Info.Module.Name == "builtins" && SelfMatching.Contains(xc.Info.Name) && MatchArgs(xc) == null)
                        return BoundType(m, sub, xc, name) ?? PyType.Unknown;
                    var attr = pos >= 0 ? MatchArgs(xc) is { } names && pos < names.Count ? names[pos] : null : c.KeywordNames[kw];
                    if (attr == null) return PyType.Unknown;
                    var attrType = AttributeType(xc, attr);
                    return attrType.Missing.Count > 0 ? PyType.Unknown : BoundType(m, sub, attrType.Type, name) ?? PyType.Unknown;
                }));
            }
        }
        return null;
    }

    static bool Binds(Pattern p, string name) => p switch
    {
        MatchCapture c => c.Name == name,
        MatchStar s => s.Name == name,
        MatchAs a => a.Name == name || Binds(a.Pattern, name),
        MatchOr o => o.Alternatives.Any(alt => Binds(alt, name)),
        MatchSequence sq => sq.Items.Any(i => Binds(i, name)),
        MatchMapping mp => mp.Rest == name || mp.Patterns.Any(i => Binds(i, name)),
        MatchClass c => c.Positional.Any(i => Binds(i, name)) || c.KeywordPatterns.Any(i => Binds(i, name)),
        _ => false,
    };

    /// <summary>The type of the items that item <paramref name="at"/> of a sequence pattern matches in <paramref name="x"/>; for the star, the items it takes (one type for all of them).</summary>
    PyType SequenceItemType(MatchSequence sq, PyType x, int at)
    {
        if (x is not ClassType { IsInstance: true } c) return PyType.Unknown;
        if (c.TupleItems != null && c.TupleItems.Any(i => i is TypeVarTupleType)) return PyType.Unknown;
        if (TupleItemsOf(c) is { } items)
        {
            if (sq.Items[at] is MatchStar)
            {
                // the items between those matched before the star and those matched after it
                int after = sq.Items.Count - at - 1;
                var taken = items.Skip(at).Take(items.Count - at - after).ToList();
                return PyType.Union(taken);
            }
            foreach (var (sub, index) in ItemPatterns(sq, items.Count))
                if (ReferenceEquals(sub, sq.Items[at])) return index >= 0 && index < items.Count ? items[index] : PyType.Unknown;
            return PyType.Unknown;
        }
        // tuple[int, *tuple[str, ...]] with `case (a, *rest)`: the items the pattern reaches by position, the others all together
        int starAt = sq.Items.ToList().FindIndex(i => i is MatchStar);
        if (AtLeastParts(c) is { } p && starAt >= 0)
        {
            int fromEnd = sq.Items.Count - at;
            if (at < starAt && at < p.Before.Count) return p.Before[at];
            if (at > starAt && fromEnd <= p.After.Count) return p.After[p.After.Count - fromEnd];
            int after = sq.Items.Count - starAt - 1;
            if (at == starAt && starAt <= p.Before.Count && after <= p.After.Count)
                return PyType.Union(p.Before.Skip(starAt).Append(p.Item).Concat(p.After.Take(p.After.Count - after)));
        }
        return IterElement(c);
    }

    /// <summary>The key and value types of a mapping; with a constant key of a TypedDict, the value is that key's.</summary>
    (PyType Key, PyType Value) MappingItemTypes(PyType x, Expr? key)
    {
        if (x is not ClassType { IsInstance: true } c) return (PyType.Unknown, PyType.Unknown);
        if (TypedDictFieldsOf(c.Info) is { } fields)
        {
            if (key == null) return (Inst("str"), Inst("object"));
            return (Inst("str"), StringValue(key) is { } k && fields.FirstOrDefault(f => f.Field.Name == k).Field is { } field ? field.Type : PyType.Unknown);
        }
        if (TypingClass("Mapping") is { } mapping && SpecializeAs(c, mapping.Info) is { Args.Count: 2 } spec) return (spec.Args[0], spec.Args[1]);
        return (PyType.Unknown, PyType.Unknown);
    }

    /// <summary>The builtins whose class pattern matches its one positional sub-pattern against the subject itself.</summary>
    static readonly HashSet<string> SelfMatching = new() { "bool", "bytearray", "bytes", "dict", "float", "frozenset", "int", "list", "set", "str", "tuple" };

    /// <summary>
    /// The attribute names a class pattern's positional sub-patterns stand for: a `__match_args__ = ("x", "y")` in the
    /// class or a base, else the fields of a named tuple, else the init fields of a dataclass that are not keyword-only.
    /// </summary>
    /// <summary>The attribute names of a class pattern's positional sub-patterns (null when the class says none), and whether the class matches itself with one (int(x)).</summary>
    public (IReadOnlyList<string>? Names, bool SelfMatching) MatchArgsOf(ClassType c) =>
        (MatchArgs(c), Mro(c.Info).Any(e => e is ClassType b && b.Info.Module.Name == "builtins" && SelfMatching.Contains(b.Info.Name)));

    IReadOnlyList<string>? MatchArgs(ClassType c)
    {
        if (FindMember(c, "__match_args__", false, null, out _) is { ClassSymbol: { } sym } hit)
        {
            foreach (var d in sym.Declarations)
                if (d.Node is Expr target && hit.Owner.Info.Module.ParentOf(target) is AssignStmt { Value: TupleExpr tuple } && tuple.Items.All(i => StringValue(i) != null))
                    return tuple.Items.Select(i => StringValue(i)!).ToList();
            // typeshed's NamedTuple only declares it (`__match_args__: ClassVar[tuple[str, ...]]`): the fields below say what it is
            if (!hit.Owner.Info.Module.IsStub) return null;
        }
        if (NamedTupleFieldsOf(c.Info) is { } tupleFields) return tupleFields.Select(f => f.Name).ToList();
        if (IsDataclass(c.Info)) return DataclassFields(c).Where(f => f.InInit && !f.KwOnly).Select(f => f.Name).ToList();
        return null;
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

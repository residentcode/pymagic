using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

public sealed partial class TypeEvaluator
{
    /// <summary>Converts an annotation expression to the type it denotes (class objects become instances, etc.).</summary>
    public PyType AnnotationType(ModuleInfo m, Expr ann) => Annotation(m, ann, null, deferred: true);

    /// <param name="lookup">
    /// Scope for resolving bare names. Used for string annotations, whose parsed fragment has no semantic references;
    /// null for ordinary expressions that the binder already resolved.
    /// </param>
    /// <param name="deferred">
    /// A real annotation (of a parameter, return or variable), which postponed annotations and stubs never evaluate where it stands;
    /// false for type expressions the runtime does evaluate (alias values, class bases, the functional TypedDict).
    /// </param>
    PyType Annotation(ModuleInfo m, Expr e, Scope? lookup, bool deferred = false)
    {
        switch (e)
        {
            case ConstExpr { Kind: ConstKind.None }: return NoneInstance;
            case ConstExpr { Kind: ConstKind.Str, IsFString: false } s:
                return ForwardReference(m, s);
            case NameExpr or AttributeExpr:
                return ToAnnotation(ValueOf(m, e, lookup, deferred));
            case SubscriptExpr sub:
                return AnnotationSubscript(m, sub, lookup, deferred);
            case BinaryExpr { Op: "|" } b:
                return PyType.Union(Annotation(m, b.Left, lookup, deferred), Annotation(m, b.Right, lookup, deferred));
            case StarExpr star:
                // `*Ts` (tuple items, Callable parameters, `*args: *Ts`) is what Unpack[Ts] spells
                return Annotation(m, star.Value, lookup, deferred) is TypeVarTupleType ts ? ts : PyType.Unknown;
            default:
                return PyType.Unknown;
        }
    }

    /// <summary>Evaluates a name/attribute chain used inside an annotation as a value.</summary>
    PyType ValueOf(ModuleInfo m, Expr e, Scope? lookup, bool deferred = false)
    {
        switch (e)
        {
            // under `from __future__ import annotations` (and in stubs) a name means its symbol, a class bound later included, as a quoted one does
            case NameExpr n when lookup == null && deferred && (m.PostponedAnnotations || m.IsStub) && m.Semantics.ReferenceOf(n)?.Symbol is { } sym:
                return TypeOfSymbol(sym);
            case NameExpr n when lookup == null: return TypeOf(m, n);
            case NameExpr n:
            {
                var sym = lookup!.Lookup(n.Name);
                return sym != null ? TypeOfSymbol(sym) : BuiltinNameType(n.Name);
            }
            case AttributeExpr a: return AttributeType(ValueOf(m, a.Value, lookup, deferred), a.Attr).Type;
            default: return PyType.Unknown;
        }
    }

    PyType ForwardReference(ModuleInfo m, ConstExpr s)
    {
        string text = s.Text.Trim();
        int q = 0;
        while (q < text.Length && text[q] != '"' && text[q] != '\'') q++;
        if (q >= text.Length) return PyType.Unknown;
        char quote = text[q];
        int run = text.Skip(q).TakeWhile(c => c == quote).Count();
        int qlen = run >= 6 ? 3 : 1;
        if (text.Length < q + 2 * qlen) return PyType.Unknown;
        string inner = text.Substring(q + qlen, text.Length - q - 2 * qlen).Trim();
        if (inner.Length == 0) return PyType.Unknown;

        var parsed = Parser.Parse(inner);
        if (parsed.Diagnostics.Count > 0 || parsed.Module.Body.Count != 1 || parsed.Module.Body[0] is not ExprStmt es)
            return PyType.Unknown;
        var scope = m.Semantics.InnermostScopeAt(s.Start);
        return Annotation(m, es.Value, scope);
    }

    /// <summary>A value found in an annotation position -> the type of things it describes.</summary>
    PyType ToAnnotation(PyType v)
    {
        return MapMembers(v, t =>
        {
            switch (t)
            {
                case ClassType { IsInstance: false } c: return c.AsInstance();
                case TypeVarType or ParamSpecType or TypeVarTupleType: return t;
                case SpecialFormType f:
                    return f.Name switch
                    {
                        "Any" => PyType.Any,
                        "NoReturn" or "Never" => PyType.Never,
                        "Self" => new TypeVarType("Self"),
                        "LiteralString" => Inst("str"),
                        "List" => Inst("list"),
                        "Dict" => Inst("dict"),
                        "Set" => Inst("set"),
                        "FrozenSet" => Inst("frozenset"),
                        "Tuple" => Inst("tuple"),
                        "Type" => Inst("type"),
                        "DefaultDict" => CollectionsInst("defaultdict"),
                        "OrderedDict" => CollectionsInst("OrderedDict"),
                        "Counter" => CollectionsInst("Counter"),
                        "Deque" => CollectionsInst("deque"),
                        "ChainMap" => CollectionsInst("ChainMap"),
                        "TypedDict" => Inst("dict", Inst("str"), PyType.Any),
                        "Callable" => new SignatureType(null, PyType.Unknown),
                        _ => PyType.Unknown,
                    };
                case ClassType { IsInstance: true } inst when IsNone(inst): return inst;
                default: return PyType.Unknown;
            }
        });
    }

    readonly Dictionary<string, ClassType?> _collectionsClasses = new();

    PyType CollectionsInst(string name, params PyType[] args)
    {
        var module = _ws.GetModule("collections");
        var c = module == null ? null : ModuleClass(_collectionsClasses, module, name);
        return c == null ? PyType.Unknown : new ClassType(c.Info, args, true);
    }

    /// <summary>The annotation form back to a value: instances of classes become the class object (for `Alias = list[int]`).</summary>
    PyType AnnotationToValue(PyType ann) => MapMembers(ann, t => t is ClassType { IsInstance: true } c ? c.AsClassObject() : t);

    PyType AnnotationSubscript(ModuleInfo m, SubscriptExpr sub, Scope? lookup, bool deferred = false)
    {
        var baseValue = ValueOf(m, sub.Value, lookup, deferred);
        IReadOnlyList<Expr> args = sub.Index is TupleExpr { Parenthesized: false } t ? t.Items : new[] { sub.Index };

        return MapMembers(baseValue, b =>
        {
            switch (b)
            {
                case SpecialFormType f: return SpecialFormSubscript(m, f.Name, args, lookup, deferred);
                case ClassType { IsInstance: false } c:
                    if (c.Info.Name == "tuple" && c.Info.Module.Name == "builtins") return TupleAnnotation(m, args, lookup, deferred);
                    if (c.Info.Name == "type" && c.Info.Module.Name == "builtins" && args.Count == 1)
                        return TypeOfClassObject(Annotation(m, args[0], lookup, deferred));
                    return new ClassType(c.Info, args.Select(a => Annotation(m, a, lookup, deferred)).ToList(), true);
                default: return PyType.Unknown;
            }
        });
    }

    PyType TypeOfClassObject(PyType instance) => MapMembers(instance, t => t switch
    {
        ClassType { IsInstance: true } c => c.AsClassObject(),
        TypeVarType { Name: "Self" } => new TypeVarType("type[Self]"),
        TypeVarType tv when !tv.Name.StartsWith("type[", StringComparison.Ordinal) => new TypeVarType($"type[{tv.Name}]"),   // type[T]: solved from the class an argument is
        UnknownType u => ClassOfAny(u),
        _ => PyType.Unknown,
    });

    /// <summary>
    /// type[Any], or the class of something unknown (an unresolved import): an instance of `type` that keeps the Any, so the
    /// members `type` has keep their types and any other attribute is the Any instead of missing, as in PyCharm.
    /// </summary>
    PyType ClassOfAny(UnknownType u) => BuiltinClass("type") is { } t ? new ClassType(t.Info, new PyType[] { u }, true) : u;

    /// <summary>type[Any], or its own class (what `type(cls)` and `cls.__class__` give: the metaclass of an unknown class).</summary>
    static bool IsClassOfAny(ClassType c) =>
        c is { Args: [UnknownType] } && c.Info.Name == "type" && c.Info.Module.Name == "builtins";

    PyType TupleAnnotation(ModuleInfo m, IReadOnlyList<Expr> args, Scope? lookup, bool deferred = false)
    {
        var tuple = BuiltinClass("tuple");
        if (tuple == null) return PyType.Unknown;
        // tuple[T, ...] is homogeneous; tuple[()] is empty
        if (args.Count == 2 && args[1] is ConstExpr { Kind: ConstKind.Ellipsis })
            return new ClassType(tuple.Info, new[] { Annotation(m, args[0], lookup, deferred) }, true);
        if (args.Count == 1 && args[0] is TupleExpr { Items.Count: 0 })
            return new ClassType(tuple.Info, new[] { PyType.Unknown }, true, Array.Empty<PyType>());
        return TupleOf(tuple, args.Select(a => Annotation(m, a, lookup, deferred)).ToList());
    }

    /// <summary>A tuple of these items; with a `*Ts` among them its length is not known, so its element type is not either.</summary>
    static ClassType TupleOf(ClassType tuple, IReadOnlyList<PyType> items) =>
        new(tuple.Info, new[] { items.Any(x => x is TypeVarTupleType) ? PyType.Unknown : PyType.Union(items) }, true, items);

    PyType SpecialFormSubscript(ModuleInfo m, string form, IReadOnlyList<Expr> args, Scope? lookup, bool deferred = false)
    {
        PyType A(int i) => i < args.Count ? Annotation(m, args[i], lookup, deferred) : PyType.Unknown;
        switch (form)
        {
            case "Optional": return PyType.Union(A(0), NoneInstance);
            case "Union": return PyType.Union(args.Select(a => Annotation(m, a, lookup, deferred)));
            case "ClassVar": case "Final": case "Annotated": case "Required": case "NotRequired":
            case "ReadOnly": case "Unpack":
                return A(0);
            case "TypeGuard": case "TypeIs": return Inst("bool");
            case "Type": return TypeOfClassObject(A(0));
            case "Tuple": return TupleAnnotation(m, args, lookup, deferred);
            case "List": return Inst("list", A(0));
            case "Set": return Inst("set", A(0));
            case "FrozenSet": return Inst("frozenset", A(0));
            case "Dict": return Inst("dict", A(0), A(1));
            case "DefaultDict": return CollectionsInst("defaultdict", A(0), A(1));
            case "OrderedDict": return CollectionsInst("OrderedDict", A(0), A(1));
            case "Counter": return CollectionsInst("Counter", A(0));
            case "Deque": return CollectionsInst("deque", A(0));
            case "ChainMap": return CollectionsInst("ChainMap", A(0), A(1));
            case "Literal": return PyType.Union(args.Select(a => LiteralType(m, a, lookup, deferred)));
            case "Callable":
            {
                if (args.Count < 2) return new SignatureType(null, PyType.Unknown);
                PyType ret = A(1);
                if (args[0] is ListExpr l)
                {
                    // Callable[[int, *Ts], R]: the *Ts is a `*args: *Ts` parameter after the leading ones
                    var ps = l.Items.Select((x, i) => Annotation(m, x, lookup, deferred) is var t && t is TypeVarTupleType
                        ? new ParamInfo($"_{i}", t, ParamCategory.VarPositional, false)
                        : new ParamInfo($"_{i}", t, ParamCategory.PositionalOnly, false)).ToList();
                    // parameters after the *Ts (Callable[[*Ts, int], R]) are left for later: it takes any arguments
                    int star = ps.FindIndex(p => p.Category == ParamCategory.VarPositional);
                    return star >= 0 && star < ps.Count - 1 ? new SignatureType(null, ret) : new SignatureType(ps, ret);
                }
                // Callable[P, R] and Callable[Concatenate[X, P], R]: the leading parameters, then what P stands for
                switch (Annotation(m, args[0], lookup, deferred))
                {
                    case ParamSpecType p: return new SignatureType(Array.Empty<ParamInfo>(), ret, p);
                    case SignatureType { Params: { } leading, Spec: { } tail }: return new SignatureType(leading, ret, tail);
                }
                return new SignatureType(null, ret); // Callable[..., R]
            }
            case "Concatenate":
            {
                // the parameter list it stands for, kept as a signature: X, Y positional-only, then P (`...` takes anything)
                if (args.Count < 2 || Annotation(m, args[^1], lookup, deferred) is not ParamSpecType spec) return new SignatureType(null, PyType.Unknown);
                var leading = args.Take(args.Count - 1).Select((x, i) =>
                    new ParamInfo($"_{i}", Annotation(m, x, lookup, deferred), ParamCategory.PositionalOnly, false)).ToList();
                return new SignatureType(leading, PyType.Unknown, spec);
            }
            default: return PyType.Unknown;
        }
    }

    /// <summary>One argument of Literal[...]: a value of str, int, bytes or bool, None, an enum member, or another Literal.</summary>
    PyType LiteralType(ModuleInfo m, Expr e, Scope? lookup, bool deferred = false)
    {
        PyType WithValue(string cls, object? value) =>
            Inst(cls) is ClassType c && value != null ? c with { LiteralValue = value } : Inst(cls);
        switch (e)
        {
            case ConstExpr { Kind: ConstKind.Int } i: return WithValue("int", LiteralInt(i.Text, negative: false));
            case ConstExpr { Kind: ConstKind.Str, IsFString: false } s: return WithValue("str", StringValue(s));
            case ConstExpr { Kind: ConstKind.Bytes } b: return WithValue("bytes", LiteralBytesValue(b));
            case ConstExpr { Kind: ConstKind.True }: return WithValue("bool", true);
            case ConstExpr { Kind: ConstKind.False }: return WithValue("bool", false);
            case ConstExpr { Kind: ConstKind.None }: return NoneInstance;
            case UnaryExpr { Op: "-", Operand: ConstExpr { Kind: ConstKind.Int } ni }: return WithValue("int", LiteralInt(ni.Text, negative: true));
            case NameExpr or AttributeExpr:
                // Literal[Color.RED]
                return TypeOf(m, e) is ClassType { IsInstance: true, EnumMember: not null } member ? member : PyType.Unknown;
            case SubscriptExpr:
                return Annotation(m, e, lookup, deferred);      // Literal[Literal["a"], "b"]
            default: return PyType.Unknown;
        }
    }

    static object? LiteralInt(string text, bool negative)
    {
        text = text.Replace("_", "");
        long value;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { if (!long.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value)) return null; }
        else if (text.StartsWith("0o", StringComparison.OrdinalIgnoreCase)) { try { value = Convert.ToInt64(text[2..], 8); } catch (Exception) { return null; } }
        else if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) { try { value = Convert.ToInt64(text[2..], 2); } catch (Exception) { return null; } }
        else if (!long.TryParse(text, out value)) return null;
        return negative ? -value : value;
    }

    /// <summary>The text between the quotes of a bytes literal.</summary>
    static LiteralBytes? LiteralBytesValue(ConstExpr c)
    {
        string text = c.Text;
        int q = 0;
        while (q < text.Length && text[q] != '"' && text[q] != '\'') q++;
        if (q >= text.Length) return null;
        int n = text.Length - q >= 6 && text[q + 1] == text[q] && text[q + 2] == text[q] ? 3 : 1;
        return text.Length < q + 2 * n ? null : new LiteralBytes(text.Substring(q + n, text.Length - q - 2 * n));
    }
}
